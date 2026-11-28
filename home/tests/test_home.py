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


def turn(turn_id, time_="2026-11-27T13:07:30Z", transcript="מה השעה", tools=None):
    return {"turnId": turn_id, "time": time_, "record": {
        "transcript": transcript, "reply": "שלוש", "toolCalls": tools or [],
        "usage": {"calls": 1, "input": 10, "cacheWrite": 0, "cacheRead": 0, "output": 2}, "error": None}}


def post(api, cid, key, path, payload):
    return signed(api, cid, key, json.dumps(payload).encode(), path=path)


def enrolled(api, name="pc"):
    k, pem = new_key()
    return k, register(api, pem, name=name).json()["clientId"]


def test_turns_accepted_and_duplicates_ignored(env):
    db, api, _ = env
    k, cid = enrolled(api)
    r = post(api, cid, k, "/api/turns", {"turns": [turn("pclink-1"), turn("pclink-2")]})
    assert (r.status_code, r.json()) == (200, {"accepted": 2, "duplicates": 0})
    r = post(api, cid, k, "/api/turns", {"turns": [turn("pclink-2"), turn("pclink-3")]})
    assert r.json() == {"accepted": 1, "duplicates": 1}
    assert {t["turnId"] for t in db.recent_turns(cid)} == {"pclink-1", "pclink-2", "pclink-3"}
    assert db.get_turn(cid, "pclink-1")["record"]["transcript"] == "מה השעה"
    assert post(api, cid, k, "/api/turns", {"turns": [turn(f"t{i}") for i in range(201)]}).status_code == 400
    assert post(api, cid, k, "/api/turns", {"turns": [{"turnId": "x"}]}).status_code == 400


def test_logs_accepted(env):
    db, api, _ = env
    k, cid = enrolled(api)
    lines = [{"time": "2026-11-27T13:07:30.123+02:00", "level": "Warning", "message": "boom"},
             {"time": "2026-11-27T13:07:31Z", "level": "Information", "message": "turn: ok"}]
    assert post(api, cid, k, "/api/logs", {"lines": lines}).json() == {"accepted": 2}
    got = db.recent_logs(cid)
    assert [l["message"] for l in got] == ["turn: ok", "boom"]
    assert got[1]["time"] == "2026-11-27T11:07:30Z"
    assert [l["message"] for l in db.recent_logs(cid, level="warning")] == ["boom"]
    assert post(api, cid, k, "/api/logs", {"lines": [lines[0]] * 501}).status_code == 400


def test_new_endpoints_reject_bad_signature(env):
    _, api, _ = env
    k, cid = enrolled(api)
    other, _ = new_key()
    for path, payload in (("/api/turns", {"turns": []}), ("/api/logs", {"lines": []})):
        r = post(api, cid, other, path, payload)
        assert (r.status_code, r.json()) == (401, {"error": "bad signature"})
        assert post(api, cid, k, path, payload).status_code == 200


def test_retention_deletes_old_turns_and_logs(env):
    db, api, _ = env
    k, cid = enrolled(api)
    post(api, cid, k, "/api/turns", {"turns": [turn("old", "2020-01-01T00:00:00Z"), turn("new", "2099-01-01T00:00:00Z")]})
    post(api, cid, k, "/api/logs", {"lines": [
        {"time": "2020-01-01T00:00:00Z", "level": "Warning", "message": "old"},
        {"time": "2099-01-01T00:00:00Z", "level": "Warning", "message": "new"}]})
    assert db.cleanup(365) == 2
    assert [t["turnId"] for t in db.recent_turns(cid)] == ["new"]
    assert [l["message"] for l in db.recent_logs(cid)] == ["new"]


def test_heartbeat_delivers_command_once(env):
    db, api, ui = env
    k, cid = enrolled(api)
    assert ui.post(f"/clients/{cid}/command", data={"command": "check_update"}, follow_redirects=False).status_code == 303
    assert ui.post(f"/clients/{cid}/command", data={"command": "restart"}, follow_redirects=False).status_code == 303
    assert ui.post(f"/clients/{cid}/command", data={"command": "format_c"}, follow_redirects=False).status_code == 400
    assert ui.post(f"/clients/{cid}/control", data={"desiredVersion": "0.2.0", "hold": "1"},
                   follow_redirects=False).status_code == 303
    first = signed(api, cid, k, b"{}").json()
    assert [c["type"] for c in first["commands"]] == ["check_update", "restart"]
    assert first["hold"] is True and first["desiredVersion"] == "0.2.0"
    assert signed(api, cid, k, b"{}").json()["commands"] == []


def test_ui_pages_render(env):
    db, api, ui = env
    k, cid = enrolled(api, "saba-pc")
    tools = [{"name": "search", "input": '{"q":"x"}', "ms": 5, "resultChars": 3, "resultPrefix": "abc"}]
    post(api, cid, k, "/api/turns", {"turns": [turn("pclink-1", tools=tools)]})
    post(api, cid, k, "/api/logs", {"lines": [{"time": "2026-11-27T13:07:31Z", "level": "Warning", "message": "disk low"}]})
    page = ui.get(f"/clients/{cid}")
    assert page.status_code == 200 and "מה השעה" in page.text and 'search({&#34;q&#34;:&#34;x&#34;}) → abc' in page.text
    assert 'dir="auto"' in page.text and "Queue restart" in page.text
    assert "מה השעה" in ui.get(f"/clients/{cid}/turns/pclink-1").text
    assert ui.get(f"/clients/{cid}/turns/nope").status_code == 404
    logs = ui.get(f"/clients/{cid}/logs?level=Warning")
    assert logs.status_code == 200 and "disk low" in logs.text
    assert "disk low" not in ui.get(f"/clients/{cid}/logs?level=Information").text
    assert ui.get("/clients/c_nope").status_code == 404
    assert f"/clients/{cid}" in ui.get("/").text


def test_missing_features_page(env):
    db, api, ui = env
    k, cid = enrolled(api, "saba-pc")
    mf = [{"name": "report_missing_feature", "input": '{"request": "תזכורות לתרופות"}', "resultPrefix": "noted"}]
    broken = [{"name": "report_missing_feature", "input": "not json {", "resultPrefix": ""}]
    post(api, cid, k, "/api/turns", {"turns": [turn("a", tools=mf), turn("b", tools=broken), turn("c")]})
    html = ui.get("/missing-features").text
    assert "תזכורות לתרופות" in html and "not json {" in html and "saba-pc" in html
    assert html.count('class="turn"') == 2
