package net.backslasher.shamash.storage

import java.time.Instant
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

@Serializable
data class PartialEntry(val tMs: Long, val text: String)

@Serializable
data class Latency(val firstPartialMs: Long? = null, val stopToFinalMs: Long? = null)

@Serializable
data class ConfigInfo(
    val languageCode: String,
    val realtimeModelId: String,
    val batchModelId: String,
    val sampleRate: Int,
)

@Serializable
data class SessionRecord(
    /** ISO-8601 with offset. */
    val timestamp: String,
    val durationMs: Long,
    val partials: List<PartialEntry>,
    val realtimeFinal: String?,
    val batchText: String?,
    /** Raw provider JSON of the batch response, kept for later replay/analysis. */
    val batchRaw: String? = null,
    val latency: Latency,
    val appVersion: String,
    val config: ConfigInfo,
    val error: String?,
    /** Machine-readable error type (e.g. `auth_error`, `http_422`, `network`, `timeout`); null when no error. */
    val errorType: String? = null,
) {
    fun toJson(): String = CODEC.encodeToString(serializer(), this)

    companion object {
        val CODEC = Json {
            prettyPrint = true
            encodeDefaults = true
            explicitNulls = true
        }

        fun fromJson(text: String): SessionRecord = CODEC.decodeFromString(serializer(), text)

        fun isoTimestamp(wallMs: Long, zone: ZoneId = ZoneId.systemDefault()): String =
            DateTimeFormatter.ISO_OFFSET_DATE_TIME.format(OffsetDateTime.ofInstant(Instant.ofEpochMilli(wallMs), zone))
    }
}
