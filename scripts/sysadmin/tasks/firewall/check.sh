#!/bin/bash
# Runs as root on the lab VM after the reboot; prints PASS/FAIL lines.
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; }
for s in nginx metrics cache; do
  systemctl is-active --quiet "$s" && pass "$s still runs (blocked by the firewall, not stopped)" || fail "$s still runs (blocked by the firewall, not stopped)"
done
rules=$(nft list ruleset 2>/dev/null)
echo "INFO firewall: $(ufw status 2>/dev/null | head -1); $(echo "$rules" | grep -c .) nft lines"
m=$(echo "$LAB_HOST_SEEN_AS" | sed 's/\./\\./g')
r9100=$(echo "$rules" | grep -E 'dport (\{[^}]*\b)?9100\b')
if [ -z "$r9100" ]; then
  fail "port 9100 is open only to $LAB_HOST_SEEN_AS (no rule mentions it)"
elif echo "$r9100" | grep -E '\b(accept|jump|goto)\b' | grep -vqE "$m\b"; then
  fail "port 9100 is open only to $LAB_HOST_SEEN_AS (a rule accepts it from other addresses: $(echo "$r9100" | grep -vE "$m\b" | head -1 | xargs))"
else
  pass "port 9100 is open only to $LAB_HOST_SEEN_AS"
fi
