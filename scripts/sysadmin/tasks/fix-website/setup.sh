#!/bin/bash
# A working nginx shop site, then last night's two mistakes: a missing semicolon in the site's
# config and a "hardening" that leaves the files readable by root only. nginx failed to restart.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update -q
apt-get install -y -q nginx
mkdir -p /var/www/shop/img
cat > /var/www/shop/index.html <<EOF
<!doctype html>
<html><head><meta charset="utf-8"><title>Lab Shop</title><link rel="stylesheet" href="/style.css"></head>
<body><h1>Lab Shop</h1><p>Fresh coffee beans, roasted every week.</p><p class="build">build $TOKEN</p></body></html>
EOF
echo 'body { font-family: sans-serif; max-width: 40em; margin: auto; }' > /var/www/shop/style.css
head -c 20000 /dev/urandom > /var/www/shop/img/beans.jpg
cat > /etc/nginx/sites-available/shop <<'EOF'
server {
    listen 80 default_server;
    listen [::]:80 default_server;
    server_name shop.lab;

    root /var/www/shop
    index index.html;

    location / {
        try_files $uri $uri/ =404;
    }
}
EOF
rm -f /etc/nginx/sites-enabled/default
ln -s /etc/nginx/sites-available/shop /etc/nginx/sites-enabled/shop
chown -R root:root /var/www/shop
chmod -R go-rwx /var/www/shop
systemctl restart nginx || true
