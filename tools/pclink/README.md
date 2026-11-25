# PC-link probe

Measurement spike: can the phone stream microphone audio to the PC over the LAN with acceptable latency and no gaps? A debug-only probe in the app streams live mic audio to a WebSocket server and records round-trip times.

## Run the server

    tools/pclink/run.sh [outdir]        # outdir defaults to ./pclink-out

Builds `localhost/shamash-pclink` and runs it with `--rm -p 127.0.0.1:8777:8777 -v <outdir>:/out`. Each connection writes `pclink-<timestamp>.wav` (16 kHz mono 16-bit) and prints one line: frames, bytes, wall time, gaps, inter-arrival jitter min/median/max in ms.

For the Wi-Fi case the port must be reachable from the LAN: `PCLINK_BIND=0.0.0.0 tools/pclink/run.sh` (and open port 8777 in the PC firewall if needed).

## Connect the phone

- USB: `adb reverse tcp:8777 tcp:8777`, then use `ws://127.0.0.1:8777/` (default).
- Wi-Fi: the phone must be on the same LAN as the PC (the guest Wi-Fi is isolated, so it will not work there). Use `ws://<pc-lan-ip>:8777/`. Cleartext `ws://` is accepted by OkHttp; the app has no network security config blocking it, but verify on the device.

## Trigger (debug build only)

    adb shell am broadcast -a net.backslasher.shamash.PCLINK_PROBE --es url ws://127.0.0.1:8777/ --ei seconds 10 -p net.backslasher.shamash

`-p` (or an explicit component) is required on Android 8+ for a manifest receiver. Extras: `url` (default `ws://127.0.0.1:8777/`), `seconds` (default 10). The app should be in the foreground (kiosk) so the mic is available; if a session is running the probe may fail on AudioRecord.

## Read the result

    adb logcat -d | grep pclink:

One line with frames sent, bytes, acks, RTT min/median/max/p95 (ms), the server summary and total duration. Failures are logged with `pclink: failed: ...`. The same data is written as JSON to `<external files dir>/pclink/pclink-<timestamp>.json` on the phone (`/sdcard/Android/data/net.backslasher.shamash/files/pclink/`).

RTT = ack receive time minus the frame's `tPhone`, both on the phone's `SystemClock.elapsedRealtime`, so clock skew does not matter. `tServerMs` is server epoch time and is only useful with a synced clock.

## Protocol

One WebSocket connection per probe, `ws://host:8777/`.

1. Phone, text: `{"type":"hello","sampleRate":16000,"seconds":N,"device":"<model>"}`
2. Phone, binary frames: 4-byte big-endian uint32 `seq`, 8-byte big-endian int64 phone send time in ms (`SystemClock.elapsedRealtime`), then raw 16-bit little-endian mono PCM (3200 bytes = 100 ms).
3. Server, text, per frame: `{"type":"ack","seq":..,"tPhone":..,"tServerMs":<server epoch ms>}`
4. Phone, text: `{"type":"end"}`
5. Server, text, then close: `{"type":"summary","frames":..,"bytes":..,"seconds":..,"gaps":<missing seq count>}`
