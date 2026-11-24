#!/usr/bin/env bash
# Build the Android app inside a podman container (nothing needed on the host
# except podman).
#
#   ./build.sh                       # unit tests + debug APK
#   ./build.sh assembleRelease       # any args are passed to Gradle as tasks/flags
#
# Environment passed through to Gradle if set:
#   SHAMASH_VERSION_NAME, SHAMASH_VERSION_CODE
#   SHAMASH_KEYSTORE_FILE (host path; mounted read-only into the container),
#   SHAMASH_KEYSTORE_PASSWORD, SHAMASH_KEY_ALIAS, SHAMASH_KEY_PASSWORD
# ELEVENLABS_KEY is read from <repo>/.env if present (never printed) and passed in.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
containerfile="$repo_root/android/Containerfile"

if [ -f "$repo_root/.env" ]; then
  set -a
  # shellcheck disable=SC1091
  . "$repo_root/.env"
  set +a
fi

image_tag="$(sha256sum "$containerfile" | cut -c1-12)"
image="localhost/shamash-android-build:$image_tag"
cache_volume="shamash-gradle-cache"

if ! podman image exists "$image"; then
  echo ">> Building build image $image" >&2
  podman build -t "$image" -f "$containerfile" "$repo_root/android"
fi

if ! podman volume exists "$cache_volume"; then
  podman volume create "$cache_volume" >/dev/null
fi

if [ "$#" -eq 0 ]; then
  set -- testDebugUnitTest assembleDebug
fi

run_args=(
  --rm
  --userns=keep-id
  --user "$(id -u):$(id -g)"
  -v "$repo_root:/work:Z"
  -v "$cache_volume:/cache:Z"
  -w /work/android
  -e HOME=/cache/home
  -e ANDROID_USER_HOME=/cache/home/.android
  -e JAVA_TOOL_OPTIONS=-Duser.home=/cache/home
)
if [ -t 0 ] && [ -t 1 ]; then
  run_args+=(-it)
fi

for var in SHAMASH_VERSION_NAME SHAMASH_VERSION_CODE SHAMASH_KEYSTORE_PASSWORD SHAMASH_KEY_ALIAS SHAMASH_KEY_PASSWORD; do
  if [ -n "${!var:-}" ]; then
    run_args+=(-e "$var")
  fi
done

if [ -n "${ELEVENLABS_KEY:-}" ]; then
  run_args+=(-e ELEVENLABS_KEY)
fi

if [ -n "${SHAMASH_KEYSTORE_FILE:-}" ]; then
  keystore_host="$(realpath "$SHAMASH_KEYSTORE_FILE")"
  if [ ! -f "$keystore_host" ]; then
    echo "SHAMASH_KEYSTORE_FILE does not exist: $SHAMASH_KEYSTORE_FILE" >&2
    exit 1
  fi
  run_args+=(-v "$keystore_host:/keystore/release.keystore:ro,Z" -e SHAMASH_KEYSTORE_FILE=/keystore/release.keystore)
fi

exec podman run "${run_args[@]}" "$image" ./gradlew --console=plain "$@"
