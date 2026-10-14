#!/bin/bash
# Runs as root on the lab VM after the reboot; prints PASS/FAIL lines. The reboot itself lets go
# of a deleted file, so the space checks are guards; the real test is a forced log rotation:
# afterwards metricsd must not hold a deleted file and must still write to its log.
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; }
log=/var/log/metricsd/metrics.log
pid() { systemctl show -p MainPID --value metricsd; }
grows() { local a b; a=$(stat -c %s "$log" 2>/dev/null || echo 0); sleep 4; b=$(stat -c %s "$log" 2>/dev/null || echo 0); [ "$b" -gt "$a" ]; }
used=$(df --output=pcent / | tail -1 | tr -dc 0-9)
[ "$used" -lt 60 ] && pass "the disk has room ($used% used)" || fail "the disk has room ($used% used)"
systemctl is-active --quiet metricsd && grows && pass "metricsd runs after a reboot and writes its log" || fail "metricsd runs after a reboot and writes its log"
logrotate -d /etc/logrotate.conf 2>&1 | grep -q "$log" && pass "a logrotate rule covers $log" || fail "a logrotate rule covers $log"
logrotate -f /etc/logrotate.conf >/dev/null 2>&1
sleep 2
held=$(ls -l /proc/"$(pid)"/fd 2>/dev/null | grep -c '(deleted)')
[ "$held" = 0 ] && pass "after a forced rotation metricsd holds no deleted file" || fail "after a forced rotation metricsd holds no deleted file ($held)"
grows && pass "after a forced rotation metricsd still writes to $log" || fail "after a forced rotation metricsd still writes to $log"
