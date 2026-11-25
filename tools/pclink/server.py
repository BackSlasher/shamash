#!/usr/bin/env python3
"""PC-link probe server: receives PCM frames over WebSocket, acks each, writes a WAV."""
import asyncio
import json
import os
import statistics
import struct
import time
import wave

import websockets

OUT = os.environ.get("PCLINK_OUT", "/out")
PORT = int(os.environ.get("PCLINK_PORT", "8777"))
HEADER = 12


async def handler(ws):
    t0 = time.monotonic()
    rate = 16000
    pcm = bytearray()
    seqs = []
    arrivals = []
    frames = 0
    peer = ws.remote_address
    try:
        async for msg in ws:
            if isinstance(msg, str):
                m = json.loads(msg)
                if m.get("type") == "hello":
                    rate = int(m.get("sampleRate", 16000))
                    print(f"hello from {peer}: {m}", flush=True)
                elif m.get("type") == "end":
                    break
                continue
            if len(msg) < HEADER:
                continue
            now = time.monotonic()
            seq, t_phone = struct.unpack(">Iq", msg[:HEADER])
            arrivals.append(now)
            seqs.append(seq)
            pcm += msg[HEADER:]
            frames += 1
            await ws.send(json.dumps({"type": "ack", "seq": seq, "tPhone": t_phone,
                                      "tServerMs": int(time.time() * 1000)}))
    except websockets.ConnectionClosed:
        pass
    wall = time.monotonic() - t0
    if seqs:
        gaps = (max(seqs) - min(seqs) + 1) - len(set(seqs))
    else:
        gaps = 0
    secs = len(pcm) / (rate * 2)
    try:
        await ws.send(json.dumps({"type": "summary", "frames": frames, "bytes": len(pcm),
                                  "seconds": round(secs, 3), "gaps": gaps}))
        await ws.close()
    except websockets.ConnectionClosed:
        pass
    if pcm:
        os.makedirs(OUT, exist_ok=True)
        path = os.path.join(OUT, time.strftime("pclink-%Y%m%d-%H%M%S.wav"))
        with wave.open(path, "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(rate)
            w.writeframes(bytes(pcm))
    d = [(b - a) * 1000 for a, b in zip(arrivals, arrivals[1:])]
    jit = (f"{min(d):.1f}/{statistics.median(d):.1f}/{max(d):.1f}" if d else "n/a")
    print(f"pclink: peer={peer} frames={frames} bytes={len(pcm)} audio={secs:.2f}s "
          f"wall={wall:.2f}s gaps={gaps} interarrival_ms(min/med/max)={jit}", flush=True)


async def main():
    async with websockets.serve(handler, "0.0.0.0", PORT, max_size=1 << 20):
        print(f"pclink server listening on :{PORT}, writing to {OUT}", flush=True)
        await asyncio.Future()


if __name__ == "__main__":
    asyncio.run(main())
