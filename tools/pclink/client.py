#!/usr/bin/env python3
"""Plays the phone's role for one conversation turn (docs/link-protocol.md).

    client.py ws://host:8777/ input.wav [reply.wav] [--probe] [--fast]

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


async def run(url: str, wav_in: str, wav_out: str | None, probe: bool, fast: bool) -> int:
    pcm = load_pcm(wav_in)
    frame = RATE * 2 * FRAME_MS // 1000
    t0 = time.monotonic()

    def log(msg: str) -> None:
        print(f"[{time.monotonic() - t0:7.3f}s] {msg}", flush=True)

    reply = bytearray()
    rc = 2
    async with websockets.connect(url, max_size=1 << 20) as ws:
        hello = {"type": "hello", "sampleRate": RATE, "device": "pclink-client.py"}
        hello["mode"] = "probe" if probe else "conversation"
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


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if len(args) < 2:
        raise SystemExit(__doc__)
    sys.exit(asyncio.run(run(args[0], args[1], args[2] if len(args) > 2 else None,
                             "--probe" in sys.argv, "--fast" in sys.argv)))
