#!/bin/bash
# nginx with the old site in /var/www/html.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update -q
apt-get install -y -q nginx
rm -f /var/www/html/index.nginx-debian.html
echo '<!doctype html><html><body><h1>Lab Bakery</h1><p>version 1</p></body></html>' > /var/www/html/index.html
