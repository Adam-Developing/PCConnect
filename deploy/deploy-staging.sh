#!/usr/bin/env sh
set -eu

if [ "$#" -ne 1 ]; then echo "usage: $0 /path/to/staging.env" >&2; exit 64; fi
env_file=$1
directory=$(dirname "$0")
"$directory/preflight.sh" "$env_file"
set -a
. "$env_file"
set +a
test "$PCCONNECT_ENVIRONMENT" = "staging"

case "${PCCONNECT_ACTIVE_UPSTREAM:-api-blue:8080}" in
  api-blue:8080) inactive=api-green ;;
  api-green:8080) inactive=api-blue ;;
  *) echo "invalid PCCONNECT_ACTIVE_UPSTREAM" >&2; exit 1 ;;
esac

compose="$directory/compose.yaml"
docker compose --env-file "$env_file" -f "$compose" pull caddy "$inactive" worker postgres valkey otel-collector prometheus pushgateway node-exporter
docker compose --env-file "$env_file" -f "$compose" --profile migration pull migrator
docker compose --env-file "$env_file" -f "$compose" up -d --wait --wait-timeout 120 postgres valkey otel-collector prometheus pushgateway node-exporter
docker compose --env-file "$env_file" -f "$compose" --profile migration run --rm migrator
docker compose --env-file "$env_file" -f "$compose" --profile migration run --rm migrator --apply
docker compose --env-file "$env_file" -f "$compose" up -d --no-deps "$inactive"
ready=0
attempt=0
while [ "$attempt" -lt 60 ]; do
  if docker compose --env-file "$env_file" -f "$compose" run --rm --no-deps --entrypoint wget caddy -qO- "http://$inactive:8080/api/v2/health/ready" 2>/dev/null | grep -q '"status":"ok"'; then
    ready=1
    break
  fi
  attempt=$((attempt + 1))
  sleep 2
done
test "$ready" = "1" || { echo "$inactive did not become ready" >&2; exit 1; }
"$directory/switch-slot.sh" "$env_file" "$inactive"
docker compose --env-file "$env_file" -f "$compose" up -d --no-deps worker
edge_ready=0
attempt=0
while [ "$attempt" -lt 60 ]; do
  if curl --fail --silent --show-error --max-time 5 "https://$PCCONNECT_API_HOST/api/v2/health/live" >/dev/null 2>&1; then
    edge_ready=1
    break
  fi
  attempt=$((attempt + 1))
  sleep 2
done
test "$edge_ready" = "1" || { echo "the staging HTTPS edge did not become ready" >&2; exit 1; }
"$directory/verify-staging.sh" "https://$PCCONNECT_API_HOST"
