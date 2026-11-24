package net.backslasher.shamash.transcribe

import android.util.Log
import java.io.File
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import net.backslasher.shamash.config.ShamashConfig
import okhttp3.Call
import okhttp3.Callback
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.asRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener

private const val TAG = "Shamash"

/**
 * ElevenLabs Scribe: realtime WebSocket for live partials, batch REST for the final text.
 * [baseUrl] is overridable for tests; OkHttp upgrades an http(s) URL to a WebSocket just fine.
 * The API key is only ever sent in the `xi-api-key` header and never logged.
 */
class ElevenLabsTranscriber(
    private val apiKey: String,
    private val config: ShamashConfig,
    private val client: OkHttpClient = OkHttpClient(),
    private val baseUrl: String = "https://api.elevenlabs.io",
    private val maxQueuedBytes: Long = 1_000_000L,
    /** Send a mid-stream commit this often so the server's ~36 s auto-commit never decides for us. */
    private val commitEveryMs: Long = 25_000L,
) : Transcriber {

    override suspend fun openStream(): StreamingSession {
        val url = baseUrl.toHttpUrl().newBuilder()
            .addPathSegments("v1/speech-to-text/realtime")
            .addQueryParameter("model_id", config.realtimeModelId)
            .addQueryParameter("language_code", config.languageCode)
            .addQueryParameter("audio_format", "pcm_${config.sampleRate}")
            .addQueryParameter("commit_strategy", "manual")
            .build()
        val request = Request.Builder().url(url).header("xi-api-key", apiKey).build()
        val session = RealtimeSession()
        val t0 = System.nanoTime()
        session.ws = client.newWebSocket(request, session.listener)
        try {
            withTimeout(config.connectTimeoutMs) { session.started.await() }
            Log.d(TAG, "realtime: session ready after ${ms(t0)} ms")
        } catch (e: TimeoutCancellationException) {
            session.close()
            throw ElevenLabsNetworkException("Timed out waiting for session_started (${config.connectTimeoutMs} ms)")
        } catch (e: Throwable) {
            session.close()
            throw e
        }
        return session
    }

    private inner class RealtimeSession : StreamingSession {
        lateinit var ws: WebSocket
        val started = CompletableDeferred<Unit>()

        private val lock = Any()
        private val acc = SegmentAccumulator()
        private var commitsSent = 0
        private var commitsReceived = 0
        private var finishRequested = false
        private var failure: Throwable? = null
        private var closed = false
        private var msSinceCommit = 0L
        private var lastText = ""
        private val done = CompletableDeferred<String>()
        private val t0 = System.nanoTime()

        private val flow = MutableSharedFlow<String>(
            replay = 64, extraBufferCapacity = 64, onBufferOverflow = BufferOverflow.DROP_OLDEST,
        )
        override val partials: Flow<String> get() = flow

        private fun fail(t: Throwable) {
            synchronized(lock) { if (failure == null) failure = t }
            started.completeExceptionally(t)
            done.completeExceptionally(t)
        }

        private fun checkDone() {
            val text = synchronized(lock) {
                if (finishRequested && commitsReceived >= commitsSent) acc.committedText else null
            }
            if (text != null) done.complete(text)
        }

        val listener = object : WebSocketListener() {
            override fun onMessage(webSocket: WebSocket, text: String) {
                val msg = ServerMessage.parse(text)
                Log.d(TAG, "realtime: <- ${msg.type} at ${ms(t0)} ms")
                when (msg) {
                    is ServerMessage.SessionStarted -> started.complete(Unit)
                    is ServerMessage.Partial -> {
                        val full = synchronized(lock) { acc.onPartial(msg.text).also { lastText = it } }
                        flow.tryEmit(full)
                    }
                    is ServerMessage.Committed -> {
                        val full = synchronized(lock) {
                            commitsReceived++
                            acc.onCommitted(msg.text).also { lastText = it }
                        }
                        flow.tryEmit(full)
                        checkDone()
                    }
                    is ServerMessage.Error -> {
                        Log.w(TAG, "realtime: server error ${msg.type}: ${msg.message}")
                        fail(ElevenLabsApiException(msg.type, "${msg.type}: ${msg.message}"))
                        webSocket.cancel()
                    }
                    is ServerMessage.Warning -> Log.w(TAG, "realtime: warning: ${msg.message}")
                    is ServerMessage.Unknown -> Log.d(TAG, "realtime: ignoring message type ${msg.type}")
                }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                val code = response?.code
                Log.w(TAG, "realtime: failure after ${ms(t0)} ms: ${t::class.java.simpleName} http=$code")
                val ex: Throwable = if (code != null && code !in 200..299 && code != 101) {
                    val body = try { response.body.string() } catch (_: Exception) { "" }
                    ElevenLabsApiException("http_$code", "HTTP $code: ${ElevenLabsJson.errorDetail(body)}", code)
                } else {
                    ElevenLabsNetworkException(t.message ?: t::class.java.simpleName, t)
                }
                fail(ex)
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                Log.d(TAG, "realtime: server closing $code $reason")
                // Unexpected close before everything was committed is a failure; a close after finish() is fine.
                fail(ElevenLabsNetworkException("Socket closed by server ($code $reason)"))
                webSocket.close(1000, null)
            }
        }

        override suspend fun sendAudio(pcm: ByteArray) {
            synchronized(lock) { failure }?.let { throw it }
            if (ws.queueSize() > maxQueuedBytes) {
                val ex = ElevenLabsNetworkException("Upload stalled: ${ws.queueSize()} bytes queued")
                fail(ex)
                ws.cancel()
                throw ex
            }
            val commit = synchronized(lock) {
                msSinceCommit += pcm.size * 1000L / (config.sampleRate * 2)
                (msSinceCommit >= commitEveryMs).also {
                    if (it) {
                        msSinceCommit = 0
                        commitsSent++
                    }
                }
            }
            if (commit) Log.d(TAG, "realtime: -> periodic commit at ${ms(t0)} ms")
            send(ElevenLabsJson.audioChunk(pcm, commit, config.sampleRate))
        }

        private fun send(text: String) {
            if (!ws.send(text)) {
                val ex = synchronized(lock) { failure } ?: ElevenLabsNetworkException("Socket is closed")
                throw ex
            }
        }

        override suspend fun finish(): String {
            synchronized(lock) { failure }?.let { throw it }
            synchronized(lock) {
                commitsSent++
                finishRequested = true
            }
            val tCommit = System.nanoTime()
            send(ElevenLabsJson.audioChunk(ByteArray(0), commit = true, sampleRate = config.sampleRate))
            Log.d(TAG, "realtime: -> final commit")
            checkDone()
            val text = try {
                withTimeout(config.finalTimeoutMs) { done.await() }
            } catch (e: TimeoutCancellationException) {
                throw ElevenLabsNetworkException("Timed out waiting for committed_transcript (${config.finalTimeoutMs} ms)")
            }
            Log.d(TAG, "realtime: committed ${ms(tCommit)} ms after final commit")
            close(graceful = true)
            return text
        }

        override fun close() = close(graceful = false)

        private fun close(graceful: Boolean) {
            synchronized(lock) {
                if (closed) return
                closed = true
            }
            // The server never closes after committed_transcript; the client must.
            if (graceful) ws.close(1000, "done") else ws.cancel()
            // Make sure nobody waits forever on a closed session.
            val ex = ElevenLabsNetworkException("Session closed")
            started.completeExceptionally(ex)
            done.completeExceptionally(ex)
        }
    }

    override suspend fun transcribeBatch(wav: File): BatchResult {
        val body = MultipartBody.Builder().setType(MultipartBody.FORM)
            .addFormDataPart("model_id", config.batchModelId)
            .addFormDataPart("language_code", config.languageCode)
            .addFormDataPart("file", wav.name, wav.asRequestBody("audio/wav".toMediaType()))
            .build()
        val request = Request.Builder()
            .url(baseUrl.toHttpUrl().newBuilder().addPathSegments("v1/speech-to-text").build())
            .header("xi-api-key", apiKey)
            .post(body)
            .build()
        val batchClient = client.newBuilder()
            .connectTimeout(10, TimeUnit.SECONDS)
            .readTimeout(60, TimeUnit.SECONDS)
            .writeTimeout(60, TimeUnit.SECONDS)
            .build()
        val t0 = System.nanoTime()
        val (code, text) = batchClient.newCall(request).await()
        Log.d(TAG, "batch: HTTP $code in ${ms(t0)} ms")
        if (code !in 200..299) {
            throw ElevenLabsApiException("http_$code", "HTTP $code: ${ElevenLabsJson.errorDetail(text)}", code)
        }
        val parsed = try {
            (ElevenLabsJson.json.parseToJsonElement(text) as JsonObject)["text"] as JsonPrimitive
        } catch (e: Exception) {
            throw ElevenLabsApiException("bad_response", "Unexpected batch response: ${text.take(200)}", code)
        }
        return BatchResult(parsed.content, text)
    }

    private fun ms(t0: Long) = (System.nanoTime() - t0) / 1_000_000

    /** Executes the call; returns status and body text. IO failures become [ElevenLabsNetworkException]. */
    private suspend fun Call.await(): Pair<Int, String> = suspendCancellableCoroutine { cont ->
        cont.invokeOnCancellation { cancel() }
        enqueue(object : Callback {
            override fun onFailure(call: Call, e: IOException) {
                cont.resumeWithException(ElevenLabsNetworkException(e.message ?: e::class.java.simpleName, e))
            }

            override fun onResponse(call: Call, response: Response) {
                try {
                    response.use { cont.resume(it.code to it.body.string()) }
                } catch (e: IOException) {
                    cont.resumeWithException(ElevenLabsNetworkException(e.message ?: "read failed", e))
                }
            }
        })
    }
}
