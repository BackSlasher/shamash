#!/usr/bin/env python3
"""Type into the dockur/windows VM through the QEMU monitor socket (runs inside the container).

Usage (from the host):
  podman exec -i shamash-win11 python3 - 'KEY:meta_l-r' 'WAIT:1.5' 'TYPE:notepad' 'KEY:ret' < tools/winvm/sendkeys.py

Items: KEY:<qemu key combo>, TYPE:<ascii text>, WAIT:<seconds>. US keyboard layout assumed.
"""
import socket
import sys
import time

SOCK = "/run/shm/monitor.sock"

PLAIN = {
    " ": "spc", "-": "minus", "=": "equal", "[": "bracket_left", "]": "bracket_right",
    ";": "semicolon", "'": "apostrophe", "`": "grave_accent", "\\": "backslash",
    ",": "comma", ".": "dot", "/": "slash", "\n": "ret", "\t": "tab",
}
SHIFTED = {
    "!": "1", "@": "2", "#": "3", "$": "4", "%": "5", "^": "6", "&": "7", "*": "8", "(": "9", ")": "0",
    "_": "minus", "+": "equal", "{": "bracket_left", "}": "bracket_right", ":": "semicolon",
    '"': "apostrophe", "~": "grave_accent", "|": "backslash", "<": "comma", ">": "dot", "?": "slash",
}


def keyname(ch: str) -> str:
    if ch.isalpha() and ch.isascii():
        return f"shift-{ch.lower()}" if ch.isupper() else ch
    if ch.isdigit():
        return ch
    if ch in PLAIN:
        return PLAIN[ch]
    if ch in SHIFTED:
        return f"shift-{SHIFTED[ch]}"
    raise ValueError(f"no key for {ch!r}")


def main(items: list[str]) -> None:
    s = socket.socket(socket.AF_UNIX)
    s.connect(SOCK)
    s.settimeout(0.3)

    def drain() -> None:
        try:
            while s.recv(4096):
                pass
        except socket.timeout:
            pass

    def send(key: str, delay: float = 0.12) -> None:
        s.sendall(f"sendkey {key}\n".encode())
        time.sleep(delay)
        drain()

    drain()
    for item in items:
        kind, _, arg = item.partition(":")
        if kind == "KEY":
            send(arg, 0.3)
        elif kind == "WAIT":
            time.sleep(float(arg))
        elif kind == "TYPE":
            for ch in arg:
                send(keyname(ch))
        else:
            raise SystemExit(f"bad item {item!r}")
    s.close()


if __name__ == "__main__":
    main(sys.argv[1:])
