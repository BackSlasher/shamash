package net.backslasher.shamash.tts

import java.io.ByteArrayOutputStream
import kotlinx.coroutines.runBlocking
import net.backslasher.shamash.config.ShamashConfig
import okhttp3.OkHttpClient
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test

/**
 * Opt-in: needs env ELEVENLABS_KEY and `-PliveApi=true`.
 *   ./build.sh testDebugUnitTest -PliveApi=true --tests '*Live*'
 */
class ElevenLabsSpeakerLiveTest {
    @Test fun speaksHebrewIntoTheSink() = runBlocking {
        val key = System.getenv("ELEVENLABS_KEY")
        assumeTrue("ELEVENLABS_KEY not set", !key.isNullOrBlank())
        assumeTrue("-PliveApi=true not passed", System.getProperty("shamash.liveApi") == "true")

        var firstAudioMs = -1L
        var started = 0L
        val out = ByteArrayOutputStream()
        val sink = object : PcmSink {
            override fun write(buf: ByteArray, off: Int, len: Int) {
                if (firstAudioMs < 0) firstAudioMs = (System.nanoTime() - started) / 1_000_000
                out.write(buf, off, len)
            }
            override fun finish() {}
            override fun stopNow() {}
            override fun release() {}
        }
        val speaker = ElevenLabsSpeaker(key!!, ShamashConfig(appVersion = "live-test"), OkHttpClient(), { sink })
        started = System.nanoTime()
        speaker.speak("שלום, זאת בדיקה של הדיבור בעברית.")
        val totalMs = (System.nanoTime() - started) / 1_000_000
        println("LIVE tts: first audio after $firstAudioMs ms, total $totalMs ms, bytes ${out.size()}")
        assertTrue("no PCM arrived", out.size() > 16_000) // > 0.5 s of audio
        assertTrue("first audio took $firstAudioMs ms", firstAudioMs in 0..3_000)
    }
}
