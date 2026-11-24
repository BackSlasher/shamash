package net.backslasher.shamash.storage

import java.time.ZoneOffset
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SessionRecordTest {
    private val record = SessionRecord(
        timestamp = SessionRecord.isoTimestamp(1_700_000_000_000L, ZoneOffset.ofHours(2)),
        durationMs = 4200,
        partials = listOf(PartialEntry(500, "שלום"), PartialEntry(1200, "שלום עולם")),
        realtimeFinal = "שלום עולם",
        batchText = "שלום, עולם.",
        batchRaw = """{"text":"x"}""",
        latency = Latency(firstPartialMs = 500, stopToFinalMs = 900),
        appVersion = "0.1.0",
        config = ConfigInfo("heb", "scribe_v3_live", "scribe_v3", 16_000),
        error = null,
    )

    @Test
    fun roundTrip() {
        assertEquals(record, SessionRecord.fromJson(record.toJson()))
    }

    @Test
    fun roundTripWithErrorAndNulls() {
        val r = record.copy(batchText = null, realtimeFinal = null, latency = Latency(), error = "boom")
        val back = SessionRecord.fromJson(r.toJson())
        assertEquals(r, back)
        assertEquals("boom", back.error)
        assertNull(back.latency.firstPartialMs)
    }

    @Test
    fun jsonIsPrettyWithExpectedKeysAndOffsetTimestamp() {
        val json = record.toJson()
        assertTrue(json.contains("\n"))
        for (k in listOf("timestamp", "durationMs", "partials", "tMs", "realtimeFinal", "batchText", "latency",
            "firstPartialMs", "stopToFinalMs", "appVersion", "config", "error")) {
            assertTrue("missing $k", json.contains("\"$k\""))
        }
        assertEquals("2023-11-15T00:13:20+02:00", record.timestamp)
    }
}
