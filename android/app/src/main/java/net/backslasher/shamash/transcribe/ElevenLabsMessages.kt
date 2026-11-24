package net.backslasher.shamash.transcribe

import java.io.IOException
import java.util.Base64
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/** Connectivity-class failure (no network, DNS, TLS, timeout, stalled socket). An [IOException]. */
class ElevenLabsNetworkException(message: String, cause: Throwable? = null) :
    IOException(message, cause), TranscriberError {
    override val errorType: String get() = "network"
    override val isNetwork: Boolean get() = true
}

/** The server answered with an error: HTTP 4xx/5xx (batch, or the WebSocket upgrade) or an error message. */
class ElevenLabsApiException(
    override val errorType: String,
    message: String,
    val httpStatus: Int? = null,
) : Exception(message), TranscriberError {
    override val isNetwork: Boolean get() = false
}

object ElevenLabsJson {
    val json = Json { ignoreUnknownKeys = true }

    /** Error message types the realtime API can send (README, Verified facts). */
    val ERROR_TYPES = setOf(
        "error", "auth_error", "quota_exceeded", "commit_throttled", "unaccepted_terms", "rate_limited",
        "queue_overflow", "resource_exhausted", "session_time_limit_exceeded", "input_error",
        "invalid_request", "chunk_size_exceeded", "insufficient_audio_activity", "transcriber_error",
    )

    fun audioChunk(pcm: ByteArray, commit: Boolean, sampleRate: Int): String = buildJsonObject {
        put("message_type", "input_audio_chunk")
        put("audio_base_64", if (pcm.isEmpty()) "" else Base64.getEncoder().encodeToString(pcm))
        put("commit", commit)
        put("sample_rate", sampleRate)
    }.toString()

    /** Extracts a readable text from the `detail` field of an HTTP error body (string, object or list). */
    fun errorDetail(body: String): String {
        val detail: JsonElement? = try {
            (json.parseToJsonElement(body) as? kotlinx.serialization.json.JsonObject)?.get("detail")
        } catch (_: Exception) {
            null
        }
        return when (detail) {
            null -> body.take(500)
            is JsonPrimitive -> detail.content
            else -> detail.toString()
        }
    }
}

@Serializable
private data class RawServerMessage(
    @SerialName("message_type") val messageType: String? = null,
    val text: String? = null,
    val error: String? = null,
    val warning: String? = null,
    @SerialName("session_id") val sessionId: String? = null,
)

/** Parsed realtime server message. */
sealed interface ServerMessage {
    val type: String

    data class SessionStarted(val sessionId: String?) : ServerMessage {
        override val type get() = "session_started"
    }

    data class Partial(val text: String) : ServerMessage {
        override val type get() = "partial_transcript"
    }

    data class Committed(val text: String) : ServerMessage {
        override val type get() = "committed_transcript"
    }

    data class Error(override val type: String, val message: String) : ServerMessage
    data class Warning(val message: String) : ServerMessage {
        override val type get() = "warning"
    }

    data class Unknown(override val type: String) : ServerMessage

    companion object {
        fun parse(text: String): ServerMessage {
            val raw = try {
                ElevenLabsJson.json.decodeFromString(RawServerMessage.serializer(), text)
            } catch (e: Exception) {
                return Unknown("unparseable")
            }
            val type = raw.messageType ?: return Unknown("missing_type")
            return when {
                type == "session_started" -> SessionStarted(raw.sessionId)
                type == "partial_transcript" -> Partial(raw.text.orEmpty())
                type == "committed_transcript" -> Committed(raw.text.orEmpty())
                type == "warning" -> Warning(raw.warning ?: raw.error ?: "")
                type in ElevenLabsJson.ERROR_TYPES -> Error(type, raw.error ?: raw.warning ?: type)
                else -> Unknown(type)
            }
        }
    }
}

/**
 * Cumulative realtime text. Each partial is the full text of the current segment (replace, never
 * append); committed segments are concatenated with a space in front of the running partial.
 * Not thread-safe; callers synchronise.
 */
class SegmentAccumulator {
    private val committed = StringBuilder()
    private var partial = ""

    /** Text of everything committed so far. */
    val committedText: String get() = committed.toString()

    /** Committed segments plus the current partial. */
    fun onPartial(text: String): String {
        partial = text
        return join(committed.toString(), text)
    }

    /** Returns the running text including this committed segment. */
    fun onCommitted(text: String): String {
        val joined = join(committed.toString(), text.trim())
        committed.clear()
        committed.append(joined)
        partial = ""
        return joined
    }

    private fun join(a: String, b: String): String = when {
        a.isEmpty() -> b
        b.isEmpty() -> a
        else -> "$a $b"
    }
}
