package net.backslasher.shamash.core

import java.io.File
import java.io.IOException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.currentTime
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import net.backslasher.shamash.audio.AudioCapture
import net.backslasher.shamash.audio.Cues
import net.backslasher.shamash.config.ShamashConfig
import net.backslasher.shamash.config.TriggerMode
import net.backslasher.shamash.storage.FileSessionStore
import net.backslasher.shamash.storage.SessionRecord
import net.backslasher.shamash.transcribe.FakeTranscriber
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

@OptIn(ExperimentalCoroutinesApi::class)
class SessionControllerTest {
    @get:Rule val tmp = TemporaryFolder()

    private class FakeCapture : AudioCapture {
        @Volatile var running = false
        var chunks = 0
        override fun start(): Flow<ByteArray> = flow {
            running = true
            while (running) {
                emit(ByteArray(3200))
                chunks++
                delay(100)
            }
        }
        override fun stop() { running = false }
    }

    private class FakeCues : Cues {
        val events = mutableListOf<String>()
        override fun start() { events += "start" }
        override fun stop() { events += "stop" }
    }

    private class Env(
        val scope: TestScope,
        val dir: File,
        val config: ShamashConfig,
        val transcriber: FakeTranscriber = FakeTranscriber(),
    ) {
        val capture = FakeCapture()
        val cues = FakeCues()
        val controller = SessionController(
            scope = scope.backgroundScope,
            config = config,
            capture = capture,
            transcriber = transcriber,
            store = FileSessionStore(dir),
            cues = cues,
            clock = object : Clock {
                override fun elapsedMs() = scope.testScheduler.currentTime
                override fun wallMs() = 1_700_000_000_000L + scope.testScheduler.currentTime
            },
            ioDispatcher = StandardTestDispatcher(scope.testScheduler),
        )
        val state get() = controller.model.value.state
        fun files(ext: String) = dir.listFiles { f -> f.extension == ext }.orEmpty().toList()
        fun record() = SessionRecord.fromJson(files("json").single().readText())
    }

    private fun TestScope.env(
        config: ShamashConfig = ShamashConfig(appVersion = "test"),
        transcriber: FakeTranscriber = FakeTranscriber(),
    ) = Env(this, File(tmp.root, "recordings"), config, transcriber)

    @Test
    fun tapGoesWorkingThenListening_secondTapReturnsToIdleWithBatchText() = runTest {
        val e = env()
        assertEquals(ShamashState.IDLE, e.state)
        e.controller.onTap()
        assertEquals(ShamashState.WORKING, e.state)
        advanceTimeBy(1_000); runCurrent()
        assertEquals(ShamashState.LISTENING, e.state)
        assertEquals(listOf("start"), e.cues.events)
        advanceTimeBy(2_500); runCurrent()
        assertEquals("שלום מה שלומך", e.controller.model.value.transcript)
        assertFalse(e.controller.model.value.isFinal)

        e.controller.onTap()
        advanceTimeBy(1); runCurrent()
        assertEquals(ShamashState.WORKING, e.state)
        assertEquals(listOf("start", "stop"), e.cues.events)
        advanceTimeBy(5_000); runCurrent()
        assertEquals(ShamashState.IDLE, e.state)
        assertEquals("שלום, מה שלומך?", e.controller.model.value.transcript)
        assertTrue(e.controller.model.value.isFinal)

        assertEquals(1, e.files("wav").size)
        val rec = e.record()
        assertNull(rec.error)
        assertEquals("שלום, מה שלומך?", rec.batchText)
        assertEquals("שלום מה שלומך", rec.realtimeFinal)
        assertEquals(3, rec.partials.size)
        assertNotNull(rec.latency.firstPartialMs)
        assertTrue(rec.latency.stopToFinalMs!! in 900L..1000L) // finish 300 + batch 600 + capture drain
        assertTrue(e.files("wav").single().length() > 44)
    }

