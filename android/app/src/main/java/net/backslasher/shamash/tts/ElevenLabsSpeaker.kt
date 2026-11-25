package net.backslasher.shamash.tts

import android.util.Log
import java.io.IOException
import kotlin.coroutines.coroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import net.backslasher.shamash.config.ShamashConfig
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody

private const val TAG = "Shamash"

class TtsHttpException(val code: Int, detail: String) : RuntimeException("TTS HTTP $code: $detail")

/** Streams `POST /v1/text-to-speech/{voice}/stream` (raw PCM 16 kHz) into a [PcmSink] as it arrives. */
class ElevenLabsSpeaker(
    private val apiKey: String,
    private val config: ShamashConfig,
    private val client: OkHttpClient,
    private val sinkFactory: () -> PcmSink = { AudioTrackSink(TTS_SAMPLE_RATE) },
    private val baseUrl: String = "https://api.elevenlabs.io",
) : Speaker {

    override suspend fun speak(text: String) {
        withContext(Dispatchers.IO) {
          coroutineScope {
            val body = JsonObject(
                mapOf(
                    "text" to JsonPrimitive(text),
                    "model_id" to JsonPrimitive(config.ttsModelId),
                    "language_code" to JsonPrimitive(config.ttsLanguageCode),
                ),
            ).toString().toRequestBody("application/json".toMediaType())
            val request = Request.Builder()
                .url("$baseUrl/v1/text-to-speech/${config.ttsVoiceId}/stream?output_format=pcm_16000")
                .header("xi-api-key", apiKey)
                .post(body)
                .build()
            val call = client.newCall(request)
            val sink = sinkFactory()
            val done = AtomicBoolean(false)
            // Blocking reads cannot see cancellation: a watcher child reacts to it and kills the call and the sound.
            val watcher = launch {
                try {
                    awaitCancellation()
                } catch (c: CancellationException) {
                    if (!done.get()) { sink.stopNow(); call.cancel() }
                    throw c
                }
            }
            val started = System.nanoTime()
            var firstMs = -1L
            var total = 0L
            try {
                call.execute().use { resp ->
                    if (!resp.isSuccessful) {
                        val detail = resp.body?.string()?.take(300).orEmpty()
                        throw TtsHttpException(resp.code, detail)
                    }
                    val src = resp.body!!.source()
                    val buf = ByteArray(8192)
                    var carry = -1 // odd trailing byte of the previous read
                    while (true) {
                        var off = 0
                        if (carry >= 0) { buf[0] = carry.toByte(); off = 1; carry = -1 }
                        val n = src.read(buf, off, buf.size - off)
                        if (n < 0) break
                        var len = off + n
                        if (firstMs < 0 && len > 0) {
                            firstMs = (System.nanoTime() - started) / 1_000_000
                            Log.d(TAG, "tts: first audio after $firstMs ms")
                        }
                        if (len % 2 == 1) { carry = buf[len - 1].toInt() and 0xFF; len-- }
                        if (len > 0) { sink.write(buf, 0, len); total += len }
                    }
                }
                sink.finish()
                Log.d(TAG, "tts: first audio after $firstMs ms, total ${(System.nanoTime() - started) / 1_000_000} ms, bytes $total")
            } catch (e: IOException) {
                // Cancellation closes the call, which surfaces here as an IOException: let the coroutine machinery decide.
                coroutineContext.ensureActive()
                throw e
            } finally {
                done.set(true)
                watcher.cancel()
                sink.release()
            }
          }
        }
    }

    companion object {
        const val TTS_SAMPLE_RATE = 16_000
    }
}
