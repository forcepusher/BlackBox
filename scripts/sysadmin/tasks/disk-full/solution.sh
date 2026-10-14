#!/bin/bash
# Reference solution, run as root by sysadmin-test.ps1 -SelfTest to prove the checks pass.
set -euo pipefail
rm -f /var/log/reportd/reportd.log.[0-9]* /var/tmp/reportd-core.*.dump
export DEBIAN_FRONTEND=noninteractive
command -v logrotate >/dev/null || { apt-get update -q && apt-get install -y -q logrotate; } # not in Debian's cloud image
cat > /etc/logrotate.d/reportd <<'EOF'
/var/log/reportd/*.log {
    daily
    maxsize 100M
    rotate 7
    compress
    delaycompress
    missingok
    notifempty
    copytruncate
    su reportd reportd
}
EOF
logrotate -d /etc/logrotate.d/reportd
