#!/usr/bin/env bash
# Windows 11 test VM for the PC service, run with dockur/windows inside rootless podman (KVM).
#
#   tools/winvm/run.sh            # (re)start the VM container; first run downloads + installs Windows
#   tools/winvm/ssh.sh [cmd]      # PowerShell over SSH once setup.ps1 (in oem/) has installed OpenSSH
#   podman logs -f shamash-win11  # install progress; web viewer at http://127.0.0.1:8006
#
# Disk image and state live outside the repo in $SHAMASH_WINVM_DIR (default ~/vm/shamash-win11).
# Everything is bound to 127.0.0.1. The Windows account is shamash / shamash (throwaway test VM).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
vm_dir="${SHAMASH_WINVM_DIR:-$HOME/vm/shamash-win11}"
key="$HOME/.ssh/shamash-vm"

mkdir -p "$vm_dir/storage" "$vm_dir/shared" "$vm_dir/oem"
if [ ! -f "$key" ]; then
  ssh-keygen -q -t ed25519 -N "" -f "$key" -C shamash-vm
fi
cp "$here"/oem/* "$vm_dir/oem/"
cp "$key.pub" "$vm_dir/oem/administrators_authorized_keys"

exec podman run -d --replace --name shamash-win11 \
  --device /dev/kvm --device /dev/net/tun --cap-add NET_ADMIN \
  -e VERSION=11 -e RAM_SIZE=8G -e CPU_CORES=4 -e DISK_SIZE=64G \
  -e USERNAME=shamash -e PASSWORD=shamash -e LOG=Y -e USER_PORTS=22,8777 \
  -p 127.0.0.1:8006:8006 -p 127.0.0.1:3389:3389 -p 127.0.0.1:2222:22 -p 0.0.0.0:8777:8777 \
  -v "$vm_dir/storage:/storage:Z" -v "$vm_dir/shared:/shared:Z" -v "$vm_dir/oem:/oem:Z" \
  --stop-timeout 120 ghcr.io/dockur/windows:latest
