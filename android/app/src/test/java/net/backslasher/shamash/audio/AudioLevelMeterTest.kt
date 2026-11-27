package net.backslasher.shamash.audio

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class AudioLevelMeterTest {
    private fun chunk(amp: Int, n: Int = 1600) = ByteArray(n * 2) { i -> if (i % 2 == 0) (amp and 0xFF).toByte() else (amp shr 8).toByte() }

    @Test fun silenceIsZeroAndFullScaleIsOne() {
        val m = AudioLevelMeter()
        assertEquals(0f, m.level(chunk(0)), 0f)
        assertEquals(1f, m.level(chunk(32767)), 0.01f)
        assertEquals(0f, m.level(ByteArray(0)), 0f)
    }

    @Test fun logScaleHalvingAmplitudeDropsAboutTenthOfRange() {
        val m = AudioLevelMeter()
        val full = m.level(chunk(16384)) // -6 dB
        val half = m.level(chunk(8192)) // -12 dB
        assertEquals(0.1f, full - half, 0.01f)
        assertTrue(m.level(chunk(10)) < 0.3f)
    }

    @Test fun fastAttackSlowRelease() {
        val m = AudioLevelMeter()
        val up = m.update(chunk(16384))
        assertEquals(m.level(chunk(16384)), up, 0.001f)
        val down = m.update(chunk(0))
        assertTrue(down in 0.01f..(up - 0.1f))
        m.reset()
        assertEquals(0f, m.update(chunk(0)), 0f)
    }
}
