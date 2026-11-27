package net.backslasher.shamash.core

enum class ShamashState { IDLE, LISTENING, WORKING, SPEAKING, ERROR }

/** Rough classification so the UI can pick a short Hebrew line. */
enum class ErrorKind { NONE, NO_NETWORK, MICROPHONE, GENERIC }

/** Everything the UI needs to render; the UI holds no other state. */
data class ScreenModel(
    val state: ShamashState = ShamashState.IDLE,
    /** What was heard (link mode: the PC transcript). */
    val transcript: String = "",
    val isFinal: Boolean = false,
    val errorMessage: String? = null,
    val errorKind: ErrorKind = ErrorKind.NONE,
    /** Link mode: the assistant reply text, shown under [transcript] until the next listening starts. */
    val reply: String = "",
)
