# Link mode (phone streams to the PC)

Protocol: `docs/link-protocol.md` (unchanged). Link mode is on when `ShamashConfig.pcLinkUrl` is non-null.

## Build

```
./build.sh assembleDebug -PpcLinkUrl=ws://192.168.1.138:8777/
```

`SHAMASH_PC_LINK_URL` in the environment wins over `-PpcLinkUrl`. Without either, the build is standalone (ElevenLabs) exactly as before. The debug build allows cleartext `ws://`; release does not.

## What changed

- `config/ShamashConfig`: `pcLinkUrl` (from `BuildConfig.PC_LINK_URL`), `linkReplyTimeoutMs = 60_000`.
- `link/LinkMessages.kt`: pure parsing of PC text messages, `hello` encoder, `LinkNetworkException` (isNetwork, errorType `network`), `LinkPcException` (errorType `pc_<stage>`).
- `link/LinkTranscriber.kt`: `Transcriber` + `ReplyProvider`. One OkHttp WebSocket per turn, an unlimited Channel fed by the listener, consumed in order by `transcribeBatch` and `awaitReply`.
- `core/SessionController`: optional `replyProvider`; `runReply` / `drainEmptyTurn`; tap cancels a reply in flight.
- `ShamashViewModel`: builds `LinkTranscriber` as transcriber and replyProvider in link mode; logs `link: mode=pc url=...` or `link: mode=standalone` (tag `Shamash`).

## State machine vs protocol

| App state | Protocol |
|---|---|
| WORKING (opening) | connect, send `hello` |
| LISTENING | binary frames (seq, time, PCM); the first 200 ms are not streamed (cue leak), as in standalone mode |
| tap/max duration -> WORKING | `end`; `finish()` returns "" at once; `transcribeBatch` waits for `transcript` (no upload; the WAV is still saved locally) |
| WORKING, transcript shown | wait for `reply` (text replaces the transcript on screen) |
| SPEAKING | first audio frame after `audio_start`; `audio_end` drains the sink; `done` closes the socket -> IDLE |
| tap in WORKING (reply wait) or SPEAKING | `cancel`, sink `stopNow`, close -> IDLE |
| ERROR | connect failure, socket drop, 60 s reply timeout (NO_NETWORK); PC `error` (GENERIC, errorType `pc_<stage>`) |

Empty transcript: the PC sends `done`; the controller consumes it (5 s limit) and goes IDLE with empty text. A double tap shorter than `minBatchAudioMs` skips `transcribeBatch` but `end` is still sent, and the same drain path handles the PC's answer.

## Not done / not tested

- Not run on a phone or against the real PC server (no adb, PC side is a parallel task). Only MockWebServer and fakes.
- `AudioTrackSink` playback in link mode is untested (a fake sink is used).
- The reply text is not stored in the session JSON (`SessionRecord` is saved before the reply arrives; adding it would reorder the save). The session JSON `batchText` holds the PC transcript.
- The max-duration "end" and the 120 s STT budget: `batchTimeoutMs` (90 s) bounds the wait for `transcript`.
- A session job cancelled between `end` and `awaitReply` could leave a socket open until the PC closes it.
