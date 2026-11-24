package net.backslasher.shamash.core

/** Time source so tests can control latency measurements. */
interface Clock {
    /** Monotonic milliseconds, for durations. */
    fun elapsedMs(): Long

    /** Wall-clock epoch milliseconds, for timestamps and file names. */
    fun wallMs(): Long
}

object SystemClock : Clock {
    override fun elapsedMs(): Long = System.nanoTime() / 1_000_000L
    override fun wallMs(): Long = System.currentTimeMillis()
}
