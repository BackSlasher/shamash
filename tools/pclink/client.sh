#!/usr/bin/env bash
# Run client.py inside the pclink image (which has the websockets package).
#   tools/pclink/client.sh ws://192.168.1.138:8777/ /path/in.wav /path/out.wav [--fast] [--probe]
# Host paths for the WAVs are mounted; the output WAV lands next to the input.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
url="$1"; in="$(realpath "$2")"; shift 2
out=""
if [ "$#" -gt 0 ] && [[ "$1" != --* ]]; then out="$1"; shift; fi
indir="$(dirname "$in")"
podman image exists localhost/shamash-pclink || podman build -t localhost/shamash-pclink -f "$here/Containerfile" "$here" >/dev/null
args=("ws://host.containers.internal:${url##*:}" "/data/$(basename "$in")")
[[ "$url" == ws://127.0.0.1* || "$url" == ws://localhost* ]] || args[0]="$url"
[ -n "$out" ] && args+=("/data/$(basename "$out")")
exec podman run --rm --userns=keep-id --user "$(id -u):$(id -g)" --network host \
  -v "$indir:/data:Z" -v "$here/client.py:/app/client.py:ro,Z" localhost/shamash-pclink \
  python -u /app/client.py "${args[@]}" "$@"
