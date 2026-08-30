#!/usr/bin/env bash
# CI integration mode: bring up ONLY the database container plus the db-init
# sidecar, apply db/sql scripts in order, and print the connection string the
# integration test run should use. Used by .github/workflows/ci.yml. Exit
# code is non-zero if any script fails (sqlcmd runs with -b).
set -euo pipefail
cd "$(dirname "$0")/.."

COMPOSE="docker compose --profile ci"
DB_WAIT_SECONDS="${DB_WAIT_SECONDS:-120}"
SQL_PORT="${SQL_PORT:-1433}"

echo "==> starting db only (profile ci)"
$COMPOSE up -d db

echo "==> waiting for db health"
db_id="$($COMPOSE ps -q db)"
if [ -z "$db_id" ]; then
  echo "db container not found" >&2
  exit 1
fi
deadline=$(( $(date +%s) + DB_WAIT_SECONDS ))
while true; do
  status="$(docker inspect -f '{{.State.Health.Status}}' "$db_id" 2>/dev/null || echo unknown)"
  if [ "$status" = "healthy" ]; then
    echo "db is healthy"
    break
  fi
  if [ "$(date +%s)" -ge "$deadline" ]; then
    echo "db did not become healthy within ${DB_WAIT_SECONDS}s (status: $status)" >&2
    docker logs "$db_id" | tail -20 >&2 || true
    exit 1
  fi
  sleep 2
done

echo "==> applying database scripts in order (001, 002, seed/003)"
$COMPOSE run --rm db-init

echo ""
echo "database is ready for integration tests"
echo "ConnectionStrings__Corridor=Server=localhost,${SQL_PORT};Database=Corridor;User Id=sa;Password=CorridorDev1!;TrustServerCertificate=True;Encrypt=True"
