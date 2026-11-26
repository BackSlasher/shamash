package net.backslasher.shamash.link

import java.io.File
import java.util.concurrent.CopyOnWriteArrayList
import kotlinx.coroutines.DelicateCoroutinesApi
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import net.backslasher.shamash.config.ShamashConfig
import net.backslasher.shamash.pclink.PcLinkFrame
import net.backslasher.shamash.transcribe.TranscriberError
import net.backslasher.shamash.tts.PcmSink
import okhttp3.OkHttpClient
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test

@OptIn(DelicateCoroutinesApi::class)
class LinkTranscriberTest {
    private lateinit var server: MockWebServer
    private val config = ShamashConfig(appVersion = "test")
    private val texts = CopyOnWriteArrayList<String>()
    private val frames = CopyOnWriteArrayList<ByteArray>()
    @Volatile private var serverSocket: WebSocket? = null
    @Volatile private var onEnd: (WebSocket) -> Unit = {}
    @Volatile private var closedByClient = false

    private class FakeSink : PcmSink {
        val events = CopyOnWriteArrayList<String>()
        val bytes = java.io.ByteArrayOutputStream()
        override fun write(buf: ByteArray, off: Int, len: Int) { bytes.write(buf, off, len); events += "write" }
        override fun finish() { events += "finish" }
        override fun stopNow() { events += "stopNow" }
        override fun release() { events += "release" }
    }

    private val sink = FakeSink()
    private var sinkRate = -1

