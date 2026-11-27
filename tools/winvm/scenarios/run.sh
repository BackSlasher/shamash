#!/usr/bin/env bash
# Scenario suite for the PC service on the win11-office VM. See README.md.
#   run.sh [--restore] [--install <Setup.exe>] [--expect-version <v>] [--only <name>] [--url ws://127.0.0.1:8777/] [--tts]
# config.json (API keys, allowTextTurns=true, document roots) is NOT handled here: the maintainer
# must have deployed it to the VM (and, with --restore, into the snapshot or after the restore).
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
winvm="$(dirname "$here")"
vm_dir="${SHAMASH_WINVM_DIR:-$HOME/vm/shamash-win11}"
restore=0 tts="" setup="" only="" expect="" url="ws://127.0.0.1:8777/"
while [ $# -gt 0 ]; do
  case "$1" in
    --restore) restore=1 ;;
    --install) setup="$(realpath "$2")"; shift ;;
    --expect-version) expect="$2"; shift ;;
    --only) only="$2"; shift ;;
    --url) url="$2"; shift ;;
    --tts) tts="--tts" ;;
    *) sed -n '2,6p' "$0"; exit 2 ;;
  esac
  shift
done
pause() { python3 -c 'import time,sys; time.sleep(float(sys.argv[1]))' "$1"; }
wait_ssh() {
  for _ in $(seq 1 60); do "$winvm/ssh.sh" 'Get-Date' >/dev/null 2>&1 && return 0; pause 6; done
  echo "VM did not answer ssh" >&2; return 1
}

if [ "$restore" = 1 ]; then "$winvm/snapshot.sh" restore win11-office || exit 1; pause 30; fi
wait_ssh || exit 1
if [ -n "$setup" ]; then
  mkdir -p "$vm_dir/shared" && cp "$setup" "$vm_dir/shared/Setup.exe" || exit 1
  "$winvm/ssh.sh" 'Copy-Item \\host.lan\Data\Setup.exe C:\Users\shamash\Setup.exe -Force; Start-Process C:\Users\shamash\Setup.exe -ArgumentList "--silent" -Wait' || exit 1
  echo "waiting ~75 s for the indexer and the service"; pause 75
fi

# Which version is running? Newest log, last "heartbeat, version X" or "after-install X".
version="$("$winvm/ssh.sh" '$f = Get-ChildItem $env:LocalAppData\Shamash\logs\*.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1; if ($f) { $m = Select-String -Path $f.FullName -Pattern "heartbeat, version (\S+)|after-install (\S+)" | Select-Object -Last 1; if ($m) { $g = $m.Matches[0].Groups; if ($g[1].Success) { $g[1].Value } else { $g[2].Value } } }' 2>/dev/null | tr -d '\r' | tail -n 1)"
version="${version%,}"
echo "installed version: ${version:-unknown}"
if [ -n "$expect" ] && [[ "$version" != "$expect"* ]]; then
  echo "expected version $expect, found ${version:-unknown}" >&2; exit 4
fi

fail=0 total=0
for f in "$here"/*.json; do
  name="$(basename "$f" .json)"
  [ -z "$only" ] || [ "$only" = "$name" ] || continue
  total=$((total+1))
  python3 "$here/check.py" "$f" --url "$url" ${tts:+"$tts"} || fail=$((fail+1))
  if grep -q 'Start-Service WSearch' "$f"; then echo "waiting 70 s for the search index"; pause 70; fi
done
[ "$total" -gt 0 ] || { echo "no scenario matched" >&2; exit 2; }
echo "$((total-fail))/$total scenarios passed"
[ "$fail" -eq 0 ]
