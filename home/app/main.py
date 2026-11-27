"""Shamash home server: API on HOME_API_PORT (8000), UI on HOME_UI_PORT (8001), one process.

Run: `python -m app.main`  (HOME_MODE=all|api|ui selects listeners; default all).
"""
import asyncio
import json
import os
from contextlib import asynccontextmanager

import uvicorn
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

from . import auth
from .db import Database, now_iso
from .ui import create_ui_app


def create_api_app(db: Database, enrol_token: str, retention_days: int = 365) -> FastAPI:
    @asynccontextmanager
    async def lifespan(app):
        task = asyncio.create_task(cleanup_loop(db, retention_days))
        yield
        task.cancel()

    app = FastAPI(title="Shamash home API", docs_url=None, redoc_url=None, openapi_url=None, lifespan=lifespan)

    def unauthorized():
        return JSONResponse({"error": "unauthorized"}, status_code=401)

    async def authenticate(request: Request):
        """Returns (client_row, body) or (None, None)."""
        body = await request.body()
        client = db.get_client(request.headers.get("X-Shamash-Client", ""))
        if client is None or client["revoked"]:
            return None, None
        if not auth.verify(client["public_key_pem"], request.headers.get("X-Shamash-Timestamp", ""), body,
                           request.headers.get("X-Shamash-Signature", "")):
            return None, None
        return client, body

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
        client, body = await authenticate(request)
        if client is None:
            return unauthorized()
        try:
            payload = json.loads(body or b"{}")
            if not isinstance(payload, dict):
                raise ValueError
        except ValueError:
            return JSONResponse({"error": "bad request"}, status_code=400)
        db.add_heartbeat(client["id"], payload)
        desired, hold = db.steering()
        return {"serverTime": now_iso(), "desiredVersion": desired, "hold": hold, "commands": []}

    @app.post("/api/turns")
    async def turns(request: Request):
        return JSONResponse({"error": "not implemented"}, status_code=501)

    @app.post("/api/logs")
    async def logs(request: Request):
        return JSONResponse({"error": "not implemented"}, status_code=501)

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
