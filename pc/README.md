# Shamash PC service (scaffold)

A .NET 10 Generic Host worker for the Windows PC. Currently a placeholder that proves the build, package and unattended-update pipeline. The real service (speech recognition, Claude agent, TTS) is not here yet.

## Contents

- `Shamash.Pc/` - the app. `Program.cs` calls `VelopackApp.Build().Run()` first, then starts the host.
  - `UpdateService` - on startup and every 6 hours checks `https://github.com/BackSlasher/shamash` (GitHub Releases, stable only) via Velopack. If an update exists it is downloaded, then applied with `ApplyUpdatesAndRestart` only inside the update window. If `UpdateManager.IsInstalled` is false it logs "not installed via Velopack, skipping updates" and does nothing.
  - `UpdateWindow` - pure static-style parsing/containment logic (unit tested).
  - `HeartbeatService` - logs the assembly informational version once a minute.
  - `LinkServer` / `LinkHostedService` - phone link probe server (see below); `LinkFrame`, `LinkSessionStats`, `WavWriter` are the pure parts.
- `Shamash.Pc.Tests/` - xunit tests for `UpdateWindow` (same-day window, window crossing midnight, empty window, malformed input falls back to the 03:00-05:00 default).
- `build.sh` - runs everything in podman (`mcr.microsoft.com/dotnet/sdk:10.0`), caches in volume `shamash-dotnet-cache` (NuGet packages in `/cache/nuget`, `vpk` in `/cache/tools`).

## Logging choice

Serilog (`Serilog.Extensions.Hosting`, `Serilog.Sinks.Console`, `Serilog.Sinks.File`). Microsoft.Extensions.Logging has no built-in file provider, and a rolling daily file (14 kept) is needed because the user never sees a console. Logs go to `%LocalAppData%\Shamash\logs\shamash-YYYYMMDD.log`, or `logs\` next to the exe if that folder is unavailable.

## Commands

All from the repo root; only podman is needed on the host. The build image is `pc/Containerfile` (the official .NET 10 SDK image plus `zstd`, which vpk needs to build delta packages; without it `pack` fails with "Could not find 'zstd' binary" as soon as a previous release sits in `pc/out/releases`). Keep `pc/out/releases` between releases, or fetch the previous one with `pc/build.sh vpk "[win]" download github ...`, so deltas get built.

```
pc/build.sh test       # dotnet test
pc/build.sh publish    # dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o pc/out/publish
pc/build.sh pack       # vpk [win] pack ... -> pc/out/releases
pc/build.sh upload     # vpk [win] upload github ... (publishes a real GitHub release)
pc/build.sh vpk ...    # run vpk with any arguments
```

`pack` runs:

```
vpk "[win]" pack --packId Shamash.Pc --packVersion $SHAMASH_VERSION_NAME --packDir out/publish \
  --mainExe Shamash.Pc.exe --runtime win-x64 --channel win --outputDir out/releases
```

`upload` runs:

```
vpk "[win]" upload github --repoUrl https://github.com/BackSlasher/shamash --publish --merge \
  --releaseName "Shamash $SHAMASH_VERSION_NAME" --tag "v$SHAMASH_VERSION_NAME" \
  --token $GITHUB_TOKEN --channel win --outputDir out/releases
```

The token is taken from `GITHUB_TOKEN`, else from `gh auth token` on the host, and passed into the container as an environment variable (not echoed). `--channel win` is explicit because `vpk upload` defaults the channel to the host OS (`linux` here) and would not find the Windows files. `--merge` lets vpk add its assets to a release that already exists; without it vpk stops with "There is already an existing release tagged 'v0.1.0'" (seen 2026-11-25). The phone APK is published first by `tools/release.sh --publish` (its `gh release create` refuses an existing tag), then this upload merges into the same release, so one tag carries both halves.

The `vpk` version (`vpk_version` in `build.sh`) must equal the `Velopack` package version in `Shamash.Pc.csproj` (both 1.2.161).

## Environment variables

| variable | where | meaning |
|---|---|---|
| `SHAMASH_VERSION_NAME` | build | app and package version, default `0.1.0` (MSBuild `Version`) |
| `GITHUB_TOKEN` | upload | token for the release upload |
| `SHAMASH_UPDATE_WINDOW` | runtime | `HH:mm-HH:mm` local time, default `03:00-05:00`; may cross midnight; malformed falls back to default |
| `SHAMASH_UPDATE_NOW` | runtime | `1` applies a downloaded update immediately |
| `SHAMASH_UPDATE_CHECK_MINUTES` | runtime | minutes between update checks, default 360, minimum 1 |
| `SHAMASH_LINK_PORT` | runtime | phone link probe port, default 8777 |
| `ELEVENLABS_KEY` | runtime | ElevenLabs API key (conversation STT and TTS) |
| `ANTHROPIC_API_KEY` | runtime | Anthropic API key (conversation LLM) |
| `SHAMASH_LLM` | runtime | `claude` (default) or `echo` |
| `SHAMASH_TTS_VOICE_ID` | runtime | ElevenLabs voice, default `JBFqnCBsd6RMkjVDRZzb` |
| `SHAMASH_CLAUDE_MODEL` | runtime | default `claude-opus-5-5` |

## Phone link probe

Port of `tools/pclink/server.py`: Kestrel (ASP.NET Core, framework reference, no extra packages) runs inside the Generic Host and serves a WebSocket at `ws://<host>:8777/` (plain ws for now). Port from `SHAMASH_LINK_PORT`. If the port cannot be bound it logs "link: failed to start" and the rest of the service keeps running.