    @Test
    fun openFailureGoesToErrorAndSavesNothing() = runTest {
        val e = env(transcriber = FakeTranscriber(failOpen = true))
        e.controller.onTap()
        advanceTimeBy(1_000); runCurrent()
        assertEquals(ShamashState.ERROR, e.state)
        assertEquals(ErrorKind.NO_NETWORK, e.controller.model.value.errorKind)
        assertNotNull(e.controller.model.value.errorMessage)
        assertEquals(0, e.capture.chunks)
        assertTrue(e.files("wav").isEmpty())
        assertTrue(e.files("json").isEmpty())
    }

    @Test
    fun openTimeoutGoesToError() = runTest {
        val e = env(transcriber = FakeTranscriber(openDelayMs = 60_000))
        e.controller.onTap()
        advanceTimeBy(8_001); runCurrent()
        assertEquals(ShamashState.ERROR, e.state)
        assertTrue(e.files("wav").isEmpty())
    }

    @Test
    fun maxDurationAutoStops() = runTest {
        val e = env(config = ShamashConfig(maxListeningMs = 3_000, appVersion = "test"))
        e.controller.onTap()
        advanceTimeBy(1_000); runCurrent()
        assertEquals(ShamashState.LISTENING, e.state)
        advanceTimeBy(2_500); runCurrent() // past 3 s of listening
        assertEquals(ShamashState.WORKING, e.state)
        assertEquals(listOf("start", "stop"), e.cues.events)
        assertFalse(e.capture.running)
        advanceTimeBy(5_000); runCurrent()
        assertEquals(ShamashState.IDLE, e.state)
        assertTrue(e.record().durationMs in 3_000..3_100)
    }

    @Test
    fun tapInErrorGoesToIdleKeepingLastTranscript_andNextTapStarts() = runTest {
        val e = env()
        // one good session first
        e.controller.onTap(); advanceTimeBy(2_000); runCurrent()
        e.controller.onTap(); advanceTimeBy(5_000); runCurrent()
        assertEquals(ShamashState.IDLE, e.state)
        // now fail to open
        e.transcriber.failOpen = true
        e.controller.onTap(); advanceTimeBy(1_000); runCurrent()
        assertEquals(ShamashState.ERROR, e.state)
        e.controller.onTap()
        assertEquals(ShamashState.IDLE, e.state)
        assertEquals("שלום, מה שלומך?", e.controller.model.value.transcript)
        assertNull(e.controller.model.value.errorMessage)
        e.transcriber.failOpen = false
        e.controller.onTap(); advanceTimeBy(1_000); runCurrent()
        assertEquals(ShamashState.LISTENING, e.state)
    }

    @Test
    fun batchFailureGoesToErrorButSavesWavAndJsonWithError() = runTest {
        val e = env(transcriber = FakeTranscriber(failBatch = true))
        e.controller.onTap(); advanceTimeBy(2_000); runCurrent()
        e.controller.onTap(); advanceTimeBy(5_000); runCurrent()
        assertEquals(ShamashState.ERROR, e.state)
        assertEquals(1, e.files("wav").size)
        val rec = e.record()
        assertNotNull(rec.error)
        assertTrue(rec.error!!.contains("batch failed"))
        assertNull(rec.batchText)
    }

    @Test
    fun tapsDuringWorkingAreIgnored() = runTest {
        val e = env()
        e.controller.onTap()
        e.controller.onTap() // double tap while opening
        advanceTimeBy(1_000); runCurrent()
        assertEquals(ShamashState.LISTENING, e.state)
        e.controller.onTap(); advanceTimeBy(1); runCurrent()
        assertEquals(ShamashState.WORKING, e.state)
        e.controller.onTap() // ignored
        advanceTimeBy(5_000); runCurrent()
        assertEquals(ShamashState.IDLE, e.state)
        assertEquals(1, e.files("json").size)
    }

    @Test
    fun holdModeStartsOnPressAndStopsOnRelease() = runTest {
        val e = env(config = ShamashConfig(triggerMode = TriggerMode.HOLD, appVersion = "test"))
        e.controller.onPressDown(); advanceTimeBy(1_000); runCurrent()
        assertEquals(ShamashState.LISTENING, e.state)
        e.controller.onTap() // tap callback after release must not toggle
        assertEquals(ShamashState.LISTENING, e.state)
        e.controller.onPressUp(); advanceTimeBy(5_000); runCurrent()
        assertEquals(ShamashState.IDLE, e.state)
    }
}
