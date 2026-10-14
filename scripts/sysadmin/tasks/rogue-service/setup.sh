#!/bin/bash
# A backdoor-like listener on port 31337 with a harmless-sounding name, started two ways: a
# systemd service, and an @reboot line in root's crontab that brings it back if the service is
# only stopped and disabled.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
command -v cron >/dev/null || { apt-get update -q && apt-get install -y -q cron; } # not in Debian's cloud image
install -d /usr/local/lib/netlogd
cat > /usr/local/lib/netlogd/netlogd <<'EOF'
#!/usr/bin/env python3
# netlogd: network log relay
import socket
s = socket.socket()
s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(("0.0.0.0", 31337))
s.listen(5)
while True:
    c, _ = s.accept()
    c.sendall(b"netlogd ready\n")
    c.close()
EOF
chmod 755 /usr/local/lib/netlogd/netlogd
cat > /etc/systemd/system/systemd-netlogd.service <<'EOF'
[Unit]
Description=Network Log Relay
After=network.target

[Service]
ExecStart=/usr/local/lib/netlogd/netlogd
User=nobody
Restart=always

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable --now systemd-netlogd
# While the service holds the port this copy just fails to bind and exits.
{ crontab -l -u root 2>/dev/null || true; echo '@reboot sleep 20 && /usr/local/lib/netlogd/netlogd >/dev/null 2>&1'; } | crontab -u root -
