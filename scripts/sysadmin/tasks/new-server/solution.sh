#!/bin/bash
# Reference solution, run as root by sysadmin-test.ps1 -SelfTest to prove the checks pass.
set -euo pipefail
id deploy >/dev/null 2>&1 || useradd -m -s /bin/bash deploy
install -d -m 700 -o deploy -g deploy /home/deploy/.ssh
install -m 600 -o deploy -g deploy upload/ops.pub /home/deploy/.ssh/authorized_keys
echo 'deploy ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/90-deploy
chmod 440 /etc/sudoers.d/90-deploy
visudo -cq
rm -f /etc/ssh/sshd_config.d/50-cloud-init.conf
sed -i 's/^PermitRootLogin yes/PermitRootLogin no/' /etc/ssh/sshd_config
printf 'PermitRootLogin no\nPasswordAuthentication no\nKbdInteractiveAuthentication no\n' > /etc/ssh/sshd_config.d/01-lockdown.conf
sshd -t
systemctl restart ssh
