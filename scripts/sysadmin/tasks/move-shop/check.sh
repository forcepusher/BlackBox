#!/bin/bash
# Runs as root on the new server after the reboot, with the old server switched off; prints PASS/FAIL lines.
# HTTP goes to the address requests from this PC arrive at (QEMU forwards them to the guest IP).
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; }
h="Host: $LAB_IP"
ok=1
for img in logo.png banner.jpg; do
  want=$(python3 - "$TOKEN" "$img" <<'PY'
import hashlib, random, sys
size = 120000 if sys.argv[2] == "logo.png" else 300000
data = bytearray(random.Random(sys.argv[1] + sys.argv[2]).randbytes(size))
data[0:4] = b"\x89P\x00\n"
print(hashlib.sha256(data).hexdigest())
PY
)
  got=$(curl -s -m 10 -H "$h" "http://$LAB_GUEST_IP/img/$img" | sha256sum | cut -d' ' -f1)
  [ "$want" = "$got" ] || ok=0
done
[ $ok = 1 ] && pass "the images arrive byte for byte" || fail "the images arrive byte for byte"
for i in $(seq 1 30); do
  api=$(curl -s -m 10 -H "$h" http://$LAB_GUEST_IP/api/products)
  echo "$api" | grep -q "Item 17 $TOKEN" && break
  sleep 3
done
echo "$api" | grep -q "Item 17 $TOKEN" && pass "the products come from the moved database" || fail "the products come from the moved database"
orders=$(sudo -u postgres psql -d shop -tAc "SELECT count(*) || ' ' || sum(qty) FROM orders" 2>/dev/null)
[ "$orders" = "1000 3000" ] && pass "the order history came along (1000 orders)" || fail "the order history came along (got: ${orders:-no shop database})"
pid=$(systemctl show -p MainPID --value shop-api 2>/dev/null)
u=$([ "${pid:-0}" != 0 ] && ps -o user= -p "$pid")
[ -n "$u" ] && [ "$u" != root ] && pass "shop-api runs after a reboot, as its own user ($u)" || fail "shop-api runs after a reboot, as its own user (${u:-not running})"
