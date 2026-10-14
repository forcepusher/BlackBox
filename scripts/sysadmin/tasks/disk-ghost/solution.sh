#!/bin/bash
# Reference solution, run as root by sysadmin-test.ps1 -SelfTest to prove the checks pass.
set -euo pipefail
echo "[say] The space was held by /var/log/metricsd/metrics.log, deleted with rm while metricsd still had it open; restarting metricsd released it, and logrotate now truncates the log in place (copytruncate)."
systemctl restart metricsd
cat > /etc/logrotate.d/metricsd <<'EOF'
/var/log/metricsd/metrics.log {
    daily
    rotate 7
    compress
    missingok
    notifempty
    copytruncate
}
EOF
