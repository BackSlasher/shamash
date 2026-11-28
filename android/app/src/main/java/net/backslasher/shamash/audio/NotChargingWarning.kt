package net.backslasher.shamash.audio

import android.content.Context
import android.media.AudioAttributes
import android.media.MediaPlayer
import android.speech.tts.TextToSpeech
import android.util.Log
import java.util.Locale
import net.backslasher.shamash.R

private const val TAG = "Shamash"
const val NOT_CHARGING_TEXT = "הטלפון לא בטעינה, בדוק את הכבל"

/**
 * Plays the bundled Hebrew warning once, at the current media volume. If the clip cannot be played,
 * falls back to the system text-to-speech with a Hebrew voice. Never loops.
 */
class NotChargingWarning(private val context: Context) {
    private var player: MediaPlayer? = null
    private var tts: TextToSpeech? = null

    fun play() {
        stop()
        try {
            val p = MediaPlayer.create(
                context, R.raw.not_charging,
                AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build(),
                android.media.AudioManager.AUDIO_SESSION_ID_GENERATE,
            ) ?: throw IllegalStateException("MediaPlayer.create returned null")
            p.isLooping = false
            p.setOnCompletionListener { it.release(); if (player === it) player = null }
            p.setOnErrorListener { mp, what, extra ->
                Log.w(TAG, "charging: clip error $what/$extra, using TextToSpeech")
                mp.release(); if (player === mp) player = null
                speakFallback()
                true
            }
            player = p
            p.start()
        } catch (e: Exception) {
            Log.w(TAG, "charging: clip failed (${e.message}), using TextToSpeech")
            speakFallback()
        }
    }

    fun stop() {
        try { player?.release() } catch (_: Exception) { }
        player = null
        try { tts?.shutdown() } catch (_: Exception) { }
        tts = null
    }

    private fun speakFallback() {
        var engine: TextToSpeech? = null
        engine = TextToSpeech(context.applicationContext) { status ->
            val t = engine ?: return@TextToSpeech
            if (status != TextToSpeech.SUCCESS) { t.shutdown(); return@TextToSpeech }
            t.language = Locale("he", "IL")
            t.speak(NOT_CHARGING_TEXT, TextToSpeech.QUEUE_FLUSH, null, "not_charging")
        }
        tts = engine
    }
}
