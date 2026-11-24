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
import kotlinx.coroutines.delay
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
import net.backslasher.shamash.storage.ConfigInfo
import net.backslasher.shamash.storage.Latency
import net.backslasher.shamash.storage.PartialEntry
import net.backslasher.shamash.storage.SessionFiles
import net.backslasher.shamash.storage.SessionRecord
import net.backslasher.shamash.storage.SessionStore
import net.backslasher.shamash.transcribe.StreamingSession
import net.backslasher.shamash.transcribe.Transcriber

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
    private val clock: Clock = SystemClock,
    private val ioDispatcher: CoroutineDispatcher = Dispatchers.IO,
    /** Minimum time between the start cue and opening the microphone, so the tone is not recorded. */
    private val cueSettleMs: Long = 250L,
) {
    private val _model = MutableStateFlow(ScreenModel())
    val model: StateFlow<ScreenModel> = _model.asStateFlow()

    private var lastFinal = ""
    private var sessionJob: Job? = null
    private var stopSignal: CompletableDeferred<Unit>? = null

    // ---- input ----

    fun onTap() {
        if (config.triggerMode == TriggerMode.HOLD) {
            if (_model.value.state == ShamashState.ERROR) clearError()
            return
        }
        when (_model.value.state) {
            ShamashState.IDLE -> startSession()
            ShamashState.LISTENING -> stopSignal?.complete(Unit)
            ShamashState.ERROR -> clearError()
            ShamashState.WORKING, ShamashState.SPEAKING -> Unit // reserved for interruption later
        }
    }

    fun onPressDown() {
        if (config.triggerMode != TriggerMode.HOLD) return
        when (_model.value.state) {
            ShamashState.IDLE -> startSession()
            ShamashState.ERROR -> clearError()
            else -> Unit
        }
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
        val tapAt = clock.elapsedMs()
        cues.start()

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
        var cancelled = false

        try {
            val wait = cueSettleMs - (clock.elapsedMs() - tapAt)
            if (wait > 0) delay(wait)

            listeningWallMs = clock.wallMs()
            val f = withContext(ioDispatcher) { store.create(listeningWallMs) }
            files = f
            val w = withContext(ioDispatcher) { WavWriter(f.wav, config.sampleRate) }
            wav = w

            listeningStartedAt = clock.elapsedMs()
            setModel(ShamashState.LISTENING, "")

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
            captureJob = scope.launch(ioDispatcher) {
                try {
                    capture.start().collect { chunk ->
                        w.write(chunk)
                        if (streamError.get() == null) {
                            try {
                                session.sendAudio(chunk)
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

            withTimeoutOrNull(config.maxListeningMs) { signal.await() } // null means max duration reached: auto-stop

            stopAt = clock.elapsedMs()
            capture.stop()
            cues.stop()
            _model.update { it.copy(state = ShamashState.WORKING) }
            if (withTimeoutOrNull(2_000) { captureJob.join() } == null) captureJob.cancel()
            withContext(ioDispatcher) { w.close() }

            val failure = captureError.get()?.let { "Microphone capture failed: ${describe(it)}" to ErrorKind.MICROPHONE }
                ?: streamError.get()?.let { "Streaming failed: ${describe(it)}" to classify(it) }
            if (failure != null) {
                error = failure.first
                errorKind = failure.second
            } else {
                try {
                    withTimeout(config.finalTimeoutMs) {
                        realtimeFinal = session.finish()
                        val result = transcriber.transcribeBatch(f.wav)
                        batchText = result.text
                        batchRaw = result.raw
                    }
                    stopToFinalMs = clock.elapsedMs() - stopAt
                } catch (e: TimeoutCancellationException) {
                    error = "Timed out waiting for the final transcript (${config.finalTimeoutMs} ms)"
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    error = "Transcription failed: ${describe(e)}"
                    errorKind = classify(e)
                }
            }
        } catch (e: CancellationException) {
            cancelled = true
            error = "Cancelled"
            throw e
        } catch (e: Exception) {
            error = "Unexpected error: ${describe(e)}"
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
            setModel(ShamashState.IDLE, text, isFinal = true)
        } else {
            fail(error ?: "Unknown error", errorKind)
        }
    }

    private fun describe(e: Throwable): String = e.message?.takeIf { it.isNotBlank() } ?: e::class.java.simpleName

    private fun classify(e: Throwable): ErrorKind = if (e is IOException) ErrorKind.NO_NETWORK else ErrorKind.GENERIC
}
