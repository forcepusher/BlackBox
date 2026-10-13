#!/bin/bash
# A web server, a metrics exporter on 9100 and an internal cache on 6379, all listening on every
# address, and no firewall.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update -q
apt-get install -y -q nginx
cat > /usr/local/bin/lab-listener <<'EOF'
#!/usr/bin/env python3
# Answers every connection on the given port with one line.
import socketserver, sys
class Handler(socketserver.BaseRequestHandler):
    def handle(self):
        self.request.sendall(sys.argv[2].encode() + b"\n")
socketserver.ThreadingTCPServer.allow_reuse_address = True
socketserver.ThreadingTCPServer(("0.0.0.0", int(sys.argv[1])), Handler).serve_forever()
EOF
chmod 755 /usr/local/bin/lab-listener
unit() {
  cat > "/etc/systemd/system/$1.service" <<EOF
[Unit]
Description=$4
After=network.target

[Service]
ExecStart=/usr/local/bin/lab-listener $2 $3
DynamicUser=yes
Restart=always

[Install]
WantedBy=multi-user.target
EOF
}
unit metrics 9100 "lab_up=1" "Prometheus-style metrics exporter"
unit cache 6379 "+PONG" "Internal cache"
systemctl daemon-reload
systemctl enable --now metrics cache
ufw disable >/dev/null 2>&1 || true
