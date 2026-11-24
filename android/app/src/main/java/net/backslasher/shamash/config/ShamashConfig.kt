package net.backslasher.shamash.config

import net.backslasher.shamash.BuildConfig

enum class TriggerMode { TAP_TOGGLE, HOLD }

/** Plain data class so a later task can load overrides from a file. */
data class ShamashConfig(
    val triggerMode: TriggerMode = TriggerMode.TAP_TOGGLE,
    val maxListeningMs: Long = 120_000L,
    val connectTimeoutMs: Long = 8_000L,
    /** Budget for `session.finish()` (final commit and committed_transcript). */
    val realtimeFinishTimeoutMs: Long = 10_000L,
    /** Budget for the batch upload (up to ~4 MB) plus transcription. */
    val batchTimeoutMs: Long = 90_000L,
    val sampleRate: Int = 16_000,
    val languageCode: String = "heb",
    val batchModelId: String = "scribe_v3",
    val realtimeModelId: String = "scribe_v3_live",
    val appVersion: String = "dev",
    /** True for offline testing (-PfakeTranscriber=true): uses FakeTranscriber instead of ElevenLabs. */
    val useFakeTranscriber: Boolean = BuildConfig.FAKE_TRANSCRIBER,
) {
    companion object {
        val DEFAULT = ShamashConfig(appVersion = BuildConfig.VERSION_NAME)
    }
}
