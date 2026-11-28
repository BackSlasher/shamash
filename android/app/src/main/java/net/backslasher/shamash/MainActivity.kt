package net.backslasher.shamash

import android.Manifest
import android.content.pm.PackageManager
import android.os.Bundle
import android.util.Log
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.content.ContextCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import java.io.File
import net.backslasher.shamash.kiosk.DeviceInfo
import net.backslasher.shamash.kiosk.KioskPolicy
import net.backslasher.shamash.kiosk.ScreenKeeper
import net.backslasher.shamash.config.ShamashConfig
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import net.backslasher.shamash.audio.AndroidCues
import net.backslasher.shamash.core.ChargeState
import net.backslasher.shamash.core.ErrorKind
import net.backslasher.shamash.core.ScreenModel
import net.backslasher.shamash.core.ShamashState
import net.backslasher.shamash.ui.ShamashScreen

/** Set by the maintenance gesture; survives activity recreation, cleared when the process dies. */
private var maintenanceUnlocked = false

class MainActivity : ComponentActivity() {
    private val viewModel: ShamashViewModel by viewModels()
    private var micGranted by mutableStateOf(false)
    private var maintenance by mutableStateOf(false)
    // Application context and create/destroy lifetime: the screen-off broadcast arrives after the
    // activity has already been stopped, so a start/stop-scoped receiver never sees it.
    private val screenKeeper by lazy { ScreenKeeper(applicationContext) }

    private val micPermission =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { micGranted = it }

    /** Sets the media stream to `ShamashConfig.mediaVolume` (fraction of max); the volume keys then adjust that stream. */
    private fun applyMediaVolume() {
        val fraction = ShamashConfig.DEFAULT.mediaVolume ?: return
        volumeControlStream = android.media.AudioManager.STREAM_MUSIC
        val am = getSystemService(android.media.AudioManager::class.java) ?: return
        val stream = android.media.AudioManager.STREAM_MUSIC
        val max = am.getStreamMaxVolume(stream)
        val target = (max * fraction).toInt().coerceIn(0, max)
        val current = am.getStreamVolume(stream)
        if (current == target) return
        try {
            am.setStreamVolume(stream, target, 0)
            Log.i("Shamash", "volume: media $current -> $target of $max")
        } catch (e: Exception) {
            Log.w("Shamash", "volume: could not set media volume: ${e.message}")
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        KioskPolicy.applyIfDeviceOwner(this) // before the permission check: it pre-grants the microphone
        setShowWhenLocked(true)
        setTurnScreenOn(true)
        screenKeeper.start()
        enableEdgeToEdge()
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        // Immersive sticky, so an ordinary install looks the same as lock task mode.
        WindowInsetsControllerCompat(window, window.decorView).apply {
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            hide(WindowInsetsCompat.Type.systemBars())
        }
        DeviceInfo.probeOnce(this, File(getExternalFilesDir(null) ?: filesDir, "recordings"))

        micGranted = hasMic()
        if (!micGranted) micPermission.launch(Manifest.permission.RECORD_AUDIO)

        val controller = viewModel.controller
        DebugHooks.speak = controller::speak
        val cues = AndroidCues(this)
        maintenance = maintenanceUnlocked
        setContent {
            BackHandler { } // absorb the back gesture
            val model by controller.model.collectAsStateWithLifecycle()
            val levelState = controller.level.collectAsStateWithLifecycle()
            val shown = if (micGranted) {
                model
            } else {
                ScreenModel(
                    ShamashState.ERROR, "", false,
                    "Microphone permission denied. Tap to ask again.", ErrorKind.MICROPHONE,
                )
            }
            val charge by viewModel.charge.collectAsStateWithLifecycle()
            ShamashScreen(
                model = shown,
                notCharging = charge as? ChargeState.NotCharging,
                onTap = {
                    if (micGranted) controller.onTap() else micPermission.launch(Manifest.permission.RECORD_AUDIO)
                },
                onPressDown = { if (micGranted) controller.onPressDown() },
                onPressUp = { if (micGranted) controller.onPressUp() },
                level = { levelState.value },
                maintenance = maintenance,
                onMaintenanceUnlock = {
                    maintenanceUnlocked = true
                    maintenance = true
                    try { stopLockTask() } catch (_: Exception) { }
                    cues.stop() // best-effort haptic and tone
                },
            )
        }
    }

    override fun onDestroy() {
        DebugHooks.speak = null
        screenKeeper.stop()
        super.onDestroy()
    }

    override fun onResume() {
        super.onResume()
        micGranted = hasMic()
        applyMediaVolume()
        if (ShamashConfig.DEFAULT.lockTask && !maintenanceUnlocked) KioskPolicy.enterLockTaskIfPossible(this)
    }

    private fun hasMic() =
        ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED
}