Protocol:

- Text `{"type":"hello","sampleRate":16000,"seconds":N,"device":"..."}` first; text `{"type":"end"}` when the phone is done.
- Binary frames: 4-byte big-endian uint32 seq, 8-byte big-endian int64 phone send time (ms), then raw 16-bit mono PCM (3200 bytes = 100 ms at 16 kHz). Frames under 12 bytes are ignored. Messages over 1 MB close the socket.
- Every frame is answered with `{"type":"ack","seq":S,"tPhone":T,"tServerMs":<unix ms>}`.
- On `end` or close: `{"type":"summary","frames":N,"bytes":N,"seconds":<3 decimals>,"gaps":<missing seq count>}`, then the server closes.

The PCM is written to `%LocalAppData%\Shamash\pclink\pclink-yyyyMMdd-HHmmss.wav` (`.\pclink` if LocalAppData is unavailable), mono 16-bit at the hello's sample rate. Log lines (Information):

```
link: listening on http://0.0.0.0:8777
link: hello from <ip:port>: {"type":"hello",...}
link: peer=<ip:port> frames=N bytes=N audio=S.SSs wall=S.SSs gaps=N interarrival_ms(min/med/max)=a/b/c
```

Test from the Android debug build (one quoted string, because adb joins its arguments with spaces):

```
adb shell "am broadcast -a net.backslasher.shamash.PCLINK_PROBE -p net.backslasher.shamash --es url ws://<pc-ip>:8777/ --ei seconds 10"
```

### Windows Firewall

The first time `Shamash.Pc.exe` listens, Windows Defender Firewall may show an allow prompt, or (on a Public network profile, or when no one can answer the prompt) silently block inbound connections. Allow it from an elevated PowerShell:

```
New-NetFirewallRule -DisplayName "Shamash link" -Direction Inbound -Protocol TCP -LocalPort 8777 -Action Allow -Profile Private
```

