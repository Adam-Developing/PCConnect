#!/usr/bin/env bash
set -euo pipefail
umask 077

if [[ $# -ne 3 ]]; then
  echo "usage: $0 /path/to/staging.env BACKUP_ID RECOVERY_TARGET_UTC" >&2
  exit 64
fi
environment_file=$1
backup_id=$2
recovery_target=$3
source "$environment_file"
[[ "${PCCONNECT_ENVIRONMENT:-}" == "staging" ]] || { echo "restore rehearsals only run in staging" >&2; exit 77; }
for command in age docker rclone sha256sum tar; do command -v "$command" >/dev/null || { echo "missing command: $command" >&2; exit 1; }; done
: "${PCCONNECT_RESTORE_ROOT:?set an isolated PCCONNECT_RESTORE_ROOT}"
: "${PCCONNECT_AGE_IDENTITY_FILE:?set PCCONNECT_AGE_IDENTITY_FILE to approved escrow material}"

restore_root=$(realpath -m "$PCCONNECT_RESTORE_ROOT")
[[ "$restore_root" == /srv/pcconnect-v2/restore-rehearsals/* ]] || { echo "restore root must be below /srv/pcconnect-v2/restore-rehearsals" >&2; exit 77; }
[[ ! -e "$restore_root" ]] || { echo "restore target already exists: $restore_root" >&2; exit 73; }
mkdir -p "$restore_root/download/base" "$restore_root/download/wal" "$restore_root/download/tombstones" "$restore_root/data" "$restore_root/wal" "$restore_root/keys" "$restore_root/tombstones"
started=$(date +%s)

rclone copy "$PCCONNECT_BACKUP_DESTINATION/base/$backup_id" "$restore_root/download/base" --immutable --checksum
rclone copy "$PCCONNECT_BACKUP_DESTINATION/wal" "$restore_root/download/wal" --immutable --checksum
rclone copy "$PCCONNECT_BACKUP_DESTINATION/tombstones" "$restore_root/download/tombstones" --immutable --checksum
(cd "$restore_root/download/base" && sha256sum --check SHA256SUMS)

age --decrypt --identity "$PCCONNECT_AGE_IDENTITY_FILE" "$restore_root/download/base/postgres-base.tar.gz.age" \
  | tar --extract --gzip --directory "$restore_root/data"
age --decrypt --identity "$PCCONNECT_AGE_IDENTITY_FILE" "$restore_root/download/base/recovery-keys.tar.age" \
  | tar --extract --directory "$restore_root/keys"

for encrypted in "$restore_root"/download/wal/*.age; do
  [[ -e "$encrypted" ]] || continue
  name=$(basename "$encrypted" .age)
  (cd "$restore_root/download/wal" && sha256sum --check "$name.age.sha256")
  age --decrypt --identity "$PCCONNECT_AGE_IDENTITY_FILE" --output "$restore_root/wal/$name" "$encrypted"
done

latest_ledger=$(find "$restore_root/download/tombstones" -maxdepth 1 -type f -name '*.csv.age' | sort | tail -n 1)
[[ -n "$latest_ledger" ]] || { echo "no deletion tombstone ledger was found" >&2; exit 1; }
(cd "$restore_root/download/tombstones" && sha256sum --check "$(basename "$latest_ledger").sha256")
age --decrypt --identity "$PCCONNECT_AGE_IDENTITY_FILE" --output "$restore_root/tombstones/latest.csv" "$latest_ledger"

cat >> "$restore_root/data/postgresql.auto.conf" <<EOF
restore_command = 'cp /restore/wal/%f %p'
recovery_target_time = '$recovery_target'
recovery_target_action = 'promote'
EOF
touch "$restore_root/data/recovery.signal"

container="pcconnect-restore-${backup_id,,}"
cleanup() { docker rm --force "$container" >/dev/null 2>&1 || true; }
trap cleanup EXIT
docker run --detach --name "$container" --network none \
  --security-opt no-new-privileges:true --cap-drop ALL --cap-add CHOWN --cap-add DAC_OVERRIDE --cap-add FOWNER --cap-add SETGID --cap-add SETUID \
  --mount "type=bind,src=$restore_root/data,dst=/var/lib/postgresql/18/docker" \
  --mount "type=bind,src=$restore_root/wal,dst=/restore/wal,readonly" \
  --mount "type=bind,src=$restore_root/keys,dst=/restore/keys,readonly" \
  --mount "type=bind,src=$restore_root/tombstones,dst=/restore/tombstones,readonly" \
  "$PCCONNECT_POSTGRES_IMAGE" >/dev/null

for _ in {1..120}; do
  if docker exec "$container" pg_isready --username pcconnect --dbname pcconnect >/dev/null 2>&1; then break; fi
  sleep 2
done
docker exec "$container" pg_isready --username pcconnect --dbname pcconnect
for _ in {1..120}; do
  if docker exec "$container" psql --username pcconnect --dbname pcconnect --no-psqlrc --tuples-only --no-align --command \
    "SELECT NOT pg_is_in_recovery()" 2>/dev/null | grep -qx 't'; then break; fi
  sleep 2
done
docker exec "$container" psql --username pcconnect --dbname pcconnect --no-psqlrc --tuples-only --no-align --command \
  "SELECT NOT pg_is_in_recovery()" | grep -qx 't'
docker exec --interactive "$container" psql --username pcconnect --dbname pcconnect --no-psqlrc --set ON_ERROR_STOP=1 <<'SQL'
CREATE TEMP TABLE replay_tombstones(subject_digest_hex text, deleted_at timestamptz, restore_replay_version integer);
\copy replay_tombstones FROM '/restore/tombstones/latest.csv' WITH (FORMAT csv, HEADER true)
WITH replay AS (
  SELECT decode(subject_digest_hex,'hex') AS subject_digest FROM replay_tombstones WHERE restore_replay_version=1
), victims AS (
  SELECT u.id FROM users u
  WHERE EXISTS (
    SELECT 1 FROM replay r
    WHERE r.subject_digest=hmac(
      convert_to('pcconnect.deleted.v1|' || u.id::text,'UTF8'),
      decode(btrim(pg_read_file('/restore/keys/deletion_tombstone_key')),'base64'),
      'sha256'))
)
DELETE FROM users u USING victims v WHERE u.id=v.id;
SQL
docker exec "$container" psql --username pcconnect --dbname pcconnect --no-psqlrc --tuples-only --command \
  "SELECT count(*) FROM users; SELECT count(*) FROM devices; SELECT count(*) FROM deletion_tombstones;"

duration=$(( $(date +%s) - started ))
cat > "$restore_root/rehearsal-result.json" <<EOF
{"formatVersion":1,"backupId":"$backup_id","recoveryTarget":"$recovery_target","durationSeconds":$duration,"databaseReady":true,"keysDecrypted":true,"walReplayRequested":true,"deletionTombstonesReplayed":true,"tombstoneLedger":"$(basename "$latest_ledger")"}
EOF
echo "isolated restore rehearsal passed in ${duration}s: $restore_root/rehearsal-result.json"
