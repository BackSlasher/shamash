"""SQLite storage (stdlib sqlite3, no ORM) with startup migrations."""
import json
import secrets
import sqlite3
import threading
from datetime import datetime, timedelta, timezone

MIGRATIONS = [
    """
    CREATE TABLE clients (
        id TEXT PRIMARY KEY,
        name TEXT NOT NULL,
        public_key_pem TEXT NOT NULL UNIQUE,
        created_at TEXT NOT NULL,
        last_seen_at TEXT,
        last_version TEXT,
        revoked INTEGER NOT NULL DEFAULT 0
    );
    CREATE TABLE heartbeats (
        client_id TEXT NOT NULL,
        at TEXT NOT NULL,
        json TEXT NOT NULL
    );
    CREATE INDEX heartbeats_client_at ON heartbeats (client_id, at);
    CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT);
    """,
    """
    CREATE TABLE turns (
        client_id TEXT NOT NULL,
        turn_id TEXT NOT NULL,
        time TEXT NOT NULL,
        record TEXT NOT NULL,
        received_at TEXT NOT NULL,
        PRIMARY KEY (client_id, turn_id)
    );
    CREATE INDEX turns_client_time ON turns (client_id, time);
    CREATE TABLE logs (
        client_id TEXT NOT NULL,
        time TEXT NOT NULL,
        level TEXT NOT NULL,
        message TEXT NOT NULL,
        received_at TEXT NOT NULL
    );
    CREATE INDEX logs_client_time ON logs (client_id, time);
    CREATE TABLE client_control (
        client_id TEXT PRIMARY KEY,
        hold INTEGER NOT NULL DEFAULT 0,
        desired_version TEXT,
        pending_commands TEXT NOT NULL DEFAULT '[]'
    );
    """,
]

COMMANDS = ("check_update", "restart")


def now_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


