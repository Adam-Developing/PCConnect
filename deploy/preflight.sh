#!/usr/bin/env sh
set -eu

if [ "$#" -ne 1 ]; then echo "usage: $0 /path/to/environment.env" >&2; exit 64; fi
env_file=$1
test -f "$env_file"
set -a
. "$env_file"
set +a

for command in docker curl openssl; do command -v "$command" >/dev/null || { echo "missing command: $command" >&2; exit 1; }; done
docker compose version >/dev/null
test "${PCCONNECT_ENVIRONMENT:-}" = "staging" || test "${PCCONNECT_ENVIRONMENT:-}" = "production"
test -n "${PCCONNECT_RELEASE:-}"
test -d "${PCCONNECT_SECRET_ROOT:-/nonexistent}"
test "${PCCONNECT_API_HOST:-}" != "${PCCONNECT_WEBAUTHN_RP_ID:-}" || { echo "API host and WebAuthn RP host must be distinct Caddy sites" >&2; exit 1; }
test "${PCCONNECT_WEB_ORIGIN:-}" = "https://${PCCONNECT_WEBAUTHN_RP_ID:-}" || { echo "PCCONNECT_WEB_ORIGIN must be the HTTPS WebAuthn RP origin" >&2; exit 1; }
printf '%s' "${PCCONNECT_ANDROID_PACKAGE_NAME:-}" | grep -Eq '^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$' || { echo "invalid PCCONNECT_ANDROID_PACKAGE_NAME" >&2; exit 1; }
printf '%s' "${PCCONNECT_ANDROID_CERT_SHA256:-}" | grep -Eq '^([0-9A-F]{2}:){31}[0-9A-F]{2}$' || { echo "PCCONNECT_ANDROID_CERT_SHA256 must be an uppercase colon-delimited SHA-256 fingerprint" >&2; exit 1; }
printf '%s' "${PCCONNECT_ANDROID_WEBAUTHN_ORIGIN:-}" | grep -Eq '^android:apk-key-hash:[A-Za-z0-9_-]{43}$' || { echo "invalid PCCONNECT_ANDROID_WEBAUTHN_ORIGIN" >&2; exit 1; }
test "${PCCONNECT_ANDROID_RP_HOST:-}" = "$PCCONNECT_WEBAUTHN_RP_ID" || { echo "PCCONNECT_ANDROID_RP_HOST must equal PCCONNECT_WEBAUTHN_RP_ID" >&2; exit 1; }
test "$(stat -c '%u' "$PCCONNECT_SECRET_ROOT")" = "0" || { echo "secret root must be owned by root" >&2; exit 1; }
secret_root_mode=$(stat -c '%a' "$PCCONNECT_SECRET_ROOT")
test "$secret_root_mode" = "700" || test "$secret_root_mode" = "750" || { echo "secret root must be mode 700 or 750" >&2; exit 1; }

images="PCCONNECT_API_IMAGE PCCONNECT_WORKER_IMAGE PCCONNECT_MIGRATOR_IMAGE PCCONNECT_CADDY_IMAGE PCCONNECT_POSTGRES_IMAGE PCCONNECT_VALKEY_IMAGE PCCONNECT_OTEL_IMAGE PCCONNECT_PROMETHEUS_IMAGE PCCONNECT_PUSHGATEWAY_IMAGE PCCONNECT_NODE_EXPORTER_IMAGE"
for variable in $images; do
  eval "reference=\${$variable:-}"
  printf '%s' "$reference" | grep -Eq '@sha256:[0-9a-f]{64}$' || { echo "$variable must be pinned by sha256 digest" >&2; exit 1; }
done

required="postgres_password postgres_connection token_hashing_key legacy_credential_hashing_key reminder_key_id reminder_key email_key_id email_key deletion_tombstone_key export_encryption_key data_protection_password data_protection.pfx smtp_host smtp_username smtp_password smtp_from"
for name in $required; do
  path="$PCCONNECT_SECRET_ROOT/$name"
  test -s "$path" || { echo "missing or empty secret: $path" >&2; exit 1; }
  mode=$(stat -c '%a' "$path")
  test "$mode" = "600" || test "$mode" = "400" || { echo "secret must be mode 600 or 400: $path" >&2; exit 1; }
done

app_secrets="postgres_connection token_hashing_key legacy_credential_hashing_key reminder_key_id reminder_key email_key_id email_key deletion_tombstone_key export_encryption_key data_protection_password data_protection.pfx smtp_host smtp_username smtp_password smtp_from"
for name in $app_secrets; do
  path="$PCCONNECT_SECRET_ROOT/$name"
  test "$(stat -c '%u' "$path")" = "1654" || { echo "app secret must be owned by container UID 1654: $path" >&2; exit 1; }
done
test "$(stat -c '%u' "$PCCONNECT_SECRET_ROOT/postgres_password")" = "0" || { echo "postgres_password must be owned by root" >&2; exit 1; }

for directory in postgres wal-archive exports data-protection caddy-data caddy-config prometheus; do
  test -d "$PCCONNECT_STATE_ROOT/$directory" || { echo "missing state directory: $PCCONNECT_STATE_ROOT/$directory" >&2; exit 1; }
done
test "$(stat -c '%u' "$PCCONNECT_STATE_ROOT/exports")" = "1654" || { echo "exports state must be owned by container UID 1654" >&2; exit 1; }
test "$(stat -c '%u' "$PCCONNECT_STATE_ROOT/data-protection")" = "1654" || { echo "data-protection state must be owned by container UID 1654" >&2; exit 1; }
test "$(stat -c '%u' "$PCCONNECT_STATE_ROOT/prometheus")" = "65532" || { echo "prometheus state must be owned by container UID 65532" >&2; exit 1; }
docker compose --env-file "$env_file" -f "$(dirname "$0")/compose.yaml" config --quiet
echo "preflight passed for $PCCONNECT_ENVIRONMENT release $PCCONNECT_RELEASE"
