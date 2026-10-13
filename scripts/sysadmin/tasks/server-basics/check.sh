#!/bin/bash
# Runs as root on the lab VM after the reboot; prints PASS/FAIL lines.
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; }
h=$(hostname)
[ "$h" = web01 ] && [ "$(cat /etc/hostname)" = web01 ] && pass "the hostname is web01 after a reboot" || fail "the hostname is web01 after a reboot ($h, /etc/hostname: $(cat /etc/hostname))"
getent hosts web01 >/dev/null && pass "web01 resolves (no 'unable to resolve host' from sudo)" || fail "web01 resolves (no 'unable to resolve host' from sudo)"
tz=$(timedatectl show -p Timezone --value)
[ "$tz" = Europe/Berlin ] && pass "the time zone is Europe/Berlin" || fail "the time zone is Europe/Berlin ($tz)"
swap=$(swapon --show=SIZE --bytes --noheadings | awk '{s += $1} END {print s + 0}')
[ "$swap" -ge $((1900 * 1024 * 1024)) ] && pass "about 2 GB of swap is on after a reboot ($((swap / 1048576)) MB)" || fail "about 2 GB of swap is on after a reboot ($((swap / 1048576)) MB)"
swapon --show=NAME,TYPE --noheadings | grep -q file && pass "the swap is a file" || fail "the swap is a file ($(swapon --show=NAME,TYPE --noheadings | tr '\n' ' '))"
sw=$(cat /proc/sys/vm/swappiness)
[ "$sw" = 10 ] && pass "vm.swappiness is 10 after a reboot" || fail "vm.swappiness is 10 after a reboot ($sw)"
grep -qs 'admin ALL=(ALL) ALL' /etc/sudoers.d/90-cloud-init-users && pass "sudo still asks admin for the password (not loosened)" || fail "sudo still asks admin for the password (not loosened)"
