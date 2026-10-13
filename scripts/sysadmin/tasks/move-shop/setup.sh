#!/bin/bash
# The new server is the lab's fresh one; the old one is set up by old-server.ps1 (old/setup.sh).
set -euo pipefail
! systemctl is-active --quiet nginx
