package net.backslasher.shamash

import android.Manifest
import android.content.pm.PackageManager
import android.os.Bundle
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import net.backslasher.shamash.core.ErrorKind
import net.backslasher.shamash.core.ScreenModel
import net.backslasher.shamash.core.ShamashState
import net.backslasher.shamash.ui.ShamashScreen

class MainActivity : ComponentActivity() {
    private val viewModel: ShamashViewModel by viewModels()
    private var micGranted by mutableStateOf(false)

    private val micPermission =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { micGranted = it }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        micGranted = hasMic()
        if (!micGranted) micPermission.launch(Manifest.permission.RECORD_AUDIO)

        val controller = viewModel.controller
        setContent {
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
            )
        }
    }

    override fun onResume() {
        super.onResume()
        micGranted = hasMic()
    }

    private fun hasMic() =
        ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED
}
