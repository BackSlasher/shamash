package net.backslasher.shamash.audio

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import java.io.IOException
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.flowOn

interface AudioCapture {
    /** Cold flow of ~100 ms chunks of 16-bit little-endian mono PCM. Completes after [stop]. */
    fun start(): Flow<ByteArray>

    fun stop()
}

class AudioRecordCapture(
    private val sampleRate: Int = 16_000,
    private val chunkMs: Int = 100,
) : AudioCapture {
    /** Per-start state, so a late stop() or a finishing old flow can never affect a newer start(). */
    private class Run {
        val running = AtomicBoolean(true)
        @Volatile var record: AudioRecord? = null
    }

    @Volatile private var current: Run? = null

    @SuppressLint("MissingPermission")
    override fun start(): Flow<ByteArray> {
        val run = Run() // created now, so a stop() before the read loop begins still wins
        current = run
        return flow {
            val minBuffer = AudioRecord.getMinBufferSize(
                sampleRate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT,
            )
            if (minBuffer <= 0) throw IOException("AudioRecord.getMinBufferSize failed: $minBuffer")
            val chunkBytes = sampleRate * 2 * chunkMs / 1000
            val rec = AudioRecord(
                MediaRecorder.AudioSource.VOICE_RECOGNITION,
                sampleRate,
                AudioFormat.CHANNEL_IN_MONO,
                AudioFormat.ENCODING_PCM_16BIT,
                maxOf(minBuffer * 2, chunkBytes * 4),
            )
            try {
                if (rec.state != AudioRecord.STATE_INITIALIZED) {
                    throw IOException("AudioRecord failed to initialise (microphone busy or no permission)")
                }
                run.record = rec
                rec.startRecording()
                if (rec.recordingState != AudioRecord.RECORDSTATE_RECORDING) {
                    throw IOException("AudioRecord did not start recording")
                }
                val buf = ByteArray(chunkBytes)
                while (run.running.get()) {
                    var off = 0
                    while (off < chunkBytes && run.running.get()) {
                        val n = rec.read(buf, off, chunkBytes - off)
                        if (n < 0) {
                            if (!run.running.get()) break
                            throw IOException("AudioRecord.read error $n")
                        }
                        off += n
                    }
                    if (off > 0) emit(buf.copyOf(off))
                }
            } finally {
                run.running.set(false)
                run.record = null
                if (current === run) current = null
                rec.release()
            }
        }.flowOn(Dispatchers.IO)
    }

    override fun stop() {
        val run = current ?: return
        run.running.set(false)
        try {
            run.record?.stop()
        } catch (_: IllegalStateException) {
            // already stopped or released
        }
    }
}
