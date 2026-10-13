#!/bin/bash
# Reference solution, run as root by sysadmin-test.ps1 -SelfTest to prove the checks pass.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
v=v22.11.0
curl -fsSL "https://nodejs.org/dist/$v/node-$v-linux-x64.tar.xz" | tar -xJ -C /opt
ln -sf /opt/node-$v-linux-x64/bin/node /usr/local/bin/node
useradd --system --home-dir /opt/tiny-arena --shell /usr/sbin/nologin arena
mkdir -p /opt/tiny-arena
tar -xzf /home/admin/game-server.tar.gz -C /opt/tiny-arena --strip-components=1
cd /opt/tiny-arena && PATH=/opt/node-$v-linux-x64/bin:$PATH npm ci --omit=dev
chown -R arena:arena /opt/tiny-arena
cat > /etc/systemd/system/tiny-arena.service <<'EOF'
[Unit]
Description=Tiny Arena game server
After=network.target

[Service]
User=arena
WorkingDirectory=/opt/tiny-arena
Environment=HOST=127.0.0.1 PORT=3000
ExecStart=/usr/local/bin/node server.js
Restart=always

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable --now tiny-arena
apt-get update -q
apt-get install -y -q nginx
cat > /etc/nginx/sites-available/tiny-arena <<'EOF'
server {
    listen 80 default_server;
    location /play {
        proxy_pass http://127.0.0.1:3000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_read_timeout 1h;
    }
}
EOF
rm -f /etc/nginx/sites-enabled/default
ln -sf /etc/nginx/sites-available/tiny-arena /etc/nginx/sites-enabled/tiny-arena
nginx -t
systemctl reload nginx
