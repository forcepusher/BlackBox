#!/bin/bash
# Reference solution, run as root by sysadmin-test.ps1 -SelfTest to prove the checks pass.
set -euo pipefail
sed -i 's|root /var/www/shop$|root /var/www/shop;|' /etc/nginx/sites-available/shop
chown -R root:root /var/www/shop
find /var/www/shop -type d -exec chmod 755 {} +
find /var/www/shop -type f -exec chmod 644 {} +
nginx -t
systemctl restart nginx
