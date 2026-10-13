#!/bin/bash
# Reference solution, run as root by sysadmin-test.ps1 -SelfTest to prove the checks pass.
set -euo pipefail
cat > /usr/local/sbin/backup-app <<'EOF'
#!/bin/bash
set -euo pipefail
dir=/var/backups/app
mkdir -p "$dir"
tar -czf "$dir/app-$(date +%F).tar.gz" -C /srv/app data
ls -1t "$dir"/app-*.tar.gz | tail -n +8 | xargs -r rm -f
EOF
chmod 755 /usr/local/sbin/backup-app
cat > /etc/systemd/system/backup-app.service <<'EOF'
[Unit]
Description=Back up /srv/app/data

[Service]
Type=oneshot
ExecStart=/usr/local/sbin/backup-app
EOF
cat > /etc/systemd/system/backup-app.timer <<'EOF'
[Unit]
Description=Nightly backup of /srv/app/data

[Timer]
OnCalendar=*-*-* 02:30:00
Persistent=true

[Install]
WantedBy=timers.target
EOF
systemctl daemon-reload
systemctl enable --now backup-app.timer
systemctl start backup-app.service
