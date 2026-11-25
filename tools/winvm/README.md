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
- The QEMU monitor socket at `/run/shm/monitor.sock` in the container accepts `sendkey` and
  `screendump`, which is enough to drive the desktop without the web viewer, including the Run
  dialog and UAC prompts.
- Over SSH the session is a normal user session: Velopack's `Setup.exe --silent` installed to
  `%LocalAppData%\Shamash.Pc` with exit code 0 and no prompt. A logon scheduled task with no
  execution time limit starts the stub `Shamash.Pc.exe`, which spawns `current\Shamash.Pc.exe`
  and exits, so the task reports Ready while the app runs.
