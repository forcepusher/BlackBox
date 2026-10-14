#!/bin/bash
# Runs as root on the lab VM after the reboot; prints PASS/FAIL lines. Waits for cron itself to run
# the job (it runs every 2 minutes): a new line in today's report is the proof.
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; }
f=/var/reports/sales-$(date +%F).txt
before=$(cat /var/reports/sales-*.txt 2>/dev/null | wc -l)
grep -q $'\r' /usr/local/bin/sales-report && fail "the script has Unix line endings" || pass "the script has Unix line endings"
mode=$(stat -c %a /etc/cron.d/sales-report 2>/dev/null)
echo "INFO /etc/cron.d: $(ls -l /etc/cron.d | grep -v '^total' | awk '{print $1, $3, $NF}' | tr '\n' ';')"
got=0
for i in $(seq 1 30); do
  sleep 5
  now=$(cat /var/reports/sales-*.txt 2>/dev/null | wc -l)
  if [ "$now" -gt "$before" ]; then got=1; break; fi
done
[ $got = 1 ] && pass "cron runs the job on its own (a new report line within $((i * 5)) s)" || fail "cron runs the job on its own (no new report line in 150 s; cron.d mode $mode)"
tail -1 "$f" 2>/dev/null | grep -q "($TOKEN)" && pass "the line comes from the sales job" || fail "the line comes from the sales job"
owner=$(stat -c %U "$f" 2>/dev/null)
[ "$owner" = reports ] && pass "it runs as the user reports" || fail "it runs as the user reports (the report belongs to $owner)"
