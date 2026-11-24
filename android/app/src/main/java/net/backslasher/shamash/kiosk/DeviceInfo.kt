package net.backslasher.shamash.kiosk

import android.content.Context
import android.content.Intent
import android.os.Build
import android.speech.RecognitionSupport
import android.speech.RecognitionSupportCallback
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer
import android.util.Log
import java.io.File
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import net.backslasher.shamash.BuildConfig

private const val TAG = "Shamash"

@Serializable
data class DeviceInfoRecord(
    val model: String,
    val androidRelease: String,
    val sdkInt: Int,
    val appVersion: String,
    val deviceOwner: Boolean,
    val inLockTask: Boolean,
    val onDeviceRecognitionAvailable: Boolean,
    /** Result of checkRecognitionSupport for he-IL, or why it was not probed. */
    val onDeviceHebrew: String,
)

/** One-off probe on first launch; result goes to `<recordings>/device-info.json`. Nothing depends on it. */
object DeviceInfo {
    fun probeOnce(context: Context, recordingsDir: File) {
        val out = File(recordingsDir, "device-info.json")
        if (out.exists()) return
        try {
            recordingsDir.mkdirs()
            val available = SpeechRecognizer.isOnDeviceRecognitionAvailable(context)
            fun write(hebrew: String) {
                val rec = DeviceInfoRecord(
                    Build.MODEL, Build.VERSION.RELEASE, Build.VERSION.SDK_INT, BuildConfig.VERSION_NAME,
                    KioskPolicy.isDeviceOwner(context), KioskPolicy.isInLockTask(context), available, hebrew,
                )
                out.writeText(Json { prettyPrint = true }.encodeToString(DeviceInfoRecord.serializer(), rec))
                Log.i(TAG, "device-info: on-device recognition available=$available, he-IL: $hebrew")
            }
            if (!available) {
                write("not_probed: on-device recognition unavailable")
                return
            }
            val recognizer = SpeechRecognizer.createOnDeviceSpeechRecognizer(context)
            val intent = Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH)
                .putExtra(RecognizerIntent.EXTRA_LANGUAGE, "he-IL")
            recognizer.checkRecognitionSupport(
                intent, context.mainExecutor,
                object : RecognitionSupportCallback {
                    override fun onSupportResult(s: RecognitionSupport) {
                        write(
                            "installed=${s.installedOnDeviceLanguages} pending=${s.pendingOnDeviceLanguages} " +
                                "supported=${s.supportedOnDeviceLanguages}",
                        )
                        recognizer.destroy()
                    }

                    override fun onError(error: Int) {
                        write("error_$error")
                        recognizer.destroy()
                    }
                },
            )
        } catch (e: Exception) {
            Log.w(TAG, "device-info probe failed: ${e.message}")
        }
    }
}
