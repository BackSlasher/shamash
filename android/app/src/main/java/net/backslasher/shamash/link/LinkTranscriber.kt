package net.backslasher.shamash.link

import android.os.Build
import android.util.Log
import java.io.File
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.withContext
import net.backslasher.shamash.config.ShamashConfig
import net.backslasher.shamash.core.BatteryReading
import net.backslasher.shamash.pclink.PcLinkFrame
import net.backslasher.shamash.transcribe.BatchResult
import net.backslasher.shamash.transcribe.StreamingSession
import net.backslasher.shamash.transcribe.Transcriber
import net.backslasher.shamash.tts.AudioTrackSink
import net.backslasher.shamash.tts.PcmSink
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import okio.ByteString.Companion.toByteString

private const val TAG = "Shamash"

interface ReplyProvider {
    /** Waits for the assistant reply and plays its audio. Calls [onReplyText] when the text arrives and [onSpeakingStarted] at the first audio frame; returns when playback is done. Throws on error. */
    suspend fun awaitReply(onReplyText: (String) -> Unit, onSpeakingStarted: () -> Unit)

    /** Tap while thinking/speaking: send cancel, stop audio, close. */
    fun cancelReply()
}

/**
 * Link mode: the microphone goes to the PC over one WebSocket per turn; the same socket carries the
 * transcript, the reply text and the reply audio back. See docs/link-protocol.md.
 */
