# Shamash

Voice-assistant button for one person. See `brief.md` for the full brief. This repository holds the phone half (Android kiosk app). The PC half comes later.

## Verified facts (checked against official docs on 2026-11-24)

These were read from the live documentation, not from memory. Re-verify before relying on them in a year.

### ElevenLabs speech-to-text

Source: `elevenlabs.io/docs/api-reference/speech-to-text/convert`, `.../v-1-speech-to-text-realtime`, `.../docs/overview/models`, `.../docs/capabilities/speech-to-text`.

- **Auth:** header `xi-api-key: <key>` on both REST and the WebSocket handshake. Alternative for untrusted clients: a single-use `token` query parameter. We use the header for the POC because the key is compiled in anyway.
- **Hebrew:** the docs' language list writes `heb` (ISO 639-3). The parameter accepts 639-1 or 639-3. We send `heb`. Scribe v2 lists Hebrew in the "Good (10-20% WER)" tier.
- **Models:** `scribe_v3` is now the batch model ("successor to scribe_v2"); `scribe_v3_live` is the realtime model ("successor to scribe_v2_realtime"). The brief names v2; both v2 ids still work. The app defaults to v3 and keeps the ids in one config object. Realtime default if omitted is `scribe_v2_realtime`, so always pass `model_id`.

**Batch:** `POST https://api.elevenlabs.io/v1/speech-to-text`, `multipart/form-data`.

| field | value |
|---|---|
| `model_id` | `scribe_v3` (required) |
| `file` | the audio; WAV is accepted; minimum 100 ms, max 5 GB |
| `language_code` | `heb` |
| `file_format` | `pcm_s16le_16` for raw 16-bit 16 kHz mono LE (lower latency), or `other` (default) for a WAV container |
| `timestamps_granularity` | `word` / `character` / `none` |
| `tag_audio_events`, `diarize` | booleans, default off |

Response: `{ "language_code", "language_probability", "text", "words": [ { "text", "type": "word|spacing|audio_event", "start", "end", "logprob", "speaker_id" } ] }`. Errors come back as `422` with `detail`.

**Realtime:** `wss://api.elevenlabs.io/v1/speech-to-text/realtime` with query parameters:

| param | value |
|---|---|
| `model_id` | `scribe_v3_live` |
| `language_code` | `heb` |
| `audio_format` | `pcm_16000` (16-bit LE mono, the recommended rate; default) |
| `commit_strategy` | `manual` (default) / `vad` / `turn_prediction` |
| `include_timestamps` | `true` to also get `committed_transcript_with_timestamps` |
| `keepalive_interval_ms` | 500-10000, off by default |

Client sends only one message type:

```json
{"message_type":"input_audio_chunk","audio_base_64":"<base64 PCM>","commit":false,"sample_rate":16000}
```

Send 0.1-1 s per chunk. To finish: send `{"message_type":"input_audio_chunk","audio_base_64":"","commit":true,"sample_rate":16000}` and wait for `committed_transcript`. With manual commits the server auto-commits after about 36 s of audio, so commit every 20-30 s on long utterances. The server closes the socket after 15 s without any client message.

Server messages, all keyed by `message_type`:

- `session_started` `{ "session_id", "config": {...} }`
- `partial_transcript` `{ "text" }`
- `committed_transcript` `{ "text" }`
- `committed_transcript_with_timestamps` `{ "text", "language_code", "words": [...] }` (only with `include_timestamps`)
- `warning` `{ "warning" }`
- Errors: `{ "message_type": "<type>", "error": "<text>" }` where type is one of `error`, `auth_error`, `quota_exceeded`, `commit_throttled`, `unaccepted_terms`, `rate_limited`, `queue_overflow`, `resource_exhausted`, `session_time_limit_exceeded`, `input_error`, `invalid_request`, `chunk_size_exceeded`, `insufficient_audio_activity`, `transcriber_error`. An error is sent before the socket closes.

