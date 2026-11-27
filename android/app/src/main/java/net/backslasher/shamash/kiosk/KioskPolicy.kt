package net.backslasher.shamash.kiosk

import android.Manifest
import android.app.Activity
import android.app.ActivityManager
import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager
import android.os.UserManager
import android.provider.Settings
import android.util.Log
import net.backslasher.shamash.MainActivity
import net.backslasher.shamash.config.ShamashConfig

private const val TAG = "Shamash"

/**
 * Device-owner policy for kiosk mode. Everything here is a no-op unless the app is device owner,
 * so a development install behaves as an ordinary app (and never shows the screen-pinning dialog).
 */
object KioskPolicy {
    private fun dpm(context: Context) = context.getSystemService(DevicePolicyManager::class.java)

    fun isDeviceOwner(context: Context): Boolean =
        dpm(context)?.isDeviceOwnerApp(context.packageName) == true

    fun isInLockTask(context: Context): Boolean =
        context.getSystemService(ActivityManager::class.java)?.lockTaskModeState !=
            ActivityManager.LOCK_TASK_MODE_NONE

    /**
     * Applies all policies (idempotent; cheap enough to run on every launch). Each step is isolated.
     * [lockTask] false keeps the status bar (and so Quick Settings) usable; see ShamashConfig.lockTask.
     */
    fun applyIfDeviceOwner(context: Context, lockTask: Boolean = ShamashConfig.DEFAULT.lockTask) {
        val dpm = dpm(context) ?: return
        val pkg = context.packageName
        if (!dpm.isDeviceOwnerApp(pkg)) {
            Log.i(TAG, "kiosk: not device owner, running as ordinary app")
            return
        }
        val admin = ComponentName(context, ShamashAdminReceiver::class.java)

        // The manifest's lockTaskMode="if_whitelisted" locks the task automatically whenever the
        // package is allowlisted, so with lockTask=false the allowlist must be empty.
        step("lock task packages") { dpm.setLockTaskPackages(admin, if (lockTask) arrayOf(pkg) else emptyArray()) }
        // Only the power menu (long-press power: Power off / Restart) stays reachable while locked;
        // no home, overview, notifications, system info or keyguard.
        step("lock task features") { dpm.setLockTaskFeatures(admin, DevicePolicyManager.LOCK_TASK_FEATURE_GLOBAL_ACTIONS) }
        step("persistent home") {
            val filter = IntentFilter(Intent.ACTION_MAIN).apply {
                addCategory(Intent.CATEGORY_HOME)
                addCategory(Intent.CATEGORY_DEFAULT)
            }
            dpm.addPersistentPreferredActivity(admin, filter, ComponentName(pkg, MainActivity::class.java.name))
        }
        step("stay on while plugged in") {
            val plugged = BatteryManager.BATTERY_PLUGGED_AC or BatteryManager.BATTERY_PLUGGED_USB or
                BatteryManager.BATTERY_PLUGGED_WIRELESS
            dpm.setGlobalSetting(admin, Settings.Global.STAY_ON_WHILE_PLUGGED_IN, plugged.toString())
        }
        step("adb enabled") { dpm.setGlobalSetting(admin, Settings.Global.ADB_ENABLED, "1") }
        step("keyguard disabled") { Log.i(TAG, "kiosk: setKeyguardDisabled returned ${dpm.setKeyguardDisabled(admin, true)}") }
        step("status bar disabled=$lockTask") {
            Log.i(TAG, "kiosk: setStatusBarDisabled($lockTask) returned ${dpm.setStatusBarDisabled(admin, lockTask)}")
        }
        // DISALLOW_SAFE_BOOT is deliberately not set: safe boot is the recovery path for the POC
        // (owner is present; in safe mode third-party apps including the device owner do not run,
        // so Settings and the adb "Allow USB debugging?" dialog are reachable).
        for (r in listOf(
            UserManager.DISALLOW_FACTORY_RESET,
            UserManager.DISALLOW_ADD_USER,
            UserManager.DISALLOW_SYSTEM_ERROR_DIALOGS,
        )) {
            step("restriction $r") { dpm.addUserRestriction(admin, r) }
        }
        step("microphone grant") {
            if (dpm.canAdminGrantSensorsPermissions()) {
                try {
                    val ok = dpm.setPermissionGrantState(
                        admin, pkg, Manifest.permission.RECORD_AUDIO,
                        DevicePolicyManager.PERMISSION_GRANT_STATE_GRANTED,
                    )
                    Log.i(TAG, "kiosk: RECORD_AUDIO grant ${if (ok) "applied" else "refused (returned false)"}")
                } catch (e: SecurityException) {
                    Log.w(TAG, "kiosk: RECORD_AUDIO grant not allowed: ${e.message}")
                }
            } else {
                Log.w(TAG, "kiosk: admin cannot grant sensor permissions; microphone dialog will show")
            }
        }
    }

    /** Starts lock task mode when device owner and not already locked. Never called for ordinary installs. */
    fun enterLockTaskIfPossible(activity: Activity) {
        if (!isDeviceOwner(activity) || isInLockTask(activity)) return
        // A non-allowlisted startLockTask() would show the screen-pinning dialog, so require the allowlist.
        val permitted = dpm(activity)?.isLockTaskPermitted(activity.packageName) == true
        if (!permitted) {
            Log.w(TAG, "kiosk: package not lock-task permitted, skipping startLockTask")
            return
        }
        step("startLockTask") { activity.startLockTask() }
    }

    private inline fun step(name: String, block: () -> Unit) {
        try {
            block()
        } catch (e: Exception) {
            Log.w(TAG, "kiosk: step '$name' failed: ${e::class.java.simpleName}: ${e.message}")
        }
    }
}
