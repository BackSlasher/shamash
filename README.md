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
- **Useful user restrictions** for a kiosk: `DISALLOW_FACTORY_RESET`, `DISALLOW_SAFE_BOOT`, `DISALLOW_ADD_USER`, `DISALLOW_SYSTEM_ERROR_DIALOGS` (API 28+). Do not add `DISALLOW_ADJUST_VOLUME`; the hearing aids need it.
- **Battery optimisation:** not a device-owner API. The app is always in the foreground, so it is moot; if needed, `adb shell dumpsys deviceidle whitelist +net.backslasher.shamash`.
- **Android versions:** Android 16 is API 36. Pixel 7 gets five years of OS updates from October 2022, so it is current through 2027. `minSdk 33` per the brief, `compileSdk`/`targetSdk 37`.

### Owner decisions since the brief (2026-11-24)

- Offline or any API failure is a complete fail: grey Error, nothing recorded, no partial results. This replaces done-criterion 5 ("the audio is still saved").
- The screen must never turn off; enforced by device-owner policy.
- The POC is an audio test, not a UX test; the owner is present when it is used.
- Maximum listening duration is configurable (default 120 s), not hard-coded.
- Releases go out as a signed APK on GitHub Releases for Obtainium.

## Build

Prerequisite: `podman` only. Nothing else is installed on the host (JDK, Android SDK and Gradle live in the build image).

```
./build.sh                  # runs testDebugUnitTest + assembleDebug
./build.sh assembleRelease  # any arguments are passed to Gradle
```

The first run builds the image `localhost/shamash-android-build:<hash>` (a few minutes); Gradle caches live in the podman volume `shamash-gradle-cache`.

Output: `android/app/build/outputs/apk/debug/app-debug.apk`.

### API key

The build fails with a clear message unless the ElevenLabs key is provided, either as

- `ELEVENLABS_KEY=...` in `<repo>/.env` (`build.sh` sources it and passes it into the container), or
- `elevenlabs.key=...` in `android/local.properties`.

Both files are git-ignored. The key is compiled into `BuildConfig.ELEVENLABS_KEY`; never print or commit it.

### Install on the phone (manual, not part of the build)

The debug APK is `testOnly` (so device owner can be removed over adb later), so it needs `-t`:

```
adb install -t -r android/app/build/outputs/apk/debug/app-debug.apk
```

### Pulling recordings

Each session writes `<yyyyMMdd-HHmmss>.wav` (16 kHz mono 16-bit PCM) and a `.json` sidecar into `getExternalFilesDir(null)/recordings`, i.e.

```
adb pull /sdcard/Android/data/net.backslasher.shamash/files/recordings ./recordings
```

(`/sdcard` is the same as `/storage/emulated/0`.) Nothing is ever deleted automatically.

### Current status

Stages 1-2: the transcriber is a `FakeTranscriber` (fake Hebrew partials and final, no network). The real ElevenLabs transcriber replaces it in `ShamashViewModel`.

## Provisioning and kiosk

TODO (launcher, device owner, lock task, removal command: later task).
