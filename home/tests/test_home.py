import base64
import json
import sqlite3
import time

import pytest
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from fastapi.testclient import TestClient

from app.auth import signed_message
from app.db import Database
from app.main import create_api_app
from app.ui import create_ui_app

TOKEN = "tok"


def new_key():
    k = ec.generate_private_key(ec.SECP256R1())
    pem = k.public_key().public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo).decode()
    return k, pem


@pytest.fixture
def env(tmp_path):
    db = Database(str(tmp_path / "t.db"))
    return db, TestClient(create_api_app(db, TOKEN)), TestClient(create_ui_app(db))


def register(api, pem, name="pc", token=TOKEN):
    return api.post("/api/register", json={"enrolToken": token, "publicKeyPem": pem, "name": name, "version": "1"})


def signed(api, cid, key, body, ts=None, path="/api/heartbeat"):
    ts = str(int(time.time()) if ts is None else ts)
    sig = base64.b64encode(key.sign(signed_message(ts, body), ec.ECDSA(hashes.SHA256()))).decode()
    return api.post(path, content=body, headers={
        "X-Shamash-Client": cid, "X-Shamash-Timestamp": ts, "X-Shamash-Signature": sig,
        "Content-Type": "application/json"})


def test_health(env):
    assert env[1].get("/api/health").json() == {"ok": True}


def test_register(env):
    _, api, _ = env
    k, pem = new_key()
    assert register(api, pem, token="bad").status_code == 403
    a = register(api, pem)
    assert a.status_code == 200
    assert register(api, pem).json() == a.json()
    _, pem2 = new_key()
    assert register(api, pem2).json()["clientId"] != a.json()["clientId"]


def test_heartbeat_ok_and_steering(env):
    db, api, _ = env
    k, pem = new_key()
    cid = register(api, pem).json()["clientId"]
    db.set_setting("desiredVersion", "0.2.0")
    db.set_setting("hold", "1")
    body = json.dumps({"version": "0.1.21", "health": "idle", "turnsToday": 3}).encode()
    r = signed(api, cid, k, body)
    assert r.status_code == 200
    assert r.json()["desiredVersion"] == "0.2.0" and r.json()["hold"] is True and r.json()["commands"] == []
    assert db.recent_heartbeats(cid)[0]["data"]["turnsToday"] == 3
    assert db.get_client(cid)["last_version"] == "0.1.21"


def test_heartbeat_rejections(env):
    db, api, _ = env
    k, pem = new_key()
    cid = register(api, pem).json()["clientId"]
    body = b"{}"
    other, _ = new_key()
    r = signed(api, cid, other, body)
    assert (r.status_code, r.json()) == (401, {"error": "bad signature"})
    r = signed(api, cid, k, body, ts=int(time.time()) - 400)
    assert r.status_code == 401 and r.json()["error"] == "stale timestamp"
    assert abs(r.json()["serverUnix"] - time.time()) < 5 and r.json()["serverTime"].endswith("Z")
    r = signed(api, "c_nope", k, body)
    assert (r.status_code, r.json()) == (401, {"error": "unknown client"})
    assert signed(api, cid, k, body).status_code == 200
    db.revoke(cid)
    r = signed(api, cid, k, body)
    assert (r.status_code, r.json()) == (401, {"error": "revoked"})


def test_stale_is_only_revealed_after_signature_check(env):
    _, api, _ = env
    k, pem = new_key()
    cid = register(api, pem).json()["clientId"]
    other, _ = new_key()
    r = signed(api, cid, other, b"{}", ts=int(time.time()) - 7200)
    assert r.json() == {"error": "bad signature"}


def test_ui_renders_client(env):
    db, api, ui = env
    k, pem = new_key()
    cid = register(api, pem, name="saba-pc").json()["clientId"]
    body = json.dumps({"version": "0.1.21", "health": "idle", "turnsToday": 2,
                       "usageToday": {"calls": 1, "input": 1000000, "cacheRead": 0, "cacheWrite": 0, "output": 0},
                       "phone": {"lastSeenAt": "2026-11-27T13:00:00Z", "battery": 71, "charging": True}}).encode()
    signed(api, cid, k, body)
    html = ui.get("/").text
    assert "saba-pc" in html and cid in html and "$4.00" in html and "71%" in html
    assert ui.post("/settings", data={"desiredVersion": "0.3", "hold": "1"}, follow_redirects=False).status_code == 303
    assert db.steering() == ("0.3", True)
    ui.post(f"/clients/{cid}/revoke", follow_redirects=False)
    assert db.get_client(cid)["revoked"] == 1


def test_retention(env):
    db, api, _ = env
    k, pem = new_key()
    cid = register(api, pem).json()["clientId"]
    db.add_heartbeat(cid, {"version": "x"}, at="2020-01-01T00:00:00Z")
    db.add_heartbeat(cid, {"version": "y"})
    assert db.cleanup(365) == 1
    assert len(db.recent_heartbeats(cid)) == 1
    assert db.get_client(cid) is not None
