package net.backslasher.shamash.tts

import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import net.backslasher.shamash.config.ShamashConfig
import okhttp3.OkHttpClient
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test
import java.io.ByteArrayOutputStream
import java.util.concurrent.atomic.AtomicBoolean

class ElevenLabsSpeakerTest {
    private lateinit var server: MockWebServer
    private val config = ShamashConfig(appVersion = "test")

    private class FakeSink : PcmSink {
        val bytes = ByteArrayOutputStream()
        val finished = AtomicBoolean(false)
        val stopped = AtomicBoolean(false)
        val released = AtomicBoolean(false)
        override fun write(buf: ByteArray, off: Int, len: Int) { synchronized(bytes) { bytes.write(buf, off, len) } }
        override fun finish() { finished.set(true) }
        override fun stopNow() { stopped.set(true) }
        override fun release() { released.set(true) }
    }

    @Before fun setUp() { server = MockWebServer().also { it.start() } }
    @After fun tearDown() { server.close() }

    private fun speaker(sink: FakeSink, client: OkHttpClient = OkHttpClient()) =
        ElevenLabsSpeaker("secret-key", config, client, { sink }, server.url("/").toString().trimEnd('/'))

    @Test fun postsToStreamEndpointAndHandsPcmToTheSink() = runBlocking {
        val pcm = ByteArray(10_001) { (it % 251).toByte() } // odd length: the stray byte must not be dropped mid-stream
        server.enqueue(MockResponse.Builder().code(200).body(okio.Buffer().write(pcm)).build())
        val sink = FakeSink()
        speaker(sink).speak("שלום, זאת בדיקה")

        val req = server.takeRequest()
        assertEquals("POST", req.method)
        assertEquals("/v1/text-to-speech/${config.ttsVoiceId}/stream", req.url.encodedPath)
        assertEquals("pcm_16000", req.url.queryParameter("output_format"))
        assertEquals("secret-key", req.headers["xi-api-key"])
        assertTrue(req.headers["Content-Type"]!!.startsWith("application/json"))
        val sent = req.body!!.utf8()
        assertTrue(sent, sent.contains("\"model_id\":\"${config.ttsModelId}\""))
        assertTrue(sent, sent.contains("\"language_code\":\"he\""))
        assertTrue(sent, sent.contains("שלום, זאת בדיקה"))

        // 10001 bytes: the odd trailing byte is held back (a 16-bit sample cannot be split), the rest arrives.
        assertArrayEquals(pcm.copyOf(10_000), sink.bytes.toByteArray())
        assertTrue(sink.finished.get())
        assertTrue(sink.released.get())
    }

    @Test fun httpErrorThrowsAndReleasesTheSink() = runBlocking {
        server.enqueue(MockResponse.Builder().code(401).body("""{"detail":"nope"}""").build())
        val sink = FakeSink()
        try {
            speaker(sink).speak("x")
            fail("expected TtsHttpException")
        } catch (e: TtsHttpException) {
            assertEquals(401, e.code)
        }
        assertTrue(sink.released.get())
    }

    @Test fun cancellingStopsTheSinkAndReleasesIt() = runBlocking {
        // Headers arrive but the body never completes.
        server.enqueue(MockResponse.Builder().code(200).body(okio.Buffer().write(ByteArray(2000))).bodyDelay(30, java.util.concurrent.TimeUnit.SECONDS).build())
        val sink = FakeSink()
        val job = async(start = CoroutineStart.DEFAULT) { speaker(sink).speak("שלום") }
        delay(500)
        job.cancelAndJoin()
        assertTrue(sink.stopped.get())
        assertTrue(sink.released.get())
        assertTrue(!sink.finished.get())
    }
}
