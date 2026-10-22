#!/bin/bash
# Runs as root on the lab VM after the reboot, after check-host.ps1 (which stored rows through nginx
# and left the players' traffic log and those rows in upload/); prints PASS/FAIL lines.
# The data checks compare PostgreSQL with the seed (made again by files/seed.py), row by row as
# md5s computed the same way on both sides, and look there for every write the API acknowledged.
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; }
db=/var/lib/game-api/game.db

if runuser -u postgres -- psql -X -tAc "select 1" >/dev/null 2>&1; then
  pass "PostgreSQL runs after the reboot"
  echo "INFO $(runuser -u postgres -- psql -X -tAc "select version()" | cut -c1-60)"
else
  fail "PostgreSQL runs after the reboot ($(command -v psql >/dev/null && echo "psql can't connect" || echo "not installed"))"
fi
systemctl is-enabled --quiet game-api && systemctl is-active --quiet game-api && pass "game-api runs after the reboot" || fail "game-api runs after the reboot ($(systemctl is-enabled game-api 2>&1), $(systemctl is-active game-api 2>&1))"

pid=$(systemctl show -p MainPID --value game-api 2>/dev/null)
url=""
[ "${pid:-0}" != 0 ] && url=$(tr '\0' '\n' < /proc/$pid/environ 2>/dev/null | sed -n 's/^DATABASE_URL=//p' | head -1)
masked=$(printf '%s' "$url" | sed -E 's#(://[^:/@]*:)[^@]*@#\1***@#')
case "$url" in
  postgres*) pass "game-api uses PostgreSQL (DATABASE_URL=$masked)";;
  *) fail "game-api uses PostgreSQL (its DATABASE_URL: ${masked:-none}; /etc/game-api/env: $(grep -h '^DATABASE_URL' /etc/game-api/env 2>/dev/null | sed -E 's#(://[^:/@]*:)[^@]*@#\1***@#' | head -1))";;
esac
held=""
cg=$(systemctl show -p ControlGroup --value game-api 2>/dev/null)
for p in $(cat "/sys/fs/cgroup$cg/cgroup.procs" 2>/dev/null); do
  held="$held$(ls -l /proc/$p/fd 2>/dev/null | grep -oE '/[^ ]*(game\.db|\.db)(-wal|-shm)?$' | sort -u | tr '\n' ' ')"
done
[ "${pid:-0}" != 0 ] && [ -z "$held" ] && pass "game-api has no SQLite file open" || fail "game-api has no SQLite file open (${held:-not running})"

# The data, the players' writes, the seeded rows through the API, the backup job.
python3 - "$url" <<'PY'
import base64, bz2, glob, gzip, hashlib, json, lzma, os, pwd, re, subprocess, sys, tempfile, time, urllib.request
from urllib.parse import urlparse, parse_qs
sys.path.insert(0, "files")
import seed

TOKEN = os.environ["TOKEN"]
URL = sys.argv[1]
LIMIT = 75  # seconds without an answer (the ticket allows a minute)
seeded = seed.rows(TOKEN)


def say(kind, text):
    print(kind + " " + text, flush=True)


def psql(dbname, port, sql):
    env = dict(os.environ, PGOPTIONS="-c TimeZone=UTC -c DateStyle=ISO")
    r = subprocess.run(["runuser", "-u", "postgres", "--", "psql", "-X", "-q", "-At", "-F", "\t", "-v", "ON_ERROR_STOP=1",
                        "-p", str(port), "-d", dbname, "-c", sql], capture_output=True, text=True, env=env)
    if r.returncode:
        raise RuntimeError((r.stderr or r.stdout).strip().replace("\n", " ")[:300])
    return [line.split("\t") for line in r.stdout.split("\n") if line]


def http(method, path, body=None):
    req = urllib.request.Request("http://127.0.0.1" + path, data=body, method=method, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=20) as resp:
            return resp.status, resp.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()
    except Exception as e:
        return 0, str(e).encode()


