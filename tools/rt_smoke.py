# Realtime smoke test for ElevenLabs Scribe (streams a WAV like the app does).
# Usage (needs the `websockets` Python package; never prints the key):
#   set -a; . .env; set +a; python3 tools/rt_smoke.py tools/fixtures/hebrew_v3.wav
import asyncio, base64, json, os, sys, time, wave
import websockets

KEY = os.environ["ELEVENLABS_KEY"]
URL = ("wss://api.elevenlabs.io/v1/speech-to-text/realtime"
       "?model_id=scribe_v3_live&language_code=heb&audio_format=pcm_16000&commit_strategy=manual")

async def main(path):
    w = wave.open(path, "rb")
    pcm = w.readframes(w.getnframes())
    chunk = 3200  # 100 ms at 16 kHz 16-bit mono
    t0 = time.time()
    async with websockets.connect(URL, additional_headers={"xi-api-key": KEY}) as ws:
        print(f"connected {time.time()-t0:.2f}s")
        async def reader():
            try:
                async for m in ws:
                    d = json.loads(m)
                    print(f"{time.time()-t0:6.2f} <- {d.get('message_type')}: {json.dumps(d, ensure_ascii=False)[:300]}")
            except websockets.ConnectionClosed as e:
                print(f"{time.time()-t0:6.2f} closed: {e}")
        r = asyncio.create_task(reader())
        for i in range(0, len(pcm), chunk):
            await ws.send(json.dumps({"message_type": "input_audio_chunk",
                                      "audio_base_64": base64.b64encode(pcm[i:i+chunk]).decode(),
                                      "commit": False, "sample_rate": 16000}))
            await asyncio.sleep(0.1)
        print(f"{time.time()-t0:6.2f} -> commit")
        await ws.send(json.dumps({"message_type": "input_audio_chunk", "audio_base_64": "",
                                  "commit": True, "sample_rate": 16000}))
        try:
            await asyncio.wait_for(r, 10)
        except asyncio.TimeoutError:
            print("timeout waiting; closing")
            await ws.close()

asyncio.run(main(sys.argv[1]))
