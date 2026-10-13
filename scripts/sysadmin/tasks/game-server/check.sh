#!/bin/bash
# Runs as root on the lab VM after the reboot; prints PASS/FAIL lines.
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; }

# The game server: whatever node process listens on TCP (systemd, pm2 or a container alike).
listeners() { ss -ltnpH | grep -E 'users:\(\("node(js)?"' ; }
lines=$(listeners)
if [ -z "$lines" ]; then
  fail "the game server is running after a reboot (no node process listens)"
  exit 0
fi
pass "the game server is running after a reboot"
ports=$(echo "$lines" | awk '{print $4}' | sed 's/.*://' | sort -u | tr '\n' ' ')
pids=$(echo "$lines" | grep -oE 'pid=[0-9]+' | cut -d= -f2 | sort -u)
echo "INFO node listens on: $(echo "$lines" | awk '{print $4}' | sort -u | tr '\n' ' ')"

root=0
for pid in $pids; do
  user=$(ps -o user= -p "$pid" | tr -d ' ')
  echo "INFO pid $pid runs as $user: $(tr '\0' ' ' < /proc/$pid/cmdline)"
  [ "$user" = root ] && root=1
  ver=$(/proc/$pid/exe --version 2>/dev/null)
  echo "INFO node version $ver"
done
[ $root = 0 ] && pass "it runs as an unprivileged user" || fail "it runs as an unprivileged user (runs as root)"

major=$(/proc/$(echo $pids | awk '{print $1}')/exe --version 2>/dev/null | sed 's/^v//; s/\..*//')
[ "${major:-0}" -ge 20 ] && pass "it runs on Node.js 20 or newer" || fail "it runs on Node.js 20 or newer (v$major)"

if ss -ltnpH 'sport = :80' | grep -q nginx; then pass "nginx serves port 80 after a reboot"; else fail "nginx serves port 80 after a reboot"; fi

# Crash test: kill it hard and wait for something to bring it back on the same port.
kill -9 $pids
back=0
for i in $(seq 1 30); do
  sleep 1
  now=$(listeners)
  if [ -n "$now" ]; then
    newpids=$(echo "$now" | grep -oE 'pid=[0-9]+' | cut -d= -f2 | sort -u)
    if [ "$newpids" != "$pids" ]; then back=1; break; fi
  fi
done
[ $back = 1 ] && pass "it comes back by itself after a crash (in ${i}s)" || fail "it comes back by itself after a crash (not within 30s of kill -9)"
echo "PORTS $ports"