def md5(text):
    return hashlib.md5(text.encode("utf-8")).hexdigest()


def canon(values):
    out = []
    for v in values:
        if v is None:
            out.append("\x01")
        elif isinstance(v, (bytes, bytearray, memoryview)):
            out.append(bytes(v).hex())
        else:
            out.append(str(int(v)) if isinstance(v, bool) else str(v))
    return md5("\x1f".join(out))


# The players' traffic (from this PC) and the rows check-host.ps1 stored.
def read_jsonl(path):
    rows = []
    try:
        with open(path, encoding="utf-8-sig") as f:
            for line in f:
                try:
                    rows.append(json.loads(line))
                except ValueError:
                    pass
    except OSError:
        return None
    return rows


traffic = read_jsonl("upload/traffic.jsonl")
host = read_jsonl("upload/host-writes.jsonl") or []
writes = []  # (acked, write) in the order they were sent
if traffic is not None:
    for e in traffic:
        if e.get("ev") == "req" and e.get("w"):
            writes.append((bool(e.get("ok")), e["w"], e.get("ms")))
writes += [(True, w, None) for w in host]

# Traffic: did the API keep answering?
if traffic is not None:
    start = next((e["ms"] for e in traffic if e.get("ev") == "start"), None)
    stop = next((e["ms"] for e in reversed(traffic) if e.get("ev") == "stop"), None)
    reqs = [e for e in traffic if e.get("ev") == "req"]
    end = stop or (reqs[-1]["ms"] if reqs else start)

    def longest(times):
        pts = [start] + sorted(t for t in times if start <= t <= end) + [end]
        gap, at = max(((b - a, a) for a, b in zip(pts, pts[1:])), default=(0, start))
        return gap / 1000.0, time.strftime("%H:%M:%S", time.gmtime(at / 1000.0))

    if start is None or not reqs:
        say("FAIL", "the players' traffic ran during the work (nothing in its log)")
    else:
        minutes = (end - start) / 60000.0
        ok = [e for e in reqs if e.get("ok")]
        tries_failed = len(reqs) - len(ok)
        statuses = {}
        for e in reqs:
            if not e.get("ok"):
                statuses[str(e.get("status"))] = statuses.get(str(e.get("status")), 0) + 1
        say("INFO", "traffic: %d tries over %.1f min, %d answered, failed tries by status: %s" % (
            len(reqs), minutes, len(ok), ", ".join("%s: %d" % kv for kv in sorted(statuses.items())) or "none"))
        stale = sum(1 for e in reqs if e.get("op") == "get-save" and e.get("same") is False)
        if stale:
            say("INFO", "traffic: %d reads of a cloud save got something else than the last save the API had accepted" % stale)
        if stop is None:
            say("FAIL", "the players' traffic ran until the work was done (its log ends at %s UTC without a stop)" % time.strftime("%H:%M:%S", time.gmtime(end / 1000.0)))
        else:
            say("PASS", "the players' traffic ran until the work was done (%.1f min)" % minutes)
        g_all, at_all = longest(e["ms"] for e in ok)
        g_w, at_w = longest(e["ms"] for e in ok if e.get("w"))
        text = "longest stretch without an answer %.0f s (from %s UTC), without an accepted write %.0f s (from %s UTC)" % (g_all, at_all, g_w, at_w)
        say("PASS" if max(g_all, g_w) <= LIMIT else "FAIL", "the API kept serving the players, with no outage over a minute (%s)" % text)

