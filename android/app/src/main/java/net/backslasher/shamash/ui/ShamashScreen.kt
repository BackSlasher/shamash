package net.backslasher.shamash.ui

import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.runtime.State
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import net.backslasher.shamash.BuildConfig
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.text.BasicText
import androidx.compose.foundation.text.TextAutoSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shadow
import androidx.compose.ui.graphics.TileMode
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.input.pointer.pointerInput
import kotlinx.coroutines.withTimeoutOrNull
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDirection
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlin.math.cos
import kotlin.math.max
import kotlin.math.sin
import net.backslasher.shamash.core.ErrorKind
import net.backslasher.shamash.core.ScreenModel
import net.backslasher.shamash.core.ShamashState

private const val MAINTENANCE_HOLD_MS = 5_000L
private const val TWO_PI = (2 * Math.PI).toFloat()

/** The whole screen is one touch target. Renders [model]; owns no state besides animation phases. */
@Composable
fun ShamashScreen(
    model: ScreenModel,
    onTap: () -> Unit,
    onPressDown: () -> Unit = {},
    onPressUp: () -> Unit = {},
    maintenance: Boolean = false,
    onMaintenanceUnlock: () -> Unit = {},
    /** Microphone level 0..1; read only in the draw phase of the listening meter. */
    level: () -> Float = { 0f },
) {
    val unlock by rememberUpdatedState(onMaintenanceUnlock)
    val tap by rememberUpdatedState(onTap)
    val down by rememberUpdatedState(onPressDown)
    val up by rememberUpdatedState(onPressUp)

    val transition = rememberInfiniteTransition(label = "bg")
    // Idle drift: very slow, seamless loop (OLED burn-in protection).
    val drift: State<Float> = transition.animateFloat(
        0f, 1f, infiniteRepeatable(tween(150_000, easing = LinearEasing)), label = "drift",
    )
    // Listening pulse: ~1.2 s per half cycle.
    val pulse: State<Float> = transition.animateFloat(
        0f, 1f, infiniteRepeatable(tween(1_200, easing = FastOutSlowInEasing), RepeatMode.Reverse), label = "pulse",
    )
    // Working sweep: slow rotation of the gradient axis.
    val sweep: State<Float> = transition.animateFloat(
        0f, 1f, infiniteRepeatable(tween(5_000, easing = LinearEasing)), label = "sweep",
    )

    Box(
        Modifier
            .fillMaxSize()
            .background(Color.Black)
            .drawBehind { drawBackground(model.state, drift.value, pulse.value, sweep.value) }
            .pointerInput(Unit) {
                awaitEachGesture {
                    val first = awaitFirstDown()
                    down()
                    // Maintenance hatch: 3+ fingers held continuously for MAINTENANCE_HOLD_MS.
                    // Such a gesture is never a tap, and its events are consumed once it fires.
                    var multiTouch = false
                    // A swipe is not a tap either: with the status bar enabled, the swipe that opens
                    // the Quick Settings drawer also reaches us and must not start a session.
                    var moved = false
                    val slop = viewConfiguration.touchSlop
                    var fired = false
                    var holdStart: Long? = null
                    var last: androidx.compose.ui.input.pointer.PointerEvent
                    while (true) {
                        val remaining = holdStart?.let { MAINTENANCE_HOLD_MS - (System.currentTimeMillis() - it) }
                        val event = if (remaining != null && !fired) {
                            withTimeoutOrNull(remaining.coerceAtLeast(1)) { awaitPointerEvent() }
                        } else {
                            awaitPointerEvent()
                        }
                        if (event == null) { // timeout: fingers still down, nothing changed
                            fired = true
                            unlock()
                            continue
                        }
                        last = event
                        val pressed = event.changes.count { it.pressed }
                        if (pressed >= 2) multiTouch = true
                        event.changes.firstOrNull { it.id == first.id }?.let {
                            if ((it.position - first.position).getDistance() > slop) moved = true
                        }
                        if (pressed >= 3) { if (holdStart == null) holdStart = System.currentTimeMillis() } else holdStart = null
                        if (fired) event.changes.forEach { it.consume() }
                        if (pressed == 0) break
                    }
                    up()
                    if (!multiTouch && !moved && !last.changes.any { it.isConsumed }) tap()
                }
            },
        contentAlignment = Alignment.Center,
    ) {
        if (maintenance) {
            BasicText(
                "maintenance mode \u2013 kiosk unlocked",
                modifier = Modifier.align(Alignment.BottomCenter).padding(16.dp),
                style = textStyle(14).copy(textDirection = TextDirection.Ltr, textAlign = TextAlign.Center),
            )
        }
        CompositionLocalProvider(LocalLayoutDirection provides LayoutDirection.Rtl) {
            if (model.state == ShamashState.ERROR) {
                ErrorContent(model)
            } else if (model.reply.isEmpty() && (model.state == ShamashState.IDLE || model.state == ShamashState.LISTENING)) {
              if (model.transcript.isNotEmpty()) {
                BasicText(
                    text = model.transcript,
                    modifier = Modifier.fillMaxSize().padding(horizontal = 32.dp, vertical = 48.dp),
                    style = textStyle(64),
                    autoSize = TextAutoSize.StepBased(minFontSize = 24.sp, maxFontSize = 64.sp, stepSize = 2.sp),
                )
              }
            } else {
                HeardAndReply(model)
            }
            if (model.state == ShamashState.LISTENING) {
                LevelMeter(level)
                Hint("לחץ שוב כשסיימת")
            } else if (model.state == ShamashState.WORKING) {
                Hint("רגע...")
            }
        }
    }
}

