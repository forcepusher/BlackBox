#!/bin/bash
# Reference solution, run as root by sysadmin-test.ps1 -SelfTest to prove the checks pass.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update -q
apt-get install -y -q postgresql
conf=$(ls -d /etc/postgresql/*/main | head -1)
echo "listen_addresses = '*'" > "$conf/conf.d/listen.conf"
echo "host shop shop 10.0.2.0/24 scram-sha-256" >> "$conf/pg_hba.conf"
sudo -u postgres psql -c "create user shop with password 'Sh0p-Lab-2026'"
sudo -u postgres psql -c "create database shop owner shop"
systemctl enable postgresql
systemctl restart postgresql