Observed live (2026-11-25):

- **Partials are cumulative.** Each `partial_transcript` carries the full text of the current segment so far: replace, never append. The app concatenates committed segments (space-separated) in front of the running partial.
- **The client must close the socket.** The server does not close after `committed_transcript`; the app closes with code 1000. Scribe returns `session_started` about 0.3 s after connect and the first partial about 0.5 s after audio starts.

Realtime concurrency on the Free plan is 6 sessions; irrelevant for one phone.

### Android device owner and kiosk

Sources: `developer.android.com/work/dpc/dedicated-devices/{lock-task-mode,cookbook}`, `.../reference/android/app/admin/DevicePolicyManager`, `.../reference/android/R.attr#lockTaskMode`, `.../guide/topics/manifest/application-element`.

- **Set device owner:** factory reset, skip adding any Google account, enable developer options and USB debugging, install the APK, then
  `adb shell dpm set-device-owner net.backslasher.shamash/.kiosk.ShamashAdminReceiver`.
  Precondition in the docs: no accounts on the device.
- **Remove device owner (development):** `android:testOnly="true"` marks an APK as installable only via adb (`adb install -t`). `adb shell dpm remove-active-admin <component>` removes an active admin, including device owner, only for a `testOnly` app. The dpm help text on the device is the authority for this; the developer site does not document the command. Debug builds set `testOnly`; release builds do not, so a release build can only be removed by factory reset (or by the app itself calling `clearDeviceOwnerApp`, which the docs mark test-only).
- **Lock task:** device owner calls `setLockTaskPackages(admin, arrayOf(pkg))` and `setLockTaskFeatures(admin, LOCK_TASK_FEATURE_NONE)`; the activity declares `android:lockTaskMode="if_whitelisted"` and calls `startLockTask()`. In lock task mode the status bar is blank, Home and Overview are hidden, other apps cannot start activities, and the keyguard is disabled. Default features when `setLockTaskFeatures` is not called: only `LOCK_TASK_FEATURE_GLOBAL_ACTIONS` (power menu). From API 34 packages and features are one bundled policy.
- **Home app:** `addPersistentPreferredActivity(admin, IntentFilter(MAIN + HOME + DEFAULT), component)`; the activity needs `MAIN`/`HOME`/`DEFAULT` in its intent filter, `launchMode="singleInstance"` recommended.
- **Screen never off:** `setGlobalSetting(admin, STAY_ON_WHILE_PLUGGED_IN, (BATTERY_PLUGGED_AC or USB or WIRELESS).toString())` is still permitted for device owners, but only if `setMaximumTimeToLock` is not used. Plus `FLAG_KEEP_SCREEN_ON` on the window. `setMaximumTimeToLock(admin, 0)` means "no restriction", not "never lock", so we do not use it.
- **ADB stays on:** `setGlobalSetting(admin, ADB_ENABLED, "1")` is still permitted for device owners. Do not add `DISALLOW_DEBUGGING_FEATURES`.
- **Microphone pre-grant:** `setPermissionGrantState(admin, pkg, RECORD_AUDIO, PERMISSION_GRANT_STATE_GRANTED)`. On Android 12+ sensor permissions (RECORD_AUDIO, CAMERA, location...) are restricted, but "a device owner, by default, may continue granting these permissions" unless provisioning opted out with `EXTRA_PROVISIONING_SENSORS_PERMISSION_GRANT_OPT_OUT`. adb provisioning does not opt out. With `targetSdk >= 35` a disallowed grant throws `SecurityException` instead of failing silently, so wrap it and check `canAdminGrantSensorsPermissions()` first.
- **Useful user restrictions** for a kiosk: `DISALLOW_FACTORY_RESET`, `DISALLOW_ADD_USER`, `DISALLOW_SYSTEM_ERROR_DIALOGS` (API 28+). Do not add `DISALLOW_ADJUST_VOLUME`; the hearing aids need it. `DISALLOW_SAFE_BOOT` is deliberately not set (see Escape hatches).
- **Battery optimisation:** not a device-owner API. The app is always in the foreground, so it is moot; if needed, `adb shell dumpsys deviceidle whitelist +net.backslasher.shamash`.
- **Android versions:** Android 16 is API 36. Pixel 7 gets five years of OS updates from October 2022, so it is current through 2027. `minSdk 33` per the brief, `compileSdk`/`targetSdk 37`.

