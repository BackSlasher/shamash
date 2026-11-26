package net.backslasher.shamash.config

import net.backslasher.shamash.BuildConfig

enum class TriggerMode { TAP_TOGGLE, HOLD }

/** Plain data class so a later task can load overrides from a file. */
data class ShamashConfig(
    val triggerMode: TriggerMode = TriggerMode.TAP_TOGGLE,
    val maxListeningMs: Long = 120_000L,
    /**
     * Sessions shorter than this (a double tap) skip the batch upload: the WAV has no usable audio
     * and Scribe answers HTTP 400 "File is corrupted" for a 0.1 s file (seen in the field 2026-11-25).
     */
    val minBatchAudioMs: Long = 500L,
    val connectTimeoutMs: Long = 8_000L,
    /** Budget for `session.finish()` (final commit and committed_transcript). */
    val realtimeFinishTimeoutMs: Long = 10_000L,
    /** Budget for the batch upload (up to ~4 MB) plus transcription. */
    val batchTimeoutMs: Long = 90_000L,
    val sampleRate: Int = 16_000,
    val languageCode: String = "heb",
    val batchModelId: String = "scribe_v3",
    val realtimeModelId: String = "scribe_v3_live",
    /** TTS (verified 2026-11-25): only eleven_v3 / eleven_v4 / eleven_v4_turbo accept language_code "he". */
    val ttsModelId: String = "eleven_v4_turbo",
    /** Premade "George" (from the docs sample), verified to speak Hebrew with the project key. */
    val ttsVoiceId: String = "JBFqnCBsd6RMkjVDRZzb",
    val ttsLanguageCode: String = "he",
    val appVersion: String = "dev",
    /**
     * True: full kiosk (lock task, status bar disabled; Quick Settings unreachable, so Wi-Fi can only
     * change over adb or in maintenance mode). False (owner decision 2026-11-25): regular app that is
     * still the HOME launcher when device owner, with the status bar and Quick Settings available so
     * the owner can join a new Wi-Fi anywhere.
     */
    val lockTask: Boolean = false,
    /** True for offline testing (-PfakeTranscriber=true): uses FakeTranscriber instead of ElevenLabs. */
    val useFakeTranscriber: Boolean = BuildConfig.FAKE_TRANSCRIBER,
    /** Non-null (-PpcLinkUrl=ws://...): link mode, the phone talks to the PC instead of ElevenLabs. */
    val pcLinkUrl: String? = BuildConfig.PC_LINK_URL.takeIf { it.isNotBlank() },
    /** Link mode: budget for the whole reply (text plus audio playback) after the transcript. */
    val linkReplyTimeoutMs: Long = 60_000L,
) {
    companion object {
        val DEFAULT = ShamashConfig(appVersion = BuildConfig.VERSION_NAME)
    }
}
