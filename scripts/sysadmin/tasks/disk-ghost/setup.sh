#!/bin/bash
# metricsd keeps its log open and appends to it. Its log grew huge, and someone "cleaned it up"
# with rm while metricsd ran: the name is gone, the space is not (the open file stays until
# metricsd lets go of it), so df says the disk is full and du finds nothing. Nothing rotates the
# log either. fallocate makes the size at once, without writing the data.
set -euo pipefail
useradd --system --no-create-home --shell /usr/sbin/nologin metricsd
install -d -o metricsd -g metricsd /var/log/metricsd
cat > /usr/local/bin/metricsd <<'EOF'
#!/bin/bash
# Metrics collector: samples load and free memory every second into its log, kept open.
exec >>/var/log/metricsd/metrics.log
while true; do
  echo "$(date -Is) load=$(cut -d' ' -f1 /proc/loadavg) mem_available_kb=$(awk '/MemAvailable/ {print $2}' /proc/meminfo)"
  sleep 1
done
EOF
chmod 755 /usr/local/bin/metricsd
cat > /etc/systemd/system/metricsd.service <<'EOF'
[Unit]
Description=Metrics collector
After=network.target

[Service]
User=metricsd
ExecStart=/usr/local/bin/metricsd
Restart=always

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable --now metricsd
sleep 2
avail=$(df --output=avail -B1 / | tail -1)
fallocate -l $((avail - 400 * 1024 * 1024)) /var/log/metricsd/metrics.log
rm /var/log/metricsd/metrics.log
sleep 2
used=$(df --output=pcent / | tail -1 | tr -dc 0-9)
[ "$used" -ge 90 ]
