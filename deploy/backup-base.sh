#!/usr/bin/env bash
set -euo pipefail
umask 077

if [[ $# -ne 1 ]]; then echo "usage: $0 /path/to/environment.env" >&2; exit 64; fi
environment_file=$1
directory=$(cd "$(dirname "$0")" && pwd)
source "$environment_file"

if [[ "${PCCONNECT_ENVIRONMENT:-}" == "production" && "${PCCONNECT_PRODUCTION_BACKUP_APPROVED:-}" != "I_HAVE_EXPLICIT_APPROVAL" ]]; then
  echo "production backup automation requires recorded explicit approval" >&2
  exit 77
fi
for command in age curl docker rclone sha256sum tar; do command -v "$command" >/dev/null || { echo "missing command: $command" >&2; exit 1; }; done
: "${PCCONNECT_BACKUP_ROOT:?set PCCONNECT_BACKUP_ROOT}"
: "${PCCONNECT_BACKUP_AGE_RECIPIENT:?set PCCONNECT_BACKUP_AGE_RECIPIENT}"
: "${PCCONNECT_BACKUP_DESTINATION:?set PCCONNECT_BACKUP_DESTINATION}"

backup_id=$(date -u +'%Y%m%dT%H%M%SZ')
output="$PCCONNECT_BACKUP_ROOT/base/$backup_id"
mkdir -p "$output"
compose=(docker compose --env-file "$environment_file" -f "$directory/compose.yaml")

base_tmp="$output/postgres-base.tar.gz.age.partial"
"${compose[@]}" exec -T --user postgres postgres \
  pg_basebackup --username=pcconnect --pgdata=- --format=tar --gzip --wal-method=fetch --checkpoint=fast \
  | age --encrypt --recipient "$PCCONNECT_BACKUP_AGE_RECIPIENT" --output "$base_tmp"
mv "$base_tmp" "$output/postgres-base.tar.gz.age"

keys_tmp="$output/recovery-keys.tar.age.partial"
tar --create --directory "$PCCONNECT_SECRET_ROOT" \
  postgres_password postgres_connection reminder_key_id reminder_key email_key_id email_key \
  deletion_tombstone_key export_encryption_key data_protection_password data-protection.pfx \
  | age --encrypt --recipient "$PCCONNECT_BACKUP_AGE_RECIPIENT" --output "$keys_tmp"
mv "$keys_tmp" "$output/recovery-keys.tar.age"

(cd "$output" && sha256sum postgres-base.tar.gz.age recovery-keys.tar.age > SHA256SUMS)
cat > "$output/manifest.json" <<EOF
{"formatVersion":1,"backupId":"$backup_id","environment":"$PCCONNECT_ENVIRONMENT","release":"$PCCONNECT_RELEASE","encryption":"age","includesDatabase":true,"includesRecoveryKeys":true}
EOF
(cd "$output" && sha256sum manifest.json >> SHA256SUMS)

rclone copy "$output" "$PCCONNECT_BACKUP_DESTINATION/base/$backup_id" --immutable --checksum
rclone check "$output" "$PCCONNECT_BACKUP_DESTINATION/base/$backup_id" --checksum --one-way
completed=$(date +%s)
printf '# TYPE pcconnect_backup_last_success_timestamp_seconds gauge\npcconnect_backup_last_success_timestamp_seconds %s\n' "$completed" \
  | curl --fail --silent --show-error --data-binary @- "http://127.0.0.1:9091/metrics/job/pcconnect_base_backup/environment/$PCCONNECT_ENVIRONMENT"
echo "encrypted base backup verified: $backup_id"
