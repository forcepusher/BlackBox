#!/bin/bash
# Reference solution, run as root by sysadmin-test.ps1 -SelfTest while the players' traffic runs,
# to prove the checks pass. PostgreSQL gets the app's own tables (init-db); a bulk copy runs while
# the API keeps serving; then game-api stops for the final sync (rows added since, by id; saves and
# friendships changed since), the sequences go past every id SQLite ever handed out, the API is
# switched over and started again. The nightly backup moves to pg_dump; the SQLite file stays.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq postgresql python3-psycopg2 >/dev/null
systemctl enable --now postgresql
pw=$(head -c 32 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24)
sudo -u postgres psql -v ON_ERROR_STOP=1 -q -c "CREATE ROLE game LOGIN PASSWORD '$pw'" \
  -c "CREATE DATABASE game OWNER game ENCODING 'UTF8' TEMPLATE template0"
pgurl="postgresql://game:$pw@127.0.0.1:5432/game"
DATABASE_URL="$pgurl" runuser -u game -- python3 /opt/game-api/app.py init-db

cat > copy.py <<'PY'
"""copy.py bulk | final STATE: SQLite (the API's file) to PostgreSQL (DATABASE_URL), in COPY's text format."""
import io, json, os, sqlite3, sys, time
import psycopg2

COLS = {"players": "id, name, country, avatar, is_banned, created_at", "scores": "id, player_id, score, level, created_at",
        "saves": "player_id, data, updated_at", "friendships": "player_id, friend_id, created_at"}


def esc(v):
    if v is None:
        return "\\N"
    if isinstance(v, bytes):
        return "\\\\x" + v.hex()
    return str(v).replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n").replace("\r", "\\r")


def copy(cur, table, rows, into=None):
    buf = io.StringIO("".join("\t".join(esc(v) for v in r) + "\n" for r in rows))
    cur.copy_expert("COPY %s (%s) FROM STDIN" % (into or table, COLS[table]), buf)
    return len(rows)


src = sqlite3.connect("/var/lib/game-api/game.db", timeout=30, isolation_level=None)
pg = psycopg2.connect(os.environ["DATABASE_URL"])
cur = pg.cursor()
if sys.argv[1] == "bulk":
    src.execute("BEGIN")  # one snapshot of all tables
    since = time.strftime("%Y-%m-%d %H:%M:%S", time.gmtime(time.time() - 120))
    done = {t: copy(cur, t, src.execute("SELECT %s FROM %s" % (c, t)).fetchall()) for t, c in COLS.items()}
    state = {"players": src.execute("SELECT max(id) FROM players").fetchone()[0],
             "scores": src.execute("SELECT max(id) FROM scores").fetchone()[0], "since": since}
    src.execute("COMMIT")
    pg.commit()
    print(json.dumps(state))
    print("bulk copy: %s" % done, file=sys.stderr)
else:
    state = json.loads(sys.argv[2])
    done = {}
    done["players"] = copy(cur, "players", src.execute("SELECT %s FROM players WHERE id > ?" % COLS["players"], (state["players"],)).fetchall())
    done["scores"] = copy(cur, "scores", src.execute("SELECT %s FROM scores WHERE id > ?" % COLS["scores"], (state["scores"],)).fetchall())
    for t, key, update in (("saves", "player_id", "data = EXCLUDED.data, updated_at = EXCLUDED.updated_at"),
                           ("friendships", "player_id, friend_id", None)):
        stamp = "updated_at" if t == "saves" else "created_at"
        cur.execute("CREATE TEMP TABLE new_%s (LIKE %s)" % (t, t))
        done[t] = copy(cur, t, src.execute("SELECT %s FROM %s WHERE %s >= ?" % (COLS[t], t, stamp), (state["since"],)).fetchall(), "new_" + t)
        cur.execute("INSERT INTO %s SELECT * FROM new_%s ON CONFLICT (%s) DO %s" % (t, t, key, "UPDATE SET " + update if update else "NOTHING"))
    for t in ("players", "scores"):
        seq = src.execute("SELECT seq FROM sqlite_sequence WHERE name = ?", (t,)).fetchone()[0]
        cur.execute("SELECT setval(pg_get_serial_sequence(%s, 'id'), GREATEST((SELECT max(id) FROM " + t + "), %s))", (t, seq))
    for t in COLS:
        a = src.execute("SELECT count(*) FROM %s" % t).fetchone()[0]
        cur.execute("SELECT count(*) FROM %s" % t)
        b = cur.fetchone()[0]
        if a != b:
            sys.exit("%s: %d rows in SQLite, %d in PostgreSQL" % (t, a, b))
    pg.commit()
    print("final sync: %s" % done, file=sys.stderr)
PY
chmod 644 copy.py

state=$(DATABASE_URL="$pgurl" runuser -u game -- python3 copy.py bulk)
t0=$(date +%s%N)
systemctl stop game-api
# If the final sync fails, the API goes back up on SQLite as it was.
trap 'systemctl start game-api' ERR
DATABASE_URL="$pgurl" runuser -u game -- python3 copy.py final "$state"
cp -p /etc/game-api/env /etc/game-api/env.sqlite
printf '# game-api settings (see /opt/game-api/README.md); moved to PostgreSQL, the SQLite ones are in env.sqlite\nDATABASE_URL=%s\n' "$pgurl" > /etc/game-api/env
chown root:game /etc/game-api/env
chmod 640 /etc/game-api/env
mkdir -p /etc/systemd/system/game-api.service.d
printf '[Unit]\nAfter=postgresql.service\nWants=postgresql.service\n' > /etc/systemd/system/game-api.service.d/postgresql.conf
systemctl daemon-reload
systemctl start game-api
trap - ERR
for i in $(seq 1 60); do curl -fsS -o /dev/null http://127.0.0.1/health && break; sleep 0.5; done
echo "the API was down for $(( ($(date +%s%N) - t0) / 1000000 )) ms"

cat > /usr/local/bin/game-api-backup <<'SH'
#!/bin/bash
# Nightly backup of the game database (cron: /etc/cron.d/game-api-backup); 14 days are kept.
# Since the move to PostgreSQL a pg_dump of the database game; the old SQLite file is left as it is.
set -euo pipefail
dir=/var/backups/game-api
mkdir -p "$dir"
f="$dir/game-$(date +%F).dump"
/usr/sbin/runuser -u postgres -- pg_dump -Fc game > "$f.tmp"
mv "$f.tmp" "$f"
find "$dir" -name 'game-*' -mtime +14 -delete
SH
/usr/local/bin/game-api-backup
ls -la /var/backups/game-api
