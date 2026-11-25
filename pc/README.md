# Shamash PC service (scaffold)

A .NET 10 Generic Host worker for the Windows PC. Currently a placeholder that proves the build, package and unattended-update pipeline. The real service (speech recognition, Claude agent, TTS) is not here yet.

## Contents

- `Shamash.Pc/` - the app. `Program.cs` calls `VelopackApp.Build().Run()` first, then starts the host.
  - `UpdateService` - on startup and every 6 hours checks `https://github.com/BackSlasher/shamash` (GitHub Releases, stable only) via Velopack. If an update exists it is downloaded, then applied with `ApplyUpdatesAndRestart` only inside the update window. If `UpdateManager.IsInstalled` is false it logs "not installed via Velopack, skipping updates" and does nothing.
  - `UpdateWindow` - pure static-style parsing/containment logic (unit tested).
  - `HeartbeatService` - logs the assembly informational version once a minute.
- `Shamash.Pc.Tests/` - xunit tests for `UpdateWindow` (same-day window, window crossing midnight, empty window, malformed input falls back to the 03:00-05:00 default).
- `build.sh` - runs everything in podman (`mcr.microsoft.com/dotnet/sdk:10.0`), caches in volume `shamash-dotnet-cache` (NuGet packages in `/cache/nuget`, `vpk` in `/cache/tools`).

## Logging choice

Serilog (`Serilog.Extensions.Hosting`, `Serilog.Sinks.Console`, `Serilog.Sinks.File`). Microsoft.Extensions.Logging has no built-in file provider, and a rolling daily file (14 kept) is needed because the user never sees a console. Logs go to `%LocalAppData%\Shamash\logs\shamash-YYYYMMDD.log`, or `logs\` next to the exe if that folder is unavailable.

## Commands

All from the repo root; only podman is needed on the host.

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

## Not verified

- Anything running on Windows: that the exe starts, that Setup.exe installs, shortcuts, per-user install location, uninstall.
- The actual update cycle (check, download, apply, restart), the update window behaviour at runtime, and `IsInstalled` detection. The GitHub source reading `releases.win.json` from the real repo is untested until a second release exists.
- Release assets are unsigned; Windows SmartScreen may warn on first run.
- vpk warns that `VelopackApp.Run()` "does not look like your application's entry point" because of top-level async `Main`. It is the first statement; whether this matters on Windows is unverified.
- Delta packages do not exist yet (first release); delta updates are untested.