# PostgreSQL: the database the API uses (or, if it does not, one that has a players table).
pg = None
try:
    try:
        pwd.getpwnam("postgres")
    except KeyError:
        raise RuntimeError("PostgreSQL is not installed")
    dbname, port = None, 5432
    if URL.startswith("postgres"):
        u = urlparse(URL)
        q = parse_qs(u.query)
        dbname = (q.get("dbname") or [u.path.lstrip("/") or u.username or ""])[0] or None
        port = int((q.get("port") or [u.port or 5432])[0])
    if not dbname:
        for (d,) in psql("postgres", port, "SELECT datname FROM pg_database WHERE NOT datistemplate AND datname <> 'postgres'"):
            if psql(d, port, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'players'")[0][0] != "0":
                dbname = d
                break
    if not dbname:
        raise RuntimeError("no PostgreSQL database with a players table")
    found = psql(dbname, port, "SELECT table_schema FROM information_schema.tables WHERE table_name = 'players' "
                 "AND table_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY (table_schema = 'public') DESC LIMIT 1")
    if not found:
        raise RuntimeError("no players table in the database %s" % dbname)
    schema = found[0][0]
    enc = psql(dbname, port, "SELECT pg_encoding_to_char(encoding) FROM pg_database WHERE datname = current_database()")[0][0]
    pg = (dbname, port, schema)
except Exception as e:
    say("FAIL", "every seeded row is in PostgreSQL unchanged (%s)" % e)
    say("FAIL", "every write the API accepted during the work is in PostgreSQL (no database to look in)")

if pg:
    dbname, port, schema = pg
    types, data = {}, {}
    try:
        for table, cols in seed.COLUMNS.items():
            got = psql(dbname, port, "SELECT column_name, data_type FROM information_schema.columns WHERE table_schema = '%s' "
                       "AND table_name = '%s'" % (schema, table))
            types[table] = dict((c, t) for c, t in got)
    except Exception as e:
        say("INFO", "reading the column types failed: %s" % e)
    for table in seed.COLUMNS:
        types.setdefault(table, {})
    say("INFO","database %s (%s), tables in %s: %s" % (dbname, enc, schema, "; ".join(
        "%s(%s)" % (t, ", ".join("%s %s" % (c, types[t].get(c, "MISSING")) for c in cols)) for t, cols in seed.COLUMNS.items())))

    def expr(table, col):
        t = types[table].get(col)
        c = '"%s"' % col
        if t is None:
            return "'<missing>'"
        if t == "boolean":
            e = "CASE WHEN %s IS NULL THEN NULL WHEN %s THEN '1' ELSE '0' END" % (c, c)
        elif t == "bytea":
            e = "encode(%s, 'hex')" % c
        elif t.startswith("timestamp") and "with time zone" in t:
            e = "to_char(%s AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')" % c
        elif t.startswith("timestamp"):
            e = "to_char(%s, 'YYYY-MM-DD HH24:MI:SS')" % c
        else:
            e = "%s::text" % c
        return "coalesce(%s, chr(1))" % e

    def row_md5(table, cols):
        return "md5(concat_ws(chr(31), %s))" % ", ".join(expr(table, c) for c in cols)

    def blob_sha(table, col):
        return "coalesce(encode(sha256(%s), 'hex'), '')" % col if types[table].get(col) == "bytea" else "md5(%s::text)" % col

    key = {"players": "id::text", "scores": "id::text", "saves": "player_id::text", "friendships": "player_id::text || ',' || friend_id::text"}
    extra = {
        "players": [row_md5("players", ["name"]), blob_sha("players", "avatar")],
        "scores": [row_md5("scores", ["player_id", "score", "level"])],
        "saves": ["encode(sha256(convert_to(data::text, 'UTF8')), 'hex')"],
        "friendships": [],
    }
    try:
        for table, cols in seed.COLUMNS.items():
            sql = "SELECT %s FROM \"%s\".\"%s\"" % (", ".join([key[table], row_md5(table, cols)] + extra[table]), schema, table)
            data[table] = dict((r[0], r[1:]) for r in psql(dbname, port, sql))
    except Exception as e:
        say("FAIL", "every seeded row is in PostgreSQL unchanged (reading it failed: %s)" % e)
        data = None

    if data is not None:
        changed_saves = set(str(w["player_id"]) for _, w, _ in writes if w.get("kind") == "save")
        problems, counts = [], []
        for table, rows in seeded.items():
            missing, differ, n = [], [], 0
            for row in rows:
                k = "%s,%s" % (row[0], row[1]) if table == "friendships" else str(row[0])
                if table == "saves" and k in changed_saves:
                    continue
                n += 1
                have = data[table].get(k)
                if have is None:
                    missing.append(k)
                elif have[0] != canon(row):
                    differ.append(k)
            counts.append("%s %d" % (table, n))
            if missing or differ:
                problems.append("%s: %d of %d missing%s, %d differ%s" % (table, len(missing), n, (" (" + " ".join(missing[:4]) + ")") if missing else "",
                                                                     len(differ), (" (" + " ".join(differ[:4]) + ")") if differ else ""))
        if problems:
            say("FAIL", "every seeded row is in PostgreSQL unchanged (" + "; ".join(problems) + ")")
        else:
            say("PASS", "every seeded row is in PostgreSQL unchanged (" + ", ".join(counts) + "; saves the players changed since are checked below)")

        # Every write the API acknowledged. A save may also hold what a later try sent whose answer never came.
        lost = {"player": [], "score": [], "save": [], "friend": []}
        acked = {"player": 0, "score": 0, "save": 0, "friend": 0}
        saves = {}
        for i, (ok, w, ms) in enumerate(writes):
            kind = w.get("kind")
            if kind == "save":
                s = saves.setdefault(str(w["player_id"]), [])
                s.append((ok, w["sha256"], ms))
                continue
            if not ok:
                continue
            acked[kind] += 1
            if kind == "player":
                have = data["players"].get(str(w["id"]))
                want_avatar = w.get("avatar_sha256") or ""
                if have is None or have[1] != md5(w["name"]) or (types["players"].get("avatar") == "bytea" and have[2] != want_avatar):
                    lost["player"].append((w["id"], ms))
            elif kind == "score":
                have = data["scores"].get(str(w["id"]))
                if have is None or have[1] != canon([w["player_id"], w["score"], w.get("level")]):
                    lost["score"].append((w["id"], ms))
            elif kind == "friend":
                if "%s,%s" % (w["player_id"], w["friend_id"]) not in data["friendships"]:
                    lost["friend"].append(("%s,%s" % (w["player_id"], w["friend_id"]), ms))
        for pid, tries in saves.items():
            last = max((i for i, t in enumerate(tries) if t[0]), default=None)
            if last is None:
                continue
            acked["save"] += 1
            allowed = set(t[1] for t in tries[last:])
            have = data["saves"].get(pid)
            if have is None or have[1] not in allowed:
                lost["save"].append((pid, tries[last][2]))
        total = sum(acked.values())
        if traffic is None:
            say("FAIL", "every write the API accepted during the work is in PostgreSQL (no traffic log)")
        elif any(lost.values()):
            def when(ms):
                return time.strftime("%H:%M:%S", time.gmtime(ms / 1000.0)) if ms else "check"
            parts = []
            for kind, items in lost.items():
                if items:
                    times = [ms for _, ms in items if ms]
                    span = " between %s and %s UTC" % (when(min(times)), when(max(times))) if times else ""
                    parts.append("%d of %d %ss%s, e.g. %s" % (len(items), acked[kind], kind, span, " ".join(str(k) for k, _ in items[:3])))
            say("FAIL", "every write the API accepted during the work is in PostgreSQL (lost: " + "; ".join(parts) + ")")
        else:
            say("PASS", "every write the API accepted during the work is in PostgreSQL (%d: %s)" % (total, ", ".join("%d %ss" % (v, k) for k, v in acked.items())))

# The seeded rows with the hard parts, through nginx and the API, byte for byte.
players =dict((p[0], p) for p in seeded["players"])
bad = []
for pid in (1, 2, 3, 4, 5):
    code, body = http("GET", "/api/players/%d" % pid)
    try:
        got = json.loads(body.decode("utf-8"))
        avatar = None if got.get("avatar") is None else base64.b64decode(got["avatar"])
        p = players[pid]
        if code != 200 or got.get("name") != p[1] or got.get("country") != p[2] or avatar != p[3] or bool(got.get("banned")) != bool(p[4]):
            bad.append("player %d" % pid)
    except Exception:
        bad.append("player %d (HTTP %d)" % (pid, code))
for pid, text, _ in seeded["saves"]:
    if pid in (1, 3, 5):
        code, body = http("GET", "/api/saves/%d" % pid)
        if code != 200 or body != text.encode("utf-8"):
            bad.append("save %d (HTTP %d, %d of %d bytes)" % (pid, code, len(body), len(text.encode("utf-8"))))
if bad:
    say("FAIL", "seeded players and saves come back through the API exactly as they were (wrong: " + ", ".join(bad) + ")")
else:
    say("PASS", "seeded players and saves come back through the API exactly as they were (emoji, quotes, avatars, an empty one, NULL and empty countries, saves up to 30 KB)")

# The old SQLite file, kept as a fallback: still whole, with the seeded players. Read as its owner,
# so SQLite's -wal and -shm files keep belonging to the service.
db = "/var/lib/game-api/game.db"
if not os.path.exists(db):
    say("FAIL", "the old SQLite file is kept (no %s)" % db)
else:
    owner = pwd.getpwuid(os.stat(db).st_uid).pw_name
    r = subprocess.run(["runuser", "-u", owner, "--", "sqlite3", "-readonly", db, "PRAGMA integrity_check; SELECT count(*) FROM players WHERE id <= 3000;"],
                       capture_output=True, text=True)
    lines = r.stdout.split()
    want = len(seeded["players"])
    if r.returncode == 0 and lines[:1] == ["ok"] and lines[-1:] == [str(want)]:
        say("PASS", "the old SQLite file is kept, whole, with the %d seeded players" % want)
    else:
        say("FAIL", "the old SQLite file is kept, whole, with the %d seeded players (%s)" % (want, (r.stdout + r.stderr).strip().replace("\n", " ")[:200]))

# The nightly backup: find the job (cron or a systemd timer), run it as it would run, and look for
# a PostgreSQL dump with a player who signed up just before.
marker = "backup check " + TOKEN
code, body = http("POST", "/api/players", json.dumps({"name": marker}).encode())
if code != 201:
    say("INFO", "backup: signing up the marker player failed (HTTP %d); looking for the seeded player 1 instead" % code)
    marker = "Ada \U0001f409 " + TOKEN
jobs = []
pattern = re.compile(r"backup|dump|pg_|game", re.I)


def cron_command(text):
    out, i = "", 0
    while i < len(text):
        if text[i] == "\\" and text[i + 1:i + 2] == "%":
            out, i = out + "%", i + 2
        elif text[i] == "%":
            return out, text[i + 1:].replace("%", "\n")
        else:
            out, i = out + text[i], i + 1
    return out, None


def cron_file(path, user=None):
    env = {"SHELL": "/bin/sh", "PATH": "/usr/bin:/bin"}
    try:
        lines = open(path, encoding="utf-8", errors="replace").read().split("\n")
    except OSError:
        return
    for line in lines:
        s = line.strip()
        if not s or s.startswith("#"):
            continue
        m = re.match(r"^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$", s)
        if m:
            env[m.group(1)] = m.group(2).strip().strip("'\"")
            continue
        parts = s.split(None, (1 if s.startswith("@") else 5) + (0 if user else 1))
        if len(parts) < (2 if s.startswith("@") else 6) + (0 if user else 1):
            continue
        who = user or parts[-2]
        command = parts[-1]
        if s.startswith("@reboot") or not pattern.search(command) or "run-parts" in command:
            continue
        jobs.append(("cron: " + s, "cron", who, cron_command(command), dict(env)))


cron_file("/etc/crontab")
for f in sorted(glob.glob("/etc/cron.d/*")):
    cron_file(f)
for f in sorted(glob.glob("/var/spool/cron/crontabs/*")):
    cron_file(f, os.path.basename(f))
for f in sorted(glob.glob("/etc/cron.daily/*")):
    if subprocess.run(["dpkg", "-S", f], capture_output=True).returncode == 0:
        continue  # a package's own job (dpkg backs up its status file)
    try:
        if pattern.search(os.path.basename(f) + open(f, errors="replace").read()):
            jobs.append(("cron.daily: " + f, "cron", "root", (f, None), {"SHELL": "/bin/sh", "PATH": "/usr/local/sbin:/usr/local/bin:/sbin:/bin:/usr/sbin:/usr/bin"}))
    except OSError:
        pass
timers = subprocess.run(["systemctl", "list-timers", "--all", "--no-legend", "--plain"], capture_output=True, text=True).stdout
for t in re.findall(r"(\S+\.timer)", timers):
    unit = subprocess.run(["systemctl", "show", "-p", "Unit", "--value", t], capture_output=True, text=True).stdout.strip()
    execs = subprocess.run(["systemctl", "show", "-p", "ExecStart", "--value", unit], capture_output=True, text=True).stdout
    if pattern.search(unit + " " + execs) and not re.search(r"apt|man-db|logrotate|dpkg|e2scrub|fstrim|systemd-tmpfiles", unit):
        jobs.append(("systemd timer " + t, "timer", t, unit, None))


def dump_text(path):
    """The SQL in a PostgreSQL dump (any pg_dump format, maybe compressed); None if it is none."""
    if os.path.isdir(path):
        if not os.path.exists(os.path.join(path, "toc.dat")):
            return None
        r = subprocess.run(["pg_restore", "-f", "-", path], capture_output=True)
        return r.stdout.decode("utf-8", "replace") if r.returncode == 0 else None
    try:
        raw = open(path, "rb").read()
    except OSError:
        return None
    for magic, unpack in ((b"\x1f\x8b", gzip.decompress), (b"\xfd7zXZ", lzma.decompress), (b"BZh", bz2.decompress)):
        if raw.startswith(magic):
            try:
                raw = unpack(raw)
            except Exception:
                return None
    if raw.startswith(b"\x28\xb5\x2f\xfd"):
        r = subprocess.run(["zstd", "-dc"], input=raw, capture_output=True)
        raw = r.stdout if r.returncode == 0 else b""
    if raw.startswith(b"PGDMP") or raw[257:262] == b"ustar":
        with tempfile.NamedTemporaryFile(delete=False) as f:
            f.write(raw)
        r = subprocess.run(["pg_restore", "-f", "-", f.name], capture_output=True)
        os.unlink(f.name)
        return r.stdout.decode("utf-8", "replace") if r.returncode == 0 else None
    text = raw.decode("utf-8", "replace")
    return text if "PostgreSQL database dump" in text or "PostgreSQL database cluster dump" in text else None


def new_files(since):
    found = []
    for top in ("/var/backups", "/var/lib/postgresql", "/srv", "/opt", "/root", "/home", "/backup", "/backups", "/mnt"):
        for root, dirs, files in os.walk(top):
            dirs[:] = [d for d in dirs if not re.fullmatch(r"\d+", d) or root != "/var/lib/postgresql"]
            if os.path.exists(os.path.join(root, "toc.dat")) and os.path.getmtime(os.path.join(root, "toc.dat")) > since:
                found.append(root)
                dirs[:] = []
                continue
            for f in files:
                p = os.path.join(root, f)
                try:
                    if os.path.isfile(p) and os.path.getmtime(p) > since and os.path.getsize(p) > 0:
                        found.append(p)
                except OSError:
                    pass
    return found


done = None
tried = []
for label, how, who, what, env in jobs:
    since = time.time() - 0.5
    try:
        if how == "cron":
            try:
                home = pwd.getpwnam(who).pw_dir
            except KeyError:
                home = "/"
            command, stdin = what
            env = dict(env, HOME=env.get("HOME", home), LOGNAME=who, USER=who)
            shell = env.get("SHELL", "/bin/sh")
            argv = [shell, "-c", command] if who == "root" else ["/usr/sbin/runuser", "-u", who, "--", shell, "-c", command]
            if label.startswith("cron.daily"):
                argv = [command]
            r = subprocess.run(argv, input=(stdin or "").encode(), capture_output=True, env=env, timeout=600)
        else:
            r = subprocess.run(["systemctl", "start", "--wait", what], capture_output=True, timeout=600)
        outcome = "exit %d" % r.returncode + (": " + (r.stderr or r.stdout).decode("utf-8", "replace").strip().replace("\n", " ")[:160] if r.returncode else "")
    except Exception as e:
        outcome = "could not run: %s" % e
    hits = []
    for p in new_files(since):
        text = dump_text(p)
        if text is not None and marker in text:
            hits.append(p)
    tried.append("%s (%s, new files: %s)" % (label, outcome, ", ".join(new_files(since)[:4]) or "none"))
    if hits:
        done = (label, how, who, hits[0])
        break
if done:
    label, how, who, path = done
    say("PASS", "the nightly backup job writes a PostgreSQL dump with current data (%s -> %s)" % (label, path))
else:
    say("FAIL", "the nightly backup job writes a PostgreSQL dump with current data (%s)" % ("; ".join(tried) if tried else "no backup job found in cron or systemd timers"))
    label, how, who = jobs[0][:3] if jobs else (None, None, None)
if how == "cron":
    ok = subprocess.run(["systemctl", "is-active", "--quiet", "cron"]).returncode == 0 and subprocess.run(["systemctl", "is-enabled", "--quiet", "cron"]).returncode == 0
    say("PASS" if ok else "FAIL", "cron, which runs the backup job, runs and starts at boot")
elif how == "timer":
    ok = subprocess.run(["systemctl", "is-active", "--quiet", who]).returncode == 0
    say("PASS" if ok else "FAIL", "the timer %s that runs the backup job is active after the reboot" % who)
else:
    say("FAIL", "the backup job is scheduled (nothing in cron or systemd timers)")
PY

# The API serves everything with the SQLite file gone (it really uses PostgreSQL); then it is put back.
away=/var/lib/.bbcheck-sqlite-away
if [ -e "$db" ]; then
  restore() {
    systemctl stop game-api 2>/dev/null
    rm -f "$db" "$db-wal" "$db-shm"
    for f in "$away"/*; do [ -e "$f" ] && mv "$f" /var/lib/game-api/; done
    rmdir "$away" 2>/dev/null
    systemctl start game-api 2>/dev/null
  }
  systemctl stop game-api
  mkdir -p "$away"
  for f in "$db" "$db-wal" "$db-shm"; do [ -e "$f" ] && mv "$f" "$away/"; done
  trap restore EXIT
  systemctl start game-api
  for i in $(seq 1 30); do [ "$(curl -s -m 5 -o /dev/null -w '%{http_code}' http://127.0.0.1/health)" = 200 ] && break; sleep 2; done
  p1=$(curl -s -m 10 http://127.0.0.1/api/players/1)
  n=$(curl -s -m 20 http://127.0.0.1/api/leaderboard | python3 -c "import json, sys; print(len(json.load(sys.stdin)))" 2>/dev/null)
  if echo "$p1" | grep -q "$TOKEN" && [ "$n" = 20 ]; then
    pass "with the SQLite file moved away and game-api restarted, the API still serves the players and the leaderboard"
  else
    fail "with the SQLite file moved away and game-api restarted, the API still serves the players and the leaderboard (player 1: $(echo "$p1" | head -c 80); leaderboard entries: ${n:-none})"
  fi
  restore
  trap - EXIT
  for i in $(seq 1 30); do [ "$(curl -s -m 5 -o /dev/null -w '%{http_code}' http://127.0.0.1/health)" = 200 ] && break; sleep 2; done
fi
true
