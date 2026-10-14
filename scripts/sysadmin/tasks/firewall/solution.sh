#!/bin/bash
# Reference solution, run as root by sysadmin-test.ps1 -SelfTest to prove the checks pass.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get install -y -q ufw
ufw default deny incoming
ufw default allow outgoing
ufw allow 22/tcp
ufw allow 80/tcp
ufw allow 443/tcp
ufw allow from "$LAB_HOST_SEEN_AS" to any port 9100 proto tcp
ufw --force enable
