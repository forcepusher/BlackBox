#!/usr/bin/env python3
"""game-api: the game's backend. Players, scores, cloud saves and friends as JSON over HTTP on
127.0.0.1:8080 (nginx serves it at /api/ and /health).

The database comes from DATABASE_URL (in /etc/game-api/env):
  sqlite:////var/lib/game-api/game.db            a SQLite file (Python's sqlite3)
  postgresql://user:password@host:5432/dbname    PostgreSQL (needs python3-psycopg2)
On start it creates the tables that are missing; "app.py init-db" does only that.
"""
import base64
import json
import os
import re
import sys
import threading
import time
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

URL = os.environ.get("DATABASE_URL", "sqlite:////var/lib/game-api/game.db")
LISTEN = ("127.0.0.1", int(os.environ.get("PORT", "8080")))
MAX_SAVE = 256 * 1024

# The tables, per database: what the models in the game's server code map to.
SCHEMA = {
    "sqlite": [
        ("players", """CREATE TABLE players (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            country TEXT,
            avatar BLOB,
            is_banned BOOLEAN NOT NULL DEFAULT 0,
            created_at DATETIME NOT NULL)"""),
        ("scores", """CREATE TABLE scores (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            player_id INTEGER NOT NULL REFERENCES players (id),
            score INTEGER NOT NULL,
            level TEXT,
            created_at DATETIME NOT NULL)""",
         "CREATE INDEX scores_player ON scores (player_id, score)"),
        ("saves", """CREATE TABLE saves (
            player_id INTEGER PRIMARY KEY REFERENCES players (id),
            data TEXT NOT NULL,
            updated_at DATETIME NOT NULL)"""),
        ("friendships", """CREATE TABLE friendships (
            player_id INTEGER NOT NULL REFERENCES players (id),
            friend_id INTEGER NOT NULL REFERENCES players (id),
            created_at DATETIME NOT NULL,
            PRIMARY KEY (player_id, friend_id))"""),
    ],
    "postgresql": [
        ("players", """CREATE TABLE players (
            id BIGSERIAL PRIMARY KEY,
            name TEXT NOT NULL,
            country TEXT,
            avatar BYTEA,
            is_banned BOOLEAN NOT NULL DEFAULT FALSE,
            created_at TIMESTAMP NOT NULL)"""),
        ("scores", """CREATE TABLE scores (
            id BIGSERIAL PRIMARY KEY,
            player_id BIGINT NOT NULL REFERENCES players (id),
            score BIGINT NOT NULL,
            level TEXT,
            created_at TIMESTAMP NOT NULL)""",
         "CREATE INDEX scores_player ON scores (player_id, score)"),
        ("saves", """CREATE TABLE saves (
            player_id BIGINT PRIMARY KEY REFERENCES players (id),
            data TEXT NOT NULL,
            updated_at TIMESTAMP NOT NULL)"""),
        ("friendships", """CREATE TABLE friendships (
            player_id BIGINT NOT NULL REFERENCES players (id),
            friend_id BIGINT NOT NULL REFERENCES players (id),
            created_at TIMESTAMP NOT NULL,
            PRIMARY KEY (player_id, friend_id))"""),
    ],
}


class Database:
    """One of the two engines behind the same SQL (written with ? for parameters)."""

    def __init__(self, url):
        scheme = url.split(":", 1)[0].split("+", 1)[0].lower()
        self.idle = []
        self.lock = threading.Lock()
        if scheme == "sqlite":
            import sqlite3
            self.kind = "sqlite"
            path = url.split(":///", 1)[1] if ":///" in url else url.split(":", 1)[1]

            def connect():
                conn = sqlite3.connect(path, timeout=15, check_same_thread=False)
                conn.execute("PRAGMA foreign_keys = ON")
                return conn
            self.connect = connect
            self.integrity = (sqlite3.IntegrityError,)
            self.unavailable = (sqlite3.OperationalError,)
        elif scheme in ("postgresql", "postgres"):
            import psycopg2
            self.kind = "postgresql"
            dsn = "postgresql:" + url.split(":", 1)[1]
            self.connect = lambda: psycopg2.connect(dsn, connect_timeout=10)
            self.integrity = (psycopg2.IntegrityError,)
            self.unavailable = (psycopg2.OperationalError, psycopg2.InterfaceError)
        else:
            raise SystemExit("DATABASE_URL: unknown database %r" % scheme)

    def sql(self, text):
        return text if self.kind == "sqlite" else text.replace("?", "%s")

    def run(self, work):
        """Runs work(cursor) in one transaction on a pooled connection and returns its result."""
        for attempt in (1, 2):
            with self.lock:
                conn = self.idle.pop() if self.idle else None
            reused = conn is not None
            if conn is None:
                conn = self.connect()
            try:
                cur = conn.cursor()
                result = work(cur)
                conn.commit()
            except self.unavailable:
                # A pooled connection the server dropped (a restart, say): once more with a new one.
                try:
                    conn.close()
                except Exception:
                    pass
                if reused and attempt == 1 and self.kind == "postgresql":
                    continue
                raise
            except BaseException:
                try:
                    conn.rollback()
                except Exception:
                    conn.close()
                    raise
                self.put(conn)
                raise
            self.put(conn)
            return result

    def put(self, conn):
        with self.lock:
            if len(self.idle) < 8:
                self.idle.append(conn)
                return
        conn.close()

    def init(self):
        def work(cur):
            for table, *ddl in SCHEMA[self.kind]:
                if self.kind == "sqlite":
                    cur.execute("SELECT name FROM sqlite_master WHERE type = 'table' AND name = ?", (table,))
                    exists = cur.fetchone() is not None
                else:
                    cur.execute("SELECT to_regclass(%s)", (table,))
                    exists = cur.fetchone()[0] is not None
                if not exists:
                    for statement in ddl:
                        cur.execute(statement)
        self.run(work)


