# Shamash PC service (scaffold)

A .NET 10 Generic Host worker for the Windows PC. Currently a placeholder that proves the build, package and unattended-update pipeline. The real service (speech recognition, Claude agent, TTS) is not here yet.

## Contents

- `Shamash.Pc.Core/` - the logic (net10.0 library, unit tested); `Shamash.Pc/` - the Windows host (WinExe, WinForms tray icon and status window: `TrayApp`, `StatusForm`, `TrayPromotion`). The list below is the logic in Core except `Program.cs`.
- `Shamash.Pc/` - the app. `Program.cs` calls `VelopackApp.Build().Run()` first, then starts the host.
  - `UpdateService` - on startup and every 6 hours checks `https://github.com/BackSlasher/shamash` (GitHub Releases, stable only) via Velopack. If an update exists it is downloaded, then applied with `ApplyUpdatesAndRestart` only inside the update window. If `UpdateManager.IsInstalled` is false it logs "not installed via Velopack, skipping updates" and does nothing.
  - `UpdateWindow` - pure static-style parsing/containment logic (unit tested).
  - `HeartbeatService` - logs the assembly informational version once a minute.
  - `LinkServer` / `LinkHostedService` - phone link probe server (see below); `LinkFrame`, `LinkSessionStats`, `WavWriter` are the pure parts.
- `Shamash.Pc.Tests/` - xunit tests for `UpdateWindow` (same-day window, window crossing midnight, empty window, malformed input falls back to the 03:00-05:00 default).
- `build.sh` - runs everything in podman (`mcr.microsoft.com/dotnet/sdk:10.0`), caches in volume `shamash-dotnet-cache` (NuGet packages in `/cache/nuget`, `vpk` in `/cache/tools`).

## Tray icon and status window

The service shows no window. The owner sees a coloured disc with a white shin on the taskbar: green idle, orange answering, red error, grey starting. Right-click menu: Status..., Open log folder, Open recordings folder, Check for updates (or "Install update <v> now" when one is pending), Quit Shamash. Double-click opens the status window (version, link, config, turns, update state, recent log, buttons for the log folder, update check and copying diagnostics). Closing the window only hides it. Quit stops the service until the next logon or a manual start of the `Shamash.Pc` task.

Windows 11 puts new tray icons in the overflow flyout, so on startup `TrayPromotion` sets `IsPromoted=1` under `HKCU\Control Panel\NotifyIconSettings\<id>` for our exe (retried for ~30 s while Explorer creates the entry). It only writes when the value is missing, so an icon the user hid stays hidden.

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
| `SHAMASH_DOCUMENT_ROOTS` | runtime | semicolon-separated folders the document tools may search and read; config.json key `documentRoots` (string array); default on Windows: Documents and Desktop, elsewhere none |

## Tools

With `llm=claude` the reply model runs a tool loop (`ToolLoop`, `ClaudeReplyModel`): the tools are sent with each Messages call; on `tool_use` blocks every tool runs, the assistant content and one `tool_result` block per call go back, and it repeats for at most 8 rounds, after which a last call with `tool_choice` none forces a text answer. Tool errors reach the model as text starting `error:` and are never thrown. Each call (name, compact input, ms, result length) is saved in the turn JSON under `toolCalls` and the "turn: reply" log line and the status window show the count. `echo` mode has no tools.

| Tool | Input | Result |
|---|---|---|
| `search_documents` | `query` (keywords), optional `kind` (`any`, `docx`, `pdf`, `xlsx`, `txt`), optional `max` (1-20, default 10) | a `method:` header (`index` or `walk`) then lines `n. name \| folder relative to the root \| yyyy-MM-dd \| size KB`, newest first |
| `read_document` | `path` (full path, or `folder/name` as search showed it), optional `max_chars` (default 6000, cap 30000) | text of .docx (paragraphs and tables, cells tab-separated), .pdf (PdfPig), .xlsx (sheet name, first 200 rows, tabs), .txt/.md/.csv (UTF-8, BOM detected), cut with `[truncated, N chars total]` |

How search works: on Windows it queries the Windows Search index through OLE DB (`Provider=Search.CollatorDSO`, table `SYSTEMINDEX`, one `SCOPE='file:<root>'` per document root; `CONTAINS` prefix match of all words ANDed, or `FREETEXT` of the whole query (catches Hebrew prefixed forms such as `העות` for `העות'מאנית`), or all words in the file name via `LIKE`; newest first). Claude keeps no index of its own. Words are split on whitespace, apostrophes, geresh, gershayim, quotes and hyphens. Once per process the service reads `HKEY_CLASSES_ROOT\<ext>\PersistentHandler` for .docx, .xlsx, .txt and .pdf: an extension without a handler is indexed by name only (on a machine without Office that is .docx and .xlsx). When the index answers, a content walk restricted to those name-only extensions (plus .txt when the index found no .txt, since a BOM-less UTF-8 file is not indexed) is added, merged by full path, index hits first; each line ends with `index` or `walk`. If the index throws (E_FAIL for a minute after boot, no index, not Windows) or returns nothing, the whole walk runs: file-name match on any word, and for files under 5 MB a content match requiring all words, capped at 2000 files and 10 s (the header says when the cap stopped it). Hebrew is plain UTF-16 end to end. The tool description tells the model to give Hebrew words without prefix letters and to retry once with another form before saying a file does not exist.

