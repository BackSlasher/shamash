package net.backslasher.shamash.link

import java.io.IOException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import net.backslasher.shamash.core.BatteryReading
import net.backslasher.shamash.transcribe.TranscriberError

/** PC -> phone text messages of the conversation protocol (docs/link-protocol.md). */
sealed interface LinkMessage {
    data class Transcript(val text: String, val raw: String) : LinkMessage
    data class Reply(val text: String) : LinkMessage
    data class AudioStart(val sampleRate: Int) : LinkMessage
    data object AudioEnd : LinkMessage
    data object Done : LinkMessage
    data class Error(val stage: String, val message: String) : LinkMessage
    /** Unknown type or unparseable text: ignored by the phone. */
    data object Unknown : LinkMessage
}

object LinkMessages {
    fun parse(text: String): LinkMessage = try {
        val o = Json.parseToJsonElement(text).jsonObject
        fun str(key: String) = o[key]?.jsonPrimitive?.content
        when (str("type")) {
            "transcript" -> LinkMessage.Transcript(str("text").orEmpty(), text)
            "reply" -> LinkMessage.Reply(str("text").orEmpty())
            "audio_start" -> LinkMessage.AudioStart(o["sampleRate"]?.jsonPrimitive?.int ?: 16_000)
            "audio_end" -> LinkMessage.AudioEnd
            "done" -> LinkMessage.Done
            "error" -> LinkMessage.Error(str("stage") ?: "internal", str("message").orEmpty())
            else -> LinkMessage.Unknown
        }
    } catch (_: Exception) {
        LinkMessage.Unknown
    }

    fun hello(device: String, sampleRate: Int, battery: BatteryReading? = null): String = buildJsonObject {
        put("type", "hello")
        put("sampleRate", sampleRate)
        put("device", device)
        put("mode", "conversation")
        if (battery != null) {
            put("battery", buildJsonObject {
                put("level", battery.level)
                put("charging", battery.charging)
            })
        }
    }.toString()

    const val END = """{"type":"end"}"""
    const val CANCEL = """{"type":"cancel"}"""
}

/** Connectivity failure on the link (connect refused, socket dropped). Shown as NO_NETWORK. */
class LinkNetworkException(message: String, cause: Throwable? = null) :
    IOException(message, cause), TranscriberError {
    override val errorType: String get() = "network"
    override val isNetwork: Boolean get() = true
}

/** The PC reported an error; [errorType] is `pc_` plus the protocol stage (stt, llm, tts, internal). */
class LinkPcException(stage: String, message: String) : Exception(message.ifBlank { "PC error in $stage" }), TranscriberError {
    override val errorType: String = "pc_$stage"
    override val isNetwork: Boolean get() = false
}
