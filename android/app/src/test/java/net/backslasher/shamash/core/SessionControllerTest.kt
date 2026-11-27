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
import net.backslasher.shamash.link.ReplyProvider
import net.backslasher.shamash.storage.FileSessionStore
import net.backslasher.shamash.storage.SessionRecord
import net.backslasher.shamash.transcribe.FakeTranscriber
import net.backslasher.shamash.tts.FakeSpeaker
import net.backslasher.shamash.tts.Speaker
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
        @Volatile var hiByte: Int = 0 // sample amplitude of the emitted chunks (0 = silence)
        override fun start(): Flow<ByteArray> = flow {
            running = true
            while (running) {
                emit(ByteArray(3200) { i -> if (i % 2 == 1) hiByte.toByte() else 0 })
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
        val speaker: Speaker = FakeSpeaker(),
        val replyProvider: ReplyProvider? = null,
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
            speaker = speaker,
            replyProvider = replyProvider,
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
        speaker: Speaker = FakeSpeaker(),
        replyProvider: ReplyProvider? = null,
    ) = Env(this, File(tmp.root, "recordings"), config, transcriber, speaker, replyProvider)

    private class FakeReplyProvider(
        val replyText: String = "היי, מה נשמע",
        val replyDelayMs: Long = 1_000,
        val audioDelayMs: Long = 500,
        val failWith: Exception? = null,
    ) : ReplyProvider {
        var cancelled = false
        var awaited = 0
        override suspend fun awaitReply(onReplyText: (String) -> Unit, onSpeakingStarted: () -> Unit) {
            awaited++
            delay(replyDelayMs)
            failWith?.let { throw it }
            onReplyText(replyText)
            delay(100)
            onSpeakingStarted()
            delay(audioDelayMs)
        }
        override fun cancelReply() { cancelled = true }
    }

    private suspend fun TestScope.listenAndStop(e: Env) {
        e.controller.onTap()
        advanceTimeBy(1_500); runCurrent()
        assertEquals(ShamashState.LISTENING, e.state)
        e.controller.onTap()
        advanceTimeBy(1); runCurrent()
        var guard = 0
        while (!e.controller.model.value.isFinal && e.state == ShamashState.WORKING && guard++ < 500) {
            advanceTimeBy(10); runCurrent() // until the transcript is final and the reply wait begins
        }
    }

    @Test
    fun linkTurnPassesWorkingThenSpeakingThenIdleWithReply() = runTest {
        val rp = FakeReplyProvider()
        val e = env(replyProvider = rp)
        listenAndStop(e)
        assertEquals(ShamashState.WORKING, e.state)
        assertEquals("שלום, מה שלומך?", e.controller.model.value.transcript)
        assertTrue(e.controller.model.value.isFinal)
        assertEquals("", e.controller.model.value.reply)
        advanceTimeBy(1_000); runCurrent()
        assertEquals(ShamashState.WORKING, e.state)
        assertEquals("שלום, מה שלומך?", e.controller.model.value.transcript) // still on screen
        assertEquals("היי, מה נשמע", e.controller.model.value.reply)
        advanceTimeBy(200); runCurrent()
        assertEquals(ShamashState.SPEAKING, e.state)
        advanceTimeBy(1_000); runCurrent()
        assertEquals(ShamashState.IDLE, e.state)
        assertEquals("שלום, מה שלומך?", e.controller.model.value.transcript)
        assertEquals("היי, מה נשמע", e.controller.model.value.reply)
        assertFalse(rp.cancelled)
    }

    @Test
    fun nextListeningClearsTranscriptAndReply() = runTest {
        val e = env(replyProvider = FakeReplyProvider())
        listenAndStop(e)
        advanceTimeBy(2_500); runCurrent()
        assertEquals(ShamashState.IDLE, e.state)
        e.controller.onTap()
        assertEquals("", e.controller.model.value.transcript)
        assertEquals("", e.controller.model.value.reply)
    }

    @Test
    fun levelMovesWhileListeningAndIsZeroOtherwise() = runTest {
        val e = env()
        assertEquals(0f, e.controller.level.value)
        e.controller.onTap()
        advanceTimeBy(1_500); runCurrent()
        assertEquals(ShamashState.LISTENING, e.state)
        e.capture.hiByte = 0x20
        advanceTimeBy(300); runCurrent()
        assertTrue(e.controller.level.value > 0.3f)
        e.controller.onTap()
        advanceTimeBy(1); runCurrent()
        assertEquals(0f, e.controller.level.value)
    }

    @Test
    fun tapWhileLinkSpeakingCancelsAndGoesIdle() = runTest {
        val rp = FakeReplyProvider()
        val e = env(replyProvider = rp)
        listenAndStop(e)
        advanceTimeBy(1_200); runCurrent()
        assertEquals(ShamashState.SPEAKING, e.state)
        e.controller.onTap()
        assertEquals(ShamashState.IDLE, e.state)
        assertTrue(rp.cancelled)
        assertEquals("שלום, מה שלומך?", e.controller.model.value.transcript)
        assertEquals("היי, מה נשמע", e.controller.model.value.reply)
        advanceTimeBy(5_000); runCurrent()
        assertEquals(ShamashState.IDLE, e.state)
    }

    @Test
    fun tapWhileThinkingCancelsReply() = runTest {
        val rp = FakeReplyProvider()
        val e = env(replyProvider = rp)
        listenAndStop(e)
        assertEquals(ShamashState.WORKING, e.state)
        e.controller.onTap()
        assertEquals(ShamashState.IDLE, e.state)
        assertTrue(rp.cancelled)
    }

    @Test
    fun replyExceptionGoesToError() = runTest {
        val e = env(replyProvider = FakeReplyProvider(failWith = IOException("link dropped")))
        listenAndStop(e)
        advanceTimeBy(1_500); runCurrent()
        assertEquals(ShamashState.ERROR, e.state)
        assertEquals(ErrorKind.NO_NETWORK, e.controller.model.value.errorKind)
    }

    @Test
    fun replyTimeoutGoesToError() = runTest {
        val rp = FakeReplyProvider(replyDelayMs = 100_000)
        val e = env(config = ShamashConfig(appVersion = "test", linkReplyTimeoutMs = 5_000), replyProvider = rp)
        listenAndStop(e)
        advanceTimeBy(6_000); runCurrent()
        assertEquals(ShamashState.ERROR, e.state)
        assertTrue(rp.cancelled)
    }

    @Test
    fun blankTranscriptInLinkModeGoesIdleWithoutReply() = runTest {
        val rp = FakeReplyProvider(replyDelayMs = 10)
        val e = env(transcriber = FakeTranscriber(batchText = ""), replyProvider = rp)
        listenAndStop(e)
        advanceTimeBy(500); runCurrent()
        assertEquals(ShamashState.IDLE, e.state)
        assertEquals("", e.controller.model.value.transcript)
    }

    @Test
    fun tapGoesWorkingThenListening_secondTapReturnsToIdleWithBatchText() = runTest {
        val e = env()
        assertEquals(ShamashState.IDLE, e.state)
        e.controller.onTap()
        assertEquals(ShamashState.WORKING, e.state)
        advanceTimeBy(100); runCurrent() // stream still opening: amber, no cue yet
        assertEquals(ShamashState.WORKING, e.state)
        assertTrue(e.cues.events.isEmpty())
        assertEquals(0, e.capture.chunks)
        advanceTimeBy(1_000); runCurrent()
        assertEquals(ShamashState.LISTENING, e.state)
        assertEquals(listOf("start"), e.cues.events)
        assertTrue(e.capture.chunks > 0)
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
    fun doubleTapSkipsBatchUploadWithoutError() = runTest {
        // failBatch = true proves the upload is never attempted for a near-empty session.
        val e = env(transcriber = FakeTranscriber(failBatch = true))
        e.controller.onTap()
        advanceTimeBy(300); runCurrent() // open takes 200 ms, so ~100 ms of listening
        assertEquals(ShamashState.LISTENING, e.state)
        e.controller.onTap()
        advanceTimeBy(5_000); runCurrent()
        assertEquals(ShamashState.IDLE, e.state)
        val rec = e.record()
        assertNull(rec.error)
        assertEquals("", rec.batchText)
        assertEquals(1, e.files("wav").size)
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
        assertTrue(e.cues.events.isEmpty()) // no cue when the stream cannot be opened
        assertTrue(e.files("wav").isEmpty())
        assertTrue(e.files("json").isEmpty())
    }

    @Test
    fun finalStepTimeoutMapsToNoNetwork() = runTest {
        val e = env(transcriber = FakeTranscriber(batchDelayMs = 200_000))
        e.controller.onTap(); advanceTimeBy(2_000); runCurrent()
        e.controller.onTap(); advanceTimeBy(100_000); runCurrent()
        assertEquals(ShamashState.ERROR, e.state)
        assertEquals(ErrorKind.NO_NETWORK, e.controller.model.value.errorKind)
        assertEquals("timeout", e.record().errorType)
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

    @Test
    fun speakGoesGreenThenBackToIdleKeepingTheText() = runTest {
        val e = env(speaker = FakeSpeaker(msPerChar = 100))
        e.controller.speak("שלום") // 400 ms
        assertEquals(ShamashState.SPEAKING, e.state)
        assertEquals("שלום", e.controller.model.value.transcript)
        advanceTimeBy(300); runCurrent()
        assertEquals(ShamashState.SPEAKING, e.state)
        advanceTimeBy(200); runCurrent()
        assertEquals(ShamashState.IDLE, e.state)
        assertEquals("שלום", e.controller.model.value.transcript)
        assertTrue(e.controller.model.value.isFinal)
    }

    @Test
    fun speakIsIgnoredOutsideIdle() = runTest {
        val e = env()
        e.controller.onTap() // WORKING
        e.controller.speak("שלום")
        assertEquals(ShamashState.WORKING, e.state)
    }

    @Test
    fun tapWhileSpeakingCancelsTheSpeech() = runTest {
        var cancelled = false
        val speaker = object : Speaker {
            override suspend fun speak(text: String) {
                try { delay(60_000) } catch (c: kotlinx.coroutines.CancellationException) { cancelled = true; throw c }
            }
        }
        val e = env(speaker = speaker)
        e.controller.speak("משפט ארוך")
        advanceTimeBy(1_000); runCurrent()
        assertEquals(ShamashState.SPEAKING, e.state)
        e.controller.onTap()
        runCurrent()
        assertEquals(ShamashState.IDLE, e.state)
        assertEquals("משפט ארוך", e.controller.model.value.transcript)
        assertTrue(cancelled)
        advanceTimeBy(70_000); runCurrent()
        assertEquals(ShamashState.IDLE, e.state)
    }

    @Test
    fun speakerFailureShowsError() = runTest {
        val e = env(speaker = object : Speaker {
            override suspend fun speak(text: String) { throw java.io.IOException("boom") }
        })
        e.controller.speak("שלום"); runCurrent()
        assertEquals(ShamashState.ERROR, e.state)
    }
}
