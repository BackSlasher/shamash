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

## Office 2016 on the VM

Files in `tools/winvm/office/`: `config.xml`, `install-office.ps1`, `outlook.prf`, `outlook-wizard-driver.ps1`.

- Source: the owner's Office 2016 Professional Plus 64-bit volume media (`SW_DVD5_Office_Professional_Plus_2016_64Bit_English_MLF_X20-42432.ISO`)
  on his Google Drive, pulled with `rclone copy saba-d:OFFICE2016/<name> ~/vm/shamash-win11/shared/office/`.
- Install: put the ISO, `config.xml` and `install-office.ps1` in `shared/office/`, then `tools/winvm/ssh.sh '\\host.lan\Data\office\install-office.ps1'`
  (copies and mounts the ISO, runs `setup.exe /config C:\office-config.xml`, Word + Excel + Outlook only, no restart).
  Office is at `C:\Program Files\Microsoft Office\Office16`.
- No product key, by owner decision: it runs unactivated in the grace period. `cscript ospp.vbs` cannot run over SSH
  (WSH memory error), so the activation state cannot be queried that way.
- Right after setup PowerShell failed to start ("Starting the CLR failed with HRESULT 80004005"), which killed every SSH
  session until `podman restart shamash-win11`. The same SSH wedge (banner timeout, then connection reset) happened again
  at the end of the Outlook work, right after `Stop-Process -Name WINWORD -Force` followed by Word COM calls; treat Office
  COM plus forced kills as a trigger and expect a container restart.
- First-run registry keys (HKCU): `Software\Microsoft\Office\16.0\Common\General\ShownFirstRunOptin=1`,
  `Software\Microsoft\Office\16.0\Registration\AcceptAllEulas=1` (set during the install work), and for Outlook
  `Software\Microsoft\Office\16.0\Outlook\Setup\First-Run=1` and `FirstRun=1`.
- Outlook profile `Shamash`, one PST and no mail account, PST at
  `C:\Users\shamash\Documents\Outlook Files\<account>.pst` (copy of the live file, 937 MB; it opened without repair).
  What it took (the PRF alone was not enough):
  1. `outlook.exe /importprf C:\outlook.prf` (file in `office/outlook.prf`, CRLF line endings; copy it from
     `\\host.lan\Data\outlook.prf`). This creates the profile and an empty "Outlook data file" service but did NOT apply
     `PathToPersonalFolders` or `Name`, even with Section-6 style mappings (`PathToPersonalFolders=PT_STRING8,6700`); cause not found.
  2. Write the path into the service key (the one with `001f3d09=MSUPST MS` and `001f300a=mspst.dll`) under
     `HKCU:\Software\Microsoft\Office\16.0\Outlook\Profiles\Shamash\<guid>` as binary UTF-16 value `001f6700`
     (plus `001f3001`=display name), and set `HKCU:\Software\Microsoft\Office\16.0\Outlook` `DefaultProfile`=`Shamash`.
  3. Outlook still shows the "Welcome to Microsoft Outlook 2016" wizard because the profile has no account, and it blocks COM.
     Start Outlook in the interactive session (scheduled task, principal `shamash`, `LogonType Interactive`; an Outlook
     started from SSH runs in session 0 with invisible dialogs) and click through: Next, No, Next, "Use Outlook without an
     email account", Next, Finish. `office/outlook-wizard-driver.ps1` does this by sending messages to the dialog controls
     (names listed in `C:\click.txt`, one per line). The wizard does not come back afterwards.
  4. A force-killed Outlook makes the next start ask "start in safe mode?" (hidden in session 0 and hangs COM): delete
     `HKCU:\Software\Microsoft\Office\16.0\Outlook\Resiliency` before launching.
  There is no `Windows Messaging Subsystem\Profiles` key for Outlook 2016; the profile lives under `Office\16.0\Outlook\Profiles`.
- COM check over SSH works once the profile is right: `(New-Object -ComObject Outlook.Application).GetNamespace("MAPI")`
  gives 1 store (`<account>`), Inbox 2263 items, 1045 unread via `Items.Restrict("[UnRead] = true")`, newest received
  2026-11-12. `CurrentProfileName` read back empty from SSH, but the store is there. A second `New-Object` while the first
  COM-started Outlook is still alive fails with 0x80080005; kill Outlook between runs (and clear `Resiliency`).
- `Get-ChildItem -Filter *.doc` also returns `.docx` (8.3 matching); filter on `.Extension`.
- Snapshot `win11-office` (saved 2026-11-27 02:51): seeded corpus + real Desktop sample from the saba mirror + the PST +
  Office 2016 Word/Excel/Outlook; no Shamash, no secrets, no Outlook profile. It predates the profile above, so after a
  restore the profile steps (PRF import, registry path, wizard click-through) must be redone, or take a new snapshot once done.

### Outlook object-model guard on the VM

Outlook warns "A program is trying to access e-mail address information" when Security Center does not report a
current antivirus. On the VM that happens on every boot for the first minutes (Defender itself reports signature age 0),
so every deploy applies the policy that disables the warning for this user:
`HKCU:\Software\Policies\Microsoft\Office\16.0\Outlook\Security` with `ObjectModelGuard=2` and `AdminSecurityMode=3`
(DWORDs), and deletes `HKCU:\Software\Microsoft\Office\16.0\Outlook\Resiliency` so a killed Outlook does not ask
about safe mode. The `win11-office` snapshot (re-saved 2026-11-27 03:54) already holds the Outlook profile "Shamash"
(PST only) but not this policy; set it after a restore. On the owner's PC the policy is deliberately not applied unless
the prompt proves recurrent there.
