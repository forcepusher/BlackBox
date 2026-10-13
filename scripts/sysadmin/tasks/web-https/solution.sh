#!/bin/bash
# Reference solution, run as root by sysadmin-test.ps1 -SelfTest to prove the checks pass.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update -q
apt-get install -y -q nginx
mkdir -p /var/www
tar -xzf /home/admin/site.tar.gz -C /var/www
openssl req -x509 -newkey rsa:2048 -nodes -days 365 -subj "/CN=studio.lab.test" -addext "subjectAltName=DNS:studio.lab.test" \
  -keyout /etc/ssl/private/site.key -out /etc/ssl/certs/site.crt
cat > /etc/nginx/sites-available/site <<'EOF'
server {
    listen 80 default_server;
    return 301 https://$host$request_uri;
}
server {
    listen 443 ssl default_server;
    ssl_certificate /etc/ssl/certs/site.crt;
    ssl_certificate_key /etc/ssl/private/site.key;
    root /var/www/site;
    index index.html;
}
EOF
rm -f /etc/nginx/sites-enabled/default
ln -sf /etc/nginx/sites-available/site /etc/nginx/sites-enabled/site
nginx -t
systemctl reload nginx
