package net.backslasher.shamash.kiosk

import android.app.admin.DeviceAdminReceiver
import android.content.Context
import android.content.Intent
import android.util.Log

/** Device admin component; becomes device owner via `dpm set-device-owner` (see README). */
class ShamashAdminReceiver : DeviceAdminReceiver() {
    override fun onLockTaskModeEntering(context: Context, intent: Intent, pkg: String) {
        Log.i("Shamash", "lock task mode entering: $pkg")
    }

    override fun onLockTaskModeExiting(context: Context, intent: Intent) {
        Log.i("Shamash", "lock task mode exiting")
    }
}
