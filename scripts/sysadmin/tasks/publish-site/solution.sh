#!/bin/bash
# Reference solution, run as root by sysadmin-test.ps1 -SelfTest (after solution-host.ps1 copied the site to /tmp).
set -euo pipefail
cp -r /tmp/site-v2/. /var/www/html/
chmod -R a+rX /var/www/html
