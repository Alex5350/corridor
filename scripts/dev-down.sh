#!/usr/bin/env bash
# Stop the Corridor demo stack. Keeps the named db volume by default so demo
# data survives restarts; run with CORRIDOR_PURGE=1 to remove the volume too.
set -euo pipefail
cd "$(dirname "$0")/.."

if [ "${CORRIDOR_PURGE:-0}" = "1" ]; then
  echo "==> stopping the stack and removing the db volume"
  docker compose --profile full --profile ci down -v
  echo "done (db volume removed; next dev-up starts from a clean database)"
else
  echo "==> stopping the stack (db volume kept)"
  docker compose --profile full --profile ci down
  echo "done (run with CORRIDOR_PURGE=1 to drop the db volume as well)"
fi
