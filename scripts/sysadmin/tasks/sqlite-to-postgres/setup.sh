#!/bin/bash
# The game's backend as it runs today: game-api (Python, user game) on 127.0.0.1:8080 behind nginx,
# its data in one SQLite file in WAL mode (a few thousand players, two and a half years of scores,
# cloud saves), backed up every night by cron with sqlite3 .backup. Its code also speaks
# PostgreSQL, but neither PostgreSQL nor psycopg2 is installed. The players' traffic
# (traffic-host.ps1, on this PC) starts once this is done.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq nginx sqlite3 cron >/dev/null

useradd --system --home-dir /var/lib/game-api --shell /usr/sbin/nologin game
install -d -m 755 /opt/game-api /etc/game-api
install -m 644 files/game-api/app.py files/game-api/README.md /opt/game-api/
printf '# game-api settings (see /opt/game-api/README.md)\nDATABASE_URL=sqlite:////var/lib/game-api/game.db\n' > /etc/game-api/env
install -d -o game -g game -m 750 /var/lib/game-api
DATABASE_URL=sqlite:////var/lib/game-api/game.db runuser -u game -- python3 /opt/game-api/app.py init-db
python3 files/seed.py create /var/lib/game-api/game.db "$TOKEN"
chown game:game /var/lib/game-api/game.db*

cat > /etc/systemd/system/game-api.service <<'UNIT'
[Unit]
Description=Game API (players, scores, saves)
After=network.target

[Service]
User=game
EnvironmentFile=/etc/game-api/env
ExecStart=/usr/bin/python3 /opt/game-api/app.py
Restart=on-failure
RestartSec=3

[Install]
WantedBy=multi-user.target
UNIT

cat > /etc/nginx/sites-available/game <<'SITE'
server {
    listen 80 default_server;
    listen [::]:80 default_server;
    location /api/ {
        proxy_pass http://127.0.0.1:8080;
    }
    location = /health {
        proxy_pass http://127.0.0.1:8080;
    }
    location / {
        return 404;
    }
}
SITE
rm -f /etc/nginx/sites-enabled/default
ln -sf /etc/nginx/sites-available/game /etc/nginx/sites-enabled/game
nginx -t -q
systemctl daemon-reload
systemctl enable --now game-api
systemctl reload nginx

# The nightly backup, and last night's copy.
cat > /usr/local/bin/game-api-backup <<'SH'
#!/bin/bash
# Nightly backup of the game database (cron: /etc/cron.d/game-api-backup); 14 days are kept.
set -euo pipefail
dir=/var/backups/game-api
mkdir -p "$dir"
sqlite3 /var/lib/game-api/game.db ".backup '$dir/game-$(date +%F).db'"
find "$dir" -name 'game-*' -mtime +14 -delete
SH
chmod 755 /usr/local/bin/game-api-backup
printf '# Nightly backup of the game database\n15 3 * * * root /usr/local/bin/game-api-backup\n' > /etc/cron.d/game-api-backup
/usr/local/bin/game-api-backup
touch -d "$(date -d yesterday +%F) 03:15" /var/backups/game-api/game-*.db
mv /var/backups/game-api/game-$(date +%F).db /var/backups/game-api/game-$(date -d yesterday +%F).db
systemctl enable --now cron

for i in $(seq 1 30); do curl -fsS -o /dev/null http://127.0.0.1/health && break; sleep 1; done
curl -fsS http://127.0.0.1/api/players/1 | grep -q "$TOKEN"
curl -fsS http://127.0.0.1/api/leaderboard | python3 -c "import json, sys; assert len(json.load(sys.stdin)) == 20"
