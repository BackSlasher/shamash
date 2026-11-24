# Shamash: proof-of-concept brief

This file briefs the coding agent that will build the first prototype. Read it fully before writing code. Where it says "verify", check the current official documentation instead of relying on memory or on this file.

## 1. What Shamash is

Shamash (שמש, the helper candle that lights the others) is a voice assistant for one specific person: an elderly academic who still works daily on a Windows PC, editing documents, publications and lectures.

- His eyesight is poor. He uses a very large TV as a monitor.
- His hearing is reduced. He wears hearing aids that connect to a phone over Bluetooth.
- He speaks to the assistant in Hebrew. The assistant must understand and answer in Hebrew.
- He is not technical and will never see a terminal, a settings screen or an error dialog.

The finished product is one big button with a coloured glow. He presses it, the glow changes to show it is listening, and he talks. The assistant (Claude, running on his PC) does the work: organising and deduplicating document folders, finding a document by its content, translating documents, helping with email. It answers by voice. Pressing the button while it is working interrupts it.

The button is an old Pixel 7 running a dedicated kiosk app. The whole screen is the button.

## 2. Where this POC fits

The eventual system has two halves:

1. **Phone (Kotlin kiosk app):** button, glow, microphone, speaker. Deliberately dumb.
2. **PC service:** speech recognition, the Claude agent, speech synthesis. Talks to the phone over the local network.

**This POC is the phone half only, standing alone.** There is no PC service, no Claude and no spoken reply yet. To make the phone useful by itself, the POC calls a cloud speech-to-text API directly and shows the recognised words on screen.

The POC exists to answer three questions:

1. Does Hebrew speech recognition work well enough on his real voice, from where the phone will really sit?
2. Does the big-button interaction feel right to him?
3. Is the kiosk setup robust (survives reboot, cannot be escaped by accident)?

The most valuable output is **a folder of real recordings** made from the phone's real position. Those recordings will be replayed through other speech engines later. Treat saving them as a core feature, not as debug logging.

## 3. Hardware facts

- Google Pixel 7, to be factory reset and dedicated to this. Verify the Android version it is on and set the minimum SDK accordingly (Android 13 / API 33 or later is a safe floor).
- The earpiece speaker (top, used for calls) is broken. The bottom speaker works. Route all audio to the bottom speaker or to connected Bluetooth hearing aids, never the earpiece.
- The vibration motor may be dead. Call it, but never rely on it.
- The phone will sit on a stand on his desk, permanently on USB power, within arm's reach.

## 4. What the POC does

### 4.1 Interaction

