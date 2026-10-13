#!/bin/bash
# Runs as root on the lab VM after the reboot; prints PASS/FAIL lines. The connections go to
# the server's own network address, so they arrive from 10.0.2.15, inside the app network.
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; }
if ! command -v psql >/dev/null; then fail "PostgreSQL is installed"; exit 0; fi
for i in $(seq 1 20); do ss -ltnH 'sport = :5432' | grep -q . && break; sleep 1; done
ss -ltnH 'sport = :5432' | grep -q . && pass "PostgreSQL runs after a reboot" || fail "PostgreSQL runs after a reboot"
ss -ltnH 'sport = :5432' | grep -qE "(\*|0\.0\.0\.0|$LAB_GUEST_IP):5432" && pass "it listens on the network" || fail "it listens on the network ($(ss -ltnH 'sport = :5432' | awk '{print $4}' | tr '\n' ' '))"
out=$(PGPASSWORD='Sh0p-Lab-2026' PGCONNECT_TIMEOUT=5 psql "host=$LAB_GUEST_IP user=shop dbname=shop" -tAc "select current_user || ',' || current_database()" 2>&1)
[ "$out" = "shop,shop" ] && pass "shop logs in to shop over the network with the password" || fail "shop logs in to shop over the network with the password ($out)"
out=$(PGPASSWORD='wrong' PGCONNECT_TIMEOUT=5 psql "host=$LAB_GUEST_IP user=shop dbname=shop" -tAc "select 1" 2>&1)
[ "$out" != "1" ] && pass "a wrong password is refused" || fail "a wrong password is refused"
out=$(PGPASSWORD='' PGCONNECT_TIMEOUT=5 psql -w "host=$LAB_GUEST_IP user=postgres dbname=postgres" -tAc "select 1" 2>&1)
[ "$out" != "1" ] && pass "postgres can't be logged in to over the network without a password" || fail "postgres can't be logged in to over the network without a password"
owner=$(sudo -u postgres psql -tAc "select pg_get_userbyid(datdba) from pg_database where datname = 'shop'" 2>/dev/null)
[ "$owner" = shop ] && pass "the database shop belongs to the user shop" || fail "the database shop belongs to the user shop (owner: $owner)"
hba=$(sudo -u postgres psql -tAc "show hba_file" 2>/dev/null)
rules=$(grep -vE '^\s*(#|$)' "$hba" 2>/dev/null)
echo "INFO pg_hba: $(echo "$rules" | tr -s ' ' | tr '\n' ';')"
wide=$(echo "$rules" | awk '$1 ~ /^host/ && ($4 == "all" || $4 == "0.0.0.0/0" || $4 == "::/0") && $5 != "reject"')
[ -z "$wide" ] && pass "pg_hba.conf lets no one in from everywhere" || fail "pg_hba.conf lets no one in from everywhere ($wide)"
trust=$(echo "$rules" | awk '$1 ~ /^host/ && $NF == "trust"')
[ -z "$trust" ] && pass "no network access without a password (no trust rules)" || fail "no network access without a password ($trust)"
