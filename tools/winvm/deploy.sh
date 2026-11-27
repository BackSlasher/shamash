#!/usr/bin/env bash
# Deploy the PC service to the test VM after a snapshot restore:
#   tools/winvm/deploy.sh <Setup.exe> [--expect-version <v>]
# Steps: wait for ssh; write config.json from .env (tools/pc-config.py, llm=claude, allowTextTurns on) and copy it
# to %LocalAppData%\Shamash; install Setup.exe silently; remove the config from the shared folder; apply the
# Outlook object-model-guard policy for this VM user (see README "Outlook object-model guard on the VM");
# wait for the indexer and the service; print the version the service logs. Secrets are never printed.
set -euo pipefail
cd "$(dirname "$0")/../.."
setup="${1:?Setup.exe path}"; shift || true
expect=""
while [ $# -gt 0 ]; do case "$1" in --expect-version) expect="$2"; shift 2;; *) echo "unknown arg $1" >&2; exit 2;; esac; done
vm_dir="$HOME/vm/shamash-win11"
ssh=tools/winvm/ssh.sh

echo "waiting for ssh"
for _ in $(seq 1 40); do
  if "$ssh" '"up"' 2>/dev/null | grep -q up; then break; fi
  python3 -c 'import time; time.sleep(6)'
done
"$ssh" '"up"' 2>/dev/null | grep -q up || { echo "VM ssh not reachable" >&2; exit 3; }

echo "config"
python3 tools/pc-config.py "$vm_dir/shared/config.json" --llm claude >/dev/null
python3 - "$vm_dir/shared/config.json" <<'EOF'
import json, sys
p = sys.argv[1]; c = json.load(open(p, encoding="utf-8")); c["allowTextTurns"] = True
json.dump(c, open(p, "w", encoding="utf-8"), indent=2)
EOF
"$ssh" 'New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\Shamash" | Out-Null; Copy-Item \\host.lan\Data\config.json "$env:LOCALAPPDATA\Shamash\config.json" -Force; "config copied"' | tail -1
rm -f "$vm_dir/shared/config.json"

echo "install"
cp "$setup" "$vm_dir/shared/Setup.exe"
"$ssh" 'Copy-Item \\host.lan\Data\Setup.exe C:\Users\shamash\Setup.exe -Force; Start-Process C:\Users\shamash\Setup.exe -ArgumentList "--silent" -Wait; "installed"' | tail -1
rm -f "$vm_dir/shared/Setup.exe"

echo "outlook policy"
"$ssh" '$k = "HKCU:\Software\Policies\Microsoft\Office\16.0\Outlook\Security"; New-Item -Path $k -Force | Out-Null; New-ItemProperty -Path $k -Name ObjectModelGuard -Value 2 -PropertyType DWord -Force | Out-Null; New-ItemProperty -Path $k -Name AdminSecurityMode -Value 3 -PropertyType DWord -Force | Out-Null; Remove-Item "HKCU:\Software\Microsoft\Office\16.0\Outlook\Resiliency" -Recurse -Force -ErrorAction SilentlyContinue; "policy set"' | tail -1

echo "waiting ~75 s for the indexer and the service"
python3 -c 'import time; time.sleep(75)'
ver=$("$ssh" 'Get-ChildItem "$env:LOCALAPPDATA\Shamash\logs\*.log" | Sort-Object LastWriteTime | Select-Object -Last 1 | ForEach-Object { (Get-Content $_.FullName | Select-String -Pattern "heartbeat, version (\S+)|after-install (\S+)" | Select-Object -Last 1).Matches[0].Groups | Where-Object { $_.Value -and $_.Name -ne "0" } | Select-Object -Last 1 -ExpandProperty Value }' 2>/dev/null | tr -d '\r' | tail -1)
echo "installed version: ${ver:-unknown}"
if [ -n "$expect" ] && [[ "${ver:-}" != "$expect"* ]]; then echo "expected $expect" >&2; exit 4; fi