or per program: `New-NetFirewallRule -DisplayName "Shamash link" -Direction Inbound -Program "$env:LocalAppData\Shamash.Pc\current\Shamash.Pc.exe" -Action Allow` (the path changes per Velopack version folder only if you use `current\`; unverified whether the rule survives updates, so the port rule is safer). Drop `-Profile Private` to include Public networks. Kestrel binding `0.0.0.0` on a non-reserved port as a normal user needs no URL ACL (that is a http.sys/HttpListener requirement); this is Kestrel's own socket, but it has not been tried on the Windows VM yet (unverified). Not verified either: the firewall prompt behaviour itself.

Verified on Linux in podman only: the in-process integration test (`ClientWebSocket`: hello, 3 frames, end; 3 acks, summary frames=3 bytes=9600 gaps=0, WAV written). A trimming/single-file `publish` for win-x64 succeeds and `Shamash.Pc.exe` grew to about 104 MB; it also leaves `aspnetcorev2_inprocess.dll` (IIS module, unused) beside the exe.

## Conversation mode

A hello with `"mode":"conversation"` (see `docs/link-protocol.md`) runs one turn per connection after `end`: ElevenLabs batch STT (`scribe_v3`, `heb`, WAV built with `WavWriter`; audio under 0.5 s skips STT and counts as an empty transcript) -> `transcript` -> Claude (`Anthropic` NuGet 13.14.0, non-beta Messages API, `output_config.effort` low, system prompt in `ClaudeReplyModel.SystemPrompt`) -> `reply` -> ElevenLabs streaming TTS (`eleven_v4_turbo`, `pcm_16000`) -> `audio_start`, binary PCM chunks as they arrive (even lengths; an odd trailing byte is carried to the next chunk), `audio_end`, `done`, close. Any failure sends `error` with stage `stt`/`llm`/`tts`/`internal` and closes. `cancel` or a closed socket cancels the pipeline and nothing more is sent. Probe mode (no `mode`, or `probe`) is unchanged.

Settings are loaded once at startup: environment variables win over `%LocalAppData%\Shamash\config.json` (fallback `.\config.json`; first existing file only), camelCase keys:

```json
{ "elevenLabsKey": "...", "anthropicApiKey": "...", "llm": "claude", "ttsVoiceId": "JBFqnCBsd6RMkjVDRZzb", "claudeModel": "claude-opus-5-5" }
```

Env names: `ELEVENLABS_KEY`, `ANTHROPIC_API_KEY`, `SHAMASH_LLM`, `SHAMASH_TTS_VOICE_ID`, `SHAMASH_CLAUDE_MODEL`. Key values are never logged, only whether they are present. Without an ElevenLabs key a turn fails with stage `internal`, "no ElevenLabs key configured" (probe mode still works); without an Anthropic key and `llm=claude` it fails with stage `llm`.

`SHAMASH_LLM=echo` makes the reply equal to the transcript, so STT and TTS can be tried without an Anthropic key.

History: one in-memory conversation for the life of the process, user and assistant entries alternating, capped at the last 40 entries (20 exchanges), dropped in pairs. A turn that fails before the reply does not add to it.

Log lines (Information unless noted):

```
config: elevenLabsKey=true anthropicApiKey=false llm=claude
turn: transcript N chars in X ms
turn: reply N chars in X ms
turn: tts first byte X ms, total Y ms, bytes B
turn: done total X ms            (or: turn: cancelled at <stage>)
turn: error at <stage>: <message>   (Warning)
link: conversation peer=... frames=N bytes=N audio=S.SSs wall=S.SSs
```

Each turn's audio is written to `%LocalAppData%\Shamash\pclink\pclink-yyyyMMdd-HHmmss.wav` (written at `end`, before the pipeline runs), with `pclink-yyyyMMdd-HHmmss.json` beside it (transcript, reply, timings in ms, TTS bytes, error or cancelledAt, done).

## Update flow

1. `Shamash.Pc-win-Setup.exe` is a one-click, per-user installer: it installs under `%LocalAppData%\Shamash.Pc` without admin rights or prompts and creates Desktop and Start Menu shortcuts.
2. The installed app checks GitHub Releases at startup and every 6 hours. A found update is downloaded in the background.
3. Once inside the update window the app calls `ApplyUpdatesAndRestart`. Velopack keeps the previous version in its own folder and switches the `current` folder to the new one.
4. Releasing: bump `SHAMASH_VERSION_NAME` (same value as the APK release), run the phone `tools/release.sh --publish`, then `pc/build.sh publish`, `pack`, `upload`. Each version needs a new, higher version number.

## Verified (2026-11-25, on Linux in podman)

- `pc/build.sh test`: 20 tests passed.
- `pc/build.sh publish` produces a win-x64 single-file `Shamash.Pc.exe` (about 76 MB).
- `pc/build.sh pack` works on Linux: vpk 1.2.161 logs "Directive enabled for cross-compiling from Linux (current os) to Windows" and produces `Shamash.Pc-win-Setup.exe`, `Shamash.Pc-0.1.0-full.nupkg`, `Shamash.Pc-win-Portable.zip`, `RELEASES`, `releases.win.json`, `assets.win.json`.
- `pc/build.sh upload` with `--merge` attached `Shamash.Pc-win-Setup.exe`, `Shamash.Pc-0.1.0-full.nupkg`, `Shamash.Pc-win-Portable.zip`, `releases.win.json` and `RELEASES` to the existing release <https://github.com/BackSlasher/shamash/releases/tag/v0.1.0>, next to the phone APK.

## Verified on Windows 11 (2026-11-25, in the test VM from `tools/winvm/`)

- `Setup.exe --silent`, run from an SSH session as the normal user, exits 0 with no prompt and installs to `%LocalAppData%\Shamash.Pc` (`Shamash.Pc.exe` stub, `Update.exe`, `current\`, `packages\`).
- Started by a logon scheduled task with no execution time limit, the 0.1.0 app logged its heartbeat and "No update available" against the real repo within 2 s.
- With v0.1.1 published and the app restarted: "Update available: 0.1.1", "Update 0.1.1 downloaded" 4 s later (the full 37 MB package, not the 7.5 KB delta vpk had built). After a restart with `SHAMASH_UPDATE_NOW=1` the process came back as 0.1.1 from `current\`, `sq.version` 0.1.1.
- With v0.1.2 published and the app restarted without the override at 15:03 local: downloaded, then "Outside the update window, holding update 0.1.2 until 03:00".
- Fully unattended path: with 0.1.2 running under `SHAMASH_UPDATE_NOW=1` and `SHAMASH_UPDATE_CHECK_MINUTES=1`, v0.1.3 was published and nothing touched the VM. The log shows "Update available: 0.1.3" at 15:07:48, downloaded at :50, "Applying update 0.1.3 and restarting", and the new process heartbeating as 0.1.3 at 15:07:55, 7 s after detection. The scheduled task stays Ready throughout because Update.exe relaunches the app outside the task.
- The log file is `%LocalAppData%\Shamash\logs\shamash-<yyyyMMdd>.log`.

## Verified end to end with the phone (2026-11-26)

- v0.1.4 (the first build with the link server) was published while the VM app ran 0.1.3 with `SHAMASH_UPDATE_NOW=1` and one-minute checks: "Applying update 0.1.4 and restarting" at 09:55:55, "link: listening on http://0.0.0.0:8777" from the 0.1.4 process at 09:56:01. The inbound port rule had been added beforehand with the PowerShell line below (Private profile, which is what the VM's network is). Kestrel bound 0.0.0.0:8777 as the normal user with no URL ACL.
- The Pixel 7 Pro (debug build, on the same Wi-Fi) ran the probe against the VM through the host's published port: 100 frames, 100 acks, RTT min/median/max/p95 10/19/137/35 ms, no gaps. The VM logged the hello with the phone's real LAN address and wrote `pclink-20261126-095707.wav`, 10.00 s of 16 kHz mono, which held real room audio (nobody spoke; RMS 86, peak 525).

## Verified on Windows 11 with real ElevenLabs calls (2026-11-26, v0.1.6, `llm=echo`)

- Night window, hands off: the service started 20:51 on 0.1.5 with the window set to 20:59-21:09 and one-minute checks, found v0.1.6 at 20:53:42, logged "holding update 0.1.6 until 20:59", and applied it at 21:03:46 (the first 5-minute window poll after 20:59) with nobody touching the VM. Note for the real deployment: Velopack also applies an already-downloaded update at the next app start regardless of the window, seen twice when the task was restarted.
- `config.json` under `%LocalAppData%\Shamash` was read and logged as `config: elevenLabsKey=true anthropicApiKey=false llm=echo`.
- `tools/pclink/client.sh` turn with a 7 s synthetic Hebrew sentence: `transcript` arrived 1.4 s after `end` with the exact text ("שלום. תמצא בבקשה את המאמר על ירושלים בתקופה העות'מאנית ותעביר אותו לאנגלית. תודה."), the echo reply followed, TTS first byte 0.3 s later, 7 s of 16 kHz reply audio streamed back, `done` at 2.5 s. With a 12 s noisy sample STT took 6.9 s and TTS first byte 0.9 s. So the ElevenLabs request shapes are confirmed.
- A silent 3.7 s turn from the Pixel in link mode: empty transcript in 0.8 s, `done`, phone back to idle.

## Not verified

- The Claude step (`claude-opus-5-5`, `output_config.effort`): the SDK call compiles and is covered by fakes, but the VM has no Anthropic key yet, so no real reply has been produced.
- A spoken turn from the phone (someone talking into it) and the phone playing the reply audio through its speaker; only a silent turn ran from the phone.
- Uninstall, shortcuts, and SmartScreen on a browser-downloaded Setup.exe (the VM copy came over the shared folder).
- The firewall prompt itself: the rule was added before the service first listened.
- Why the delta package is not used: Velopack downloaded the full package every time, though vpk builds 7 KB deltas.
- Release assets are unsigned; Windows SmartScreen may warn on first run.
- vpk warns that `VelopackApp.Run()` "does not look like your application's entry point" because of top-level async `Main`. Updates work regardless.