class LinkTranscriber(
    private val url: String,
    private val client: OkHttpClient,
    private val config: ShamashConfig,
    private val sinkFactory: (sampleRate: Int) -> PcmSink = { AudioTrackSink(it) },
    private val device: String = Build.MODEL ?: "unknown",
    private val ioDispatcher: CoroutineDispatcher = Dispatchers.IO,
    /** Latest battery reading for the hello message; null when unknown. */
    private val battery: () -> BatteryReading? = { null },
) : Transcriber, ReplyProvider {

    private sealed interface Event {
        data class Text(val msg: LinkMessage) : Event
        class Binary(val bytes: ByteArray) : Event
        data class Closed(val reason: String) : Event
        data class Failed(val cause: Throwable) : Event
    }

    /** Everything belonging to one turn's socket. */
    private class Turn(val ws: WebSocket, val events: Channel<Event>) {
        @Volatile var terminal: Event? = null
        @Volatile var replyPending = false
        @Volatile var sink: PcmSink? = null
        @Volatile var closed = false

        suspend fun next(): Event {
            terminal?.let { return it }
            val e = events.receive()
            if (e is Event.Closed || e is Event.Failed) terminal = e
            return e
        }

        fun closeSocket() {
            if (closed) return
            closed = true
            try {
                ws.close(1000, null)
            } catch (_: Exception) {
            }
        }
    }

    @Volatile private var turn: Turn? = null

    private val _heard = MutableStateFlow("")
    private val _replyText = MutableStateFlow("")

    /** The PC's transcript of the current turn, set the moment it arrives; empty until then and reset at each turn start. */
    val heard: StateFlow<String> = _heard.asStateFlow()

    /** The PC's reply text of the current turn, set the moment it arrives. */
    val replyText: StateFlow<String> = _replyText.asStateFlow()

    override suspend fun openStream(): StreamingSession {
        _heard.value = ""
        _replyText.value = ""
        val events = Channel<Event>(Channel.UNLIMITED)
        val opened = CompletableDeferred<Unit>()
        val listener = object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                opened.complete(Unit)
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                events.trySend(Event.Text(LinkMessages.parse(text)))
            }

            override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                events.trySend(Event.Binary(bytes.toByteArray()))
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                events.trySend(Event.Closed("closed by PC ($code $reason)"))
                webSocket.close(1000, null)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                events.trySend(Event.Closed("closed ($code $reason)"))
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                val ex = LinkNetworkException(t.message ?: t::class.java.simpleName, t)
                opened.completeExceptionally(ex)
                events.trySend(Event.Failed(ex))
            }
        }
        val ws = try {
            client.newWebSocket(Request.Builder().url(url).build(), listener)
        } catch (e: IllegalArgumentException) {
            throw LinkNetworkException("Bad link URL: ${e.message}", e)
        }
        val t = Turn(ws, events)
        try {
            opened.await()
        } catch (e: Throwable) {
            t.closeSocket()
            throw e
        }
        if (!ws.send(LinkMessages.hello(device, config.sampleRate, battery()))) {
            t.closeSocket()
            throw LinkNetworkException("Could not send hello")
        }
        Log.i(TAG, "link: connected")
        turn = t
        return LinkSession(t)
    }

    private inner class LinkSession(private val t: Turn) : StreamingSession {
        private val seq = AtomicLong()

        override val partials: Flow<String> = emptyFlow()

        override suspend fun sendAudio(pcm: ByteArray) {
            val frame = PcLinkFrame.encode(seq.getAndIncrement(), System.currentTimeMillis(), pcm)
            if (!t.ws.send(frame.toByteString())) throw LinkNetworkException("Link send queue rejected audio frame")
        }

        override suspend fun finish(): String {
            t.replyPending = true // the same socket now carries the transcript and the reply
            if (!t.ws.send(LinkMessages.END)) {
                t.replyPending = false
                throw LinkNetworkException("Could not send end")
            }
            return ""
        }

        /** Closes the socket only if the turn is not waiting for a reply (failure before `end`). */
        override fun close() {
            if (!t.replyPending) t.closeSocket()
        }
    }

    /** Waits for the PC's `transcript`; [wav] is ignored, the audio already went over the socket. */
    override suspend fun transcribeBatch(wav: File): BatchResult {
        val t = turn ?: throw LinkNetworkException("Link not open")
        var ok = false
        try {
            while (true) {
                when (val e = t.next()) {
                    is Event.Text -> when (val m = e.msg) {
                        is LinkMessage.Transcript -> {
                            ok = true
                            _heard.value = m.text
                            return BatchResult(m.text, m.raw)
                        }
                        is LinkMessage.Error -> throw LinkPcException(m.stage, m.message)
                        else -> Unit
                    }
                    is Event.Closed -> throw LinkNetworkException("Link ${e.reason} before the transcript")
                    is Event.Failed -> throw e.cause
                    is Event.Binary -> Unit
                }
            }
        } finally {
            if (!ok) {
                t.replyPending = false
                t.closeSocket()
            }
        }
    }

    override suspend fun awaitReply(onReplyText: (String) -> Unit, onSpeakingStarted: () -> Unit) {
        val t = turn ?: throw LinkNetworkException("Link not open")
        try {
            withContext(ioDispatcher) {
                var started = false
                while (true) {
                    when (val e = t.next()) {
                        is Event.Text -> when (val m = e.msg) {
                            is LinkMessage.Reply -> {
                                _replyText.value = m.text
                                onReplyText(m.text)
                            }
                            is LinkMessage.AudioStart -> t.sink = sinkFactory(m.sampleRate)
                            is LinkMessage.AudioEnd -> {
                                t.sink?.finish()
                                t.sink?.release()
                                t.sink = null
                            }
                            is LinkMessage.Done -> return@withContext
                            is LinkMessage.Error -> throw LinkPcException(m.stage, m.message)
                            else -> Unit // a late transcript or an unknown type
                        }
                        is Event.Binary -> {
                            val sink = t.sink
                            if (sink != null) {
                                if (!started) {
                                    started = true
                                    onSpeakingStarted()
                                }
                                sink.write(e.bytes, 0, e.bytes.size)
                            }
                        }
                        is Event.Closed -> throw LinkNetworkException("Link ${e.reason} before done")
                        is Event.Failed -> throw e.cause
                    }
                }
            }
        } finally {
            t.sink?.let {
                try {
                    it.release()
                } catch (_: Exception) {
                }
            }
            t.sink = null
            t.replyPending = false
            t.closeSocket()
        }
    }

    override fun cancelReply() {
        val t = turn ?: return
        if (t.closed) return
        try {
            t.ws.send(LinkMessages.CANCEL)
        } catch (_: Exception) {
        }
        t.sink?.stopNow()
        t.replyPending = false
        t.closeSocket()
    }
}
