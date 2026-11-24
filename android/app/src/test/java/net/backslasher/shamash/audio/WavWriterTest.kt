package net.backslasher.shamash.audio

import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class WavWriterTest {
    @get:Rule val tmp = TemporaryFolder()

    @Test
    fun headerFieldsForKnownPayload() {
        val file = File(tmp.root, "a.wav")
        val payload = ByteArray(3200) { (it % 100).toByte() }
        WavWriter(file, 16_000, 1).use {
            it.write(payload)
            it.write(payload, 0, 800)
        }
        val bytes = file.readBytes()
        val data = 4000
        assertEquals(44 + data, bytes.size)
        assertEquals("RIFF", String(bytes, 0, 4, Charsets.US_ASCII))
        assertEquals("WAVE", String(bytes, 8, 4, Charsets.US_ASCII))
        assertEquals("fmt ", String(bytes, 12, 4, Charsets.US_ASCII))
        assertEquals("data", String(bytes, 36, 4, Charsets.US_ASCII))
        val b = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        assertEquals(36 + data, b.getInt(4))
        assertEquals(16, b.getInt(16))
        assertEquals(1, b.getShort(20).toInt())
        assertEquals(1, b.getShort(22).toInt())
        assertEquals(16_000, b.getInt(24))
        assertEquals(32_000, b.getInt(28))
        assertEquals(2, b.getShort(32).toInt())
        assertEquals(16, b.getShort(34).toInt())
        assertEquals(data, b.getInt(40))
        assertEquals(payload[5], bytes[44 + 5])
    }

    @Test
    fun emptyFileHasZeroSizes() {
        val file = File(tmp.root, "e.wav")
        WavWriter(file).close()
        val b = ByteBuffer.wrap(file.readBytes()).order(ByteOrder.LITTLE_ENDIAN)
        assertEquals(44, file.length().toInt())
        assertEquals(36, b.getInt(4))
        assertEquals(0, b.getInt(40))
    }

    @Test
    fun headerIsRepatchedPeriodicallyBeforeClose() {
        val file = File(tmp.root, "p.wav")
        val second = ByteArray(32_000)
        val w = WavWriter(file, 16_000, 1)
        w.write(second)
        assertEquals(0, dataSizeOnDisk(file)) // 1 s: not yet patched
        w.write(second) // 2 s: patched
        assertEquals(64_000, dataSizeOnDisk(file))
        w.write(second)
        assertEquals(64_000, dataSizeOnDisk(file))
        w.close()
        assertEquals(96_000, dataSizeOnDisk(file))
        assertEquals(44L + 96_000, file.length())
    }

    private fun dataSizeOnDisk(f: File) = ByteBuffer.wrap(f.readBytes()).order(ByteOrder.LITTLE_ENDIAN).getInt(40)
}
