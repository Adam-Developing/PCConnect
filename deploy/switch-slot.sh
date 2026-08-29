#!/usr/bin/env sh
set -eu

if [ "$#" -ne 2 ]; then echo "usage: $0 /path/to/environment.env api-blue|api-green" >&2; exit 64; fi
env_file=$1
slot=$2
test "$slot" = "api-blue" || test "$slot" = "api-green"
set -a
. "$env_file"
set +a

compose="$(dirname "$0")/compose.yaml"
PCCONNECT_ACTIVE_UPSTREAM="$slot:8080" docker compose --env-file "$env_file" -f "$compose" up -d --no-deps --force-recreate caddy

temporary="$env_file.tmp.$$"
awk -v value="$slot:8080" '
  BEGIN { replaced=0 }
  /^PCCONNECT_ACTIVE_UPSTREAM=/ { print "PCCONNECT_ACTIVE_UPSTREAM=" value; replaced=1; next }
  { print }
  END { if (!replaced) print "PCCONNECT_ACTIVE_UPSTREAM=" value }
' "$env_file" > "$temporary"
chmod --reference="$env_file" "$temporary"
mv "$temporary" "$env_file"
echo "active API slot is now $slot; the previous slot was left runnable for rollback"
