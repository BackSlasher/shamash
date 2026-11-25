# Post-install setup for the Windows 11 test VM: OpenSSH server with key auth, no sleep.
$ErrorActionPreference = 'Continue'
Write-Output "setup.ps1 start $(Get-Date -Format o) as $env:USERNAME"

# 1. OpenSSH server: Windows capability first, GitHub release zip as fallback.
try {
    Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0 | Out-String | Write-Output
} catch { Write-Output "Add-WindowsCapability failed: $_" }

if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) {
    Write-Output "sshd missing, installing Win32-OpenSSH from GitHub"
    $zip = "$env:TEMP\OpenSSH-Win64.zip"
    Invoke-WebRequest -Uri 'https://github.com/PowerShell/Win32-OpenSSH/releases/latest/download/OpenSSH-Win64.zip' -OutFile $zip
    Expand-Archive -Path $zip -DestinationPath 'C:\Program Files' -Force
    Rename-Item 'C:\Program Files\OpenSSH-Win64' 'C:\Program Files\OpenSSH' -Force
    & powershell -NoProfile -ExecutionPolicy Bypass -File 'C:\Program Files\OpenSSH\install-sshd.ps1' | Write-Output
}

Set-Service sshd -StartupType Automatic
Start-Service sshd
New-NetFirewallRule -Name sshd -DisplayName 'OpenSSH Server' -Enabled True -Direction Inbound `
    -Protocol TCP -Action Allow -LocalPort 22 -ErrorAction SilentlyContinue | Out-Null

# 2. Key for the Administrators group (the dockur user is an administrator).
New-Item -ItemType Directory -Path C:\ProgramData\ssh -Force | Out-Null
Copy-Item C:\OEM\administrators_authorized_keys C:\ProgramData\ssh\administrators_authorized_keys -Force
icacls C:\ProgramData\ssh\administrators_authorized_keys /inheritance:r /grant 'Administrators:F' /grant 'SYSTEM:F' | Out-Null

# 3. PowerShell as the SSH shell.
New-Item -Path 'HKLM:\SOFTWARE\OpenSSH' -Force | Out-Null
New-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell `
    -Value 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -PropertyType String -Force | Out-Null
Restart-Service sshd

# 4. Never sleep or blank: the real PC is always on too.
powercfg /change standby-timeout-ac 0
powercfg /change monitor-timeout-ac 0
powercfg /hibernate off

Write-Output "sshd status: $((Get-Service sshd).Status)"
Write-Output "setup.ps1 done $(Get-Date -Format o)"
