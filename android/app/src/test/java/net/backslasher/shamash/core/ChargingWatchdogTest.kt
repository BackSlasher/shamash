package net.backslasher.shamash.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ChargingWatchdogTest {
    private class FakeClock(var now: Long = 1_000_000L) : Clock {
        override fun elapsedMs() = now
        override fun wallMs() = 1_700_000_000_000L + now
    }

    private val min = 60_000L
    private val clock = FakeClock()
    private val dog = ChargingWatchdog(clock, graceMs = 3 * min)
    private fun unplugged(level: Int = 71) = BatteryReading(level, false)
    private val plugged = BatteryReading(80, true)

    @Test fun defaultGraceIsThreeMinutes() = assertEquals(3L, ChargingWatchdog.GRACE_MINUTES)

    @Test fun chargingStaysOk() {
        val r = dog.onBattery(plugged)
        assertEquals(ChargeState.Ok, r.state)
        assertFalse(r.speak)
    }

    @Test fun unpluggedWithinGraceIsOkAndSilent() {
        dog.onBattery(unplugged())
        clock.now += 3 * min // exactly the grace: not yet "more than"
        val r = dog.tick()
        assertEquals(ChargeState.Ok, r.state)
        assertFalse(r.speak)
    }

    @Test fun unpluggedPastGraceWarnsWithLevelAndStart() {
        dog.onBattery(unplugged(71))
        val start = clock.wallMs()
        clock.now += 3 * min + 1
        val r = dog.tick()
        assertEquals(ChargeState.NotCharging(71, start), r.state)
        assertTrue(r.speak)
    }

    @Test fun levelUpdatesButOnlyOneSpokenWarningPerOutage() {
        dog.onBattery(unplugged(71))
        clock.now += 4 * min
        assertTrue(dog.tick().speak)
        clock.now += min
        assertFalse(dog.tick().speak)
        val r = dog.onBattery(unplugged(69)) // broadcast with a new level
        assertFalse(r.speak)
        assertEquals(69, (r.state as ChargeState.NotCharging).level)
        clock.now += 30 * min
        assertFalse(dog.tick().speak)
    }

    @Test fun powerBackClearsAndNextOutageWarnsAgain() {
        dog.onBattery(unplugged())
        clock.now += 4 * min
        assertTrue(dog.tick().speak)
        val cleared = dog.onBattery(plugged)
        assertEquals(ChargeState.Ok, cleared.state)
        assertFalse(cleared.speak)

        dog.onBattery(unplugged())
        clock.now += 2 * min
        assertEquals(ChargeState.Ok, dog.tick().state) // grace restarts
        clock.now += 2 * min
        assertTrue(dog.tick().speak)
    }

    @Test fun briefBlipResetsTheGraceTimer() {
        dog.onBattery(unplugged())
        clock.now += 2 * min
        dog.onBattery(plugged)
        clock.now += 1 * min
        dog.onBattery(unplugged())
        clock.now += 2 * min
        assertEquals(ChargeState.Ok, dog.tick().state)
    }

    @Test fun repeatedUnpluggedBroadcastsDoNotRestartTheTimer() {
        dog.onBattery(unplugged(71))
        clock.now += 2 * min
        dog.onBattery(unplugged(70))
        clock.now += 2 * min
        assertTrue(dog.onBattery(unplugged(70)).speak)
    }

    @Test fun tickWithoutAnyReadingIsOk() = assertEquals(ChargeState.Ok, dog.tick().state)
}
