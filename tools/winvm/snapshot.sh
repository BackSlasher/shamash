#!/usr/bin/env bash
# Whole-VM snapshots for the Windows test VM (tools/winvm/run.sh).
#
#   tools/winvm/snapshot.sh save <name>      # shut the VM down, copy its storage to snapshots/<name>, start it again
#   tools/winvm/snapshot.sh restore <name>   # shut down, replace storage with the snapshot, start
#   tools/winvm/snapshot.sh list
#   tools/winvm/snapshot.sh delete <name>
#
# Storage is a raw 64 GiB disk plus UEFI vars; the copy is `cp --reflink=auto`, which on btrfs/XFS is
# instant and shares blocks until either side is written. Snapshots live in $SHAMASH_WINVM_DIR/snapshots.
# The VM is stopped through `podman stop` (dockur sends Windows an ACPI shutdown; up to 120 s).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
vm_dir="${SHAMASH_WINVM_DIR:-$HOME/vm/shamash-win11}"
snaps="$vm_dir/snapshots"
name="shamash-win11"

stop_vm() {
  if podman container exists "$name" && [ "$(podman inspect -f '{{.State.Running}}' "$name")" = true ]; then
    echo "stopping $name (Windows shutdown)..."
    podman stop "$name" >/dev/null
  fi
}

copy_dir() { # src dst
  rm -rf "$2"
  mkdir -p "$2"
  cp -a --reflink=auto "$1"/. "$2"/
}

case "${1:-}" in
  save)
    [ -n "${2:-}" ] || { echo "usage: $0 save <name>" >&2; exit 2; }
    [ ! -e "$snaps/$2" ] || { echo "snapshot $2 exists; delete it first" >&2; exit 1; }
    stop_vm
    copy_dir "$vm_dir/storage" "$snaps/$2"
    date -Is > "$snaps/$2/.saved"
    echo "saved $snaps/$2"
    "$here/run.sh" >/dev/null && echo "VM starting again"
    ;;
  restore)
    [ -n "${2:-}" ] && [ -d "$snaps/$2" ] || { echo "usage: $0 restore <name>; have: $(ls "$snaps" 2>/dev/null | tr '\n' ' ')" >&2; exit 2; }
    stop_vm
    copy_dir "$snaps/$2" "$vm_dir/storage"
    rm -f "$vm_dir/storage/.saved"
    echo "restored $2"
    "$here/run.sh" >/dev/null && echo "VM starting (allow ~1 min before ssh.sh works)"
    ;;
  list)
    for d in "$snaps"/*/; do
      [ -d "$d" ] || continue
      printf '%s\t%s\t%s\n' "$(basename "$d")" "$(cat "$d/.saved" 2>/dev/null)" "$(du -sh "$d" 2>/dev/null | cut -f1)"
    done
    ;;
  delete)
    [ -n "${2:-}" ] && [ -d "$snaps/$2" ] || { echo "no snapshot ${2:-}" >&2; exit 2; }
    rm -rf "$snaps/$2"
    echo "deleted $2"
    ;;
  *)
    sed -n '2,10p' "$0"; exit 2 ;;
esac