    @Before fun setUp() {
        server = MockWebServer().also { it.start() }
        server.enqueue(
            MockResponse.Builder().webSocketUpgrade(object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) { serverSocket = webSocket }
                override fun onMessage(webSocket: WebSocket, text: String) {
                    texts += text
                    if (text.contains("\"end\"")) onEnd(webSocket)
                }
                override fun onMessage(webSocket: WebSocket, bytes: ByteString) { frames += bytes.toByteArray() }
                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    closedByClient = true
                    webSocket.close(1000, null)
                }
            }).build(),
        )
    }

    @After fun tearDown() { server.close() }

    private fun transcriber() = LinkTranscriber(
        url = server.url("/").toString().replaceFirst("http", "ws"),
        client = OkHttpClient(),
        config = config,
        sinkFactory = { sinkRate = it; sink },
        device = "TestPhone",
    )

    private fun <T> run(block: suspend () -> T): T = runBlocking { withTimeout(10_000) { block() } }

    private fun waitFor(cond: () -> Boolean) {
        val end = System.currentTimeMillis() + 5_000
        while (!cond() && System.currentTimeMillis() < end) Thread.sleep(10)
        assertTrue("condition not met in time", cond())
    }

    @Test fun fullTurn() = run {
        onEnd = { ws ->
            ws.send("""{"type":"transcript","text":"שלום"}""")
            ws.send("""{"type":"reply","text":"היי"}""")
            ws.send("""{"type":"audio_start","sampleRate":16000}""")
            ws.send(ByteString.of(1, 2, 3, 4))
            ws.send(ByteString.of(5, 6))
            ws.send("""{"type":"audio_end"}""")
            ws.send("""{"type":"done"}""")
        }
        val t = transcriber()
        val s = t.openStream()
        s.sendAudio(ByteArray(3200) { 1 })
        s.sendAudio(ByteArray(3200) { 2 })
        s.sendAudio(ByteArray(3200) { 3 })
        assertEquals("", s.finish())
        s.close() // must not close: the reply is still to come
        val r = t.transcribeBatch(File("unused.wav"))
        assertEquals("שלום", r.text)

        val replies = mutableListOf<String>()
        var speaking = 0
        t.awaitReply({ replies += it }, { speaking++ })
        assertEquals(listOf("היי"), replies)
        assertEquals(1, speaking)
        assertEquals(16000, sinkRate)
        assertEquals(listOf<Byte>(1, 2, 3, 4, 5, 6), sink.bytes.toByteArray().toList())
        assertEquals(listOf("write", "write", "finish", "release"), sink.events.toList())

        waitFor { texts.size >= 2 && frames.size >= 3 }
        assertEquals("""{"type":"hello","sampleRate":16000,"device":"TestPhone","mode":"conversation"}""", texts[0])
        assertEquals("""{"type":"end"}""", texts[1])
        assertEquals(listOf(0L, 1L, 2L), frames.map { PcLinkFrame.seqOf(it) })
        assertEquals(3200, PcLinkFrame.pcmOf(frames[2]).size)
        waitFor { closedByClient }
    }

    @Test fun emptyTranscriptThenDoneReturnsWithoutAudio() = run {
        onEnd = { ws ->
            ws.send("""{"type":"transcript","text":""}""")
            ws.send("""{"type":"done"}""")
        }
        val t = transcriber()
        val s = t.openStream()
        s.finish()
        assertEquals("", t.transcribeBatch(File("x")).text)
        t.awaitReply({ fail("no reply expected") }, { fail("no audio expected") })
        assertTrue(sink.events.isEmpty())
        waitFor { closedByClient }
    }

    @Test fun errorMessageThrowsPcStage() = run {
        onEnd = { ws -> ws.send("""{"type":"error","stage":"llm","message":"model down"}""") }
        val t = transcriber()
        val s = t.openStream()
        s.finish()
        try {
            t.transcribeBatch(File("x"))
            fail("expected exception")
        } catch (e: LinkPcException) {
            assertEquals("pc_llm", e.errorType)
            assertEquals(false, e.isNetwork)
        }
        waitFor { closedByClient }
    }

    @Test fun errorDuringReplyThrows() = run {
        onEnd = { ws ->
            ws.send("""{"type":"transcript","text":"a"}""")
            ws.send("""{"type":"error","stage":"tts","message":"x"}""")
        }
        val t = transcriber()
        t.openStream().finish()
        t.transcribeBatch(File("x"))
        try {
            t.awaitReply({}, {})
            fail("expected exception")
        } catch (e: LinkPcException) {
            assertEquals("pc_tts", e.errorType)
        }
    }

    @Test fun socketClosedBeforeDoneIsNetworkError() = run {
        onEnd = { ws ->
            ws.send("""{"type":"transcript","text":"a"}""")
            ws.close(1001, "bye")
        }
        val t = transcriber()
        t.openStream().finish()
        t.transcribeBatch(File("x"))
        try {
            t.awaitReply({}, {})
            fail("expected exception")
        } catch (e: Exception) {
            assertTrue(e is TranscriberError && e.isNetwork)
        }
    }

    @Test fun connectFailureIsNetworkError() = run {
        val url = server.url("/").toString().replaceFirst("http", "ws")
        server.close()
        try {
            LinkTranscriber(url, OkHttpClient(), config, { sink }, "x").openStream()
            fail("expected exception")
        } catch (e: Exception) {
            assertTrue(e is TranscriberError && e.isNetwork)
        }
    }

    @Test fun cancelReplySendsCancelStopsSinkAndCloses() = run {
        onEnd = { ws ->
            ws.send("""{"type":"transcript","text":"a"}""")
            ws.send("""{"type":"audio_start","sampleRate":16000}""")
            ws.send(ByteString.of(1, 2))
        }
        val t = transcriber()
        t.openStream().finish()
        t.transcribeBatch(File("x"))
        var speaking = false
        val job = kotlinx.coroutines.GlobalScope.launch(kotlinx.coroutines.Dispatchers.IO) {
            try { t.awaitReply({}, { speaking = true }) } catch (_: Exception) {}
        }
        waitFor { speaking }
        t.cancelReply()
        job.cancel()
        waitFor { texts.any { it.contains("cancel") } }
        assertTrue(sink.events.contains("stopNow"))
        waitFor { closedByClient }
    }
}