Path guard: `read_document` refuses any path not under a document root (after `..` is normalised) with `error: path not under a document root`. Roots come from `documentRoots` / `SHAMASH_DOCUMENT_ROOTS` (see above); only the count is logged.

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
{ "elevenLabsKey": "...", "anthropicApiKey": "...", "llm": "claude", "ttsVoiceId": "JBFqnCBsd6RMkjVDRZzb", "claudeModel": "claude-opus-5-5", "documentRoots": ["C:\\Users\\x\\Documents"] }
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

1. `Shamash.Pc-win-Setup.exe` is a one-click, per-user installer: it installs under `%LocalAppData%\Shamash.Pc` without admin rights or prompts and creates Desktop and Start Menu shortcuts. Since 0.1.7 the Velopack after-install hook (`StartupTask.cs`) also registers a logon scheduled task "Shamash.Pc" for the installing user (no execution time limit, least privilege, runs the stub from the install root) and starts it; the before-uninstall hook deletes the task. Hooks log to `%LocalAppData%\Shamash\logs\install.log`. The app holds a per-user named mutex, so a second start (installer plus task, or a task start during an update restart) exits at once.
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

## Verified: first real Claude turns (2026-11-27 07:21, v0.1.6, `llm=claude`)

- `config.json` regenerated with `tools/pc-config.py <out> --llm claude`, copied over the shared folder, service restarted: `config: elevenLabsKey=true anthropicApiKey=true llm=claude`.
- Same 7 s Hebrew sentence (a request to find an article about Ottoman Jerusalem and translate it to English). Claude answered in Hebrew within the system prompt's one-to-three sentences, asking which article and whether the author or title is known. Timings from the service log: STT 1459 ms, Claude 2443 ms, TTS first byte 952 ms, 10.2 s of reply audio, `done` at 5835 ms.
- The same sentence sent again as a second turn: the reply started with "sorry, maybe I was not clear" and narrowed the question (is it the user's own article saved on this computer), so the per-process history reaches the model. STT 988 ms, Claude 2340 ms, TTS first byte 191 ms, `done` at 4475 ms.
- Nothing from the key, the prompt or the reply is logged; only lengths and timings.

## Verified: installer registers the logon task, uninstall removes it (2026-11-26 23:44, local 0.1.7 pack)

- The VM was restored to the `win11-base` snapshot (`tools/winvm/snapshot.sh`, no Shamash on it), then `Setup.exe --silent` from the shared folder: exit 0; `install.log` shows `after-install 0.1.7`, `schtasks /Create` SUCCESS, `/Run` SUCCESS; `Get-ScheduledTask` reports Ready, `ExecutionTimeLimit PT0S`, a logon trigger; exactly one `current\Shamash.Pc.exe` process; the service logged "No update available" 7 s after the installer returned.
- `Update.exe --uninstall --silent`: exit 0, `before-uninstall 0.1.7`, `schtasks /Delete` SUCCESS, task gone. Velopack leaves `Update.exe` itself and `%LocalAppData%\Shamash` (config and logs) behind; the data folder is intentional so a reinstall keeps its config.

## Verified: 0.1.8 tray host (2026-11-27, clean `win11-base` VM, local pack)

- `Setup.exe --silent` exit 0; task Ready; exactly one `Shamash.Pc` process, SessionId 1; no console window on the desktop (screenshot).
- Config line `elevenLabsKey=true anthropicApiKey=true llm=claude` and `link: listening on http://0.0.0.0:8777` in the log.
- One conversation turn from the host (`--fast`): transcript at 6.3 s, reply at 8.6 s, done at 10.0 s.
- `Update.exe --uninstall --silent` removed the task and the process; reinstall worked.
- Icon promotion (fresh restore, 40 s after install): `NotifyIconSettings` entry for `current\Shamash.Pc.exe` has `IsPromoted=1`; log shows `tray: promoted icon` 1.1 s after `link: listening`; the green disc is on the taskbar between the chevron and the cloud icon. A second turn: transcript 1.4 s, reply 3.8 s, done 5.1 s.

## Verified: first tool-using turn (2026-11-27, 0.1.9 on the `win11-seeded` VM)

- VM restored from `win11-seeded` (58-file Hebrew/English corpus in Documents and Desktop, no Office, so the index holds docx file names but not docx bodies), config with both keys, `Setup.exe --silent`, 90 s wait for the indexer after boot.
- The 7 s test sentence (find the article about Ottoman Jerusalem and translate it): Claude called `search_documents` once with the query `ירושלים עות'מאנית` (prefix letter dropped as the tool description asks), which answered in 1.2 s with 844 chars of hits. Reply (Hebrew): it found two matching files, the survey in the מאמרים folder and a newer draft in Desktop stuff, said translation is not available yet, and offered to read or summarise one. Timings: STT 1.3 s, model incl. tool 5.0 s, TTS first byte 0.3 s, done at 8.4 s; 21 s of reply audio.
- The per-turn JSON in `%LocalAppData%\Shamash\pclink` records `toolCalls` (name, input, ms, result chars).

## Not verified

- Which path answered that search (index name match or the docx content walk); the result text is not recorded. The FREETEXT clause and the PersistentHandler read ran without error on the VM but were not inspected separately.
- A spoken turn from the phone (someone talking into it) and the phone playing the reply audio through its speaker; only a silent turn ran from the phone.
- Shortcuts, and SmartScreen on a browser-downloaded Setup.exe (the VM copy came over the shared folder).
- The firewall prompt itself: the rule was added before the service first listened.
- Why the delta package is not used: Velopack downloaded the full package every time, though vpk builds 7 KB deltas.
- Release assets are unsigned; Windows SmartScreen may warn on first run.
- vpk warns that `VelopackApp.Run()` "does not look like your application's entry point" because of top-level async `Main`. Updates work regardless.
