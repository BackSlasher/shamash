#!/usr/bin/env bash
# Run a PowerShell command in the Windows test VM (or open a shell with no arguments).
set -euo pipefail
exec ssh -i "$HOME/.ssh/shamash-vm" -p 2222 -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null \
  -o LogLevel=ERROR -o ConnectTimeout=10 shamash@127.0.0.1 "$@"
