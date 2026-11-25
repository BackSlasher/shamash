#!/usr/bin/env bash
# Grab the VM screen as PNG through the QEMU monitor socket: tools/winvm/screenshot.sh out.png
set -euo pipefail
out="${1:-screen.png}"
podman exec -i shamash-win11 python3 - <<'EOF'
import socket, time
s = socket.socket(socket.AF_UNIX); s.connect("/run/shm/monitor.sock"); s.settimeout(0.3)
def drain():
    try:
        while s.recv(4096): pass
    except socket.timeout: pass
drain(); s.sendall(b"screendump /tmp/screen.ppm\n"); time.sleep(1); drain()
EOF
podman cp shamash-win11:/tmp/screen.ppm "${out%.png}.ppm"
magick "${out%.png}.ppm" "$out"
rm -f "${out%.png}.ppm"
echo "$out"
