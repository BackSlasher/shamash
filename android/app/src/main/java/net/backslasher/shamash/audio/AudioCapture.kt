package net.backslasher.shamash.audio

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import java.io.IOException
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
    @Volatile private var record: AudioRecord? = null
    @Volatile private var running = false

    @SuppressLint("MissingPermission")
    override fun start(): Flow<ByteArray> {
        running = true
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
                record = rec
                rec.startRecording()
                if (rec.recordingState != AudioRecord.RECORDSTATE_RECORDING) {
                    throw IOException("AudioRecord did not start recording")
                }
                val buf = ByteArray(chunkBytes)
                while (running) {
                    var off = 0
                    while (off < chunkBytes && running) {
                        val n = rec.read(buf, off, chunkBytes - off)
                        if (n < 0) {
                            if (!running) break
                            throw IOException("AudioRecord.read error $n")
                        }
                        off += n
                    }
                    if (off > 0) emit(buf.copyOf(off))
                }
            } finally {
                running = false
                record = null
                rec.release()
            }
        }.flowOn(Dispatchers.IO)
    }

    override fun stop() {
        running = false
        try {
            record?.stop()
        } catch (_: IllegalStateException) {
            // already stopped or released
        }
    }
}
