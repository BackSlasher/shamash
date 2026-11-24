package net.backslasher.shamash.transcribe

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ElevenLabsMessagesTest {
    @Test
    fun parsesSessionStartedIgnoringUnknownKeys() {
        val m = ServerMessage.parse("""{"message_type":"session_started","session_id":"abc","config":{"language_code":"he","x":1}}""")
        assertEquals(ServerMessage.SessionStarted("abc"), m)
    }

    @Test
    fun parsesPartialAndCommitted() {
        assertEquals(
            ServerMessage.Partial("שלום"),
            ServerMessage.parse("""{"message_type":"partial_transcript","text":"שלום","turn_probability":0.0}"""),
        )
        assertEquals(
            ServerMessage.Committed("שלום עולם"),
            ServerMessage.parse("""{"message_type":"committed_transcript","text":"שלום עולם"}"""),
        )
        assertEquals(ServerMessage.Committed(""), ServerMessage.parse("""{"message_type":"committed_transcript","text":""}"""))
    }

    @Test
    fun parsesEveryErrorType() {
        for (t in ElevenLabsJson.ERROR_TYPES) {
            val m = ServerMessage.parse("""{"message_type":"$t","error":"bad"}""")
            assertEquals(ServerMessage.Error(t, "bad"), m)
        }
    }

    @Test
    fun parsesWarningAndUnknown() {
        assertEquals(ServerMessage.Warning("careful"), ServerMessage.parse("""{"message_type":"warning","warning":"careful"}"""))
        assertEquals(ServerMessage.Unknown("something_new"), ServerMessage.parse("""{"message_type":"something_new"}"""))
        assertEquals(ServerMessage.Unknown("unparseable"), ServerMessage.parse("not json"))
        assertEquals(ServerMessage.Unknown("missing_type"), ServerMessage.parse("{}"))
    }

    @Test
    fun audioChunkJson() {
        val j = ElevenLabsJson.audioChunk(byteArrayOf(1, 2, 3), commit = false, sampleRate = 16000)
        assertTrue(j.contains(""""audio_base_64":"AQID""""))
        assertTrue(j.contains(""""commit":false"""))
        assertTrue(j.contains(""""sample_rate":16000"""))
        assertTrue(ElevenLabsJson.audioChunk(ByteArray(0), true, 16000).contains(""""audio_base_64":"""""))
    }

    @Test
    fun errorDetailHandlesStringObjectAndGarbage() {
        assertEquals("nope", ElevenLabsJson.errorDetail("""{"detail":"nope"}"""))
        assertTrue(ElevenLabsJson.errorDetail("""{"detail":{"status":"invalid_api_key"}}""").contains("invalid_api_key"))
        assertEquals("<html>", ElevenLabsJson.errorDetail("<html>"))
    }

    @Test
    fun partialsReplaceNeverAppend() {
        val a = SegmentAccumulator()
        assertEquals("שלום", a.onPartial("שלום"))
        assertEquals("שלום עולם", a.onPartial("שלום עולם"))
        assertEquals("שלום עולם.", a.onCommitted("שלום עולם."))
    }

    @Test
    fun segmentsAreConcatenatedWithSpace() {
        val a = SegmentAccumulator()
        a.onPartial("one")
        assertEquals("one two", a.onCommitted("one two"))
        assertEquals("one two three", a.onPartial("three"))
        assertEquals("one two three four", a.onCommitted("three four"))
        assertEquals("one two three four", a.committedText)
    }

    @Test
    fun emptyCommitKeepsText() {
        val a = SegmentAccumulator()
        assertEquals("", a.onCommitted(""))
        assertEquals("x", a.onCommitted("x"))
        assertEquals("x", a.onCommitted(""))
    }
}
