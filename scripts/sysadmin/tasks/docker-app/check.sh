#!/bin/bash
# Runs as root on the lab VM after the reboot (and after check-host.ps1 recorded a check through
# the app); prints PASS/FAIL lines.
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; }
systemctl is-enabled --quiet docker 2>/dev/null && systemctl is-active --quiet docker && pass "Docker runs and starts at boot" || fail "Docker runs and starts at boot"
echo "INFO containers: $(docker ps -a --format '{{.Names}} {{.Image}} {{.Status}} {{.Ports}}' 2>/dev/null | tr '\n' ';')"
c=$(docker ps --filter publish=8080 --format '{{.ID}}' 2>/dev/null | head -1)
[ -n "$c" ] || c=$(docker ps --filter network=host --format '{{.ID}}' 2>/dev/null | head -1)
if [ -z "$c" ]; then fail "after the reboot a container serves port 8080"; exit 0; fi
pass "after the reboot a container serves port 8080"
m=$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/data"}}{{.Type}}|{{.Name}}|{{.Source}}{{end}}{{end}}' "$c")
echo "INFO /data: $m"
IFS='|' read -r type name src <<<"$m"
if [ -z "$type" ]; then
  fail "/data is kept outside the container (no volume or bind mount there)"
elif [ "$type" = volume ] && [[ "$name" =~ ^[0-9a-f]{64}$ ]]; then
  fail "/data is kept outside the container (an anonymous volume, which a new container doesn't get)"
else
  pass "/data is kept outside the container ($type ${name:-$src})"
fi
[ -n "$src" ] && grep -q "host-$TOKEN" "$src/checks.log" 2>/dev/null && pass "the check recorded through the app is stored there" || fail "the check recorded through the app is stored there"
