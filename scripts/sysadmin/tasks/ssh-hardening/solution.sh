#!/bin/bash
# Reference solution, run as root by sysadmin-test.ps1 -SelfTest to prove the checks pass.
set -euo pipefail
groupadd devs
for u in alice bob; do
  useradd -m -s /bin/bash -G devs "$u"
  install -d -m 700 -o "$u" -g "$u" "/home/$u/.ssh"
  install -m 600 -o "$u" -g "$u" "/home/admin/keys/$u.pub" "/home/$u/.ssh/authorized_keys"
done
echo '%devs ALL=(ALL:ALL) NOPASSWD: ALL' > /etc/sudoers.d/devs
chmod 440 /etc/sudoers.d/devs
visudo -cf /etc/sudoers.d/devs
usermod -L -e 1 carol
gpasswd -d carol sudo
mv /home/carol/.ssh/authorized_keys /home/carol/.ssh/authorized_keys.disabled
cat > /etc/ssh/sshd_config.d/10-lockdown.conf <<'EOF'
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no
EOF
sed -i 's/^PasswordAuthentication .*/PasswordAuthentication no/' /etc/ssh/sshd_config.d/60-cloudimg-settings.conf
sed -i 's/^PermitRootLogin .*/PermitRootLogin no/' /etc/ssh/sshd_config
sshd -t
systemctl restart ssh
