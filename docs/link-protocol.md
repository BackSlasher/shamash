# Phone-to-PC link protocol (v1, plain `ws://`, LAN only)

One WebSocket connection per conversation turn, opened by the phone to `ws://<pc>:8777/`.
Text frames are JSON objects with a `type` field. Binary frames carry audio. Unknown text
types are ignored by both sides. The PC is the only side that talks to ElevenLabs and Claude.

## Phone → PC

| type | fields | when |
|---|---|---|
| `hello` | `sampleRate` (16000), `device` (model string), `mode`: `"conversation"` or absent/`"probe"`, `seconds` (probe only) | first message after connect |
| binary | 4-byte big-endian uint32 `seq`, 8-byte big-endian int64 phone send time (ms since epoch), then raw 16-bit mono little-endian PCM at `sampleRate` (3200 bytes = 100 ms) | while the phone is listening |
| `end` | – | the user stopped talking (tap again or max duration); no more audio follows |
| `cancel` | – | the user tapped while the PC was thinking or speaking; the PC stops the pipeline and sends nothing more, then closes |

Test aid, text turns: a conversation `hello` may also carry `"text": "<transcript>"`. When it does AND
the PC setting `allowTextTurns` is true (config.json key, or `SHAMASH_ALLOW_TEXT_TURNS=1`; default false), the PC
skips speech-to-text and uses the text as the transcript. The phone then sends `end` as usual (no audio is needed;
audio frames before `end` are ignored) and the PC answers as in any turn: `transcript` (the given text), `reply`,
audio, `done`. With the setting off the `text` field is ignored. The phone app never sends it; `tools/pclink/client.py --text` does.

Frames shorter than 12 bytes are ignored. Messages over 1 MB close the socket.

## PC → phone

Probe mode (no `mode` or `mode: "probe"`): unchanged from the probe server. Each binary frame is
answered with `{"type":"ack","seq":S,"tPhone":T,"tServerMs":<unix ms>}`; after `end` or close the PC
sends `{"type":"summary","frames":N,"bytes":N,"seconds":S,"gaps":N}`, writes a WAV, and closes.

Conversation mode, in order after `end`:

| type | fields | meaning |
|---|---|---|
| `transcript` | `text` (string, may be empty) | batch speech-to-text of everything received. Empty text means nothing usable was heard: the PC then sends `done` and skips the reply. |
| `reply` | `text` | the assistant's answer as text, sent before any audio |
| `audio_start` | `sampleRate` (16000) | reply audio follows as binary frames of raw 16-bit mono little-endian PCM (no header; chunk size arbitrary) |
| binary | PCM bytes | play as they arrive |
| `audio_end` | – | no more audio |
| `done` | – | turn complete; the PC closes the socket after sending it |
| `error` | `stage`: `stt` / `llm` / `tts` / `internal`, `message` | the turn failed; the phone shows its error state. The PC closes after sending it. |

The PC never sends `ack` in conversation mode. A `cancel` from the phone may cross any of the above;
the PC stops at the next opportunity and closes without `done`.

## Timing expectations

- The phone waits up to 90 s after `end` for `transcript` (batch STT of up to 120 s of audio).
- `reply` should follow `transcript` within the LLM latency; the phone shows an amber "thinking" state meanwhile.
- The first audio frame should follow `reply` within about a second (TTS first-byte latency measured at 480 ms from the phone).

## Conversation memory

The PC keeps one running conversation in memory for the life of the process (owner decision: one
very long conversation rather than sessions). Each turn appends the user transcript and the
assistant reply.

## Not in v1

Pairing, TLS (`wss://`), authentication, partial transcripts, barge-in while speaking, device-owner
APK push. The link is LAN-only and the debug phone build allows cleartext.
