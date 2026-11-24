package net.backslasher.shamash.transcribe

import java.io.File
import java.nio.file.Files
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import net.backslasher.shamash.config.ShamashConfig
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test

class ElevenLabsTranscriberTest {
    private lateinit var server: MockWebServer
    private val config = ShamashConfig(connectTimeoutMs = 3_000, finalTimeoutMs = 3_000, appVersion = "test")

    @Before fun setUp() {
        server = MockWebServer().also { it.start() }
    }

    @After fun tearDown() {
        server.close()
    }

    private fun transcriber(commitEveryMs: Long = 25_000L) =
        ElevenLabsTranscriber("secret-key", config, baseUrl = server.url("/").toString().trimEnd('/'), commitEveryMs = commitEveryMs)

    private fun wavFile(): File = Files.createTempFile("clip", ".wav").toFile().also {
        it.writeBytes(ByteArray(100) { i -> i.toByte() })
        it.deleteOnExit()
    }

    // ---- batch ----

    @Test fun batchSendsMultipartAndParsesResponse() = runBlocking {
        val body = """{"language_code":"heb","text":"שלום עולם","words":[]}"""
        server.enqueue(MockResponse.Builder().code(200).body(body).build())
        val result = transcriber().transcribeBatch(wavFile())
        assertEquals("שלום עולם", result.text)
        assertEquals(body, result.raw)

        val req = server.takeRequest()
        assertEquals("POST", req.method)
        assertEquals("/v1/speech-to-text", req.url.encodedPath)
        assertEquals("secret-key", req.headers["xi-api-key"])
        assertTrue(req.headers["Content-Type"]!!.startsWith("multipart/form-data"))
        val sent = req.body!!.utf8()
        assertTrue(sent.contains("name=\"model_id\""))
        assertTrue(sent.contains("scribe_v3"))
        assertTrue(sent.contains("name=\"language_code\""))
        assertTrue(sent.contains("heb"))
        assertTrue(sent.contains("name=\"file\"; filename=\""))
        assertTrue(sent.contains("audio/wav"))
    }

    @Test fun batch422BecomesTypedErrorWithDetail() = runBlocking {
        server.enqueue(MockResponse.Builder().code(422).body("""{"detail":"audio too short"}""").build())
        try {
            transcriber().transcribeBatch(wavFile())
            fail("expected exception")
        } catch (e: ElevenLabsApiException) {
            assertEquals(422, e.httpStatus)
            assertEquals("http_422", e.errorType)
            assertTrue(e.message!!.contains("audio too short"))
            assertFalse(e.isNetwork)
        }
    }

    @Test fun batchConnectionFailureIsNetworkError() = runBlocking {
        val url = server.url("/").toString().trimEnd('/')
        server.close()
        val t = ElevenLabsTranscriber("k", config, baseUrl = url)
        try {
            t.transcribeBatch(wavFile())
            fail("expected exception")
        } catch (e: ElevenLabsNetworkException) {
            assertTrue(e.isNetwork)
        }
    }

    // ---- realtime ----

