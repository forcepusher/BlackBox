#!/bin/bash
# The sales report job: a script run by cron every 2 minutes as the user reports. Two things went
# wrong when someone edited it on a Windows PC: the script now has CRLF line endings (bash can't be
# found as "/bin/bash\r"), and the cron.d file was saved group-writable, which cron refuses to load.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
command -v cron >/dev/null || { apt-get update -q && apt-get install -y -q cron; } # not in Debian's cloud image
useradd --system --create-home --home-dir /home/reports --shell /usr/sbin/nologin reports
install -d -o reports -g reports -m 755 /var/reports
printf '#!/bin/bash\n# Appends the current sales total to the day'"'"'s report.\nset -eu\nf=/var/reports/sales-$(date +%%F).txt\necho "$(date -Is) total=$((RANDOM %% 900 + 100)) EUR (%s)" >> "$f"\n' "$TOKEN" | sed 's/$/\r/' > /usr/local/bin/sales-report
chmod 755 /usr/local/bin/sales-report
printf '# Sales report every 2 minutes\nSHELL=/bin/sh\nPATH=/usr/local/bin:/usr/bin:/bin\n*/2 * * * * reports /usr/local/bin/sales-report\n' > /etc/cron.d/sales-report
chmod 664 /etc/cron.d/sales-report
systemctl restart cron
