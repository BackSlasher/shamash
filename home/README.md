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

## Athena `services.nix` draft

`modules/fort/caddy.nix` reads `proxy` as a **single attrset** (`host`, `port`, `certs`, `public`) and the upstream is
`http://<service name>:<port>`. One service therefore cannot publish two hostnames/ports, so the shape below (a list)
does not work as-is. Either:

1. Extend `caddy.nix` so `proxy` may be a list (one site per entry, same `reverse_proxy http://${name}:${port}`), then use:

```nix
shamash-home = {
  image = "ghcr.io/backslasher/shamash-home:sha-<commit>";
  volumes = [ "./shamash-home/data:/data" ];
  env_file = config.sops.templates."shamash-home.env".path;
  labels = secretsLabel;
  restart = "unless-stopped";
  proxy = [ { host = "shamash-api"; port = 8000; public = true; } { host = "shamash"; port = 8001; } ];
};
```

2. Or, with no module change, two services off the same image and data directory using `HOME_MODE`
   (both open the same SQLite file; fine on one host, the write rate is tiny):

```nix
shamash-home-api = { image = "ghcr.io/backslasher/shamash-home:sha-<commit>"; environment.HOME_MODE = "api";
  volumes = [ "./shamash-home/data:/data" ]; env_file = config.sops.templates."shamash-home.env".path;
  labels = secretsLabel; restart = "unless-stopped"; proxy = { host = "shamash-api"; port = 8000; public = true; }; };
shamash-home-ui = { image = "ghcr.io/backslasher/shamash-home:sha-<commit>"; environment.HOME_MODE = "ui";
  volumes = [ "./shamash-home/data:/data" ]; env_file = config.sops.templates."shamash-home.env".path;
  labels = secretsLabel; restart = "unless-stopped"; proxy = { host = "shamash"; port = 8001; }; };
```

(Check how `services.nix` passes environment variables; `environment` above is a guess.) The sops template must render
the line `HOME_ENROL_TOKEN=<secret>`. The UI entry keeps the default `certs = [ "person" ]`; the API entry is `public = true`.
