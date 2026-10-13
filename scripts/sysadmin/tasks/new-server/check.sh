#!/bin/bash
# Runs as root on the lab VM after the reboot; prints PASS/FAIL lines.
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; }
r=$(sshd -T -C "user=root,host=pc,addr=$LAB_HOST_SEEN_AS" 2>/dev/null | awk '$1 == "permitrootlogin" {print $2}')
[ "$r" = no ] && pass "sshd lets root in by no means (PermitRootLogin no)" || fail "sshd lets root in by no means (PermitRootLogin $r)"
echo "INFO sshd_config.d: $(ls /etc/ssh/sshd_config.d | tr '\n' ' ')"
