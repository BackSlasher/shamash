package net.backslasher.shamash.audio

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager
import java.util.concurrent.Executors
import kotlin.math.PI
import kotlin.math.sin

/** Start/stop feedback. Implementations must never throw and never block the caller. */
interface Cues {
    fun start()
    fun stop()
}

object NoCues : Cues {
    override fun start() = Unit
    override fun stop() = Unit
}

/**
 * Two generated tones played through AudioTrack on the MUSIC/media stream (the earpiece is
 * broken, so never USAGE_VOICE_COMMUNICATION), plus a haptic pulse that may do nothing.
 */
class AndroidCues(context: Context) : Cues {
    private val appContext = context.applicationContext
    private val executor = Executors.newSingleThreadExecutor { r -> Thread(r, "shamash-cues").apply { isDaemon = true } }

    private val startTone: ShortArray by lazy { tone(listOf(600.0, 900.0), 80) }
    private val stopTone: ShortArray by lazy { tone(listOf(900.0, 450.0), 110) }

    override fun start() {
        buzz(35)
        play { startTone }
    }

    override fun stop() {
        buzz(70)
        play { stopTone }
    }

    private fun play(samples: () -> ShortArray) {
        try {
            executor.execute {
                try {
                    val pcm = samples()
                    val track = AudioTrack.Builder()
                        .setAudioAttributes(
                            AudioAttributes.Builder()
                                .setUsage(AudioAttributes.USAGE_MEDIA)
                                .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                                .build(),
                        )
                        .setAudioFormat(
                            AudioFormat.Builder()
                                .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                                .setSampleRate(RATE)
                                .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                                .build(),
                        )
                        .setTransferMode(AudioTrack.MODE_STATIC)
                        .setBufferSizeInBytes(pcm.size * 2)
                        .build()
                    try {
                        track.write(pcm, 0, pcm.size)
                        track.play()
                        Thread.sleep(pcm.size * 1000L / RATE + 100)
                    } finally {
                        track.release()
                    }
                } catch (_: Exception) {
                    // sound is best effort
                }
            }
        } catch (_: Exception) {
        }
    }

    private fun buzz(ms: Long) {
        try {
            val vibrator = appContext.getSystemService(VibratorManager::class.java)?.defaultVibrator
                ?: @Suppress("DEPRECATION") appContext.getSystemService(Vibrator::class.java)
            vibrator?.vibrate(VibrationEffect.createOneShot(ms, VibrationEffect.DEFAULT_AMPLITUDE))
        } catch (_: Exception) {
            // motor may be dead, or VIBRATE not granted; never rely on it
        }
    }

    private companion object {
        const val RATE = 22_050

        fun tone(freqs: List<Double>, msEach: Int): ShortArray {
            val per = RATE * msEach / 1000
            val out = ShortArray(per * freqs.size)
            val fade = RATE * 8 / 1000
            var phase = 0.0
            freqs.forEachIndexed { fi, f ->
                for (i in 0 until per) {
                    phase += 2 * PI * f / RATE
                    val env = minOf(1.0, i / fade.toDouble(), (per - i) / fade.toDouble())
                    out[fi * per + i] = (sin(phase) * env * 0.5 * Short.MAX_VALUE).toInt().toShort()
                }
            }
            return out
        }
    }
}
