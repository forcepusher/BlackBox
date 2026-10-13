#!/bin/bash
# Runs as root on the lab VM after the reboot (and after check-host.ps1 stored an order); prints PASS/FAIL lines.
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; }
systemctl is-active --quiet orders-api && pass "orders-api runs after a reboot" || fail "orders-api runs after a reboot"
user=$(ps -o user= -C python3 -o args= | awk '/orders-api\/app.py/ {print $1}' | head -1)
[ -n "$user" ] && [ "$user" != root ] && pass "it still runs as its own user ($user), not root" || fail "it still runs as its own user, not root ($user)"
d=/var/lib/orders-api; f=$d/orders.json
echo "INFO $(stat -c '%U:%G %a %n' $d $f | tr '\n' ' ')"
grep -q "A-$TOKEN-1" $f && grep -q "A-$TOKEN-2" $f && pass "the old orders are kept" || fail "the old orders are kept"
bad=$(find $d -perm -o+w | head -3)
[ -z "$bad" ] && pass "nothing in $d is writable by everyone" || fail "nothing in $d is writable by everyone ($bad)"
