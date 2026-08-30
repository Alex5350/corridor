#!/usr/bin/env bash
# Bring up the full Corridor demo stack: SQL Server container, schema + seed
# scripts, then every service. macOS friendly (docker compose v2, bash 3 safe).
# Tear down with scripts/dev-down.sh.
set -euo pipefail
cd "$(dirname "$0")/.."

COMPOSE="docker compose --profile full"
DB_WAIT_SECONDS="${DB_WAIT_SECONDS:-120}"
PORTAL_PORT="${PORTAL_PORT:-5200}"  # 5200: 5000 collides with macOS AirPlay

echo "==> starting db (SQL Server)"
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

# Apply db/sql/001, 002 and seed/003 in order with sqlcmd -b. The db-init
# sidecar carries sqlcmd (the azure-sql-edge server image ships none, so
# docker exec into the db container is not an option here).
echo "==> applying database scripts (001, 002, seed/003)"
$COMPOSE run --rm db-init

# Explicit service list: starts everything except db-init (its work is done
# and the scripts are idempotent anyway).
echo "==> starting oktasim, adfssim, legacy, portal, oktasim-shim, spa"
$COMPOSE up -d oktasim adfssim legacy portal oktasim-shim spa

echo "==> waiting for service health endpoints"
wait_for_url() {
  url="$1"
  label="$2"
  tries="${3:-60}"
  i=0
  while [ "$i" -lt "$tries" ]; do
    if curl -fsS -o /dev/null "$url" 2>/dev/null; then
      echo "$label is up"
      return 0
    fi
    i=$((i + 1))
    sleep 2
  done
  echo "$label did not answer at $url within $((tries * 2))s" >&2
  return 1
}
wait_for_url http://localhost:8080/healthz oktasim
wait_for_url http://localhost:8090/healthz adfssim
wait_for_url http://localhost:8000/healthz legacy
wait_for_url "http://localhost:${PORTAL_PORT}/healthz" portal
wait_for_url http://localhost:5173/ spa

echo ""
echo "Corridor demo stack is up:"
echo "  Portal (PermitPortal)   http://localhost:${PORTAL_PORT}   (Migration dashboard under Admin)"
echo "  okta-sim admin console  http://localhost:8080   (read-only persona UI)"
echo "  adfs-sim login page     http://localhost:8090"
echo "  TraceLink WSDL          http://localhost:8000/TraceLink.svc?wsdl"
echo "  FieldInsight SPA        http://localhost:5173"
echo ""
echo "Demo logins (synthetic users, password Demo1234! for all):"
echo "  admin@corridor.example      Admin"
echo "  inspector@corridor.example  Inspector"
echo "  officer@corridor.example    Officer"
echo "  clerk@corridor.example      Clerk"
echo ""
echo "All apps start in Adfs trust mode; flip them from the portal Admin page."
echo "Logs: docker compose --profile full logs <service>. Stop: scripts/dev-down.sh"
if [ "$PORTAL_PORT" != "5200" ] && [ "$(uname -s)" = "Darwin" ]; then
  echo ""
  echo "Note: PORTAL_PORT=$PORTAL_PORT is set. macOS AirPlay Receiver occupies"
  echo "port 5200 by default; either keep this override or disable AirPlay"
  echo "Receiver in System Settings > General > AirDrop & Handoff."
fi
