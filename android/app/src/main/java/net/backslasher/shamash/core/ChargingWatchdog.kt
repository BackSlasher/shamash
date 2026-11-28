package net.backslasher.shamash.core

/** What the battery looks like right now; also what the phone reports to the PC in `hello`. */
data class BatteryReading(val level: Int, val charging: Boolean)

/** Whether the phone is drawing power. [NotCharging] is raised only after the grace period. */
sealed interface ChargeState {
    data object Ok : ChargeState

    /** Unplugged for longer than the grace period; [sinceMillis] is the wall-clock time the outage began. */
    data class NotCharging(val level: Int, val sinceMillis: Long) : ChargeState
}

/**
 * Pure decision logic for the "phone stopped charging" warning, fed by battery readings and a
 * periodic [tick] (battery broadcasts only fire when the level or plug state changes, which can be
 * far apart). Unplugged continuously for longer than [graceMs] -> [ChargeState.NotCharging] and
 * exactly one [Result.speak] per outage; power back -> [ChargeState.Ok] and the next outage may speak again.
 */
class ChargingWatchdog(
    private val clock: Clock,
    private val graceMs: Long = GRACE_MINUTES * 60_000L,
) {
    data class Result(val state: ChargeState, val speak: Boolean)

    private var unpluggedSinceElapsed: Long? = null
    private var unpluggedSinceWall = 0L
    private var level = 0
    private var warned = false
    var state: ChargeState = ChargeState.Ok
        private set

    /** A new battery reading (also pass the current one at start-up). */
    fun onBattery(reading: BatteryReading): Result {
        level = reading.level
        if (reading.charging) {
            unpluggedSinceElapsed = null
            warned = false
            state = ChargeState.Ok
            return Result(state, false)
        }
        if (unpluggedSinceElapsed == null) {
            unpluggedSinceElapsed = clock.elapsedMs()
            unpluggedSinceWall = clock.wallMs()
        }
        return evaluate()
    }

    /** Call about every 15 s so the grace period expires without a battery broadcast. */
    fun tick(): Result = if (unpluggedSinceElapsed == null) Result(state, false) else evaluate()

    private fun evaluate(): Result {
        val since = unpluggedSinceElapsed ?: return Result(state, false)
        if (clock.elapsedMs() - since <= graceMs) return Result(state, false)
        state = ChargeState.NotCharging(level, unpluggedSinceWall)
        val speak = !warned
        warned = true
        return Result(state, speak)
    }

    companion object {
        /** Minutes unplugged before the banner and the spoken warning; short blips (a cable jiggle) stay silent. */
        const val GRACE_MINUTES = 3L
    }
}
