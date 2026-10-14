#!/bin/bash
# Runs as root on the lab VM after the reboot; prints PASS/FAIL lines.
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; }
use=$(df --output=pcent / | tail -1 | tr -dc 0-9)
[ "$use" -lt 70 ] && pass "the disk has room again ($use% used)" || fail "the disk has room again ($use% used)"
b=/srv/backups/shop-db-2026-10-12.sql.gz
[ -f $b ] && [ "$(stat -c %s $b)" -ge $((2 * 1024 * 1024 * 1024)) ] && head -c 64 $b | grep -q "BACKUP $TOKEN" &&
  pass "the database backup is untouched" || fail "the database backup is untouched"
q=/var/lib/reportd/queue.db
[ -f $q ] && [ "$(stat -c %s $q)" -ge $((1536 * 1024 * 1024)) ] && head -c 64 $q | grep -q "QUEUE $TOKEN" &&
  pass "reportd's queue (not a log) is untouched" || fail "reportd's queue (not a log) is untouched"
systemctl is-active --quiet reportd && pass "reportd runs after a reboot" || fail "reportd runs after a reboot"
sleep 3
log=/var/log/reportd/reportd.log
[ -f $log ] && [ $(( $(date +%s) - $(stat -c %Y $log) )) -lt 10 ] && pass "reportd still writes its log" || fail "reportd still writes its log"

# Rotation: some logrotate rule must cover the log, and work. Forcing it on a log made 300 MB
# bigger has to rotate it, and the rotated copies have to stay bounded.
conf=$(grep -lsE '/var/log/reportd' /etc/logrotate.conf /etc/logrotate.d/* | head -1)
if [ -z "$conf" ]; then
  fail "a logrotate rule covers /var/log/reportd"
  exit 0
fi
pass "a logrotate rule covers /var/log/reportd ($conf)"
echo "INFO $(tr '\n' ' ' < "$conf" | tr -s ' ')"
grep -qE '^\s*(size|maxsize)\s' "$conf" && echo "INFO the rule limits the log's size" || echo "INFO the rule has no size or maxsize limit"
grep -qE '^\s*rotate\s+[0-9]+' "$conf" && pass "the rule keeps a bounded number of old logs" || fail "the rule keeps a bounded number of old logs"
truncate -s +300M $log
before=$(ls /var/log/reportd | wc -l)
if logrotate -f "$conf" 2>/tmp/lr.err; then
  size=$(stat -c %s $log 2>/dev/null || echo 0)
  [ "$size" -lt $((100 * 1024 * 1024)) ] && pass "logrotate rotates the log" || fail "logrotate rotates the log (still $size bytes)"
else
  fail "logrotate rotates the log ($(head -c 300 /tmp/lr.err | tr '\n' ' '))"
fi
total=$(du -sm /var/log/reportd | cut -f1)
echo "INFO /var/log/reportd now holds $(ls /var/log/reportd | wc -l) files, $total MB (before forcing: $before files)"