/** What he said (28 sp) and under it the reply (32 sp), top two thirds, scrolling when long. */
@Composable
private fun BoxScope.HeardAndReply(model: ScreenModel) {
    Column(
        Modifier
            .fillMaxWidth()
            .fillMaxHeight(0.66f)
            .align(Alignment.TopCenter)
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 32.dp, vertical = 48.dp),
        horizontalAlignment = Alignment.Start, // right edge under RTL
    ) {
        if (model.transcript.isNotEmpty()) BasicText(model.transcript, modifier = Modifier.fillMaxWidth(), style = textStyle(28))
        if (model.reply.isNotEmpty()) {
            BasicText(model.reply, modifier = Modifier.fillMaxWidth().padding(top = 24.dp), style = textStyle(32))
        }
    }
}

/** Large hint in the bottom third. */
@Composable
private fun BoxScope.Hint(text: String) {
    Box(Modifier.align(Alignment.BottomCenter).fillMaxWidth().fillMaxHeight(1f / 3f), contentAlignment = Alignment.Center) {
        BasicText(text, style = textStyle(40).copy(textAlign = TextAlign.Center))
    }
}

/** A ring that swells with the microphone level. Composed only while listening, so it costs nothing otherwise. */
@Composable
private fun LevelMeter(level: () -> Float) {
    val shown by animateFloatAsState(level(), tween(100, easing = LinearEasing), label = "level")
    Canvas(Modifier.fillMaxSize()) {
        val l = shown.coerceIn(0f, 1f)
        val maxR = size.minDimension * 0.42f
        val r = maxR * (0.35f + 0.65f * l)
        drawCircle(Color.White.copy(alpha = 0.25f + 0.5f * l), r, Offset(size.width / 2, size.height / 2), style = Stroke(width = 14f + 26f * l))
    }
}

@Composable
private fun ErrorContent(model: ScreenModel) {
    val hebrew = when (model.errorKind) {
        ErrorKind.NO_NETWORK -> "אין חיבור"
        ErrorKind.MICROPHONE -> "אין גישה למיקרופון"
        else -> "משהו השתבש"
    }
    Column(Modifier.padding(horizontal = 32.dp), horizontalAlignment = Alignment.End) {
        BasicText(hebrew, style = textStyle(64))
        // Technical English detail for debugging only; the user never sees it in release builds.
        model.errorMessage?.takeIf { BuildConfig.DEBUG }?.let {
            BasicText(
                it,
                modifier = Modifier.padding(top = 24.dp),
                style = textStyle(18).copy(color = Color(0xFFE0E0E0), textDirection = TextDirection.Ltr, textAlign = TextAlign.Left),
            )
        }
    }
}

private fun textStyle(size: Int) = TextStyle(
    color = Color.White,
    fontSize = size.sp,
    lineHeight = (size * 1.2f).sp,
    textDirection = TextDirection.ContentOrRtl,
    textAlign = TextAlign.Right,
    shadow = Shadow(Color(0xCC000000), Offset(0f, 3f), 10f),
)

private fun androidx.compose.ui.graphics.drawscope.DrawScope.drawBackground(
    state: ShamashState,
    drift: Float,
    pulse: Float,
    sweep: Float,
) {
    val w = size.width
    val h = size.height
    val reach = max(w, h)
    when (state) {
        ShamashState.IDLE -> {
            // dim blue, centre drifting on a slow ellipse
            val a = drift * TWO_PI
            val c = Offset(w * (0.5f + 0.18f * cos(a)), h * (0.5f + 0.18f * sin(a)))
            drawRect(Brush.radialGradient(listOf(Color(0xFF16337A), Color(0xFF060F2C)), c, reach * 0.9f))
        }
        ShamashState.LISTENING -> {
            // bright red, pulsing brightness
            val inner = lerp(Color(0xFFC62828), Color(0xFFFF4B3E), pulse)
            val outer = lerp(Color(0xFF6B0F0F), Color(0xFFB71C1C), pulse)
            drawRect(Brush.radialGradient(listOf(inner, outer), Offset(w / 2, h / 2), reach * 0.8f))
        }
        ShamashState.WORKING -> {
            // amber, gradient axis sweeping around the centre
            val a = sweep * TWO_PI
            val d = Offset(cos(a), sin(a)) * (reach / 2f)
            val c = Offset(w / 2, h / 2)
            drawRect(
                Brush.linearGradient(
                    listOf(Color(0xFF5E3700), Color(0xFFB36B00), Color(0xFF5E3700)),
                    start = c - d, end = c + d, tileMode = TileMode.Mirror,
                ),
                size = Size(w, h),
            )
        }
        ShamashState.SPEAKING ->
            drawRect(Brush.radialGradient(listOf(Color(0xFF2E9E4F), Color(0xFF0E4A22)), Offset(w / 2, h / 2), reach * 0.8f))
        ShamashState.ERROR ->
            drawRect(Brush.radialGradient(listOf(Color(0xFF555555), Color(0xFF242424)), Offset(w / 2, h / 2), reach * 0.8f))
    }
}

private operator fun Offset.times(f: Float) = Offset(x * f, y * f)