### Owner decisions since the brief (2026-11-24)

- Offline or any API failure is a complete fail: grey Error, nothing recorded, no partial results. This replaces done-criterion 5 ("the audio is still saved").
- The screen must never turn off; enforced by device-owner policy.
- The POC is an audio test, not a UX test; the owner is present when it is used.
- Maximum listening duration is configurable (default 120 s), not hard-coded.
- Releases go out as a signed APK on GitHub Releases for Obtainium.

### Owner decisions (2026-11-25)

- Trigger mode: tap to toggle.
- Transcript text size: keep the 24-64 sp auto-size.
- Maximum listening duration: 120 s.
- Power button: the app re-wakes the screen (`ScreenKeeper`, wake lock on `ACTION_SCREEN_OFF`), so the phone never sits dark.

## Build

Prerequisite: `podman` only. The JDK, Android SDK and Gradle live in the build image.

```
./build.sh                  # runs testDebugUnitTest + assembleDebug
./build.sh assembleRelease  # any arguments are passed to Gradle
./build.sh assembleDebug -PfakeTranscriber=true  # debug APK that never calls ElevenLabs, for testing the mic/WAV path offline
```

The first run builds the image `localhost/shamash-android-build:<hash>` (a few minutes); Gradle caches live in the podman volume `shamash-gradle-cache`.

Outputs:

- debug: `android/app/build/outputs/apk/debug/app-debug.apk` (`testOnly`, debug-signed)
- release: `android/app/build/outputs/apk/release/app-release.apk` when signed, `app-release-unsigned.apk` without a keystore (R8 minification is on; rules in `android/app/proguard-rules.pro`)

### API key

The build fails unless the ElevenLabs key is provided, either as

- `ELEVENLABS_KEY=...` in `<repo>/.env` (`build.sh` sources it and passes it into the container), or
- `elevenlabs.key=...` in `android/local.properties`.

Both are git-ignored. The key is compiled into `BuildConfig.ELEVENLABS_KEY`; never print or commit it.

### Live API test and tools

Unit tests never touch the network. One opt-in live test streams `tools/fixtures/hebrew_v3.wav` through the realtime and batch APIs (needs the key in `.env`):

```
./build.sh testDebugUnitTest -PliveApi=true --tests '*ElevenLabsLiveTest*'
```

`tools/rt_smoke.py` is a Python realtime smoke test (needs the `websockets` package): `set -a; . .env; set +a; python3 tools/rt_smoke.py tools/fixtures/hebrew_v3.wav`. See `tools/fixtures/README.md`.

Build with `-PfakeTranscriber=true` for UI or microphone work without network. Logs use tag `Shamash`.

## Install for development (ordinary app)

The debug APK is `testOnly`, so it needs `-t`:

```
adb install -t -r android/app/build/outputs/apk/debug/app-debug.apk
```

Without device owner the app is an ordinary app: it hides the system bars (immersive) but never calls `startLockTask()`, so there is no screen-pinning dialog. The first run asks for the microphone.

## Provision a fresh phone as kiosk

