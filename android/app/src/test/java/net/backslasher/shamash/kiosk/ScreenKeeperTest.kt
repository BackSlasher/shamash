package net.backslasher.shamash.kiosk

import android.content.Context
import android.content.Intent
import android.os.Looper
import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.shadows.ShadowPowerManager

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class ScreenKeeperTest {
    private val context: Context = ApplicationProvider.getApplicationContext()

    private fun screenOff() {
        context.sendBroadcast(Intent(Intent.ACTION_SCREEN_OFF))
        shadowOf(Looper.getMainLooper()).idle()
    }

    @Test
    fun screenOffTakesWakeLockOnlyWhileStarted() {
        val keeper = ScreenKeeper(context)
        screenOff()
        assertNull(ShadowPowerManager.getLatestWakeLock())

        keeper.start()
        screenOff()
        val lock = ShadowPowerManager.getLatestWakeLock()
        assertNotNull(lock)
        assertTrue(lock.isHeld)

        keeper.stop()
        lock.release()
        screenOff()
        assertTrue(!ShadowPowerManager.getLatestWakeLock().isHeld)
    }
}
