# Unattended Office 2016 Pro Plus install on the test VM from the volume ISO in \\host.lan\Data\office.
# Run over tools/winvm/ssh.sh (elevated session). Copies the ISO locally, mounts it, runs setup with
# config.xml (Word, Excel, Outlook only, no key, grace period), then reports what got installed.
$ErrorActionPreference = 'Stop'
$src = '\\host.lan\Data\office'
$iso = Get-ChildItem "$src\*.ISO" | Select-Object -First 1
$local = "C:\office2016.iso"
if (-not (Test-Path $local)) { Copy-Item $iso.FullName $local }
Copy-Item "$src\config.xml" "C:\office-config.xml" -Force
$img = Mount-DiskImage -ImagePath $local -PassThru
$drive = ($img | Get-Volume).DriveLetter + ':'
"mounted $drive"
$setup = Join-Path $drive 'setup.exe'
$t0 = Get-Date
$p = Start-Process -FilePath $setup -ArgumentList "/config C:\office-config.xml" -Wait -PassThru
"setup exit $($p.ExitCode) after $([int]((Get-Date) - $t0).TotalSeconds) s"
Dismount-DiskImage -ImagePath $local | Out-Null
$ospp = Get-ChildItem "C:\Program Files\Microsoft Office\Office16\WINWORD.EXE","C:\Program Files (x86)\Microsoft Office\Office16\WINWORD.EXE" -ErrorAction SilentlyContinue | Select-Object -First 1
"winword: " + $(if ($ospp) { $ospp.FullName } else { "not found" })
"outlook: " + (Test-Path "C:\Program Files\Microsoft Office\Office16\OUTLOOK.EXE")
"docx PersistentHandler: " + (Get-ItemProperty 'Registry::HKEY_CLASSES_ROOT\.docx\PersistentHandler' -ErrorAction SilentlyContinue).'(default)'
