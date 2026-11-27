# Home link protocol (PC service -> home server)

The PC service "dials home" to a small server on the owner's always-on machine. Outbound HTTPS only; his PC needs
no inbound path. Nothing in a voice turn waits on this link: every call is fire-and-forget with local buffering.

## Identity

- On first run the PC generates an ECDSA P-256 key pair (`%LocalAppData%\Shamash\home-key.pem`, PKCS#8 PEM, file
  ACL: user only). The public key is sent once at registration; the private key never leaves the PC.
- Registration needs the shared **enrolment token** (`homeEnrolToken` in config.json). It only guards registration;
  every later call is authenticated by signature, not by the token.
- The server assigns a `clientId` (opaque string, e.g. `c_7f3a…`). The PC stores it next to the key
  (`home-client.json`: `{ "clientId": "...", "registeredAt": "..." }`).

## Signing

Every call except `register` carries:

```
X-Shamash-Client: <clientId>
X-Shamash-Timestamp: <unix seconds, UTC>
X-Shamash-Signature: <base64 of the DER-encoded ECDSA-SHA256 signature>
```

The signed message is the UTF-8 bytes of `"<timestamp>\n<lowercase hex SHA-256 of the request body>"` (body may be
empty: hash of zero bytes). The server rejects timestamps more than 300 s from its clock, unknown or revoked clients,
and bad signatures with `401`. Replays inside the window are accepted (calls are idempotent).

The `401` body says why, as JSON `{"error": ...}`:

- `"unknown client"`: no such clientId. The client registers again.
- `"revoked"`: the client was revoked. The client registers again.
- `"bad signature"`: the signature does not verify against the registered key (or the timestamp is not an integer).
- `"stale timestamp"`: the signature is valid but the timestamp is outside the window. This body also carries
  `"serverTime": "<ISO 8601 UTC>"` and `"serverUnix": <int>`. The server checks the signature first, so only the key
  holder ever sees this answer.

A client with a wrong clock must not break the link: on a `401` with `serverUnix` it keeps the offset
(`serverUnix` minus its own unix time), signs with `now + offset` from then on, and retries the same request once
without re-registering. Clients may also take the offset from `serverTime` in any successful heartbeat reply. Only a
`401` without `serverUnix` calls for re-registration.

## Endpoints (JSON, `Content-Type: application/json`)

### `POST /api/register`

Request: `{ "enrolToken": "...", "publicKeyPem": "-----BEGIN PUBLIC KEY-----…", "name": "<machine name>",
"version": "<service version>" }`
Response `200`: `{ "clientId": "..." }`. `403` on a wrong token. Registering again with the same public key returns
the same `clientId` (idempotent); a new key under an existing name creates a new client (the old one stays until
revoked in the UI).

### `POST /api/heartbeat` (signed)

Request:
```json
{
  "version": "0.1.21+sha",
  "uptimeSeconds": 12345,
  "health": "idle|busy|error|starting",
  "lastTurnAt": "2026-11-27T13:07:30Z" | null,
  "turnsToday": 7,
  "usageToday": { "calls": 21, "input": 9000, "cacheWrite": 0, "cacheRead": 120000, "output": 2100 } | null,
  "lastError": { "at": "...", "stage": "llm", "message": "..." } | null,
  "phone": { "lastSeenAt": "...", "battery": 71, "charging": true } | null,
  "machine": { "name": "...", "os": "Windows 11 ...", "officeInstalled": true, "indexOk": true } | null
}
```
Response `200`:
```json
{ "serverTime": "2026-11-27T13:08:00Z", "desiredVersion": "0.1.21" | null, "hold": false,
  "commands": [ { "id": "k1", "type": "upload_logs" | "restart" | "noop", "args": {} } ] }
```
`desiredVersion`/`hold` are for update steering (slice 3): the PC installs only the named version and never while
`hold` is true. `commands` are acknowledged by the next heartbeat's `ackCommands: ["k1"]` (slice 2+).

### `POST /api/turns` (signed, slice 2)

Request: `{ "turns": [ <the per-turn JSON the PC already writes under pclink/>, … ] }` (≤ 50 per call), each with a
`stamp` (the file's timestamp key) so re-uploads are deduplicated. Response `200`: `{ "accepted": N }`.

### `POST /api/logs` (signed, slice 2)

Body: gzip of the log text, `Content-Encoding: gzip`, query `?from=<iso>&to=<iso>`. Response `200`.

## Server side

- Two listeners in one container: the **API** (`/api/*`, public site, Let's Encrypt) and the **UI** (`/`, behind the
  owner's house client certificate). Data in SQLite under `/data`. Retention: one year (nightly cleanup).
- The UI shows: clients (last seen, version, revoke), today's and this week's turns and token cost, the turn log, errors,
  phone battery/charging, update controls (desired version, hold).

## PC side

- Heartbeat every `homeHeartbeatMinutes` (default 5) and right after startup; registration happens lazily when
  `homeUrl` and `homeEnrolToken` are set and no `home-client.json` exists. Failures are logged once per state change and
  retried with backoff (max 30 min). Settings: `homeUrl`, `homeEnrolToken`, `homeHeartbeatMinutes` (config.json or
  `SHAMASH_HOME_URL`, `SHAMASH_HOME_ENROL_TOKEN`, `SHAMASH_HOME_HEARTBEAT_MINUTES`).
