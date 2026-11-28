"""Server-rendered status UI (port 8001). No auth here: the house reverse proxy requires a client certificate."""
import json
from datetime import datetime, timezone
from pathlib import Path

from fastapi import FastAPI, Form, HTTPException, Request
from fastapi.responses import RedirectResponse
from .db import COMMANDS
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


def clip(s, n: int) -> str:
    s = str(s or "").replace("\n", " ")
    return s if len(s) <= n else s[:n] + "…"


def tool_line(call: dict) -> str:
    """`name(inputPrefix) → resultPrefix` for one record toolCalls entry."""
    return f"{call.get('name', '?')}({clip(call.get('input'), 80)}) → {clip(call.get('resultPrefix'), 80)}"


def missing_feature_request(call: dict) -> str:
    """The request text of a report_missing_feature call; input is a JSON string, parsed leniently (falls back to the raw text)."""
    raw = call.get("input")
    try:
        data = json.loads(raw) if isinstance(raw, str) else raw
    except ValueError:
        return str(raw or "")
    if isinstance(data, dict):
        for k in ("request", "feature", "description", "text", "summary"):
            if isinstance(data.get(k), str):
                return data[k]
        strings = [v for v in data.values() if isinstance(v, str)]
        return " ".join(strings) if strings else str(raw)
    return data if isinstance(data, str) else str(raw or "")


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

    def client_or_404(cid: str):
        c = db.get_client(cid)
        if c is None:
            raise HTTPException(404, "no such client")
        return c

    @app.get("/clients/{cid}")
    def client_page(request: Request, cid: str):
        c = client_or_404(cid)
        return TEMPLATES.TemplateResponse(request, "client.html", {
            "c": c, "control": db.control(cid), "turns": db.recent_turns(cid, 50), "tool_line": tool_line,
            "commands": COMMANDS,
        })

    @app.get("/clients/{cid}/turns/{turn_id}")
    def turn_page(request: Request, cid: str, turn_id: str):
        c = client_or_404(cid)
        t = db.get_turn(cid, turn_id)
        if t is None:
            raise HTTPException(404, "no such turn")
        return TEMPLATES.TemplateResponse(request, "turn.html", {
            "c": c, "t": t, "full": json.dumps(t["record"], ensure_ascii=False, indent=2), "tool_line": tool_line})

    @app.get("/clients/{cid}/logs")
    def logs_page(request: Request, cid: str, level: str = ""):
        c = client_or_404(cid)
        return TEMPLATES.TemplateResponse(request, "logs.html", {
            "c": c, "lines": db.recent_logs(cid, 300, level or None), "levels": db.log_levels(cid), "level": level})

    @app.get("/missing-features")
    def missing_features(request: Request):
        names = {c["id"]: c["name"] for c in db.list_clients()}
        rows = []
        for t in db.turns_mentioning("report_missing_feature"):
            for call in t["record"].get("toolCalls") or []:
                if isinstance(call, dict) and call.get("name") == "report_missing_feature":
                    rows.append({"client": names.get(t["clientId"], t["clientId"]), "clientId": t["clientId"],
                                 "turnId": t["turnId"], "time": t["time"], "request": missing_feature_request(call),
                                 "transcript": t["record"].get("transcript")})
        return TEMPLATES.TemplateResponse(request, "missing.html", {"rows": rows})

    @app.post("/clients/{cid}/control")
    def control(cid: str, desiredVersion: str = Form(""), hold: str = Form("")):
        client_or_404(cid)
        db.set_control(cid, bool(hold), desiredVersion.strip() or None)
        return RedirectResponse(f"/clients/{cid}", status_code=303)

    @app.post("/clients/{cid}/command")
    def command(cid: str, command: str = Form(...)):
        client_or_404(cid)
        try:
            db.queue_command(cid, command)
        except ValueError:
            raise HTTPException(400, "unknown command")
        return RedirectResponse(f"/clients/{cid}", status_code=303)

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
