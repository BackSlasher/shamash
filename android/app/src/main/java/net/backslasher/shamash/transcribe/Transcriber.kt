package net.backslasher.shamash.transcribe

import java.io.File
import kotlinx.coroutines.flow.Flow

interface Transcriber {
    /** Opens a live session. Throws on failure (no network, auth, ...). */
    suspend fun openStream(): StreamingSession

    /** Transcribes a complete WAV file. Throws on failure. */
    suspend fun transcribeBatch(wav: File): BatchResult
}

interface StreamingSession {
    /** Live partial text; each value is the latest full text so far. Must replay so late collectors lose nothing. */
    val partials: Flow<String>

    suspend fun sendAudio(pcm: ByteArray)

    /** Sends the commit and returns the final realtime text. */
    suspend fun finish(): String

    fun close()
}

/** [raw] is the provider's raw JSON response. */
data class BatchResult(val text: String, val raw: String)

/**
 * Implemented by exceptions a [Transcriber] throws, so the controller can classify them explicitly
 * instead of guessing from the exception class.
 */
interface TranscriberError {
    /** Short machine-readable type, stored in the session JSON (e.g. `auth_error`, `http_422`, `network`). */
    val errorType: String

    /** True when the cause is connectivity (no network, DNS, TLS, timeout, stalled socket): shown as NO_NETWORK. */
    val isNetwork: Boolean
}