- The entire screen is one touch target. There are no other controls.
- **Tap to start listening, tap again to stop.** (This is a decision made on the owner's behalf: holding a button for a long sentence is tiring for an elderly user. Keep the trigger mode behind one constant so hold-to-talk can be tried later.)
- Also stop automatically after a maximum duration (suggest 120 seconds) so a forgotten session cannot run forever.
- Play a short, distinct sound on start and on stop. Also trigger a haptic pulse.

### 4.2 States and colours

Each state must differ in brightness or motion as well as hue, because ageing eyes lose colour contrast.

| State | Look | When |
|---|---|---|
| Idle | Dim blue gradient, still | Waiting for a tap |
| Listening | Bright red gradient, slow pulse | Recording and streaming |
| Working | Amber gradient, slow sweep | After stop, waiting for the final transcript |
| Speaking | Green gradient, still | Reserved for later; define it, do not use it yet |
| Error / offline | Grey, still | No network, API failure, microphone failure |

- A dead or offline button must never look like a listening one.
- From Error, a tap retries and returns to Idle if the problem has cleared.
- Idle must not burn in the OLED: keep it dim and drift the gradient slowly.

### 4.3 Words on screen

- While listening, show the live partial transcript.
- After stop, replace it with the final transcript.
- Hebrew, right-to-left, very large type, high contrast against the state colour. Handle mixed Hebrew and English correctly (he will say English terms and names).
- Keep the last final transcript visible in Idle until the next tap.

### 4.4 Speech recognition

Provider for the POC: **ElevenLabs Scribe**.

- **Live words:** Scribe v2 Realtime over its streaming API, fed with microphone audio as it is captured.
- **Final text:** on stop, also send the complete saved clip to batch Scribe v2 and use that result as the final transcript. Batch is more accurate than streaming, and the clip is complete anyway.
- Set the language explicitly to Hebrew; do not rely on auto-detection.
- **Verify in the current ElevenLabs docs:** endpoints, authentication, model identifiers, the Hebrew language code, accepted audio formats, and the streaming message protocol. Do not guess these.
- Put recognition behind a small interface (for example `Transcriber` with a streaming session and a batch call), so another provider or the future PC link can replace it without touching the UI or the state machine.

Optional, only after everything else works: probe Android's built-in on-device recogniser (`SpeechRecognizer.createOnDeviceSpeechRecognizer` with `checkRecognitionSupport` for `he-IL` and `iw-IL`) and log whether Hebrew is offered. It probably is not. Do not build on it.

### 4.5 Recordings (core feature)

For every listening session, save:

- The raw audio as a WAV file: 16 kHz, mono, 16-bit PCM. Capture with the `VOICE_RECOGNITION` audio source.
- A JSON file beside it containing: timestamp, duration, the sequence of live partials, the final realtime text, the batch text, latency measurements (time to first partial, time from stop to final), app version, and any error.

Store them in the app's external files directory so they can be pulled with `adb pull` without root. Name files by timestamp. Never delete them automatically.

### 4.6 Kiosk behaviour

- The app registers as a launcher (HOME activity), so it starts on boot and after any crash.
- It runs in lock task mode with system UI hidden, set up through device-owner provisioning: factory reset, add no Google account, then `adb shell dpm set-device-owner` for the app's admin receiver. Verify the exact command and requirements.
- Screen stays on while the app is in front. Disable battery optimisation for the app.
- **Escape hatch for development:** a kiosk you cannot leave is a problem. Mark debug builds as test-only so device owner can be removed over adb (verify this mechanism), and document the exact removal command in the README. Keep adb enabled.
- Build order matters: get the app working as an ordinary app first, and add launcher and lock task mode last.

### 4.7 API key

- The ElevenLabs key is compiled into the app for this POC. That is accepted for a single private device.
- Read it from `local.properties` into `BuildConfig`. It must never be committed. Add the ignore rule before the first commit.
- In the real system the key will live on the PC and the phone will hold no secrets. Do not build anything that assumes the phone keeps the key long term.

## 5. Explicitly out of scope

- The PC service, the Claude agent, any tool use.
- Text-to-speech and spoken replies.
- Network pairing with the PC, discovery, TLS pinning.
- Bluetooth hearing-aid handling beyond using the default audio route.
- Settings screens, onboarding, multiple users, localisation of UI chrome (there is no chrome).
- Play Store distribution.

## 6. Technical guidance

These are defaults, not mandates. Deviate with a stated reason.

- Kotlin, single-module Android app, Jetpack Compose for the one screen.
- Coroutines and Flow. One explicit state machine owns the state in section 4.2; the UI only renders it.
- OkHttp for HTTP and WebSocket; kotlinx.serialization for JSON.
- `AudioRecord` for capture. One capture stream fans out to both the WAV writer and the streaming transcriber.
- A foreground service of type microphone is not needed while the activity is always in front; add one only if capture proves unreliable.
- Keep three seams clean, because each will be replaced later: audio capture, transcription, and state rendering.
- Request the microphone permission on first run. In kiosk mode, pre-grant it through the device-owner API so he never sees a dialog.

## 7. Done means

1. After a reboot the phone comes up showing the Idle button with no user action.
2. Tap: start sound, red pulsing screen, Hebrew words appear as he speaks.
3. Tap again: stop sound, amber, then the final Hebrew transcript appears and the screen returns to Idle.
4. Each session leaves a playable WAV and a complete JSON file that `adb pull` can retrieve.
5. With Wi-Fi off, a tap leads to the grey Error state. The audio is still saved. Turning Wi-Fi back on and tapping recovers.
6. No swipe, button or gesture leaves the app in kiosk mode. The documented adb command does.
7. The API key appears nowhere in the repository history.
8. A README covers: build, provisioning a fresh phone step by step, pulling recordings, and removing device owner.

## 8. How to work

- Start by verifying the ElevenLabs API details and the device-owner provisioning steps, and write what you found at the top of the README.
- Build in this order: plain app with button and states, audio capture and WAV saving, batch transcription, realtime transcription, sounds and polish, launcher and kiosk mode.
- Commit at each working step.
- When something in this brief turns out to be wrong or impossible, say so and propose the alternative. Do not work around it silently.
- Questions that only the owner can answer (for example, tap versus hold, text size on the real device) should be collected and asked together, not guessed.

## 9. What comes after (context only, do not build)

- The phone stops calling ElevenLabs and instead streams audio to a service on the PC over an authenticated, encrypted local connection, paired once by QR code.
- The PC runs speech recognition, drives Claude through the Claude Agent SDK with strict permissions (no deletes, confirmation before sending email), and sends synthesised Hebrew speech back for the phone to play.
- A second tap during Working or Speaking interrupts the assistant.
- The Speaking state and the audio playback path on the phone come into use then.