1. Factory reset the phone.
2. In the setup wizard connect to Wi-Fi (the app needs it) but do **not** add a Google account; skip everything else.
3. Settings > About phone: tap Build number 7 times. Then Developer options > enable USB debugging.
4. Connect USB, run `adb devices`, accept the RSA (Rivest-Shamir-Adleman) key prompt on the phone **and tick "Always allow from this computer"**; the device must show as `device`, not `unauthorized`. Then `adb reboot` once and confirm it still shows as `device`. A one-time approval is forgotten on reboot, and once the phone is in lock task mode the approval dialog may not be reachable, which locks you out of adb (this happened on 2026-11-25).
5. `adb install -t -r android/app/build/outputs/apk/debug/app-debug.apk`
6. `adb shell dpm set-device-owner net.backslasher.shamash/.kiosk.ShamashAdminReceiver`
   Expected output:
   ```
   Success: Device owner set to package ComponentInfo{net.backslasher.shamash/net.backslasher.shamash.kiosk.ShamashAdminReceiver}
   Active admin set to component {net.backslasher.shamash/net.backslasher.shamash.kiosk.ShamashAdminReceiver}
   ```
   If it fails with `Not allowed to set the device owner because there are already some accounts on the device`, remove the account (Settings > Passwords & accounts) or factory reset again and skip account setup.
7. The policies are applied in the app's `onCreate`, so the app process must start fresh after step 6. `am force-stop` and `am crash` do not stop a device-owner process; either reinstall (`adb install -t -r ...`, which kills it) or `adb reboot`, then `adb shell am start -n net.backslasher.shamash/.MainActivity`. Expected in `adb logcat -s Shamash`: `setKeyguardDisabled returned true`, `setStatusBarDisabled returned true`, `RECORD_AUDIO grant applied`, `lock task mode entering`. `adb shell dumpsys activity activities | grep mLockTaskModeState` must say `LOCKED` (`PINNED` means screen pinning, i.e. the allowlist was not applied yet).
8. `adb reboot` and confirm the phone comes up directly in the app.

`adb shell dpm` prints its own help; it documents that `remove-active-admin` only works for `testOnly` apps. `adb shell dpm` is the authority if this README drifts.

### Escape hatches

Lock task blocks the "Allow USB debugging?" dialog and the app has no other way out, so there are three ways back in, in order of preference:

1. **Maintenance unlock (three-finger hold).** Hold three or more fingers on the screen for 5 seconds. The phone buzzes, lock task stops and "maintenance mode - kiosk unlocked" shows at the bottom. Unplug and replug USB so the "Allow USB debugging" dialog appears, and tick "Always allow from this computer". The app stays unlocked until the process restarts (reinstall or reboot re-locks).
2. **Safe mode.** Reboot: hold Power + Volume Down for about 10-20 s until Fastboot Mode shows, choose "Start" (or power off and on). While the boot animation runs, hold Volume Down until "Safe mode" shows in the corner. Third-party apps, including Shamash, are disabled there, so Settings and adb work. Reboot normally afterwards.
3. **Factory reset from recovery (last resort).** Hold Power + Volume Down until Fastboot Mode shows, use the volume keys to select "Recovery Mode", press Power. At "No command" hold Power and press Volume Up once. Pick "Wipe data/factory reset", confirm, then "Reboot system now".

## Pull recordings

```
adb pull /sdcard/Android/data/net.backslasher.shamash/files/recordings ./recordings
```

Each session writes `<yyyyMMdd-HHmmss>.wav` (16 kHz mono 16-bit PCM) and a `.json` sidecar. Nothing is deleted automatically. On first launch the app also writes `device-info.json` (model, Android release, SDK_INT, app version, device-owner and lock-task flags, and whether the on-device recogniser offers `he-IL`; informational only).

Sidecar fields (`storage/SessionRecord.kt`):

| field | meaning |
|---|---|
| `timestamp` | session start, ISO-8601 with UTC offset |
| `durationMs` | length of the captured audio in milliseconds |
| `partials[]` | live partials as `{tMs, text}`; `tMs` is time since start, `text` is the cumulative text at that moment |
| `realtimeFinal` | final text from the realtime stream (committed segments), or null |
| `batchText` | final text from the batch API, or null |
| `batchRaw` | raw JSON of the batch response, kept for analysis |
| `latency.firstPartialMs` | start to first partial, or null |
| `latency.stopToFinalMs` | stop tap to final text, or null |
| `appVersion` | `versionName` of the build |
| `config.languageCode` / `realtimeModelId` / `batchModelId` / `sampleRate` | the settings used for the session |
| `error` | human-readable error, or null |
| `errorType` | machine-readable error (`network`, `timeout`, `auth_error`, `http_422`, ...), or null |

