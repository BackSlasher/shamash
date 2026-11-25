package net.backslasher.shamash.pclink

import android.os.SystemClock
import android.util.Log
import java.io.File
import java.nio.ByteBuffer
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import kotlinx.serialization.json.put
import net.backslasher.shamash.audio.AudioCapture
import net.backslasher.shamash.audio.AudioRecordCapture
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import okio.ByteString.Companion.toByteString

private const val TAG = "Shamash"

/** Wire format helpers, kept pure for unit tests. */
object PcLinkFrame {
    const val HEADER_BYTES = 12

    /** 4-byte big-endian uint32 seq, 8-byte big-endian int64 send time (ms), then raw PCM. */
    fun encode(seq: Long, tPhoneMs: Long, pcm: ByteArray): ByteArray =
        ByteBuffer.allocate(HEADER_BYTES + pcm.size)
            .putInt(seq.toInt()) // big-endian by default; low 32 bits = uint32
            .putLong(tPhoneMs)
            .put(pcm)
            .array()

    fun seqOf(frame: ByteArray): Long = ByteBuffer.wrap(frame).int.toLong() and 0xFFFFFFFFL

    fun tPhoneOf(frame: ByteArray): Long = ByteBuffer.wrap(frame).getLong(4)

    fun pcmOf(frame: ByteArray): ByteArray = frame.copyOfRange(HEADER_BYTES, frame.size)
}

data class RttStats(val count: Int, val minMs: Long, val medianMs: Long, val maxMs: Long, val p95Ms: Long) {
    companion object {
        /** Median is the lower-middle element for even counts; p95 is nearest-rank. */
        fun of(rtts: List<Long>): RttStats {
            if (rtts.isEmpty()) return RttStats(0, 0, 0, 0, 0)
            val s = rtts.sorted()
            val rank = kotlin.math.ceil(0.95 * s.size).toInt().coerceIn(1, s.size)
            return RttStats(s.size, s.first(), s[(s.size - 1) / 2], s.last(), s[rank - 1])
        }
    }
}

class PcLinkProbe(
    private val outDir: File,
    private val device: String,
    private val capture: AudioCapture = AudioRecordCapture(),
    private val client: OkHttpClient = OkHttpClient(),
    private val clock: () -> Long = { SystemClock.elapsedRealtime() },
) {
    /** Runs the probe; never throws. Returns the summary JSON, or null on failure. */
    suspend fun run(url: String, seconds: Int): JsonObject? {
        val started = clock()
        val rtts = ConcurrentHashMap<Long, Long>()
        val acks = AtomicInteger()
        val opened = CompletableDeferred<Unit>()
        val summary = CompletableDeferred<JsonObject>()
        val failure = CompletableDeferred<Throwable>()
        var ws: WebSocket? = null
        try {
            val listener = object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    opened.complete(Unit)
                }

                override fun onMessage(webSocket: WebSocket, text: String) {
                    val now = clock()
                    try {
                        val o = Json.parseToJsonElement(text).jsonObject
                        when (o["type"]?.jsonPrimitive?.content) {
                            "ack" -> {
                                rtts[o.getValue("seq").jsonPrimitive.long] = now - o.getValue("tPhone").jsonPrimitive.long
                                acks.incrementAndGet()
                            }
                            "summary" -> summary.complete(o)
                        }
                    } catch (e: Exception) {
                        Log.e(TAG, "pclink: bad server message: ${e.message}")
                    }
                }

                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    failure.complete(t)
                    opened.completeExceptionally(t)
                    summary.completeExceptionally(t)
                }

                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                    summary.completeExceptionally(java.io.IOException("closed $code before summary"))
                }
            }
            ws = client.newWebSocket(Request.Builder().url(url).build(), listener)
            if (withTimeoutOrNull(10_000) { opened.await() } == null) throw java.io.IOException("connect timeout")
            ws.send(buildJsonObject {
                put("type", "hello"); put("sampleRate", 16000); put("seconds", seconds); put("device", device)
            }.toString())

            var frames = 0
            var bytes = 0L
            val deadline = clock() + seconds * 1000L
            capture.start().collect { chunk ->
                val t = clock()
                if (!ws.send(PcLinkFrame.encode(frames.toLong(), t, chunk).toByteString())) {
                    throw java.io.IOException("send queue rejected frame $frames")
                }
                frames++
                bytes += chunk.size
                if (t >= deadline) capture.stop()
            }
            ws.send("""{"type":"end"}""")
            val srv = withTimeoutOrNull(10_000) { summary.await() } ?: throw java.io.IOException("summary timeout")
            val stats = RttStats.of(rtts.values.toList())
            val total = clock() - started
            val result = buildJsonObject {
                put("url", url); put("device", device); put("framesSent", frames); put("bytes", bytes)
                put("acks", acks.get()); put("rttCount", stats.count)
                put("rttMinMs", stats.minMs); put("rttMedianMs", stats.medianMs)
                put("rttMaxMs", stats.maxMs); put("rttP95Ms", stats.p95Ms)
                put("serverSummary", srv); put("totalMs", total)
            }
            Log.i(TAG, "pclink: frames=$frames bytes=$bytes acks=${acks.get()} " +
                "rtt_ms(min/med/max/p95)=${stats.minMs}/${stats.medianMs}/${stats.maxMs}/${stats.p95Ms} " +
                "server=$srv total_ms=$total")
            writeJson(result.toString())
            return result
        } catch (e: Exception) {
            val cause = failure.takeIf { it.isCompleted }?.await() ?: e
            Log.e(TAG, "pclink: failed: ${cause::class.java.simpleName}: ${cause.message}")
            return null
        } finally {
            capture.stop()
            ws?.close(1000, null)
        }
    }

    private fun writeJson(text: String) {
        try {
            outDir.mkdirs()
            File(outDir, "pclink-${System.currentTimeMillis()}.json").writeText(text)
        } catch (e: Exception) {
            Log.e(TAG, "pclink: could not write json: ${e.message}")
        }
    }
}
