package net.backslasher.shamash

import android.Manifest
import android.content.pm.PackageManager
import android.os.Bundle
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
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import net.backslasher.shamash.audio.AndroidCues
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
        val cues = AndroidCues(this)
        maintenance = maintenanceUnlocked
        setContent {
            BackHandler { } // absorb the back gesture
            val model by controller.model.collectAsStateWithLifecycle()
            val shown = if (micGranted) {
                model
            } else {
                ScreenModel(
                    ShamashState.ERROR, "", false,
                    "Microphone permission denied. Tap to ask again.", ErrorKind.MICROPHONE,
                )
            }
            ShamashScreen(
                model = shown,
                onTap = {
                    if (micGranted) controller.onTap() else micPermission.launch(Manifest.permission.RECORD_AUDIO)
                },
                onPressDown = { if (micGranted) controller.onPressDown() },
                onPressUp = { if (micGranted) controller.onPressUp() },
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
        screenKeeper.stop()
        super.onDestroy()
    }

    override fun onResume() {
        super.onResume()
        micGranted = hasMic()
        if (!maintenanceUnlocked) KioskPolicy.enterLockTaskIfPossible(this)
    }

    private fun hasMic() =
        ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED
}
