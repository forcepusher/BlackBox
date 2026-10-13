#!/bin/bash
# Reference solution, run as root by sysadmin-test.ps1 -SelfTest to prove the checks pass.
set -euo pipefail
chown -R orders:orders /var/lib/orders-api
chmod 750 /var/lib/orders-api
chmod 640 /var/lib/orders-api/orders.json
