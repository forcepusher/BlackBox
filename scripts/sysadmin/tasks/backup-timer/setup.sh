#!/bin/bash
# The app's data: a config, some uploads, an orders export.
set -euo pipefail
install -d /srv/app/data/config /srv/app/data/uploads
printf '[app]\nname = lab-shop\ntoken = %s\n' "$TOKEN" > /srv/app/data/config/app.ini
for i in $(seq 1 20); do head -c 40000 /dev/urandom > "/srv/app/data/uploads/photo-$i.jpg"; done
printf 'id,total\n1,12.50\n2,8.00\n' > /srv/app/data/orders.csv
