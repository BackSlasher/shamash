package net.backslasher.shamash.core

import java.io.IOException
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.withTimeoutOrNull
import net.backslasher.shamash.audio.AudioCapture
import net.backslasher.shamash.audio.Cues
import net.backslasher.shamash.audio.WavWriter
import net.backslasher.shamash.config.ShamashConfig
import net.backslasher.shamash.config.TriggerMode
import net.backslasher.shamash.link.ReplyProvider
import net.backslasher.shamash.storage.ConfigInfo
import net.backslasher.shamash.storage.Latency
import net.backslasher.shamash.storage.PartialEntry
import net.backslasher.shamash.storage.SessionFiles
import net.backslasher.shamash.storage.SessionRecord
import net.backslasher.shamash.storage.SessionStore
import net.backslasher.shamash.transcribe.StreamingSession
import net.backslasher.shamash.transcribe.Transcriber
import net.backslasher.shamash.transcribe.TranscriberError
import net.backslasher.shamash.tts.FakeSpeaker
import net.backslasher.shamash.tts.Speaker

/**
 * The one state machine. Call methods from a single thread (the UI thread); [scope] should be
 * a main-thread scope. Blocking file work is moved to [ioDispatcher].
 */
class SessionController(
    private val scope: CoroutineScope,
    private val config: ShamashConfig,
    private val capture: AudioCapture,
    private val transcriber: Transcriber,
    private val store: SessionStore,
    private val cues: Cues,
    private val speaker: Speaker = FakeSpeaker(),
    private val clock: Clock = SystemClock,
    private val ioDispatcher: CoroutineDispatcher = Dispatchers.IO,
    /** Link mode: plays the PC's reply after the transcript. Null in standalone mode. */
    private val replyProvider: ReplyProvider? = null,
) {
    private val _model = MutableStateFlow(ScreenModel())
    val model: StateFlow<ScreenModel> = _model.asStateFlow()

    private var lastFinal = ""
    private var sessionJob: Job? = null
    private var stopSignal: CompletableDeferred<Unit>? = null
    private var speakJob: Job? = null
    @Volatile private var replyInFlight = false

    // ---- input ----

    fun onTap() {
        if (config.triggerMode == TriggerMode.HOLD) {
            if (_model.value.state == ShamashState.ERROR) clearError()
            if (_model.value.state == ShamashState.SPEAKING) cancelSpeaking()
            if (_model.value.state == ShamashState.WORKING && replyInFlight) cancelReply()
            return
        }
        when (_model.value.state) {
            ShamashState.IDLE -> startSession()
            ShamashState.LISTENING -> stopSignal?.complete(Unit)
            ShamashState.ERROR -> clearError()
            ShamashState.SPEAKING -> cancelSpeaking()
            ShamashState.WORKING -> if (replyInFlight) cancelReply()
        }
    }

    fun onPressDown() {
        if (config.triggerMode != TriggerMode.HOLD) return
        when (_model.value.state) {
            ShamashState.IDLE -> startSession()
            ShamashState.ERROR -> clearError()
            ShamashState.SPEAKING -> cancelSpeaking()
            else -> Unit
        }
    }

    /** Speak [text] aloud (green). Only from IDLE; a tap while speaking interrupts. Keeps the text on screen. */
    fun speak(text: String) {
        if (_model.value.state != ShamashState.IDLE || text.isBlank()) return
        lastFinal = text
        setModel(ShamashState.SPEAKING, text, isFinal = true)
        speakJob = scope.launch {
            try {
                speaker.speak(text)
                setModel(ShamashState.IDLE, text, isFinal = true)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                fail("Speech failed: ${describe(e)}", classify(e))
            }
        }
    }

    private fun cancelReply() {
        replyInFlight = false
        replyProvider?.cancelReply()
        sessionJob?.cancel()
        setModel(ShamashState.IDLE, lastFinal, lastFinal.isNotEmpty())
    }

    private fun cancelSpeaking() {
        if (replyInFlight) {
            cancelReply()
            return
        }
        speakJob?.cancel()
        speakJob = null
        setModel(ShamashState.IDLE, lastFinal, lastFinal.isNotEmpty())
    }

    fun onPressUp() {
        if (config.triggerMode != TriggerMode.HOLD) return
        stopSignal?.complete(Unit)
    }

    // ---- state helpers ----

    private fun clearError() {
        _model.value = ScreenModel(ShamashState.IDLE, lastFinal, lastFinal.isNotEmpty())
    }

    private fun setModel(state: ShamashState, transcript: String, isFinal: Boolean = false) {
        _model.value = ScreenModel(state, transcript, isFinal)
    }

    private fun fail(message: String, kind: ErrorKind) {
        _model.value = ScreenModel(ShamashState.ERROR, "", false, message, kind)
    }

    private fun startSession() {
        if (sessionJob?.isActive == true) return // double-tap guard
        setModel(ShamashState.WORKING, "") // synchronously, so a second tap is ignored
        val signal = CompletableDeferred<Unit>()
        stopSignal = signal
        sessionJob = scope.launch { runSession(signal) }
    }

    // ---- the session ----

    private suspend fun runSession(signal: CompletableDeferred<Unit>) {
        // Amber (WORKING) while the stream opens; the start cue plays only once capture is running,
        // so everything spoken after the beep is recorded.
        val session: StreamingSession = try {
            withTimeout(config.connectTimeoutMs) { transcriber.openStream() }
        } catch (e: TimeoutCancellationException) {
            fail("Timed out opening the transcription stream (${config.connectTimeoutMs} ms)", ErrorKind.NO_NETWORK)
            return
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            fail("Cannot open transcription stream: ${describe(e)}", classify(e))
            return
        }

        val partials = mutableListOf<PartialEntry>()
        val captureError = AtomicReference<Throwable?>(null)
        val streamError = AtomicReference<Throwable?>(null)
        var files: SessionFiles? = null
        var wav: WavWriter? = null
        var partialJob: Job? = null
        var captureJob: Job? = null
        var listeningStartedAt = clock.elapsedMs()
        var listeningWallMs = clock.wallMs()
        var stopAt: Long? = null
        var firstPartialMs: Long? = null
        var stopToFinalMs: Long? = null
        var realtimeFinal: String? = null
        var batchText: String? = null
        var batchRaw: String? = null
        var error: String? = null
        var errorKind = ErrorKind.GENERIC
        var errorType: String? = null
        var cancelled = false

        try {
            listeningWallMs = clock.wallMs()
            val f = withContext(ioDispatcher) { store.create(listeningWallMs) }
            files = f
            val w = withContext(ioDispatcher) { WavWriter(f.wav, config.sampleRate) }
            wav = w

            listeningStartedAt = clock.elapsedMs()

            partialJob = scope.launch(start = CoroutineStart.UNDISPATCHED) {
                session.partials.collect { text ->
                    val t = clock.elapsedMs() - listeningStartedAt
                    partials += PartialEntry(t, text)
                    if (firstPartialMs == null) firstPartialMs = t
                    _model.update {
                        if (it.state == ShamashState.LISTENING || it.state == ShamashState.WORKING) {
                            it.copy(transcript = text, isFinal = false)
                        } else {
                            it
                        }
                    }
                }
            }

            // One collector fans each chunk out to the WAV writer and the streaming session.
            // The first SKIP_STREAM_MS of PCM stay in the WAV but are not streamed: the start cue leaks in there.
            var skipBytes = config.sampleRate * 2 * SKIP_STREAM_MS / 1000
            captureJob = scope.launch(ioDispatcher) {
                try {
                    capture.start().collect { chunk ->
                        w.write(chunk)
                        var out = chunk
                        if (skipBytes > 0) {
                            if (chunk.size <= skipBytes) {
                                skipBytes -= chunk.size
                                out = ByteArray(0)
                            } else {
                                out = chunk.copyOfRange(skipBytes, chunk.size)
                                skipBytes = 0
                            }
                        }
                        if (out.isNotEmpty() && streamError.get() == null) {
                            try {
                                session.sendAudio(out)
                            } catch (e: CancellationException) {
                                throw e
                            } catch (e: Exception) {
                                streamError.set(e)
                                signal.complete(Unit)
                            }
                        }
                    }
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    captureError.set(e)
                    signal.complete(Unit)
                }
            }

            // Capture first, then the cue, then the red screen.
            cues.start()
            setModel(ShamashState.LISTENING, "")

            withTimeoutOrNull(config.maxListeningMs) { signal.await() } // null means max duration reached: auto-stop

            stopAt = clock.elapsedMs()
            capture.stop()
            cues.stop()
            _model.update { it.copy(state = ShamashState.WORKING) }
            if (withTimeoutOrNull(2_000) { captureJob.join() } == null) captureJob.cancel()
            withContext(ioDispatcher) { w.close() }

            val failure = captureError.get()?.let { Triple("Microphone capture failed: ${describe(it)}", ErrorKind.MICROPHONE, "microphone") }
                ?: streamError.get()?.let { Triple("Streaming failed: ${describe(it)}", classify(it), errorTypeOf(it)) }
            if (failure != null) {
                error = failure.first
                errorKind = failure.second
                errorType = failure.third
            } else {
                try {
                    realtimeFinal = withTimeout(config.realtimeFinishTimeoutMs) { session.finish() }
                    if (stopAt - listeningStartedAt < config.minBatchAudioMs) {
                        // A double tap: nothing to transcribe, and Scribe rejects near-empty files.
                        batchText = ""
                    } else {
                        val result = withTimeout(config.batchTimeoutMs) { transcriber.transcribeBatch(f.wav) }
                        batchText = result.text
                        batchRaw = result.raw
                    }
                    stopToFinalMs = clock.elapsedMs() - stopAt
                } catch (e: TimeoutCancellationException) {
                    error = "Timed out waiting for the final transcript (${e.message})"
                    errorKind = ErrorKind.NO_NETWORK
                    errorType = "timeout"
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    error = "Transcription failed: ${describe(e)}"
                    errorKind = classify(e)
                    errorType = errorTypeOf(e)
                }
            }
        } catch (e: CancellationException) {
            cancelled = true
            error = "Cancelled"
            throw e
        } catch (e: Exception) {
            error = "Unexpected error: ${describe(e)}"
            errorType = errorTypeOf(e)
        } finally {
            withContext(NonCancellable) {
                partialJob?.cancel()
                captureJob?.cancel()
                try {
                    session.close()
                } catch (_: Exception) {
                }
                withContext(ioDispatcher) {
                    try {
                        wav?.close()
                    } catch (_: Exception) {
                    }
                    val f = files
                    if (f != null) {
                        val end = stopAt ?: clock.elapsedMs()
                        val record = SessionRecord(
                            timestamp = SessionRecord.isoTimestamp(listeningWallMs),
                            durationMs = end - listeningStartedAt,
                            partials = partials.toList(),
                            realtimeFinal = realtimeFinal,
                            batchText = batchText,
                            batchRaw = batchRaw,
                            latency = Latency(firstPartialMs, stopToFinalMs),
                            appVersion = config.appVersion,
                            config = ConfigInfo(
                                config.languageCode, config.realtimeModelId, config.batchModelId, config.sampleRate,
                            ),
                            error = error,
                            errorType = errorType,
                        )
                        try {
                            store.save(f, record)
                        } catch (e: Exception) {
                            if (error == null) error = "Could not save session record: ${describe(e)}"
                        }
                    }
                }
            }
        }

        if (cancelled) return
        val text = batchText
        if (error == null && text != null) {
            lastFinal = text
            val rp = replyProvider
            when {
                rp == null -> setModel(ShamashState.IDLE, text, isFinal = true)
                text.isBlank() -> drainEmptyTurn(rp)
                else -> runReply(rp, text)
            }
        } else {
            fail(error ?: "Unknown error", errorKind)
        }
    }

    /** Link mode, nothing heard: the PC sends `done` right away; consume it so the socket closes. */
    private suspend fun drainEmptyTurn(rp: ReplyProvider) {
        try {
            withTimeoutOrNull(5_000) { rp.awaitReply({}, {}) } ?: rp.cancelReply()
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
        }
        setModel(ShamashState.IDLE, "", isFinal = false)
    }

    /** Link mode: transcript shown, wait for the PC's reply text, then its audio. */
    private suspend fun runReply(rp: ReplyProvider, transcript: String) {
        replyInFlight = true
        setModel(ShamashState.WORKING, transcript, isFinal = true)
        try {
            withTimeout(config.linkReplyTimeoutMs) {
                rp.awaitReply(
                    onReplyText = { reply ->
                        if (replyInFlight) {
                            lastFinal = reply
                            _model.update { it.copy(transcript = reply, isFinal = true) }
                        }
                    },
                    onSpeakingStarted = {
                        if (replyInFlight) _model.update { it.copy(state = ShamashState.SPEAKING) }
                    },
                )
            }
            replyInFlight = false
            setModel(ShamashState.IDLE, lastFinal, isFinal = lastFinal.isNotEmpty())
        } catch (e: TimeoutCancellationException) {
            replyInFlight = false
            rp.cancelReply()
            fail("Timed out waiting for the reply (${config.linkReplyTimeoutMs} ms)", ErrorKind.NO_NETWORK)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            replyInFlight = false
            rp.cancelReply()
            fail("Reply failed: ${describe(e)}", classify(e))
        }
    }

    private companion object {
        const val SKIP_STREAM_MS = 200
    }

    private fun describe(e: Throwable): String = e.message?.takeIf { it.isNotBlank() } ?: e::class.java.simpleName

    /** Explicit mapping: providers flag connectivity errors via [TranscriberError]; bare IOExceptions count as network. */
    private fun classify(e: Throwable): ErrorKind = when {
        e is TranscriberError -> if (e.isNetwork) ErrorKind.NO_NETWORK else ErrorKind.GENERIC
        e is IOException -> ErrorKind.NO_NETWORK
        else -> ErrorKind.GENERIC
    }

    private fun errorTypeOf(e: Throwable): String = (e as? TranscriberError)?.errorType ?: e::class.java.simpleName
}
