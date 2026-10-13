#!/bin/bash
# The old server (a second lab VM, set up by old-server.ps1): the shop as it runs today. nginx
# serves the site from /var/www/shop and passes /api/ to shop-api (Python, port 8000), which
# reads the products from PostgreSQL; the database also holds the order history.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq nginx postgresql python3-psycopg2 >/dev/null

sudo -u postgres psql -v ON_ERROR_STOP=1 -q -c "CREATE ROLE shop LOGIN PASSWORD 'Shop-DB-$TOKEN'" -c "CREATE DATABASE shop OWNER shop"
PGPASSWORD="Shop-DB-$TOKEN" psql -h 127.0.0.1 -U shop -d shop -v ON_ERROR_STOP=1 -q <<SQL
CREATE TABLE products (id serial PRIMARY KEY, name text NOT NULL, price_cents int NOT NULL);
INSERT INTO products (name, price_cents) SELECT 'Item ' || g || ' $TOKEN', 100 * g FROM generate_series(1, 250) g;
CREATE TABLE orders (id serial PRIMARY KEY, product_id int NOT NULL REFERENCES products, qty int NOT NULL, created timestamptz NOT NULL DEFAULT now());
INSERT INTO orders (product_id, qty) SELECT 1 + g % 250, 1 + g % 5 FROM generate_series(1, 1000) g;
SQL

useradd --system --home-dir /opt/shop-api --shell /usr/sbin/nologin shop
install -d -m 755 /opt/shop-api
install -m 644 files/app.py /opt/shop-api/app.py
printf 'DB_HOST=127.0.0.1\nDB_NAME=shop\nDB_USER=shop\nDB_PASSWORD=Shop-DB-%s\n' "$TOKEN" > /etc/shop-api.env
chown root:shop /etc/shop-api.env
chmod 640 /etc/shop-api.env
cat > /etc/systemd/system/shop-api.service <<'UNIT'
[Unit]
Description=Shop API
After=network.target postgresql.service

[Service]
User=shop
EnvironmentFile=/etc/shop-api.env
ExecStart=/usr/bin/python3 /opt/shop-api/app.py
Restart=on-failure

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable --now shop-api

# The site: a page, a stylesheet and two images made from the token (check.sh makes them again).
install -d /var/www/shop/css /var/www/shop/img
cat > /var/www/shop/index.html <<HTML
<!doctype html>
<html><head><meta charset="utf-8"><title>Lab Shop</title><link rel="stylesheet" href="css/shop.css"></head>
<body><h1>Lab Shop $TOKEN</h1><img src="img/logo.png" alt=""><img src="img/banner.jpg" alt="">
<div id="products"></div>
<script>fetch("/api/products").then(r => r.json()).then(p => document.getElementById("products").textContent = p.length + " products")</script>
</body></html>
HTML
echo 'body { font-family: sans-serif; }' > /var/www/shop/css/shop.css
python3 - "$TOKEN" <<'PY'
import random, sys
for name, size in (("logo.png", 120000), ("banner.jpg", 300000)):
    data = bytearray(random.Random(sys.argv[1] + name).randbytes(size))
    data[0:4] = b"\x89P\x00\n"  # a NUL and a newline early on, as in real images
    open("/var/www/shop/img/" + name, "wb").write(data)
PY
cat > /etc/nginx/sites-available/shop <<'SITE'
server {
    listen 80 default_server;
    listen [::]:80 default_server;
    root /var/www/shop;
    index index.html;
    location /api/ {
        proxy_pass http://127.0.0.1:8000;
    }
}
SITE
rm -f /etc/nginx/sites-enabled/default
ln -sf /etc/nginx/sites-available/shop /etc/nginx/sites-enabled/shop
nginx -t -q
systemctl reload nginx
for i in $(seq 1 20); do curl -fsS http://127.0.0.1/api/products >/dev/null && break; sleep 1; done
curl -fsS http://127.0.0.1/api/products | python3 -c "import json, sys; assert len(json.load(sys.stdin)) == 250"
