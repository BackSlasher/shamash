package net.backslasher.shamash.transcribe

import java.io.File
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import net.backslasher.shamash.config.ShamashConfig
import okhttp3.OkHttpClient
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test

/**
 * Opt-in: needs env ELEVENLABS_KEY and `-PliveApi=true`. Never part of a normal build.
 *   ./build.sh testDebugUnitTest -PliveApi=true --tests '*ElevenLabsLiveTest*'
 */
class ElevenLabsLiveTest {
    private val fixture = File("../../tools/fixtures/hebrew_v3.wav")

    @Test fun streamsAndBatchesTheHebrewFixture() = runBlocking {
        val key = System.getenv("ELEVENLABS_KEY")
        assumeTrue("ELEVENLABS_KEY not set", !key.isNullOrBlank())
        assumeTrue("-PliveApi=true not passed", System.getProperty("shamash.liveApi") == "true")
        assertTrue("fixture missing: ${fixture.absolutePath}", fixture.exists())

        val config = ShamashConfig(appVersion = "live-test")
        val transcriber = ElevenLabsTranscriber(key!!, config, OkHttpClient())
        val pcm = fixture.readBytes().copyOfRange(44, fixture.length().toInt())

        val session = transcriber.openStream()
        val partials = mutableListOf<String>()
        val collector = async(start = CoroutineStart.UNDISPATCHED) {
            session.partials.collect { synchronized(partials) { partials += it } }
        }
        try {
            var offset = 0
            while (offset < pcm.size) {
                val end = minOf(offset + 3200, pcm.size)
                session.sendAudio(pcm.copyOfRange(offset, end))
                offset = end
                delay(100)
            }
            val committed = session.finish()
            println("LIVE realtime committed: $committed")
            println("LIVE partial count: ${partials.size}")
            assertTrue("committed: $committed", committed.contains("ירושלים") && committed.contains("לאנגלית"))
        } finally {
            collector.cancel()
            session.close()
        }

        val batch = transcriber.transcribeBatch(fixture)
        println("LIVE batch text: ${batch.text}")
        assertTrue("batch: ${batch.text}", batch.text.contains("ירושלים") && batch.text.contains("לאנגלית"))
    }
}
