package net.backslasher.shamash.core

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager
import android.util.Log
import androidx.core.content.ContextCompat
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

private const val TAG = "Shamash"
private const val TICK_MS = 15_000L

/**
 * Android side of [ChargingWatchdog]: reads ACTION_BATTERY_CHANGED (sticky, so the first read is
 * immediate), ticks the watchdog, publishes [charge] and the latest [reading], and calls [onWarn]
 * once per outage. Lives in the ViewModel (application context), so it keeps watching whichever screen is up.
 */
class BatteryMonitor(
    private val context: Context,
    scope: CoroutineScope,
    private val onWarn: () -> Unit,
    private val watchdog: ChargingWatchdog = ChargingWatchdog(SystemClock),
) {
    private val _charge = MutableStateFlow<ChargeState>(ChargeState.Ok)
    val charge: StateFlow<ChargeState> = _charge.asStateFlow()

    @Volatile var reading: BatteryReading? = null
        private set

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(c: Context, intent: Intent) = handle(intent)
    }

    init {
        val sticky = ContextCompat.registerReceiver(
            context, receiver, IntentFilter(Intent.ACTION_BATTERY_CHANGED), ContextCompat.RECEIVER_NOT_EXPORTED,
        )
        if (sticky != null) handle(sticky)
        scope.launch {
            while (true) {
                delay(TICK_MS)
                publish(watchdog.tick())
            }
        }
    }

    private fun handle(intent: Intent) {
        val level = intent.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
        val scale = intent.getIntExtra(BatteryManager.EXTRA_SCALE, 100)
        if (level < 0 || scale <= 0) return
        val r = BatteryReading((level * 100 / scale).coerceIn(0, 100), intent.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) != 0)
        reading = r
        publish(watchdog.onBattery(r))
    }

    private fun publish(result: ChargingWatchdog.Result) {
        if (_charge.value != result.state) Log.i(TAG, "charging: $result")
        _charge.value = result.state
        if (result.speak) onWarn()
    }
}
