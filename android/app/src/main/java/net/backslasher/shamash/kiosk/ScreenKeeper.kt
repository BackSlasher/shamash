package net.backslasher.shamash.kiosk

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.PowerManager
import android.util.Log
import androidx.core.content.ContextCompat

/** Turns the screen back on whenever it goes off (e.g. the power button), so the phone never sits dark. */
class ScreenKeeper(private val context: Context) {
    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(c: Context, intent: Intent) = rewake()
    }
    private var registered = false

    fun start() {
        if (registered) return
        ContextCompat.registerReceiver(
            context, receiver, IntentFilter(Intent.ACTION_SCREEN_OFF), ContextCompat.RECEIVER_NOT_EXPORTED,
        )
        registered = true
    }

    fun stop() {
        if (!registered) return
        context.unregisterReceiver(receiver)
        registered = false
    }

    // The only way for a running app to turn the screen on; deprecated but still working.
    @Suppress("DEPRECATION")
    private fun rewake() {
        Log.i("Shamash", "rewake: screen off, waking")
        try {
            val pm = context.getSystemService(PowerManager::class.java)
            val flags = PowerManager.SCREEN_BRIGHT_WAKE_LOCK or
                PowerManager.ACQUIRE_CAUSES_WAKEUP or PowerManager.ON_AFTER_RELEASE
            pm.newWakeLock(flags, "shamash:rewake").acquire(3_000L)
        } catch (e: Exception) {
            Log.w("Shamash", "rewake failed", e)
        }
    }
}
