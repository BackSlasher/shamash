#!/usr/bin/env python3
"""Runs one scenario file against the VM (called by run.sh); stdlib only.

    check.py <scenario.json> [--url ws://127.0.0.1:8777/] [--tts]
    check.py --clean-only          (just the clean-desktop step; run.sh calls it before --install)

Prints PASS or FAIL <name>: <reason> and exits 0/1. Assertions are about tool names, result
prefixes, timing and end state, never about the model's wording. See README.md for the format.
"""
import json
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
WINVM = HERE.parent
SSH = str(WINVM / "ssh.sh")
CLIENT = str(WINVM.parent / "pclink" / "client.sh")
PCLINK_DIR = r"$env:LocalAppData\Shamash\pclink"
UTF8 = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; "


def ps(cmd: str, timeout: int = 120) -> str:
    r = subprocess.run([SSH, UTF8 + cmd], capture_output=True, timeout=timeout)
    return r.stdout.decode("utf-8", "replace").lstrip("\ufeff").strip()


# Gracefully closes leftover app windows so one scenario (or run) cannot leak state into the next.
# A Word/Excel that still has a window is never killed (killing creates Document Recovery entries);
# one with MainWindowHandle 0 (window-less, ignores CloseMainWindow forever) is Stop-Processed.
# Notepad is, after the grace period, only when its title contains "תוכנית" (plan files).
CLEAN_DESKTOP_PS = (
    "$names = 'WINWORD','EXCEL','notepad'; "
    "Get-Process $names -ErrorAction SilentlyContinue | ForEach-Object { $null = $_.CloseMainWindow() }; "
    "for ($i = 0; $i -lt 8; $i++) { if (-not (Get-Process $names -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Seconds 1 }; "
    "Get-Process notepad -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like '*תוכנית*' } | Stop-Process -Force; "
    "Get-Process WINWORD,EXCEL -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -eq 0 } | "
    "ForEach-Object { 'killed window-less ' + $(if ($_.ProcessName -eq 'EXCEL') {'Excel'} else {'Word'}) + ' pid ' + $_.Id; Stop-Process -Id $_.Id -Force }; "
    "$w = Get-Process WINWORD -ErrorAction SilentlyContinue; "
    "if ($w) { 'WARNING: Word still running (title: ' + (($w | ForEach-Object { $_.MainWindowTitle }) -join '; ') + ')' }"
)
STATE_WAIT_S, STATE_POLL_S = 10, 2


def clean_desktop() -> None:
    try:
        out = ps(CLEAN_DESKTOP_PS, timeout=60)
    except Exception as e:
        out = f"WARNING: clean-desktop step failed: {type(e).__name__}: {e}"
    for line in out.splitlines():
        if line.strip():
            print(f"  {line.strip()}")


def newest_turn() -> tuple[str, dict | None]:
    """(file name, parsed JSON) of the newest per-turn JSON; ('', None) if there is none."""
    out = ps(f"$f = Get-ChildItem {PCLINK_DIR}\\*.json -ErrorAction SilentlyContinue | "
             "Sort-Object LastWriteTime | Select-Object -Last 1; "
             "if ($f) { $f.Name; Get-Content -Raw $f.FullName -Encoding UTF8 }")
    if not out:
        return "", None
    name, _, body = out.partition("\n")
    try:
        return name.strip(), json.loads(body)
    except json.JSONDecodeError:
        return name.strip(), None


def subsequence(want: list[str], have: list[str]) -> bool:
    it = iter(have)
    return all(w in it for w in want)


