"""Shamash home server: API on HOME_API_PORT (8000), UI on HOME_UI_PORT (8001), one process.

Run: `python -m app.main`  (HOME_MODE=all|api|ui selects listeners; default all).
"""
import asyncio
import json
import os
import time
from datetime import datetime, timezone
from contextlib import asynccontextmanager

import uvicorn
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

from . import auth
from .db import Database, now_iso
from .ui import create_ui_app


MAX_TURNS_PER_CALL = 200
MAX_LOG_LINES_PER_CALL = 500


def normalize_time(value) -> str:
    """ISO-8601 (any offset, optional fraction) to UTC "YYYY-MM-DDTHH:MM:SSZ", the format retention compares against."""
    t = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
    if t.tzinfo is None:
        t = t.replace(tzinfo=timezone.utc)
    return t.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def create_api_app(db: Database, enrol_token: str, retention_days: int = 365) -> FastAPI:
    @asynccontextmanager
    async def lifespan(app):
        task = asyncio.create_task(cleanup_loop(db, retention_days))
        yield
        task.cancel()

    app = FastAPI(title="Shamash home API", docs_url=None, redoc_url=None, openapi_url=None, lifespan=lifespan)

    def unauthorized(reason: str):
        content = {"error": reason}
        if reason == "stale timestamp":
            now = time.time()
            content["serverTime"] = now_iso()
            content["serverUnix"] = int(now)
        return JSONResponse(content, status_code=401)

    async def authenticate(request: Request):
        """Returns (client_row, body, None) or (None, None, reason)."""
        body = await request.body()
        client = db.get_client(request.headers.get("X-Shamash-Client", ""))
        if client is None:
            return None, None, "unknown client"
        if client["revoked"]:
            return None, None, "revoked"
        result = auth.check(client["public_key_pem"], request.headers.get("X-Shamash-Timestamp", ""), body,
                            request.headers.get("X-Shamash-Signature", ""))
        if result == "stale":
            return None, None, "stale timestamp"
        if result != "ok":
            return None, None, "bad signature"
        return client, body, None

    @app.get("/api/health")
    def health():
        return {"ok": True}

    @app.post("/api/register")
    async def register(request: Request):
        try:
            data = json.loads(await request.body())
            token, pem = data["enrolToken"], data["publicKeyPem"]
        except (ValueError, KeyError, TypeError):
            return JSONResponse({"error": "bad request"}, status_code=400)
        import hmac
        if not isinstance(token, str) or not hmac.compare_digest(token.encode(), enrol_token.encode()):
            return JSONResponse({"error": "forbidden"}, status_code=403)
        try:
            pem = auth.normalize_public_key(pem)
        except ValueError as e:
            return JSONResponse({"error": str(e)}, status_code=400)
        name = str(data.get("name") or "unnamed")[:100]
        return {"clientId": db.register(name, pem, data.get("version"))}

    @app.post("/api/heartbeat")
    async def heartbeat(request: Request):
        client, body, reason = await authenticate(request)
        if client is None:
            return unauthorized(reason)
        try:
            payload = json.loads(body or b"{}")
            if not isinstance(payload, dict):
                raise ValueError
        except ValueError:
            return JSONResponse({"error": "bad request"}, status_code=400)
        db.add_heartbeat(client["id"], payload)
        desired, hold = db.steering()
        control = db.control(client["id"])
        return {"serverTime": now_iso(), "desiredVersion": control["desiredVersion"] or desired,
                "hold": hold or control["hold"], "commands": db.take_commands(client["id"])}

    async def json_list(request: Request, key: str, limit: int):
        """(client, items, None) or (None, None, error response)."""
        client, body, reason = await authenticate(request)
        if client is None:
            return None, None, unauthorized(reason)
        try:
            items = json.loads(body or b"{}")[key]
            if not isinstance(items, list) or len(items) > limit:
                raise ValueError
        except (ValueError, KeyError, TypeError):
            return None, None, JSONResponse({"error": "bad request"}, status_code=400)
        return client, items, None

    @app.post("/api/turns")
    async def turns(request: Request):
        client, items, error = await json_list(request, "turns", MAX_TURNS_PER_CALL)
        if client is None:
            return error
        try:
            parsed = [(str(t["turnId"]), normalize_time(t["time"]), t["record"]) for t in items]
            if not all(isinstance(t[2], dict) and t[0] for t in parsed):
                raise ValueError
        except (ValueError, KeyError, TypeError):
            return JSONResponse({"error": "bad request"}, status_code=400)
        accepted, duplicates = db.add_turns(client["id"], parsed)
        return {"accepted": accepted, "duplicates": duplicates}

    @app.post("/api/logs")
    async def logs(request: Request):
        client, items, error = await json_list(request, "lines", MAX_LOG_LINES_PER_CALL)
        if client is None:
            return error
        try:
            parsed = [(normalize_time(l["time"]), str(l["level"])[:20], str(l["message"])[:4000]) for l in items]
        except (ValueError, KeyError, TypeError):
            return JSONResponse({"error": "bad request"}, status_code=400)
        return {"accepted": db.add_logs(client["id"], parsed)}

    return app


async def cleanup_loop(db: Database, retention_days: int):
    while True:
        await asyncio.sleep(24 * 3600)
        await asyncio.to_thread(db.cleanup, retention_days)


async def serve() -> None:
    enrol_token = os.environ.get("HOME_ENROL_TOKEN")
    if not enrol_token:
        raise SystemExit("HOME_ENROL_TOKEN is required")
    data_dir = os.environ.get("HOME_DATA_DIR", "/data")
    os.makedirs(data_dir, exist_ok=True)
    db = Database(os.path.join(data_dir, "home.db"))
    retention = int(os.environ.get("HOME_RETENTION_DAYS", "365"))
    db.cleanup(retention)  # also once at startup
    mode = os.environ.get("HOME_MODE", "all")
    servers = []
    if mode in ("all", "api"):
        servers.append(uvicorn.Server(uvicorn.Config(
            create_api_app(db, enrol_token, retention), host="0.0.0.0",
            port=int(os.environ.get("HOME_API_PORT", "8000")), proxy_headers=True, forwarded_allow_ips="*")))
    if mode in ("all", "ui"):
        servers.append(uvicorn.Server(uvicorn.Config(
            create_ui_app(db), host="0.0.0.0", port=int(os.environ.get("HOME_UI_PORT", "8001")))))
    # uvicorn Server.serve() installs signal handlers; the first one handles SIGTERM, so stop both together.
    await asyncio.gather(*(s.serve() for s in servers))


if __name__ == "__main__":
    asyncio.run(serve())
