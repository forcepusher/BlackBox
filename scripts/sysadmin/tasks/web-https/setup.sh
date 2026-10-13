#!/bin/bash
# The website as the designers delivered it.
set -euo pipefail
d=$(mktemp -d)
mkdir -p "$d/site/css" "$d/site/img"
cat > "$d/site/index.html" <<EOF
<!doctype html>
<html><head><meta charset="utf-8"><title>Lab Studio</title><link rel="stylesheet" href="css/main.css"></head>
<body><h1>Lab Studio</h1><p>We design things.</p><img src="img/logo.png" alt="logo"><footer>release $TOKEN</footer></body></html>
EOF
echo 'h1 { color: #333; }' > "$d/site/css/main.css"
head -c 8000 /dev/urandom > "$d/site/img/logo.png"
tar -czf /home/admin/site.tar.gz -C "$d" site
chown admin:admin /home/admin/site.tar.gz
rm -rf "$d"
