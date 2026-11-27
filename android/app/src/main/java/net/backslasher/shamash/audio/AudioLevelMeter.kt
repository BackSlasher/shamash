package net.backslasher.shamash.audio

import kotlin.math.log10
import kotlin.math.sqrt

/**
 * Turns 16-bit little-endian mono PCM chunks into a 0..1 level for the screen: RMS, log (dB) scale,
 * fast attack and slow release. Pure arithmetic, called once per chunk (~100 ms) only while recording.
 */
class AudioLevelMeter(private val floorDb: Float = -60f) {
    private var smoothed = 0f

    fun update(pcm: ByteArray): Float {
        val x = level(pcm)
        smoothed = if (x > smoothed) x else smoothed * 0.6f + x * 0.4f
        return smoothed
    }

    fun reset() {
        smoothed = 0f
    }

    /** Unsmoothed level of one chunk. */
    fun level(pcm: ByteArray): Float {
        val n = pcm.size / 2
        if (n == 0) return 0f
        var sum = 0.0
        for (i in 0 until n) {
            val s = ((pcm[2 * i + 1].toInt() shl 8) or (pcm[2 * i].toInt() and 0xFF)).toShort().toInt()
            sum += s.toDouble() * s
        }
        val rms = sqrt(sum / n) / 32768.0
        if (rms <= 0.0) return 0f
        val db = (20 * log10(rms)).toFloat()
        return ((db - floorDb) / -floorDb).coerceIn(0f, 1f)
    }
}
