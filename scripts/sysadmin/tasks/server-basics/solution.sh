#!/bin/bash
# Reference solution, run as root by sysadmin-test.ps1 -SelfTest to prove the checks pass.
set -euo pipefail
hostnamectl set-hostname web01
sed -i 's/^127\.0\.1\.1\s.*/127.0.1.1 web01/' /etc/hosts
grep -q web01 /etc/hosts || echo '127.0.1.1 web01' >> /etc/hosts
timedatectl set-timezone Europe/Berlin
fallocate -l 2G /swapfile
chmod 600 /swapfile
mkswap /swapfile
swapon /swapfile
echo '/swapfile none swap sw 0 0' >> /etc/fstab
echo 'vm.swappiness = 10' > /etc/sysctl.d/90-swappiness.conf
sysctl -p /etc/sysctl.d/90-swappiness.conf
