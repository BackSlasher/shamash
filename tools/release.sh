#!/usr/bin/env bash
# Build a signed release APK and print (or with --publish, run) the `gh release create` command.
#
# Required env: SHAMASH_KEYSTORE_FILE, SHAMASH_KEYSTORE_PASSWORD, SHAMASH_KEY_ALIAS,
#               SHAMASH_KEY_PASSWORD, SHAMASH_VERSION_NAME, SHAMASH_VERSION_CODE
# Always sign with the same keystore, or Obtainium/Android will refuse the update.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

for v in SHAMASH_KEYSTORE_FILE SHAMASH_KEYSTORE_PASSWORD SHAMASH_KEY_ALIAS SHAMASH_KEY_PASSWORD \
         SHAMASH_VERSION_NAME SHAMASH_VERSION_CODE; do
  [ -n "${!v:-}" ] || { echo "missing env var $v" >&2; exit 1; }
done

# Refuse to release uncommitted work (jj has no staging area: any diff in @ is uncommitted).
if [ -n "$(jj diff --summary)" ]; then
  echo "working copy has uncommitted changes; run 'jj commit' first" >&2
  exit 1
fi

export SHAMASH_VERSION_NAME SHAMASH_VERSION_CODE SHAMASH_KEYSTORE_FILE
./build.sh assembleRelease

built=android/app/build/outputs/apk/release/app-release.apk
[ -f "$built" ] || { echo "no signed APK at $built (signing config not applied?)" >&2; exit 1; }
apk="android/app/build/outputs/apk/release/shamash-$SHAMASH_VERSION_NAME.apk"
cp "$built" "$apk"

cmd=(gh release create "v$SHAMASH_VERSION_NAME" "$apk"
     --title "Shamash $SHAMASH_VERSION_NAME" --notes "Shamash $SHAMASH_VERSION_NAME (versionCode $SHAMASH_VERSION_CODE)")
if [ "${1:-}" = "--publish" ]; then
  "${cmd[@]}"
else
  printf '%q ' "${cmd[@]}"; echo
fi
