#!/bin/bash
# The server as some providers hand it over: root logs in with a password, PermitRootLogin yes in
# sshd_config and PasswordAuthentication yes in cloud-init's drop-in, which sshd reads before the
# image's own "PasswordAuthentication no" further down in sshd_config, so it wins.
set -euo pipefail # (grep without -q below: -q would end the pipe early and pipefail would see SIGPIPE)
echo "root:$LAB_ROOT_PASSWORD" | chpasswd
sed -i 's/^#\?PermitRootLogin .*/PermitRootLogin yes/' /etc/ssh/sshd_config
printf 'PasswordAuthentication yes\n' > /etc/ssh/sshd_config.d/50-cloud-init.conf
systemctl restart ssh
sshd -T -C user=root,host=pc,addr=10.0.2.2 | grep -x 'permitrootlogin yes' >/dev/null
sshd -T -C user=root,host=pc,addr=10.0.2.2 | grep -x 'passwordauthentication yes' >/dev/null