db = Database(URL)


def now():
    return time.strftime("%Y-%m-%d %H:%M:%S", time.gmtime())


def text_time(value):
    return value.strftime("%Y-%m-%d %H:%M:%S") if hasattr(value, "strftime") else value


def blob(value):
    return None if value is None else base64.b64encode(bytes(value)).decode()


class HttpError(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


def health(cur):
    cur.execute("SELECT 1")
    cur.fetchone()
    return {"ok": True}


def get_player(cur, pid):
    cur.execute(db.sql("SELECT id, name, country, avatar, is_banned, created_at FROM players WHERE id = ?"), (pid,))
    row = cur.fetchone()
    if row is None:
        raise HttpError(404, "no such player")
    cur.execute(db.sql("SELECT MAX(score), COUNT(*) FROM scores WHERE player_id = ?"), (pid,))
    best, games = cur.fetchone()
    return {"id": row[0], "name": row[1], "country": row[2], "avatar": blob(row[3]), "banned": bool(row[4]),
            "created_at": text_time(row[5]), "best_score": None if best is None else int(best), "games": int(games)}


def new_player(cur, body):
    name = body.get("name")
    if not isinstance(name, str) or not 1 <= len(name) <= 64:
        raise HttpError(400, "name: 1 to 64 characters")
    avatar = body.get("avatar")
    avatar = None if avatar is None else base64.b64decode(avatar)
    cur.execute(db.sql("INSERT INTO players (name, country, avatar, created_at) VALUES (?, ?, ?, ?) RETURNING id"),
                (name, body.get("country"), avatar, now()))
    return 201, {"id": cur.fetchone()[0]}


def new_score(cur, body):
    pid, score = body.get("player_id"), body.get("score")
    if not isinstance(pid, int) or not isinstance(score, int):
        raise HttpError(400, "player_id and score: numbers")
    cur.execute(db.sql("INSERT INTO scores (player_id, score, level, created_at) VALUES (?, ?, ?, ?) RETURNING id"),
                (pid, score, body.get("level"), now()))
    return 201, {"id": cur.fetchone()[0]}


def leaderboard(cur):
    cur.execute("SELECT p.id, p.name, MAX(s.score) AS best FROM scores s JOIN players p ON p.id = s.player_id "
                "WHERE p.is_banned = FALSE GROUP BY p.id, p.name ORDER BY best DESC, p.id LIMIT 20")
    return [{"id": r[0], "name": r[1], "score": int(r[2])} for r in cur.fetchall()]


def recent_scores(cur, pid):
    cur.execute(db.sql("SELECT id, score, level, created_at FROM scores WHERE player_id = ? ORDER BY id DESC LIMIT 10"), (pid,))
    return [{"id": r[0], "score": int(r[1]), "level": r[2], "created_at": text_time(r[3])} for r in cur.fetchall()]


def put_save(cur, pid, text):
    cur.execute(db.sql("INSERT INTO saves (player_id, data, updated_at) VALUES (?, ?, ?) "
                       "ON CONFLICT (player_id) DO UPDATE SET data = excluded.data, updated_at = excluded.updated_at"),
                (pid, text, now()))
    return 200, {"ok": True}


def get_save(cur, pid):
    cur.execute(db.sql("SELECT data FROM saves WHERE player_id = ?"), (pid,))
    row = cur.fetchone()
    if row is None:
        raise HttpError(404, "no save")
    return row[0]


def add_friend(cur, body):
    pid, fid = body.get("player_id"), body.get("friend_id")
    if not isinstance(pid, int) or not isinstance(fid, int) or pid == fid:
        raise HttpError(400, "player_id and friend_id: two different players")
    cur.execute(db.sql("INSERT INTO friendships (player_id, friend_id, created_at) VALUES (?, ?, ?) ON CONFLICT DO NOTHING"),
                (pid, fid, now()))
    return 201, {"ok": True}


def friends(cur, pid):
    cur.execute(db.sql("SELECT friend_id FROM friendships WHERE player_id = ? ORDER BY friend_id"), (pid,))
    return [r[0] for r in cur.fetchall()]


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def reply(self, code, body, content_type="application/json"):
        if not isinstance(body, bytes):
            body = json.dumps(body, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header("Content-Type", content_type + "; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def body(self):
        raw = self.rfile.read(int(self.headers.get("Content-Length") or 0))
        try:
            return raw.decode("utf-8")
        except UnicodeDecodeError:
            raise HttpError(400, "the body must be UTF-8")

    def json_body(self):
        try:
            body = json.loads(self.body())
        except ValueError:
            raise HttpError(400, "the body must be JSON")
        if not isinstance(body, dict):
            raise HttpError(400, "the body must be a JSON object")
        return body

    def handle_api(self, method):
        path = self.path.split("?", 1)[0]
        m = re.fullmatch(r"/api/(players|saves|friends)/(\d+)(/scores)?", path)
        pid = int(m.group(2)) if m else None
        if method == "GET" and path == "/health":
            return 200, db.run(health)
        if method == "GET" and path == "/api/leaderboard":
            return 200, db.run(leaderboard)
        if method == "POST" and path == "/api/players":
            body = self.json_body()
            return db.run(lambda cur: new_player(cur, body))
        if method == "POST" and path == "/api/scores":
            body = self.json_body()
            return db.run(lambda cur: new_score(cur, body))
        if method == "POST" and path == "/api/friends":
            body = self.json_body()
            return db.run(lambda cur: add_friend(cur, body))
        if m and m.group(1) == "players" and method == "GET":
            if m.group(3):
                return 200, db.run(lambda cur: recent_scores(cur, pid))
            return 200, db.run(lambda cur: get_player(cur, pid))
        if m and m.group(1) == "saves" and not m.group(3) and method == "PUT":
            text = self.body()
            if len(text) > MAX_SAVE:
                raise HttpError(413, "save too large")
            try:
                json.loads(text)
            except ValueError:
                raise HttpError(400, "a save must be JSON")
            return db.run(lambda cur: put_save(cur, pid, text))
        if m and m.group(1) == "saves" and not m.group(3) and method == "GET":
            return 200, db.run(lambda cur: get_save(cur, pid)).encode()
        if m and m.group(1) == "friends" and not m.group(3) and method == "GET":
            return 200, db.run(lambda cur: friends(cur, pid))
        raise HttpError(404, "not found")

    def dispatch(self, method):
        try:
            code, body = self.handle_api(method)
        except HttpError as e:
            code, body = e.code, {"error": str(e)}
        except db.integrity as e:
            if getattr(e, "pgcode", None) == "23503" or "FOREIGN KEY" in str(e):
                code, body = 404, {"error": "no such player"}
            else:
                traceback.print_exc()
                code, body = 500, {"error": "internal error"}
        except db.unavailable:
            traceback.print_exc()
            code, body = 503, {"error": "database unavailable"}
        except Exception:
            traceback.print_exc()
            code, body = 500, {"error": "internal error"}
        sys.stderr.flush()
        self.reply(code, body)

    def do_GET(self):
        self.dispatch("GET")

    def do_POST(self):
        self.dispatch("POST")

    def do_PUT(self):
        self.dispatch("PUT")

    def log_message(self, fmt, *args):
        pass  # nginx keeps the access log


class Server(ThreadingHTTPServer):
    daemon_threads = True
    request_queue_size = 64


if __name__ == "__main__":
    db.init()
    if sys.argv[1:] == ["init-db"]:
        print("tables ready in the %s database" % db.kind)
        sys.exit(0)
    print("game-api on %s:%d, %s database" % (LISTEN[0], LISTEN[1], db.kind), flush=True)
    Server(LISTEN, Handler).serve_forever()