## Remove device owner / leave kiosk (debug builds only)

```
adb shell dpm remove-active-admin net.backslasher.shamash/.kiosk.ShamashAdminReceiver
adb shell am force-stop net.backslasher.shamash
adb uninstall net.backslasher.shamash      # optional
```

If the lock task is still active after this, press Home or run `force-stop` again. The persistent HOME preference is cleared together with the owner; the phone then asks for a launcher again. Policies applied while owner (status bar, keyguard, restrictions, stay-awake) are lifted when the owner is removed.

A release build is not `testOnly`, so `remove-active-admin` refuses it: the only way out is a factory reset. The app sets `DISALLOW_FACTORY_RESET`, which blocks the Settings route; the recovery-menu route is unverified. Use the debug build while iterating.

## Release and updates (Obtainium)

Releases are signed APKs on GitHub Releases, installed and updated by Obtainium.

```
export SHAMASH_KEYSTORE_FILE=/path/to/release.jks SHAMASH_KEYSTORE_PASSWORD=... \
       SHAMASH_KEY_ALIAS=... SHAMASH_KEY_PASSWORD=... \
       SHAMASH_VERSION_NAME=0.2.0 SHAMASH_VERSION_CODE=2
tools/release.sh             # builds, prints the `gh release create` command
tools/release.sh --publish   # also runs it
```

The script refuses to run when `jj diff` shows uncommitted changes. `SHAMASH_VERSION_CODE` must increase with every release.

- The APK must be signed with the same keystore every time, or Android rejects the update. Keep the keystore backed up; it is git-ignored and must stay so. This repo does not create one.
- The first install on the kiosk phone is manual: `adb install -r <signed apk>` (release is not `testOnly`, so no `-t`). A phone that has the debug build must be uninstalled first (different signature).
- In Obtainium add the GitHub repository URL as an app source; it tracks GitHub Releases and installs the APK.
- A release build cannot have device owner removed over adb; iterate with debug.

## Device test checklist

Maps to brief section 7 "Done means".

Status as of 2026-11-25 09:30, Pixel 7 Pro, Android 16 (API 36), debug build, device owner set, phone on the owner's main Wi-Fi (after a factory reset and re-provisioning that morning):

- [x] 1. After `adb reboot` the phone shows the Idle button with no user action. Verified 09:20: app in front, lock task `LOCKED`, awake, Wi-Fi joined, adb still authorised.
- [x] 2. Tap: red pulsing screen; realtime session ready 330 ms after the tap. *Spoken Hebrew not yet tested on the device; only ambient sound.*
- [x] 3. Tap again: amber, then the final transcript, then Idle. Final text 1.2 s after the stop tap for a 7 s clip.
- [x] 4. WAV (16 kHz mono 16-bit, correct duration) and JSON per session; `adb pull` works.
- [x] 5. No network, tap: grey Error ("אין חיבור") within 13 ms, nothing recorded.
- [x] 6. Home, Back, Recents, swipe from top, swipe from bottom: focus stays in the app, lock task state `LOCKED`. `remove-active-admin` exercised 09:24: lock task went to `NONE`; `set-device-owner` again plus a reinstall brought it back to `LOCKED`.
- [x] 7. `.env` and `local.properties` are untracked; the key is only in `BuildConfig`.
- [x] 8. Provisioning steps worked end to end on the second attempt; README updated with what was learned.
- [x] 2026-11-25: after a reboot the lock task blocked the USB-debugging dialog, adb stayed `unauthorized` and a factory reset was needed. This is why the escape hatches exist (three-finger hold, safe boot allowed).
- [x] Three-finger hold verified by the owner at 09:17 from inside the kiosk: lock task `LOCKED` → `NONE`, label shown, app stayed in front. (Once, the label also appeared at the top of the screen as well as the bottom; not reproduced.)
- [x] Long sessions on the current build, silent room, driven by `adb shell input tap`: 30 s session had its periodic commit at 25.8 s, final commit answered in 188 ms, batch 7.6 s; the 120 s session auto-stopped at 120.0 s after four periodic commits, batch 3.4 s, stop-to-final 3.6 s. Both WAVs the right length, transcripts empty (silence).

