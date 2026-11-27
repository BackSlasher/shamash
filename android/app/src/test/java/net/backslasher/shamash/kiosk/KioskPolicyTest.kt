package net.backslasher.shamash.kiosk

import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class KioskPolicyTest {
    private val context: Context = ApplicationProvider.getApplicationContext()
    private val dpm get() = context.getSystemService(DevicePolicyManager::class.java)

    @Test
    fun notDeviceOwnerIsNoOp() {
        assertFalse(KioskPolicy.isDeviceOwner(context))
        KioskPolicy.applyIfDeviceOwner(context) // must not throw
        assertFalse(dpm.isLockTaskPermitted(context.packageName))
    }

    @Test
    fun deviceOwnerGetsLockTaskPackageOnlyWhenLockTaskEnabled() {
        val admin = ComponentName(context, ShamashAdminReceiver::class.java)
        shadowOf(dpm).setDeviceOwner(admin)
        assertTrue(KioskPolicy.isDeviceOwner(context))
        KioskPolicy.applyIfDeviceOwner(context, lockTask = true) // failing steps are logged, never thrown
        assertTrue(dpm.isLockTaskPermitted(context.packageName))
        // Power menu only: long-press power must still offer Power off / Restart while locked.
        assertEquals(DevicePolicyManager.LOCK_TASK_FEATURE_GLOBAL_ACTIONS, dpm.getLockTaskFeatures(admin))
        // Regular-app mode must empty the allowlist, or lockTaskMode="if_whitelisted" locks anyway.
        KioskPolicy.applyIfDeviceOwner(context, lockTask = false)
        assertFalse(dpm.isLockTaskPermitted(context.packageName))
    }
}
