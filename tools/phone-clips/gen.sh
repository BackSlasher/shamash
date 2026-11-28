#!/usr/bin/env bash
# Regenerate the Hebrew clips bundled in the phone app (android/app/src/main/res/raw).
# Needs ELEVENLABS_KEY, read from <repo>/.env (or the file named by SHAMASH_ENV); never printed.
# Spends a few ElevenLabs credits per run; the clips are committed, so run it only to change wording or voice.
#
#   tools/phone-clips/gen.sh
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
envfile="${SHAMASH_ENV:-$repo/.env}"
if [ -f "$envfile" ]; then
  set -a
  # shellcheck disable=SC1090
  . "$envfile"
  set +a
fi
: "${ELEVENLABS_KEY:?ELEVENLABS_KEY missing (put it in .env)}"

# Same voice and model as the PC's spoken replies (ShamashSettings.DefaultVoiceId, Conversation.cs).
voice="${SHAMASH_TTS_VOICE:-JBFqnCBsd6RMkjVDRZzb}"
out="$repo/android/app/src/main/res/raw"
mkdir -p "$out"

gen() { # <file name> <text>
  local name="$1" text="$2" body tmp
  body="$(python3 -c 'import json,sys; print(json.dumps({"text": sys.argv[1], "model_id": "eleven_v4_turbo", "language_code": "he"}))' "$text")"
  tmp="$(mktemp)"
  code="$(curl -sS -o "$tmp" -w '%{http_code}' -X POST \
    "https://api.elevenlabs.io/v1/text-to-speech/$voice?output_format=mp3_44100_64" \
    -H "xi-api-key: $ELEVENLABS_KEY" -H 'Content-Type: application/json' -d "$body")"
  if [ "$code" != 200 ]; then
    echo "$name: HTTP $code" >&2; head -c 300 "$tmp" >&2; echo >&2; rm -f "$tmp"; return 1
  fi
  mv "$tmp" "$out/$name"
  echo "wrote $out/$name ($(stat -c %s "$out/$name") bytes)"
}

gen not_charging.mp3 'הטלפון לא בטעינה, בדוק את הכבל'
