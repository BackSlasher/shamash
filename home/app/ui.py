"""Server-rendered status UI (port 8001). No auth here: the house reverse proxy requires a client certificate."""
from datetime import datetime, timezone
from pathlib import Path

from fastapi import FastAPI, Form, Request
from fastapi.responses import RedirectResponse
from fastapi.templating import Jinja2Templates

# USD per million tokens (Opus 5.5).
RATES = {"input": 4.0, "cacheRead": 0.20, "cacheWrite": 5.0, "output": 20.0}

TEMPLATES = Jinja2Templates(directory=str(Path(__file__).resolve().parent.parent / "templates"))


def parse_iso(s):
    if not s:
        return None
    try:
        return datetime.fromisoformat(str(s).replace("Z", "+00:00"))
    except ValueError:
        return None


def relative(s, now=None) -> str:
    t = parse_iso(s)
    if not t:
        return "never"
    secs = int(((now or datetime.now(timezone.utc)) - t).total_seconds())
    if secs < 0:
        return "just now"
    if secs < 90:
        return f"{secs}s ago"
    if secs < 5400:
        return f"{round(secs / 60)} min ago"
    if secs < 172800:
        return f"{round(secs / 3600)} h ago"
    return f"{secs // 86400} d ago"


def duration(secs) -> str:
    if not isinstance(secs, (int, float)):
        return "-"
    secs = int(secs)
    d, rem = divmod(secs, 86400)
    h, rem = divmod(rem, 3600)
    m = rem // 60
    return (f"{d}d " if d else "") + (f"{h}h " if d or h else "") + f"{m}m"


def cost(usage) -> float | None:
    if not usage:
        return None
    return sum((usage.get(k) or 0) * r for k, r in RATES.items()) / 1_000_000


def create_ui_app(db) -> FastAPI:
    app = FastAPI(title="Shamash home UI", docs_url=None, redoc_url=None, openapi_url=None)

    @app.get("/")
    def index(request: Request):
        clients = []
        for c in db.list_clients():
            hbs = db.recent_heartbeats(c["id"], 20)
            last = hbs[0]["data"] if hbs else {}
            usage = last.get("usageToday")
            clients.append({
                "row": c, "last": last, "usage": usage, "cost": cost(usage), "heartbeats": hbs,
                "seen": relative(c["last_seen_at"]), "uptime": duration(last.get("uptimeSeconds")),
                "last_turn": relative(last.get("lastTurnAt")),
            })
        desired, hold = db.steering()
        return TEMPLATES.TemplateResponse(request, "index.html", {
            "clients": clients, "desired": desired or "", "hold": hold, "relative": relative,
            "duration": duration, "cost": cost, "rates": RATES,
        })

    @app.post("/clients/{cid}/revoke")
    def revoke(cid: str):
        db.revoke(cid)
        return RedirectResponse("/", status_code=303)

    @app.post("/settings")
    def settings(desiredVersion: str = Form(""), hold: str = Form("")):
        db.set_setting("desiredVersion", desiredVersion.strip() or None)
        db.set_setting("hold", "1" if hold else "0")
        return RedirectResponse("/", status_code=303)

    return app
