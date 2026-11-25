package net.backslasher.shamash.tts

import kotlinx.coroutines.delay

/** Speaks text aloud. Suspends until playback finished; cancelling the caller stops the sound at once. */
interface Speaker {
    suspend fun speak(text: String)
}

/** Offline stand-in (-PfakeTranscriber=true and unit tests): just takes time proportional to the text. */
class FakeSpeaker(private val msPerChar: Long = 50) : Speaker {
    override suspend fun speak(text: String) = delay(text.length * msPerChar)
}
