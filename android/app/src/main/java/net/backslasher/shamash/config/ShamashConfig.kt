package net.backslasher.shamash.config

import net.backslasher.shamash.BuildConfig

enum class TriggerMode { TAP_TOGGLE, HOLD }

/** Plain data class so a later task can load overrides from a file. */
data class ShamashConfig(
    val triggerMode: TriggerMode = TriggerMode.TAP_TOGGLE,
    val maxListeningMs: Long = 120_000L,
    val connectTimeoutMs: Long = 8_000L,
    val finalTimeoutMs: Long = 20_000L,
    val sampleRate: Int = 16_000,
    val languageCode: String = "heb",
    val batchModelId: String = "scribe_v3",
    val realtimeModelId: String = "scribe_v3_live",
    val appVersion: String = "dev",
) {
    companion object {
        val DEFAULT = ShamashConfig(appVersion = BuildConfig.VERSION_NAME)
    }
}
