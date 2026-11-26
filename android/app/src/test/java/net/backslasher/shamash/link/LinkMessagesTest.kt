package net.backslasher.shamash.link

import org.junit.Assert.assertEquals
import org.junit.Test

class LinkMessagesTest {
    @Test fun transcript() {
        val raw = """{"type":"transcript","text":"שלום"}"""
        assertEquals(LinkMessage.Transcript("שלום", raw), LinkMessages.parse(raw))
    }

    @Test fun emptyTranscript() {
        assertEquals("", (LinkMessages.parse("""{"type":"transcript","text":""}""") as LinkMessage.Transcript).text)
    }

    @Test fun replyAudioDone() {
        assertEquals(LinkMessage.Reply("היי"), LinkMessages.parse("""{"type":"reply","text":"היי"}"""))
        assertEquals(LinkMessage.AudioStart(16000), LinkMessages.parse("""{"type":"audio_start","sampleRate":16000}"""))
        assertEquals(LinkMessage.AudioEnd, LinkMessages.parse("""{"type":"audio_end"}"""))
        assertEquals(LinkMessage.Done, LinkMessages.parse("""{"type":"done"}"""))
    }

    @Test fun error() {
        assertEquals(
            LinkMessage.Error("llm", "boom"),
            LinkMessages.parse("""{"type":"error","stage":"llm","message":"boom"}"""),
        )
    }

    @Test fun unknownAndGarbageAreIgnored() {
        assertEquals(LinkMessage.Unknown, LinkMessages.parse("""{"type":"future_thing"}"""))
        assertEquals(LinkMessage.Unknown, LinkMessages.parse("not json"))
        assertEquals(LinkMessage.Unknown, LinkMessages.parse("""[1,2]"""))
    }

    @Test fun helloJson() {
        val h = LinkMessages.hello("Pixel 7", 16000)
        assertEquals("""{"type":"hello","sampleRate":16000,"device":"Pixel 7","mode":"conversation"}""", h)
    }

    @Test fun errorTypeHasPcPrefix() {
        assertEquals("pc_llm", LinkPcException("llm", "x").errorType)
    }
}
