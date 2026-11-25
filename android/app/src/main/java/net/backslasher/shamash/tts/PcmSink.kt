package net.backslasher.shamash.tts

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack

/** Where decoded PCM (16-bit mono little-endian) goes. Keeps Android audio out of unit tests. */
interface PcmSink {
    /** Blocks until the bytes are queued. */
    fun write(buf: ByteArray, off: Int, len: Int)

    /** Normal end: let queued audio play out, then stop. */
    fun finish()

    /** Cancellation, callable from another thread: silence immediately and make blocked calls return. */
    fun stopNow()

    fun release()
}

class AudioTrackSink(private val sampleRate: Int) : PcmSink {
    @Volatile private var stopped = false
    private var writtenFrames = 0L
    private val track: AudioTrack

    init {
        val min = AudioTrack.getMinBufferSize(sampleRate, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT)
        track = AudioTrack.Builder()
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
                    .build(),
            )
            .setAudioFormat(
                AudioFormat.Builder()
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(sampleRate)
                    .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                    .build(),
            )
            .setTransferMode(AudioTrack.MODE_STREAM)
            .setBufferSizeInBytes(maxOf(min * 4, sampleRate)) // at least 0.5 s
            .build()
        track.play()
    }

    override fun write(buf: ByteArray, off: Int, len: Int) {
        var o = off
        var left = len
        while (left > 0 && !stopped) {
            val n = track.write(buf, o, left)
            if (n <= 0) break
            o += n
            left -= n
            writtenFrames += n / 2
        }
    }

    override fun finish() {
        if (stopped) return
        track.stop() // streaming mode: plays what is queued, then stops
        val deadline = System.currentTimeMillis() + 60_000
        while (!stopped && track.playbackHeadPosition < writtenFrames && System.currentTimeMillis() < deadline) {
            Thread.sleep(20)
        }
    }

    override fun stopNow() {
        stopped = true
        try {
            track.pause()
            track.flush()
        } catch (_: IllegalStateException) {
        }
    }

    override fun release() {
        stopped = true
        try {
            track.release()
        } catch (_: Exception) {
        }
    }
}
