#!/usr/bin/env bash
# Build and run the PC-link probe server. Usage: run.sh [outdir]  (default ./pclink-out)
# Set PCLINK_BIND=0.0.0.0 to accept connections from the LAN (Wi-Fi probe).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
outdir="$(realpath -m "${1:-./pclink-out}")"
mkdir -p "$outdir"
podman build -t localhost/shamash-pclink -f "$here/Containerfile" "$here"
exec podman run --rm --userns=keep-id --user "$(id -u):$(id -g)" \
  -p "${PCLINK_BIND:-127.0.0.1}:8777:8777" -v "$outdir:/out:Z" localhost/shamash-pclink