def check_turn(turn: dict, data: dict, wall_ms: int) -> list[str]:
    errs = []
    calls = data.get("toolCalls") or []
    names = [c["name"] for c in calls]
    ordered = [t for t in turn.get("expect_tools", []) if not t.startswith("any:")]
    if not subsequence(ordered, names):
        errs.append(f"expected tools {ordered} as an ordered subsequence, got {names}")
    for t in turn.get("expect_tools", []):
        if t.startswith("any:"):
            alts = t[4:].split("|")
            if not any(a in names for a in alts):
                errs.append(f"expected any of {alts}, got {names}")
    for t in turn.get("forbid_tools", []):
        if t in names:
            errs.append(f"forbidden tool {t} was called (calls: {names})")
    if "only_tools" in turn:
        extra = [n for n in names if n not in turn["only_tools"]]
        if extra:
            errs.append(f"tools outside {turn['only_tools']} were called: {extra}")
    for tool, prefix in (turn.get("expect_result_prefix") or {}).items():
        mine = [c for c in calls if c["name"] == tool]
        if mine and not any("resultPrefix" in c for c in mine):
            errs.append(f"{tool}: resultPrefix missing (build older than 0.1.15?)")
            continue
        got = [c.get("resultPrefix", "") for c in mine]
        if not any(g.startswith(prefix) for g in got):
            errs.append(f"{tool}: expected result prefix {prefix!r}, got {got}")
    limit = turn.get("max_total_ms")
    if limit is not None:
        total = (data.get("timingsMs") or {}).get("total") or wall_ms
        if total > limit:
            errs.append(f"turn took {total} ms, limit {limit}")
    err = data.get("error")
    if err and err.get("stage") == "tts":
        print(f"  WARNING: turn ended with a TTS error (not a failure): {err.get('message')}")
    elif err:
        errs.append(f"turn reported an error: {err}")
    return errs


def run_turn(turn: dict, url: str, tts: bool = False) -> list[str]:
    before, _ = newest_turn()
    t0 = time.time()
    cmd = [CLIENT, url, "--text", turn["say"]] + ([] if tts else ["--no-tts"])
    r = subprocess.run(cmd, capture_output=True, text=True, timeout=300)
    wall = int((time.time() - t0) * 1000)
    if r.returncode != 0:
        return [f"client exited {r.returncode}: {(r.stdout + r.stderr).strip()[-300:]}"]
    name, data = before, None
    for _ in range(15):  # the JSON is written just after `done`
        name, data = newest_turn()
        if name != before and data is not None:
            break
        time.sleep(1)
    if name == before or data is None:
        return ["no new per-turn JSON appeared in the pclink folder"]
    return check_turn(turn, data, wall)


def main() -> int:
    args = sys.argv[1:]
    if args == ["--clean-only"]:
        clean_desktop()
        return 0
    url = "ws://127.0.0.1:8777/"
    if "--url" in args:
        i = args.index("--url"); url = args[i + 1]; del args[i:i + 2]
    tts = "--tts" in args
    if tts:
        args.remove("--tts")
    sc = json.loads(Path(args[0]).read_text(encoding="utf-8"))
    name = sc.get("name", Path(args[0]).stem)
    failures: list[str] = []
    r = subprocess.run([CLIENT, url, "--reset"], capture_output=True, text=True, timeout=60)
    if r.returncode != 0 or "reset_ok" not in r.stdout:
        print(f"FAIL {name}")
        print(f"  - history reset did not answer reset_ok (exit {r.returncode}): {(r.stdout + r.stderr).strip()[-300:]}")
        return 1
    try:
        if not sc.get("keep_apps"):
            clean_desktop()
        for line in sc.get("setup", []):
            ps(line)
        for n, turn in enumerate(sc.get("turns", []), 1):
            failures += [f"turn {n}: {e}" for e in run_turn(turn, url, tts)]
        for st in sc.get("expect_state", []):
            waited = 0
            got = ps(st["ps"])
            while got != st["equals"] and waited < STATE_WAIT_S:
                time.sleep(STATE_POLL_S)
                waited += STATE_POLL_S
                got = ps(st["ps"])
            if got != st["equals"]:
                failures.append(f"state {st['ps']!r}: expected {st['equals']!r}, got {got!r} (after {waited} s)")
            elif waited:
                print(f"  state {st['ps']!r}: (matched after {waited} s)")
    except Exception as e:  # ssh timeouts and the like
        failures.append(f"harness error: {type(e).__name__}: {e}")
    finally:
        for line in sc.get("teardown", []):
            try:
                ps(line)
            except Exception as e:
                failures.append(f"teardown {line!r}: {e}")
    if failures:
        print(f"FAIL {name}")
        for f in failures:
            print(f"  - {f}")
        return 1
    print(f"PASS {name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
