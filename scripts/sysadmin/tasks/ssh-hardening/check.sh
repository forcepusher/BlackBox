#!/bin/bash
# Runs as root on the lab VM after the reboot; prints PASS/FAIL lines.
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; }
cfg=$(sshd -T 2>/dev/null)
echo "$cfg" | grep -qx 'passwordauthentication no' && pass "sshd: PasswordAuthentication no" || fail "sshd: PasswordAuthentication no ($(echo "$cfg" | grep ^passwordauthentication))"
echo "$cfg" | grep -qx 'kbdinteractiveauthentication no' && pass "sshd: no keyboard-interactive passwords either" || fail "sshd: no keyboard-interactive passwords either"
echo "$cfg" | grep -qx 'permitrootlogin no' && pass "sshd: PermitRootLogin no" || fail "sshd: PermitRootLogin no ($(echo "$cfg" | grep ^permitrootlogin))"
for u in alice bob; do
  id -nG "$u" 2>/dev/null | tr ' ' '\n' | grep -qx devs && pass "$u is in the group devs" || fail "$u is in the group devs"
done
grep -rqsE '^\s*%devs\s+ALL\s*=\s*\(ALL(:ALL)?\)\s*NOPASSWD:\s*ALL' /etc/sudoers /etc/sudoers.d && pass "sudoers: %devs may sudo without a password" || echo "INFO no literal %devs NOPASSWD rule found (the login test decides)"
st=$(passwd -S carol 2>/dev/null | awk '{print $2}')
exp=$(chage -l carol 2>/dev/null | grep -i 'account expires' | cut -d: -f2 | xargs)
shell=$(getent passwd carol | cut -d: -f7)
echo "INFO carol: password status $st, account expires $exp, shell $shell"
if [ "$st" = L ] || [[ "$shell" =~ (nologin|false)$ ]] ||
   { [ -n "$exp" ] && [ "$exp" != never ] && [ "$(date -d "$exp" +%s 2>/dev/null || echo 9999999999)" -le "$(date +%s)" ]; }; then
  pass "carol's account can't be logged in to with her password"
else
  fail "carol's account can't be logged in to with her password (status $st, expires $exp, shell $shell)"
fi
[ -f /home/carol/notes.txt ] && grep -q "$TOKEN" /home/carol/notes.txt && pass "carol's files are kept" || fail "carol's files are kept"
