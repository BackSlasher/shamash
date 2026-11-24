package net.backslasher.shamash.audio

import java.io.Closeable
import java.io.File
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Streams 16-bit PCM into a canonical 44-byte-header WAV; sizes are patched on [close]. */
class WavWriter(
    file: File,
    private val sampleRate: Int = 16_000,
    private val channels: Int = 1,
) : Closeable {
    private val raf = RandomAccessFile(file, "rw")
    private var dataBytes = 0L
    private var bytesSincePatch = 0L
    private val patchEveryBytes = sampleRate.toLong() * channels * 2 * PATCH_EVERY_SECONDS
    private var closed = false

    init {
        raf.setLength(0)
        raf.write(header(0, sampleRate, channels))
    }

    @Synchronized
    fun write(pcm: ByteArray, offset: Int = 0, length: Int = pcm.size) {
        check(!closed) { "WavWriter closed" }
        raf.write(pcm, offset, length)
        dataBytes += length
        bytesSincePatch += length
        if (bytesSincePatch >= patchEveryBytes) {
            // Keep the header valid so a killed process still leaves a playable file.
            writeHeader()
            bytesSincePatch = 0
        }
    }

    private fun writeHeader() {
        raf.seek(0)
        raf.write(header(dataBytes.coerceAtMost(0xFFFF_FFFFL - 36), sampleRate, channels))
        raf.seek(HEADER_SIZE + dataBytes)
    }

    @Synchronized
    override fun close() {
        if (closed) return
        closed = true
        try {
            writeHeader()
        } finally {
            raf.close()
        }
    }

    companion object {
        const val HEADER_SIZE = 44
        const val PATCH_EVERY_SECONDS = 2

        fun header(dataSize: Long, sampleRate: Int, channels: Int, bitsPerSample: Int = 16): ByteArray {
            val byteRate = sampleRate * channels * bitsPerSample / 8
            val blockAlign = channels * bitsPerSample / 8
            return ByteBuffer.allocate(HEADER_SIZE).order(ByteOrder.LITTLE_ENDIAN).apply {
                put("RIFF".toByteArray(Charsets.US_ASCII))
                putInt((36 + dataSize).toInt())
                put("WAVE".toByteArray(Charsets.US_ASCII))
                put("fmt ".toByteArray(Charsets.US_ASCII))
                putInt(16)
                putShort(1) // PCM
                putShort(channels.toShort())
                putInt(sampleRate)
                putInt(byteRate)
                putShort(blockAlign.toShort())
                putShort(bitsPerSample.toShort())
                put("data".toByteArray(Charsets.US_ASCII))
                putInt(dataSize.toInt())
            }.array()
        }
    }
}
