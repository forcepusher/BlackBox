#!/bin/bash
# Nothing to prepare on the server: Docker isn't installed, and the app is on the PC (setup-host.ps1).
set -euo pipefail
! command -v docker >/dev/null
