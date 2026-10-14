#!/bin/bash
# Runs as root on the lab VM after the reboot; prints PASS/FAIL lines.
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; }
systemctl is-active --quiet nginx && pass "nginx is running after a reboot" || fail "nginx is running after a reboot"
bad=$(find /var/www/html -type f ! -perm -o=r | head -3)
[ -z "$bad" ] && pass "the site's files are readable by the web server" || fail "the site's files are readable by the web server ($bad)"
