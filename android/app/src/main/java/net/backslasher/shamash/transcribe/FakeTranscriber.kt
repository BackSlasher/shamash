package net.backslasher.shamash.transcribe

import java.io.File
import java.io.IOException
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow

/** Offline stand-in: emits fake Hebrew partials as audio arrives, a fake final after [finishDelayMs]. */
class FakeTranscriber(
    var failOpen: Boolean = false,
    var failBatch: Boolean = false,
    var openDelayMs: Long = 200,
    var finishDelayMs: Long = 300,
    var batchDelayMs: Long = 600,
    /** Audio chunks (~100 ms each) to wait before each partial is emitted. */
    var partialAtChunks: List<Int> = listOf(5, 12, 20),
    var partialTexts: List<String> = listOf("שלום", "שלום מה", "שלום מה שלומך"),
    var finalText: String = "שלום מה שלומך",
    var batchText: String = "שלום, מה שלומך?",
) : Transcriber {

    override suspend fun openStream(): StreamingSession {
        delay(openDelayMs)
        if (failOpen) throw IOException("fake: cannot open stream")
        return Session()
    }

    override suspend fun transcribeBatch(wav: File): BatchResult {
        delay(batchDelayMs)
        if (failBatch) throw IOException("fake: batch failed")
        return BatchResult(batchText, """{"text":"$batchText","language_code":"heb"}""")
    }

    private inner class Session : StreamingSession {
        private val flow = MutableSharedFlow<String>(replay = 64, extraBufferCapacity = 64)
        private var chunks = 0

        override val partials: Flow<String> get() = flow

        override suspend fun sendAudio(pcm: ByteArray) {
            chunks++
            val idx = partialAtChunks.indexOf(chunks)
            if (idx >= 0 && idx < partialTexts.size) flow.emit(partialTexts[idx])
        }

        override suspend fun finish(): String {
            delay(finishDelayMs)
            return finalText
        }

        override fun close() = Unit
    }
}
