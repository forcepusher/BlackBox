#!/bin/bash
# Reference solution, run as root by sysadmin-test.ps1 -SelfTest to prove the checks pass;
# solution-host.ps1 has copied the app to /tmp/statuspage.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq docker.io >/dev/null
systemctl enable --now docker
mkdir -p /opt
rm -rf /opt/statuspage && cp -r /tmp/statuspage /opt/statuspage
docker build -q -t statuspage /opt/statuspage
docker volume create statuspage-data
docker run -d --name statuspage --restart unless-stopped -p 8080:8080 -v statuspage-data:/data statuspage
for i in $(seq 1 30); do curl -fsS http://127.0.0.1:8080/ && break; sleep 2; done
