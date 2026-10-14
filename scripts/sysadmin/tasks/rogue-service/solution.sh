#!/bin/bash
# Reference solution, run as root by sysadmin-test.ps1 -SelfTest to prove the checks pass.
set -euo pipefail
systemctl disable --now systemd-netlogd
rm -f /etc/systemd/system/systemd-netlogd.service
systemctl daemon-reload
{ crontab -l -u root | grep -v netlogd || true; } | crontab -u root -
pkill -f netlogd/netlogd || true
chmod 000 /usr/local/lib/netlogd/netlogd
echo "[say] It was the systemd service systemd-netlogd plus an @reboot cron job in root's crontab."
