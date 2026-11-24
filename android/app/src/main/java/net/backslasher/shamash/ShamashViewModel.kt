package net.backslasher.shamash

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import java.io.File
import net.backslasher.shamash.audio.AndroidCues
import net.backslasher.shamash.audio.AudioRecordCapture
import net.backslasher.shamash.config.ShamashConfig
import net.backslasher.shamash.core.SessionController
import net.backslasher.shamash.storage.FileSessionStore
import net.backslasher.shamash.transcribe.ElevenLabsTranscriber
import net.backslasher.shamash.transcribe.FakeTranscriber
import okhttp3.OkHttpClient

/** Holds the controller so rotation or recreation does not kill a session. Hand-built dependency graph. */
class ShamashViewModel(app: Application) : AndroidViewModel(app) {
    val config = ShamashConfig.DEFAULT

    val controller = SessionController(
        scope = viewModelScope,
        config = config,
        capture = AudioRecordCapture(config.sampleRate),
        transcriber = if (config.useFakeTranscriber) {
            FakeTranscriber()
        } else {
            ElevenLabsTranscriber(BuildConfig.ELEVENLABS_KEY, config, OkHttpClient())
        },
        store = FileSessionStore(File(app.getExternalFilesDir(null) ?: app.filesDir, "recordings")),
        cues = AndroidCues(app),
    )
}
