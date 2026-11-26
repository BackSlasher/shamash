# Windows 11 test VM

A throwaway Windows 11 Pro VM for exercising the PC service installer and updater. It runs
[dockur/windows](https://github.com/dockur/windows) (QEMU + KVM) inside rootless podman, so
nothing is installed on the host. Verified 2026-11-25 on Arch, podman 6.1, kernel 7.2, btrfs home.

```
tools/winvm/run.sh                 # start (first run: ~8 GB download + unattended install)
tools/winvm/ssh.sh 'Get-Date'      # PowerShell over SSH, key in ~/.ssh/shamash-vm
tools/winvm/screenshot.sh out.png  # screen grab through the QEMU monitor
podman exec -i shamash-win11 python3 - 'KEY:meta_l-r' 'WAIT:2' 'TYPE:notepad' 'KEY:ret' < tools/winvm/sendkeys.py
podman stop shamash-win11          # clean ACPI shutdown (up to 2 min)
```

Web viewer: <http://127.0.0.1:8006/>. RDP: 127.0.0.1:3389, user `shamash`, password `shamash`.
Disk image and state: `~/vm/shamash-win11/` (`SHAMASH_WINVM_DIR` overrides). `shared/` there is
`\\host.lan\Data` inside Windows, which is how files get in and out.

## Facts learned

- First boot to a usable desktop with SSH took 72 min on a 1260P laptop (download included). The
  image uses 13 GB after install; the virtual disk is 64 GB.
- `oem/install.bat` is copied to `C:\OEM` and run in the last setup step as the created user
  (`shamash`, an administrator). `setup.ps1` installed the OpenSSH capability in 12 min, set
  `administrators_authorized_keys`, made PowerShell the SSH shell and disabled sleep. Its log is
  `C:\OEM\setup.log`.
- Rootless podman means dockur runs "User (passt)" networking. Only RDP is forwarded into the
  guest by default; SSH needs `USER_PORTS=22` (now in `run.sh`). Without it the host port accepts
  the connection and the guest resets it, which looks like a hung install.
- On btrfs, `chattr +C` the storage directory before the first run (dockur warns about CoW).
- LAN access to a guest port: publish it on `0.0.0.0` and list it in `USER_PORTS` (8777 for the phone link, in `run.sh`). The host listener is pasta's. A phone probe sent before the guest service was listening hit a connect timeout rather than a refusal; once the service listened, connections from the phone succeeded. Changing `-p`/`USER_PORTS` needs a container restart, which reboots Windows (about a minute, autologin brings the logon task back).
- Inside Windows, open the port once: `New-NetFirewallRule -DisplayName "Shamash link 8777" -Direction Inbound -Protocol TCP -LocalPort 8777 -Action Allow -Profile Any` over SSH (the SSH session is elevated).
- The QEMU monitor socket at `/run/shm/monitor.sock` in the container accepts `sendkey` and
  `screendump`, which is enough to drive the desktop without the web viewer, including the Run
  dialog and UAC prompts. `KEY:meta_l-b` did not focus the notification area (it opened Start).
  Notification icons can be inspected and promoted over SSH instead: `HKCU:\Control Panel\NotifyIconSettings\<id>` has
  `ExecutablePath` and `IsPromoted` (DWORD; 1 = always on the taskbar, otherwise it sits in the overflow).
- Over SSH the session is a normal user session: Velopack's `Setup.exe --silent` installed to
  `%LocalAppData%\Shamash.Pc` with exit code 0 and no prompt. A logon scheduled task with no
  execution time limit starts the stub `Shamash.Pc.exe`, which spawns `current\Shamash.Pc.exe`
  and exits, so the task reports Ready while the app runs. Since 0.1.7 the installer creates that
  task itself (`pc/Shamash.Pc/StartupTask.cs`) and the uninstaller removes it.

## Snapshots (`snapshot.sh`)

The test loop is: restore a known snapshot, install Shamash from a release or a local pack, test,
throw the result away.

```
tools/winvm/snapshot.sh save <name>      # clean Windows shutdown, copy storage/, start the VM again
tools/winvm/snapshot.sh restore <name>   # shutdown, replace storage/ with the snapshot, start
tools/winvm/snapshot.sh list
tools/winvm/snapshot.sh delete <name>
```

Snapshots live in `$SHAMASH_WINVM_DIR/snapshots/<name>/` and are made with `cp --reflink=auto`, so on
btrfs they take seconds and no space until blocks diverge (the storage directory is `chattr +C`, which
does not prevent reflinks). After a restore allow about a minute before `ssh.sh` answers. Verified
2026-11-26: a marker file written before `restore win11-base` was gone afterwards; the restored VM
booted and answered SSH in under a minute.

Existing snapshots:

- `win11-base` (2026-11-26 23:39): Windows 11 with OpenSSH, the `shamash` account, Israel time zone, the
  8777 firewall rule, no Shamash, no secrets (the `%LocalAppData%\Shamash` config and logs were removed
  before saving; keep it that way, snapshots must never hold `config.json`).

- `win11-seeded` (2026-11-27): `win11-base` plus the `tools/seed-docs` corpus (58 files, Hebrew and English, nested
  folders, duplicates, modified times over 3 years) in `C:\Users\shamash\Documents` and 5 files on the Desktop.
  Windows Search had indexed names and (where a filter exists) content within about 5 minutes of the copy and the index
  survived the snapshot reboot. No Shamash, no `%LocalAppData%\Shamash*`, no velopack folder, no secrets. Regenerate the
  corpus with the podman command in `tools/seed-docs/gen.py`.

### Windows Search over SSH

`Search.CollatorDSO` works from PowerShell over SSH with `[Console]::OutputEncoding = [Text.Encoding]::UTF8`:

```powershell
$c = New-Object System.Data.OleDb.OleDbConnection "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';"; $c.Open()
$cmd = New-Object System.Data.OleDb.OleDbCommand "SELECT TOP 10 System.ItemPathDisplay, System.DateModified FROM SYSTEMINDEX WHERE SCOPE='file:C:\Users\shamash' AND CONTAINS(System.Search.Contents, '""קטלוג""')", $c
$r = $cmd.ExecuteReader(); while ($r.Read()) { $r[0] }
```

Findings on this image (no Office): file-name `LIKE '%עירייה%'` works; Hebrew and English content of `.pdf` and `.txt` is
searched (a UTF-8 `.txt` needs a BOM or its Hebrew is not indexed); `.docx` and `.xlsx` bodies are NOT indexed (no
PersistentHandler, no OOXML filter without Office), only their names. A word with an apostrophe such as `העות'מאנית` is
split into `העות` and `מאנית`, so query `CONTAINS(*, '"העות''מאנית"')` or `'מאנית'`, not `'עות''מאנית'` (prefix `ה`
is part of the token). Right after boot the first query can fail with E_FAIL for under a minute.

Office is not installed. Research 2026-11-26: Microsoft 365 Apps installed without a license run in
reduced functionality mode (view and print only, sign-in prompt at every launch), with no documented
grace period, so an unlicensed install is of little use for automation tests; the 30-day Microsoft 365
trials need a Microsoft account and most need a card. Decision pending: the owner is checking which
Office edition the actual user has; the test VM should get the same edition (a Microsoft 365
subscription can be signed in on up to five PCs, which could include this VM).
