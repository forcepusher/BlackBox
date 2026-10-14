#!/bin/bash
# Runs as root on the lab VM after the reboot; prints PASS/FAIL lines.
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; }
dir=/var/backups/app
today=$(date +%F); yesterday=$(date -d yesterday +%F)
first=$(ls -1 $dir/app-$today.tar.gz $dir/app-$yesterday.tar.gz 2>/dev/null | head -1)
if [ -n "$first" ]; then
  pass "the backup was run once ($(basename "$first"))"
  if tar -xzOf "$first" --wildcards '*config/app.ini' 2>/dev/null | grep -q "$TOKEN" && tar -tzf "$first" | grep -q 'photo-20.jpg'; then
    pass "the archive holds /srv/app/data"
  else
    fail "the archive holds /srv/app/data"
  fi
else
  fail "the backup was run once (no $dir/app-$today.tar.gz; found: $(ls $dir 2>/dev/null | tr '\n' ' '))"
fi

# Find the job: a systemd timer at 02:30 every day, or a cron line "30 2 * * *".
set -f # calendar expressions are full of *
job=""; how=""
for t in $(systemctl list-timers --all --no-legend 2>/dev/null | grep -oE '[^ ]+\.timer'); do
  while IFS= read -r cal; do
    [ -z "$cal" ] && continue
    norm=$(systemd-analyze calendar "$cal" 2>/dev/null | sed -n 's/^ *Normalized form: //p')
    if [ "$norm" = "*-*-* 02:30:00" ]; then
      job="systemctl start --wait $(systemctl show -p Unit --value "$t")"; how="systemd timer $t"
    fi
  done < <(systemctl show -p TimersCalendar --value "$t" | grep -oE 'OnCalendar=[^;]+' | cut -d= -f2- | sed 's/ *$//')
done
set +f
if [ -z "$job" ]; then
  line=$( { cat /etc/crontab /etc/cron.d/* 2>/dev/null | sed -E 's/^\s*30\s+0?2\s+\*\s+\*\s+\*\s+\S+\s+/CRON /';
            for u in root admin; do crontab -l -u $u 2>/dev/null | sed -E 's/^\s*30\s+0?2\s+\*\s+\*\s+\*\s+/CRON /'; done; } | grep '^CRON ' | head -1)
  if [ -n "$line" ]; then job="${line#CRON }"; job="${job//\\%/%}"; how="cron: $job"; fi
fi
if [ -z "$job" ]; then
  fail "it is scheduled for 02:30 every night"
  exit 0
fi
pass "it is scheduled for 02:30 every night ($how)"
# A schedule nothing reads doesn't run: the timer must be active after the reboot, or cron must run
# (Debian's cloud image has no cron).
case "$how" in
  "systemd timer "*) what="the timer is active after the reboot"; systemctl is-active --quiet "${how#systemd timer }" && ok=1 || ok=0;;
  *) what="cron, which reads that line, runs after the reboot"; { systemctl is-active --quiet cron || systemctl is-active --quiet crond; } && ok=1 || ok=0;;
esac
[ $ok = 1 ] && pass "$what" || fail "$what"

# Retention: nine older archives, one per day before today; after one more run, 7 must be left.
for d in $(seq 1 9); do
  f=$dir/app-$(date -d "$d days ago" +%F).tar.gz
  [ -e "$f" ] || tar -czf "$f" -C /srv/app/data config
  touch -d "$(date -d "$d days ago" +%F) 02:30" "$f"
done
rm -f $dir/app-$today.tar.gz
echo "INFO running: $job"
timeout 300 bash -c "$job" >/tmp/backup-run.txt 2>&1 || echo "INFO the job exited with an error: $(tail -c 300 /tmp/backup-run.txt | tr '\n' ' ')"
left=$(ls -1 $dir/app-*.tar.gz 2>/dev/null | wc -l)
newest=$(ls -1 $dir/app-*.tar.gz 2>/dev/null | sort | tail -1)
[ -e "$dir/app-$today.tar.gz" ] && pass "the scheduled job makes today's archive" || fail "the scheduled job makes today's archive"
[ "$left" -eq 7 ] && pass "only the 7 newest archives are kept" || fail "only the 7 newest archives are kept ($left left: $(ls $dir | tr '\n' ' '))"
