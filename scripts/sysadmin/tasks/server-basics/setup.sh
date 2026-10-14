#!/bin/bash
# On this server sudo asks admin for a password, as on many company servers.
set -euo pipefail
echo "admin:$LAB_SUDO_PASSWORD" | chpasswd
echo 'admin ALL=(ALL) ALL' > /etc/sudoers.d/90-cloud-init-users
chmod 440 /etc/sudoers.d/90-cloud-init-users
visudo -c >/dev/null
