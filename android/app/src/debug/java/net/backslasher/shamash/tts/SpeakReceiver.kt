package net.backslasher.shamash.tts

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Handler
import android.os.Looper
import android.util.Log
import net.backslasher.shamash.DebugHooks

/** Debug-only trigger: adb shell am broadcast -a net.backslasher.shamash.SPEAK --es text "..." -p net.backslasher.shamash */
class SpeakReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val text = intent.getStringExtra("text")
        val hook = DebugHooks.speak
        if (text.isNullOrBlank() || hook == null) {
            Log.w("Shamash", "tts: SPEAK ignored (text blank or app not open)")
            return
        }
        Log.i("Shamash", "tts: SPEAK received, ${text.length} chars")
        Handler(Looper.getMainLooper()).post { hook(text) }
    }
}
