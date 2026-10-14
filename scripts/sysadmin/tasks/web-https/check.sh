#!/bin/bash
# Runs as root on the lab VM after the reboot; prints PASS/FAIL lines.
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; }
systemctl is-active --quiet nginx && pass "nginx is running after a reboot" || fail "nginx is running after a reboot"
grep -rqs "release $TOKEN" /var/www/site && pass "the site's files are under /var/www/site" || fail "the site's files are under /var/www/site"
name=studio.lab.test
cert=$(echo | timeout 10 openssl s_client -connect 127.0.0.1:443 -servername $name 2>/dev/null | openssl x509 2>/dev/null)
if [ -z "$cert" ]; then
  fail "a certificate is served on port 443"
  exit 0
fi
pass "a certificate is served on port 443"
info=$(echo "$cert" | openssl x509 -noout -subject -ext subjectAltName -enddate 2>/dev/null | tr '\n' ' ')
echo "INFO $info"
echo "$info" | grep -q "$name" && pass "the certificate is for $name" || fail "the certificate is for $name"
echo "$cert" | openssl x509 -noout -checkend $((360 * 86400)) >/dev/null && pass "the certificate is valid for about a year" || fail "the certificate is valid for about a year"
