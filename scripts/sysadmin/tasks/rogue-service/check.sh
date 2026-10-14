#!/bin/bash
# Runs as root on the lab VM after the reboot; prints PASS/FAIL lines. The cron copy starts 20 s
# after boot, so the checks wait until the server has been up for a minute.
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; }
up=$(cut -d. -f1 /proc/uptime)
[ "$up" -lt 60 ] && sleep $((60 - up))
ss -ltnH 'sport = :31337' | grep -q . && fail "nothing listens on port 31337 a minute after a reboot" || pass "nothing listens on port 31337 a minute after a reboot"
pgrep -f 'netlogd/netlogd' >/dev/null && fail "no netlogd process runs" || pass "no netlogd process runs"
systemctl is-enabled --quiet systemd-netlogd 2>/dev/null && fail "the systemd-netlogd service is no longer enabled" || pass "the systemd-netlogd service is no longer enabled"
crontab -l -u root 2>/dev/null | grep -v '^\s*#' | grep -q netlogd && fail "root's crontab no longer starts it" || pass "root's crontab no longer starts it"
