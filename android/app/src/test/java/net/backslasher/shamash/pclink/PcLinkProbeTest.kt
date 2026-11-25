package net.backslasher.shamash.pclink

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test

class PcLinkProbeTest {
    @Test
    fun frameLayoutIsBigEndianSeqThenTimeThenPcm() {
        val pcm = ByteArray(3200) { (it % 251).toByte() }
        val f = PcLinkFrame.encode(0x01020304L, 0x0A0B0C0D0E0F1011L, pcm)
        assertEquals(12 + 3200, f.size)
        assertArrayEquals(byteArrayOf(1, 2, 3, 4), f.copyOfRange(0, 4))
        assertArrayEquals(
            byteArrayOf(0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11), f.copyOfRange(4, 12),
        )
        assertArrayEquals(pcm, f.copyOfRange(12, f.size))
        assertEquals(0x01020304L, PcLinkFrame.seqOf(f))
        assertEquals(0x0A0B0C0D0E0F1011L, PcLinkFrame.tPhoneOf(f))
        assertArrayEquals(pcm, PcLinkFrame.pcmOf(f))
    }

    @Test
    fun seqIsUnsigned32() {
        val f = PcLinkFrame.encode(0xFFFFFFFEL, 5, ByteArray(2))
        assertEquals(0xFFFFFFFEL, PcLinkFrame.seqOf(f))
    }

    @Test
    fun rttStats() {
        val s = RttStats.of((1L..100L).toList().reversed())
        assertEquals(RttStats(100, 1, 50, 100, 95), s)
        assertEquals(RttStats(1, 7, 7, 7, 7), RttStats.of(listOf(7)))
        assertEquals(RttStats(0, 0, 0, 0, 0), RttStats.of(emptyList()))
    }
}