Also seen:

- The power button turns the screen off (`Dozing`). The app re-wakes it on `ACTION_SCREEN_OFF` (3 s wake lock, receiver held for the activity's whole lifetime, since a start/stop-scoped one is unregistered before the broadcast arrives). Verified 2026-11-25 09:48 over adb: back to `Awake` within 1 s, twice in a row.
- A 120 s session on the first build timed out in the final step ("Timed out waiting for the final transcript (20000 ms)"); the batch timeout is now 90 s and the long-session case needs a re-test.
- A swipe anywhere counts as a tap, which is fine on a stand.
- The on-device recogniser does not offer Hebrew (`he-IL: supported=[]`), as the brief expected.
- The start tone is captured in the first ~150 ms of the WAV. Batch now sends `tag_audio_events=false` so it is not labelled; whether it harms recognition of the first word needs a listening test.

Still to check with a person present:

- [ ] Spoken Hebrew: live partials appear while speaking, final transcript correct.
- [ ] Start and stop tones play from the bottom speaker, at a usable volume.
- [ ] Haptic feedback present or absent (note which).
- [ ] Hebrew text size and legibility from the stand; mixed Hebrew/English order.
- [x] A session longer than 25 s (periodic commit) and the full 120 s auto-stop, on the current build (silent room; see above).
- [x] `adb shell dpm remove-active-admin ...` then re-provision.

## PC-link spike (2026-11-25)

Beyond the POC: a measurement of the future phone → PC audio path, using `tools/pclink/` (podman WebSocket server) and the debug-only probe in the app (`tools/pclink/README.md`). Each probe streams real microphone audio as 100 ms PCM frames; the server acks every frame and writes a WAV.

| Path | Frames / acks | Gaps | RTT ms min / median / max / p95 | Server inter-arrival ms min / median / max |
|---|---|---|---|---|
| USB (`adb reverse`), 10 s | 100 / 100 | 0 | 8 / 12 / 15 / 14 | 93 / 100 / 107 |
| Main Wi-Fi, 10 s | 100 / 100 | 0 | 11 / 17 / 72 / 24 | 57 / 100 / 154 |
| Main Wi-Fi, 30 s | 299 / 299 | 0 | 10 / 17 / 275 / 28 | (not captured) |

Findings:

- The owner's guest Wi-Fi isolates clients from the LAN: the phone could ping its gateway but every connection to the PC was refused (no firewall on the PC). The phone is now on the main Wi-Fi, which works. The real system needs the phone on the main network, or USB.
- USB through `adb reverse` is a usable transport and would suit a cabled dock: lowest and steadiest latency, no Wi-Fi power-save spikes.
- Wi-Fi is fine for 100 ms frames (no gaps, median 17 ms) but has occasional spikes of 70–275 ms, consistent with the 50–130 ms ping times seen from the phone.
- Plain `ws://` is blocked by Android's default network security policy. Debug builds now carry `src/debug/res/xml/network_security_config.xml` allowing cleartext; release builds do not, so the real link must be `wss://` (or a VPN/pinned TLS, as the brief's end state already says).
- The received WAVs are correct 16 kHz mono files of the right length.

### Open questions for the owner

All four were answered on 2026-11-25 (see owner decisions).
