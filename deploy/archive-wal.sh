#!/usr/bin/env bash
set -euo pipefail
umask 077

if [[ $# -ne 1 ]]; then echo "usage: $0 /path/to/environment.env" >&2; exit 64; fi
environment_file=$1
directory=$(cd "$(dirname "$0")" && pwd)
source "$environment_file"

if [[ "${PCCONNECT_ENVIRONMENT:-}" == "production" && "${PCCONNECT_PRODUCTION_BACKUP_APPROVED:-}" != "I_HAVE_EXPLICIT_APPROVAL" ]]; then
  echo "production WAL archive automation requires recorded explicit approval" >&2
  exit 77
fi
for command in age curl docker find rclone sha256sum; do command -v "$command" >/dev/null || { echo "missing command: $command" >&2; exit 1; }; done
: "${PCCONNECT_BACKUP_ROOT:?set PCCONNECT_BACKUP_ROOT}"
: "${PCCONNECT_BACKUP_AGE_RECIPIENT:?set PCCONNECT_BACKUP_AGE_RECIPIENT}"
: "${PCCONNECT_BACKUP_DESTINATION:?set PCCONNECT_BACKUP_DESTINATION}"

source_directory="$PCCONNECT_STATE_ROOT/wal-archive"
encrypted_directory="$PCCONNECT_BACKUP_ROOT/wal"
tombstone_directory="$PCCONNECT_BACKUP_ROOT/tombstones"
mkdir -p "$encrypted_directory" "$tombstone_directory"
compose=(docker compose --env-file "$environment_file" -f "$directory/compose.yaml")

while IFS= read -r -d '' segment; do
  name=$(basename "$segment")
  encrypted="$encrypted_directory/$name.age"
  if [[ ! -f "$encrypted" ]]; then
    age --encrypt --recipient "$PCCONNECT_BACKUP_AGE_RECIPIENT" --output "$encrypted.partial" "$segment"
    mv "$encrypted.partial" "$encrypted"
    (cd "$encrypted_directory" && sha256sum "$name.age" > "$name.age.sha256")
  fi
  rclone copyto "$encrypted" "$PCCONNECT_BACKUP_DESTINATION/wal/$name.age" --immutable --checksum
  rclone copyto "$encrypted.sha256" "$PCCONNECT_BACKUP_DESTINATION/wal/$name.age.sha256" --immutable --checksum
done < <(find "$source_directory" -maxdepth 1 -type f -name '[0-9A-F]*' -print0)

# PostgreSQL has already copied these segments out of pg_wal. Retain two local
# days after verified encrypted upload so a transient remote outage is recoverable.
while IFS= read -r -d '' segment; do
  name=$(basename "$segment")
  rclone check "$encrypted_directory/$name.age" "$PCCONNECT_BACKUP_DESTINATION/wal/$name.age" --checksum --one-way
  rm -- "$segment"
done < <(find "$source_directory" -maxdepth 1 -type f -name '[0-9A-F]*' -mtime +2 -print0)

# Deletion tombstones are copied independently of PITR so restoring to an
# earlier instant cannot resurrect an account deleted after that instant. The
# ledger contains only keyed, non-reversible subject digests.
ledger_id=$(date -u +'%Y%m%dT%H%M%SZ')
ledger="$tombstone_directory/$ledger_id.csv.age"
"${compose[@]}" exec -T --user postgres postgres psql --username=pcconnect --dbname=pcconnect --no-psqlrc --command \
  "COPY (SELECT encode(subject_digest,'hex') AS subject_digest_hex,deleted_at,restore_replay_version FROM deletion_tombstones ORDER BY deleted_at,subject_digest) TO STDOUT WITH (FORMAT csv, HEADER true)" \
  | age --encrypt --recipient "$PCCONNECT_BACKUP_AGE_RECIPIENT" --output "$ledger.partial"
mv "$ledger.partial" "$ledger"
(cd "$tombstone_directory" && sha256sum "$ledger_id.csv.age" > "$ledger_id.csv.age.sha256")
rclone copyto "$ledger" "$PCCONNECT_BACKUP_DESTINATION/tombstones/$ledger_id.csv.age" --immutable --checksum
rclone copyto "$ledger.sha256" "$PCCONNECT_BACKUP_DESTINATION/tombstones/$ledger_id.csv.age.sha256" --immutable --checksum
rclone check "$ledger" "$PCCONNECT_BACKUP_DESTINATION/tombstones/$ledger_id.csv.age" --checksum --one-way

date -u +'%Y-%m-%dT%H:%M:%SZ' > "$encrypted_directory/last-success.txt"
rclone copyto "$encrypted_directory/last-success.txt" "$PCCONNECT_BACKUP_DESTINATION/wal/last-success.txt"
completed=$(date +%s)
printf '# TYPE pcconnect_wal_archive_last_success_timestamp_seconds gauge\npcconnect_wal_archive_last_success_timestamp_seconds %s\n' "$completed" \
  | curl --fail --silent --show-error --data-binary @- "http://127.0.0.1:9091/metrics/job/pcconnect_wal_archive/environment/$PCCONNECT_ENVIRONMENT"
echo "WAL and deletion-ledger encryption and remote verification passed"
