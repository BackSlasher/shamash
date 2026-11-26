#!/usr/bin/env bash
# Run client.py inside the pclink image (which has the websockets package).
#   tools/pclink/client.sh ws://192.168.1.138:8777/ /path/in.wav /path/out.wav [--fast] [--probe]
#   tools/pclink/client.sh ws://192.168.1.138:8777/ --text "תפתח את המכתב לעירייה" [/path/out.wav]
# Host paths for the WAVs are mounted; the output WAV lands next to the input.
# --text is the test aid (PC needs allowTextTurns): no input WAV, hello carries the transcript.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
url="$1"; shift
in=""; text=()
if [ "$#" -gt 0 ] && [[ "$1" == "--text" ]]; then
  [ "$#" -ge 2 ] || { echo "--text needs a value" >&2; exit 2; }
  text=(--text "$2"); shift 2
else
  in="$(realpath "$1")"; shift
fi
out=""
if [ "$#" -gt 0 ] && [[ "$1" != --* ]]; then out="$1"; shift; fi
indir="$(dirname "$in")"
[ -n "$in" ] || { indir="$(pwd)"; [ -z "$out" ] || indir="$(dirname "$(realpath "$out")")"; }
podman image exists localhost/shamash-pclink || podman build -t localhost/shamash-pclink -f "$here/Containerfile" "$here" >/dev/null
args=("ws://host.containers.internal:${url##*:}")
[[ "$url" == ws://127.0.0.1* || "$url" == ws://localhost* ]] || args[0]="$url"
[ -n "$in" ] && args+=("/data/$(basename "$in")")
[ -n "$out" ] && args+=("/data/$(basename "$out")")
exec podman run --rm --userns=keep-id --user "$(id -u):$(id -g)" --network host \
  -v "$indir:/data:Z" -v "$here/client.py:/app/client.py:ro,Z" localhost/shamash-pclink \
  python -u /app/client.py "${args[@]}" ${text[@]+"${text[@]}"} "$@"
