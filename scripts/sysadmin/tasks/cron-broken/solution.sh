#!/bin/bash
# Reference solution, run as root by sysadmin-test.ps1 -SelfTest to prove the checks pass.
set -euo pipefail
sed -i 's/\r$//' /usr/local/bin/sales-report
chmod 644 /etc/cron.d/sales-report
systemctl restart cron
