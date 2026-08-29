#!/usr/bin/env sh
set -eu

if [ "$#" -ne 2 ] || [ "$2" != "I_HAVE_EXPLICIT_APPROVAL" ]; then
  echo "usage: $0 /path/to/production.env I_HAVE_EXPLICIT_APPROVAL" >&2
  exit 64
fi
env_file=$1
directory=$(dirname "$0")
"$directory/preflight.sh" "$env_file"
set -a
. "$env_file"
set +a
test "$PCCONNECT_ENVIRONMENT" = "production"
test -n "${PCCONNECT_PRODUCTION_CHANGE_TICKET:-}"

echo "Production execution remains operator-controlled. Confirm the encrypted backup, staging evidence, rollback window, and approved change ticket before adapting the staging sequence."
exit 3
