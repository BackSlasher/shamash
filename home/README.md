# Shamash home server

Small always-on server the PC service dials home to. Protocol (the contract): [`../docs/home-protocol.md`](../docs/home-protocol.md).
Slice 1: registration, signed heartbeats, status UI. Slice 2: turn and log upload (`/api/turns`, `/api/logs`), per-client
steering (hold, desired version, one-shot commands `check_update` and `restart`) delivered in the heartbeat reply.

Python 3.12, FastAPI, SQLite (stdlib), Jinja2. One process runs two uvicorn servers (see `app/main.py`):

- API on `HOME_API_PORT` (8000): `/api/register`, `/api/heartbeat`, `/api/turns`, `/api/logs`, `/api/health`. Public; authenticated by ECDSA signature.
- UI on `HOME_UI_PORT` (8001): `/`, `/clients/<id>` (last 50 turns, steering controls), `/clients/<id>/turns/<turnId>`,
  `/clients/<id>/logs?level=`, `/missing-features`, `POST /clients/<id>/{revoke,control,command}`, `POST /settings`. **No auth of its own**: it must sit
  behind the reverse proxy's client-certificate check.

## Environment

| Variable | Default | Meaning |
|---|---|---|
| `HOME_ENROL_TOKEN` | required | shared token guarding registration only |
| `HOME_DATA_DIR` | `/data` | SQLite database (`home.db`) lives here |
| `HOME_RETENTION_DAYS` | `365` | heartbeats, turns and log lines older than this are deleted (at startup and daily); clients are kept |
| `HOME_API_PORT` / `HOME_UI_PORT` | `8000` / `8001` | listener ports |
| `HOME_MODE` | `all` | `all`, `api` or `ui`: run only one listener (for proxies that need one service per port) |

## Run locally

```
cd home
python3 -m venv .venv && .venv/bin/pip install -r requirements-dev.txt
.venv/bin/python -m pytest
HOME_ENROL_TOKEN=dev HOME_DATA_DIR=/tmp/shamash-home .venv/bin/python -m app.main
```

Docker: `docker build -t shamash-home home && docker run -p 8000:8000 -p 8001:8001 -e HOME_ENROL_TOKEN=dev -v shamash-home:/data shamash-home`.
CI (`.github/workflows/home-image.yml`) pushes `ghcr.io/backslasher/shamash-home:sha-<short>` and `:latest` on changes to `home/**`.

## Deployment on Athena

Running since 2026-11-28 as the fort compose service `shamash-home` (`modules/fort/services.nix` in the athena-nixos repo):
one container, both ports. The API is the public site `shamash-api.fort.backslasher.net` (`proxy = { host = "shamash-api";
port = 8000; public = true; }`); the UI is the `external` Caddy entry `shamash.fort.backslasher.net` →
`http://shamash-home:8001`, behind the household client certificate. The enrolment token is the sops secret
`shamash-home/enrol_token`, rendered into the `shamash-home.env` template together with `HOME_RETENTION_DAYS=365`.
Image pin: `ghcr.io/backslasher/shamash-home:sha-<short>`; bump it in services.nix, `./check.sh`, commit, push,
`athena-deploy`. A deploy that changes a rendered secret recreates every secrets-labelled fort container.
