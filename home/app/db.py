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
]


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
            return self.conn.execute("DELETE FROM heartbeats WHERE at < ?", (cutoff,)).rowcount

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
