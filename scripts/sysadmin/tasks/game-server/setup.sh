#!/bin/bash
# The game server's code, as the developers handed it over.
set -euo pipefail
tar -czf /home/admin/game-server.tar.gz -C /tmp/bb-task/files game-server
chown admin:admin /home/admin/game-server.tar.gz