class Database:
    def __init__(self, path: str):
        self.conn = sqlite3.connect(path, check_same_thread=False)
        self.conn.row_factory = sqlite3.Row
        self.lock = threading.Lock()
        self.migrate()

    def migrate(self) -> None:
        with self.lock, self.conn:
            current = self.conn.execute("PRAGMA user_version").fetchone()[0]
            for i, sql in enumerate(MIGRATIONS[current:], start=current + 1):
                self.conn.executescript(sql)
                self.conn.execute(f"PRAGMA user_version = {i}")

    # clients
    def register(self, name: str, pem: str, version: str | None) -> str:
        with self.lock, self.conn:
            row = self.conn.execute("SELECT id FROM clients WHERE public_key_pem=?", (pem,)).fetchone()
            if row:
                return row["id"]
            cid = "c_" + secrets.token_hex(8)
            self.conn.execute(
                "INSERT INTO clients (id, name, public_key_pem, created_at, last_version) VALUES (?,?,?,?,?)",
                (cid, name, pem, now_iso(), version),
            )
            return cid

    def get_client(self, cid: str):
        with self.lock:
            return self.conn.execute("SELECT * FROM clients WHERE id=?", (cid,)).fetchone()

    def list_clients(self):
        with self.lock:
            return self.conn.execute("SELECT * FROM clients ORDER BY revoked, name, created_at").fetchall()

    def revoke(self, cid: str) -> None:
        with self.lock, self.conn:
            self.conn.execute("UPDATE clients SET revoked=1 WHERE id=?", (cid,))

    # heartbeats
    def add_heartbeat(self, cid: str, payload: dict, at: str | None = None) -> None:
        at = at or now_iso()
        with self.lock, self.conn:
            self.conn.execute("INSERT INTO heartbeats (client_id, at, json) VALUES (?,?,?)",
                              (cid, at, json.dumps(payload)))
            self.conn.execute("UPDATE clients SET last_seen_at=?, last_version=COALESCE(?, last_version) WHERE id=?",
                              (at, payload.get("version"), cid))

    def recent_heartbeats(self, cid: str, limit: int = 20) -> list[dict]:
        with self.lock:
            rows = self.conn.execute(
                "SELECT at, json FROM heartbeats WHERE client_id=? ORDER BY at DESC, rowid DESC LIMIT ?",
                (cid, limit)).fetchall()
        return [{"at": r["at"], "data": json.loads(r["json"])} for r in rows]

    def cleanup(self, retention_days: int) -> int:
        cutoff = (datetime.now(timezone.utc) - timedelta(days=retention_days)).strftime("%Y-%m-%dT%H:%M:%SZ")
        with self.lock, self.conn:
            n = self.conn.execute("DELETE FROM heartbeats WHERE at < ?", (cutoff,)).rowcount
            n += self.conn.execute("DELETE FROM turns WHERE time < ?", (cutoff,)).rowcount
            n += self.conn.execute("DELETE FROM logs WHERE time < ?", (cutoff,)).rowcount
            return n

    # turns and logs
    def add_turns(self, cid: str, turns: list[tuple[str, str, dict]]) -> tuple[int, int]:
        """(turn_id, time, record) items; returns (accepted, duplicates). Existing (client, turn_id) pairs are kept."""
        accepted = 0
        with self.lock, self.conn:
            for turn_id, time_, record in turns:
                accepted += self.conn.execute(
                    "INSERT OR IGNORE INTO turns (client_id, turn_id, time, record, received_at) VALUES (?,?,?,?,?)",
                    (cid, turn_id, time_, json.dumps(record, ensure_ascii=False), now_iso())).rowcount
        return accepted, len(turns) - accepted

    def recent_turns(self, cid: str, limit: int = 50) -> list[dict]:
        with self.lock:
            rows = self.conn.execute(
                "SELECT turn_id, time, record FROM turns WHERE client_id=? ORDER BY time DESC, rowid DESC LIMIT ?",
                (cid, limit)).fetchall()
        return [{"turnId": r["turn_id"], "time": r["time"], "record": json.loads(r["record"])} for r in rows]

    def get_turn(self, cid: str, turn_id: str) -> dict | None:
        with self.lock:
            r = self.conn.execute("SELECT turn_id, time, record FROM turns WHERE client_id=? AND turn_id=?",
                                  (cid, turn_id)).fetchone()
        return {"turnId": r["turn_id"], "time": r["time"], "record": json.loads(r["record"])} if r else None

    def turns_mentioning(self, text: str, limit: int = 500) -> list[dict]:
        """Newest turns whose record text contains `text` (callers filter precisely)."""
        with self.lock:
            rows = self.conn.execute(
                "SELECT client_id, turn_id, time, record FROM turns WHERE instr(record, ?) > 0 "
                "ORDER BY time DESC, rowid DESC LIMIT ?", (text, limit)).fetchall()
        return [{"clientId": r["client_id"], "turnId": r["turn_id"], "time": r["time"],
                 "record": json.loads(r["record"])} for r in rows]

    def add_logs(self, cid: str, lines: list[tuple[str, str, str]]) -> int:
        with self.lock, self.conn:
            self.conn.executemany(
                "INSERT INTO logs (client_id, time, level, message, received_at) VALUES (?,?,?,?,?)",
                [(cid, t, lvl, msg, now_iso()) for t, lvl, msg in lines])
        return len(lines)

    def recent_logs(self, cid: str, limit: int = 300, level: str | None = None) -> list[dict]:
        sql, args = "SELECT time, level, message FROM logs WHERE client_id=?", [cid]
        if level:
            sql += " AND lower(level)=lower(?)"
            args.append(level)
        with self.lock:
            rows = self.conn.execute(sql + " ORDER BY time DESC, rowid DESC LIMIT ?", (*args, limit)).fetchall()
        return [dict(r) for r in rows]

    def log_levels(self, cid: str) -> list[str]:
        with self.lock:
            return [r[0] for r in self.conn.execute(
                "SELECT DISTINCT level FROM logs WHERE client_id=? ORDER BY level", (cid,))]

    # per-client steering (hold, desired version, one-shot commands)
    def control(self, cid: str) -> dict:
        with self.lock:
            r = self.conn.execute("SELECT * FROM client_control WHERE client_id=?", (cid,)).fetchone()
        if r is None:
            return {"hold": False, "desiredVersion": None, "commands": []}
        return {"hold": bool(r["hold"]), "desiredVersion": r["desired_version"],
                "commands": json.loads(r["pending_commands"])}

    def set_control(self, cid: str, hold: bool, desired_version: str | None) -> None:
        with self.lock, self.conn:
            self.conn.execute(
                "INSERT INTO client_control (client_id, hold, desired_version) VALUES (?,?,?) "
                "ON CONFLICT(client_id) DO UPDATE SET hold=excluded.hold, desired_version=excluded.desired_version",
                (cid, int(hold), desired_version))

    def queue_command(self, cid: str, command: str) -> None:
        if command not in COMMANDS:
            raise ValueError("unknown command")
        with self.lock, self.conn:
            self.conn.execute("INSERT OR IGNORE INTO client_control (client_id) VALUES (?)", (cid,))
            cmds = json.loads(self.conn.execute(
                "SELECT pending_commands FROM client_control WHERE client_id=?", (cid,)).fetchone()[0])
            cmds.append({"id": "k_" + secrets.token_hex(4), "type": command, "args": {}})
            self.conn.execute("UPDATE client_control SET pending_commands=? WHERE client_id=?",
                              (json.dumps(cmds), cid))

    def take_commands(self, cid: str) -> list[dict]:
        """Returns the pending commands and clears them: a command is delivered once."""
        with self.lock, self.conn:
            r = self.conn.execute("SELECT pending_commands FROM client_control WHERE client_id=?", (cid,)).fetchone()
            if r is None or r[0] == "[]":
                return []
            self.conn.execute("UPDATE client_control SET pending_commands='[]' WHERE client_id=?", (cid,))
            return json.loads(r[0])

    # settings
    def get_setting(self, key: str) -> str | None:
        with self.lock:
            row = self.conn.execute("SELECT value FROM settings WHERE key=?", (key,)).fetchone()
        return row["value"] if row else None

    def set_setting(self, key: str, value: str | None) -> None:
        with self.lock, self.conn:
            if value is None:
                self.conn.execute("DELETE FROM settings WHERE key=?", (key,))
            else:
                self.conn.execute("INSERT INTO settings (key, value) VALUES (?,?) "
                                  "ON CONFLICT(key) DO UPDATE SET value=excluded.value", (key, value))

    def steering(self) -> tuple[str | None, bool]:
        return self.get_setting("desiredVersion") or None, self.get_setting("hold") == "1"
