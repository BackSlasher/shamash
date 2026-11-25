package net.backslasher.shamash.pclink

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import android.util.Log
import java.io.File
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

/** Debug-only trigger: adb shell am broadcast -a net.backslasher.shamash.PCLINK_PROBE -p net.backslasher.shamash */
class PcLinkReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val url = intent.getStringExtra("url") ?: "ws://127.0.0.1:8777/"
        val seconds = intent.getIntExtra("seconds", 10)
        val app = context.applicationContext
        Log.i("Shamash", "pclink: starting url=$url seconds=$seconds")
        scope.launch {
            val dir = File(app.getExternalFilesDir(null) ?: app.filesDir, "pclink")
            PcLinkProbe(dir, Build.MODEL).run(url, seconds)
        }
    }

    private companion object {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    }
}
