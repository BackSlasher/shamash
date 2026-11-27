#!/usr/bin/env python3
"""Plays the phone's role for one conversation turn (docs/link-protocol.md).

    client.py ws://host:8777/ input.wav [reply.wav] [--probe] [--fast]
    client.py ws://host:8777/ [reply.wav] --text "<hebrew>"     (text turn, test aid, no WAV)
    client.py ws://host:8777/ --tool <name> '<json input>'      (run one tool directly, test aid)

--tool prints the tool's result (or "error: <message>") to stdout verbatim. Exit 0 on result,
2 on error, 3 on unknown tool. Needs allowTextTurns on the PC.

Streams the WAV (16 kHz mono 16-bit, resampled if needed) as 100 ms frames at real-time pace
(or as fast as possible with --fast), sends `end`, prints every text message with a timestamp,
and writes the reply PCM to reply.wav. Exit code 0 on `done`, 1 on `error`, 2 on protocol trouble.
"""
import asyncio
import json
import struct
import sys
import time
import wave

import websockets

FRAME_MS = 100
RATE = 16000


def load_pcm(path: str) -> bytes:
    with wave.open(path, "rb") as w:
        rate, ch, width, n = w.getframerate(), w.getnchannels(), w.getsampwidth(), w.getnframes()
        data = w.readframes(n)
    if width != 2:
        raise SystemExit(f"{path}: need 16-bit PCM, got width {width}")
    samples = list(struct.unpack(f"<{len(data)//2}h", data))
    if ch > 1:
        samples = samples[::ch]
    if rate != RATE:  # crude linear resample, fine for a functional test
        ratio = rate / RATE
        out = []
        pos = 0.0
        while pos < len(samples) - 1:
            i = int(pos)
            frac = pos - i
            out.append(int(samples[i] * (1 - frac) + samples[i + 1] * frac))
            pos += ratio
        samples = out
    return struct.pack(f"<{len(samples)}h", *samples)


async def run(url: str, wav_in: str | None, wav_out: str | None, probe: bool, fast: bool, text: str | None = None) -> int:
    pcm = b"" if text is not None else load_pcm(wav_in)
    frame = RATE * 2 * FRAME_MS // 1000
    t0 = time.monotonic()

    def log(msg: str) -> None:
        print(f"[{time.monotonic() - t0:7.3f}s] {msg}", flush=True)

    reply = bytearray()
    rc = 2
    async with websockets.connect(url, max_size=1 << 20) as ws:
        hello = {"type": "hello", "sampleRate": RATE, "device": "pclink-client.py"}
        hello["mode"] = "probe" if probe else "conversation"
        if text is not None:
            hello["text"] = text  # needs allowTextTurns on the PC
        await ws.send(json.dumps(hello))
        log(f"sent hello {hello['mode']}, {len(pcm) / RATE / 2:.1f} s of audio")

        async def receiver() -> None:
            nonlocal rc
            async for msg in ws:
                if isinstance(msg, bytes):
                    reply.extend(msg)
                    continue
                m = json.loads(msg)
                t = m.get("type")
                if t == "ack":
                    continue
                text = m.get("text")
                log(f"<- {t}" + (f": {text}" if text is not None else "") + (f" {m}" if t in ("summary", "error") else ""))
                if t == "done":
                    rc = 0
                elif t == "error":
                    rc = 1

        recv_task = asyncio.create_task(receiver())
        seq = 0
        for off in range(0, len(pcm), frame):
            chunk = pcm[off:off + frame]
            await ws.send(struct.pack(">Iq", seq, int(time.time() * 1000)) + chunk)
            seq += 1
            if not fast:
                await asyncio.sleep(FRAME_MS / 1000)
        await ws.send(json.dumps({"type": "end"}))
        log(f"sent end after {seq} frames")
        try:
            await asyncio.wait_for(recv_task, timeout=180)
        except asyncio.TimeoutError:
            log("timeout waiting for the PC")
    if wav_out and reply:
        with wave.open(wav_out, "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(RATE)
            w.writeframes(bytes(reply))
        log(f"reply audio: {len(reply)} bytes = {len(reply) / RATE / 2:.2f} s -> {wav_out}")
    elif not probe:
        log(f"reply audio: {len(reply)} bytes")
    return rc


async def run_tool(url: str, name: str, raw_input: str) -> int:
    try:
        tool_input = json.loads(raw_input)
    except json.JSONDecodeError as e:
        print(f"error: input is not JSON: {e}")
        return 2
    async with websockets.connect(url, max_size=1 << 20) as ws:
        await ws.send(json.dumps({"type": "tool", "name": name, "input": tool_input}))
        try:
            m = json.loads(await asyncio.wait_for(ws.recv(), timeout=60))
        except (asyncio.TimeoutError, websockets.ConnectionClosed):
            print("error: no reply (is allowTextTurns on?)")
            return 2
    if "result" in m:
        sys.stdout.write(m["result"])
        if not m["result"].endswith("\n"):
            sys.stdout.write("\n")
        return 0
    err = m.get("error", "unknown error")
    print(f"error: {err}")
    return 3 if err.startswith("unknown tool") else 2


if __name__ == "__main__":
    argv = sys.argv[1:]
    if "--tool" in argv:
        i = argv.index("--tool")
        if i == 0 or i + 2 >= len(argv):
            raise SystemExit(__doc__)
        sys.exit(asyncio.run(run_tool(argv[0], argv[i + 1], argv[i + 2])))
    text = None
    if "--text" in argv:
        i = argv.index("--text")
        if i + 1 >= len(argv):
            raise SystemExit(__doc__)
        text = argv[i + 1]
        del argv[i:i + 2]
    args = [a for a in argv if not a.startswith("--")]
    if text is not None:
        args.insert(1, None)  # no input WAV
    if len(args) < 2:
        raise SystemExit(__doc__)
    sys.exit(asyncio.run(run(args[0], args[1], args[2] if len(args) > 2 else None,
                             "--probe" in argv, "--fast" in argv, text)))
