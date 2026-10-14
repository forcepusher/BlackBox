#!/bin/bash
# Reference solution, run as root by sysadmin-test.ps1 -SelfTest to prove the checks pass;
# solution-host.ps1 has brought the dump and the files to /tmp/move.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq nginx postgresql python3-psycopg2 >/dev/null
tar -xzf /tmp/move/shop-files.tgz -C /
. /etc/shop-api.env
sudo -u postgres psql -v ON_ERROR_STOP=1 -q -c "CREATE ROLE shop LOGIN PASSWORD '$DB_PASSWORD'" -c "CREATE DATABASE shop OWNER shop"
sudo -u postgres pg_restore -d shop --no-owner --role=shop /tmp/move/shop.dump
useradd --system --home-dir /opt/shop-api --shell /usr/sbin/nologin shop
chown root:shop /etc/shop-api.env
systemctl daemon-reload
systemctl enable --now shop-api
rm -f /etc/nginx/sites-enabled/default
ln -sf /etc/nginx/sites-available/shop /etc/nginx/sites-enabled/shop
nginx -t -q
systemctl reload nginx
rm -rf /tmp/move