    /** Scripted server: sends session_started on open, then reacts to client messages via [onClient]. */
    private fun scriptWs(
        sendStarted: Boolean = true,
        onClient: (WebSocket, String) -> Unit,
    ): List<String> {
        val received = mutableListOf<String>()
        server.enqueue(
            MockResponse.Builder().webSocketUpgrade(object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    if (sendStarted) webSocket.send("""{"message_type":"session_started","session_id":"s1","config":{}}""")
                }

                override fun onMessage(webSocket: WebSocket, text: String) {
                    synchronized(received) { received += text }
                    onClient(webSocket, text)
                }

                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    synchronized(received) { received += "CLOSE $code" }
                    webSocket.close(1000, null)
                }
            }).build(),
        )
        return received
    }

    private fun isCommit(m: String) = m.contains("\"commit\":true")

    @Test fun streamsCumulativePartialsAndReturnsCommittedText() = runBlocking {
        var chunks = 0
        val received = scriptWs { ws, m ->
            if (isCommit(m)) {
                ws.send("""{"message_type":"partial_transcript","text":"שלום עולם יפה"}""")
                ws.send("""{"message_type":"committed_transcript","text":"שלום עולם יפה."}""")
            } else {
                chunks++
                if (chunks == 1) ws.send("""{"message_type":"partial_transcript","text":"שלום"}""")
                if (chunks == 2) ws.send("""{"message_type":"partial_transcript","text":"שלום עולם"}""")
            }
        }
        val session = transcriber().openStream()
        session.sendAudio(ByteArray(3200))
        session.sendAudio(ByteArray(3200))
        val final = session.finish()
        assertEquals("שלום עולם יפה.", final)
        val seen = withTimeout(3_000) { session.partials.take(4).toList() }
        assertEquals(listOf("שלום", "שלום עולם", "שלום עולם יפה", "שלום עולם יפה."), seen)
        assertTrue(received.size >= 3)
        assertTrue(received[0].contains("\"commit\":false"))
        assertTrue(received.any { it.contains("\"audio_base_64\":\"\"") })
        // the client closes the socket with 1000 after the committed transcript
        withTimeout(3_000) { while (synchronized(received) { "CLOSE 1000" !in received }) kotlinx.coroutines.delay(20) }
        assertEquals("secret-key", server.takeRequest().headers["xi-api-key"])
        session.close()
        session.close() // idempotent
    }

    @Test fun requestUrlCarriesRealtimeParameters() = runBlocking {
        scriptWs { _, _ -> }
        transcriber().openStream().close()
        val url = server.takeRequest().url
        assertEquals("/v1/speech-to-text/realtime", url.encodedPath)
        assertEquals("scribe_v3_live", url.queryParameter("model_id"))
        assertEquals("heb", url.queryParameter("language_code"))
        assertEquals("pcm_16000", url.queryParameter("audio_format"))
        assertEquals("manual", url.queryParameter("commit_strategy"))
    }

    @Test fun periodicCommitsAreConcatenated() = runBlocking {
        var mid = 0
        scriptWs { ws, m ->
            if (isCommit(m)) {
                mid++
                val text = if (mid == 1) "first part" else "second part"
                ws.send("""{"message_type":"committed_transcript","text":"$text"}""")
            }
        }
        // 3200 bytes = 100 ms; commit after 200 ms of audio
        val session = transcriber(commitEveryMs = 200).openStream()
        session.sendAudio(ByteArray(3200))
        session.sendAudio(ByteArray(3200)) // this one carries commit:true
        assertEquals("first part second part", session.finish())
        session.close()
    }

    @Test fun errorMessageDuringSessionFailsFinish() = runBlocking {
        scriptWs { ws, m ->
            if (isCommit(m)) ws.send("""{"message_type":"quota_exceeded","error":"out of credits"}""")
        }
        val session = transcriber().openStream()
        session.sendAudio(ByteArray(3200))
        try {
            session.finish()
            fail("expected exception")
        } catch (e: ElevenLabsApiException) {
            assertEquals("quota_exceeded", e.errorType)
            assertTrue(e.message!!.contains("out of credits"))
            assertFalse(e.isNetwork)
        }
        session.close()
    }

    @Test fun errorBeforeSessionStartedFailsOpen() = runBlocking {
        server.enqueue(
            MockResponse.Builder().webSocketUpgrade(object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    webSocket.send("""{"message_type":"auth_error","error":"bad key"}""")
                }
            }).build(),
        )
        try {
            transcriber().openStream()
            fail("expected exception")
        } catch (e: ElevenLabsApiException) {
            assertEquals("auth_error", e.errorType)
        }
    }

    @Test fun httpFailureOnUpgradeIsApiError() = runBlocking {
        server.enqueue(MockResponse.Builder().code(401).body("""{"detail":{"status":"invalid_api_key"}}""").build())
        try {
            transcriber().openStream()
            fail("expected exception")
        } catch (e: ElevenLabsApiException) {
            assertEquals(401, e.httpStatus)
            assertTrue(e.message!!.contains("invalid_api_key"))
        }
    }

    @Test fun noSessionStartedTimesOutAsNetworkError() = runBlocking {
        scriptWs(sendStarted = false) { _, _ -> }
        val t = ElevenLabsTranscriber("k", config.copy(connectTimeoutMs = 300), baseUrl = server.url("/").toString().trimEnd('/'))
        try {
            t.openStream()
            fail("expected exception")
        } catch (e: ElevenLabsNetworkException) {
            assertTrue(e.isNetwork)
        }
    }

    @Test fun connectionRefusedIsNetworkError() = runBlocking {
        val url = server.url("/").toString().trimEnd('/')
        server.close()
        try {
            ElevenLabsTranscriber("k", config, baseUrl = url).openStream()
            fail("expected exception")
        } catch (e: ElevenLabsNetworkException) {
            assertTrue(e.isNetwork)
        }
    }

    @Test fun warningAndUnknownMessagesAreIgnored() = runBlocking {
        scriptWs { ws, m ->
            if (isCommit(m)) {
                ws.send("""{"message_type":"warning","warning":"hmm"}""")
                ws.send("""{"message_type":"brand_new","x":1}""")
                ws.send("""{"message_type":"committed_transcript","text":""}""")
            }
        }
        val session = transcriber().openStream()
        assertEquals("", session.finish())
        session.close()
    }
}
