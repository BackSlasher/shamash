#!/usr/bin/env bash
# Build the PC service inside a podman container (nothing needed on the host
# except podman).
#
#   pc/build.sh test      # run unit tests
#   pc/build.sh publish   # win-x64 self-contained single file -> pc/out/publish
#   pc/build.sh pack      # vpk pack -> pc/out/releases
#   pc/build.sh upload    # vpk upload github (needs GITHUB_TOKEN or `gh auth token`)
#   pc/build.sh vpk ...   # run vpk with arbitrary arguments
#
# Environment: SHAMASH_VERSION_NAME (default 0.1.0), GITHUB_TOKEN (upload only).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image="mcr.microsoft.com/dotnet/sdk:10.0"
cache_volume="shamash-dotnet-cache"
vpk_version="1.2.161"   # keep equal to the Velopack package version in Shamash.Pc.csproj
version="${SHAMASH_VERSION_NAME:-0.1.0}"
repo_url="https://github.com/BackSlasher/shamash"

if ! podman volume exists "$cache_volume"; then
  podman volume create "$cache_volume" >/dev/null
fi

cmd="${1:-}"
[ "$#" -gt 0 ] && shift

# Script run inside the container. Installs vpk into the cache volume on first use.
inner='
set -euo pipefail
export PATH="$PATH:/cache/tools"
if ! command -v vpk >/dev/null 2>&1 || [ "$(vpk --version 2>/dev/null | tail -n1)" != "$VPK_VERSION" ]; then
  dotnet tool update --tool-path /cache/tools vpk --version "$VPK_VERSION" >&2
fi
'

run_args=(
  --rm
  --userns=keep-id
  --user "$(id -u):$(id -g)"
  -v "$repo_root:/work:Z"
  -v "$cache_volume:/cache:Z"
  -w /work/pc
  -e HOME=/cache/home
  -e DOTNET_CLI_HOME=/cache/home
  -e NUGET_PACKAGES=/cache/nuget
  -e DOTNET_NOLOGO=1
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1
  -e VPK_VERSION="$vpk_version"
  -e SHAMASH_VERSION_NAME="$version"
)
if [ -t 0 ] && [ -t 1 ]; then
  run_args+=(-it)
fi

run() { podman run "${run_args[@]}" "$image" bash -c "$1" _ "${@:2}"; }

case "$cmd" in
  test)
    run 'set -euo pipefail; dotnet test Shamash.Pc.Tests/Shamash.Pc.Tests.csproj -c Release "$@"' "$@"
    ;;
  publish)
    run 'set -euo pipefail; rm -rf out/publish; dotnet publish Shamash.Pc/Shamash.Pc.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out/publish "$@"' "$@"
    ;;
  pack)
    run "$inner"'
vpk "[win]" pack --packId Shamash.Pc --packVersion "$SHAMASH_VERSION_NAME" --packDir out/publish --mainExe Shamash.Pc.exe --runtime win-x64 --channel win --outputDir out/releases "$@"
' "$@"
    ;;
  upload)
    token="${GITHUB_TOKEN:-}"
    if [ -z "$token" ] && command -v gh >/dev/null 2>&1; then
      token="$(gh auth token 2>/dev/null || true)"
    fi
    if [ -z "$token" ]; then
      echo "No GITHUB_TOKEN set and 'gh auth token' gave nothing." >&2
      exit 1
    fi
    run_args+=(-e GITHUB_TOKEN="$token")
    run "$inner"'
vpk "[win]" upload github --repoUrl '"$repo_url"' --publish --merge --releaseName "Shamash $SHAMASH_VERSION_NAME" --tag "v$SHAMASH_VERSION_NAME" --token "$GITHUB_TOKEN" --channel win --outputDir out/releases "$@"
' "$@"
    ;;
  vpk)
    run "$inner"'vpk "$@"' "$@"
    ;;
  *)
    echo "usage: pc/build.sh {test|publish|pack|upload|vpk ...}" >&2
    exit 2
    ;;
esac
