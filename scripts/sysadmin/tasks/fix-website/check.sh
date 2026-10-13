#!/bin/bash
# Runs as root on the lab VM after the reboot; prints PASS/FAIL lines.
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; }
systemctl is-active --quiet nginx && pass "nginx is running after a reboot" || fail "nginx is running after a reboot"
nginx -t >/dev/null 2>&1 && pass "nginx's configuration is valid" || fail "nginx's configuration is valid"
[ -e /etc/nginx/sites-enabled/shop ] && pass "the shop site is still the enabled site" || fail "the shop site is still the enabled site"
grep -q "build $TOKEN" /var/www/shop/index.html 2>/dev/null && pass "the shop's page is unchanged" || fail "the shop's page is unchanged"
[ -s /var/www/shop/img/beans.jpg ] && pass "the shop's images are still there" || fail "the shop's images are still there"
workers=$(ps -o user= -C nginx | sort -u | tr '\n' ' ')
echo "INFO nginx runs as: $workers"
ps -o user=,args= -C nginx | grep 'worker process' | grep -qv '^root' && pass "nginx workers don't run as root" || fail "nginx workers don't run as root"
