#!/usr/bin/env bash
# =============================================================================
# Restore rehearsal  (02 §9)
#
# Takes the most recent encrypted backup, restores it into a scratch database,
# and asserts that what came back is a real database rather than an empty one
# that happened to exit zero. The verification gates in db/verification/checks.sql
# are run separately by `pcconnect-migrate verify` against the same scratch
# database — that is the next step in the CI job, not this script's job.
#
# Run monthly in CI, and by hand before any migration cutover:
#
#   BACKUP_LOCAL_DIR=/var/backups/pcconnect \
#   BACKUP_AGE_KEY="$(cat ~/pcconnect-backup.age.key)" \
#   PCCONNECT_DATABASE__CONNECTIONSTRING="Host=…" \
#   ./tools/restore-rehearsal.sh
#
# A rehearsal that cannot find a backup fails. It never passes quietly: the
# whole point of the exercise is that the failure arrives on a Tuesday morning
# rather than during an incident.
# =============================================================================

set -euo pipefail

umask 077

log() { printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"; }
die() { log "FATAL: $*"; exit 1; }

# ── where the backup comes from ──────────────────────────────────────────────
# Two sources, because there are two places a rehearsal legitimately runs from.
#
#   BACKUP_LOCAL_DIR   a directory this machine can already read. On the deploy
#                      host that is the backup volume itself.
#   BACKUP_SSH_SOURCE  user@host:/path, for CI or a workstation. Needs
#                      BACKUP_SSH_KEY. See "Known gap" at the foot of this file.
: "${BACKUP_LOCAL_DIR:=}"
: "${BACKUP_SSH_SOURCE:=}"
: "${BACKUP_SSH_KEY:=}"
: "${BACKUP_AGE_KEY:=}"

# Assertions. A restored production database with no users is a failed restore,
# not a quiet Tuesday.
: "${EXPECTED_MIN_USERS:=1}"
: "${EXPECTED_MIN_DEVICES:=0}"

[ -n "$BACKUP_AGE_KEY" ] || die "BACKUP_AGE_KEY is not set. The backup is encrypted and cannot be read without it."

command -v age >/dev/null 2>&1 || die "age is not installed. On the runner: 'sudo apt-get install -y age'."
command -v psql >/dev/null 2>&1 || die "psql is not installed."

work="$(mktemp -d)"
cleanup() {
    # The decrypted dump is the whole database in the clear. It exists for the
    # length of one psql invocation and never outlives this script, including
    # when the script fails.
    rm -rf "$work"
}
trap cleanup EXIT INT TERM

# ── fetch ────────────────────────────────────────────────────────────────────
archive="$work/backup.sql.age"

if [ -n "$BACKUP_LOCAL_DIR" ]; then
    [ -d "$BACKUP_LOCAL_DIR" ] || die "BACKUP_LOCAL_DIR '$BACKUP_LOCAL_DIR' is not a directory."

    latest="$(find "$BACKUP_LOCAL_DIR" -name 'pcconnect-*.sql.age' -type f -print0 \
        | xargs -0 -r ls -1t 2>/dev/null | head -n 1 || true)"

    [ -n "$latest" ] || die "No pcconnect-*.sql.age in $BACKUP_LOCAL_DIR. There is nothing to rehearse."

    log "Using $latest"
    cp "$latest" "$archive"

elif [ -n "$BACKUP_SSH_SOURCE" ]; then
    [ -n "$BACKUP_SSH_KEY" ] || die "BACKUP_SSH_SOURCE is set but BACKUP_SSH_KEY is not."

    key="$work/id"
    printf '%s\n' "$BACKUP_SSH_KEY" > "$key"
    chmod 600 "$key"

    host="${BACKUP_SSH_SOURCE%%:*}"
    path="${BACKUP_SSH_SOURCE#*:}"

    # Resolve the newest file remotely; copying the whole directory would move
    # a month of dumps across the network to read one.
    latest="$(ssh -i "$key" -o StrictHostKeyChecking=accept-new "$host" \
        "ls -1t '$path'/pcconnect-*.sql.age 2>/dev/null | head -n 1")"

    [ -n "$latest" ] || die "No pcconnect-*.sql.age under $BACKUP_SSH_SOURCE."

    log "Using $host:$latest"
    scp -q -i "$key" -o StrictHostKeyChecking=accept-new "$host:$latest" "$archive"

else
    die "Set BACKUP_LOCAL_DIR or BACKUP_SSH_SOURCE. A rehearsal with no backup to restore is not a rehearsal."
fi

size="$(wc -c < "$archive")"
log "Encrypted backup is $size bytes"
[ "$size" -ge 4096 ] || die "That backup is $size bytes. The nightly job rejects anything under 4096; so does this."

# ── decrypt ──────────────────────────────────────────────────────────────────
identity="$work/age.key"
printf '%s\n' "$BACKUP_AGE_KEY" > "$identity"

dump="$work/backup.sql"
age --decrypt --identity "$identity" --output "$dump" "$archive" \
    || die "Decryption failed. Either the key is wrong or the archive is damaged — both are findings."

rm -f "$identity"

log "Decrypted dump is $(wc -c < "$dump") bytes"

# ── restore ──────────────────────────────────────────────────────────────────
# The connection string is the .NET form the rest of the system uses, so the
# rehearsal is configured exactly like the services it rehearses for.
: "${PCCONNECT_DATABASE__CONNECTIONSTRING:?PCCONNECT_DATABASE__CONNECTIONSTRING is not set}"

# Host=h;Port=p;Database=d;Username=u;Password=w  →  psql environment.
parse() {
    printf '%s' "$PCCONNECT_DATABASE__CONNECTIONSTRING" \
        | tr ';' '\n' \
        | awk -F= -v k="$1" 'tolower($1)==tolower(k){ sub(/^[^=]*=/, ""); print; exit }'
}

PGHOST="$(parse Host)";     export PGHOST
PGPORT="$(parse Port)";     export PGPORT
PGDATABASE="$(parse Database)"; export PGDATABASE
PGUSER="$(parse Username)"; export PGUSER
PGPASSWORD="$(parse Password)"; export PGPASSWORD
: "${PGPORT:=5432}"

[ -n "$PGDATABASE" ] || die "The connection string has no Database."

case "$PGDATABASE" in
    pcconnect|pcconnect_prod|prod|production)
        # Restoring over the live database is the accident this script must not
        # be able to have. The scratch name is not a convention, it is the guard.
        die "Refusing to restore into '$PGDATABASE'. Point this at a scratch database."
        ;;
esac

log "Restoring into $PGUSER@$PGHOST:$PGPORT/$PGDATABASE"

psql --quiet --no-psqlrc --set ON_ERROR_STOP=on \
     --command "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;" \
    || die "Could not reset the scratch schema."

# The dump is not run with ON_ERROR_STOP: pg_dump output legitimately contains
# statements that fail on a fresh cluster (ownership, extensions already there).
# The assertions below are what decide whether the restore worked, because
# "psql exited zero" has never been evidence of a usable database.
psql --quiet --no-psqlrc --file "$dump" > "$work/restore.log" 2>&1 || true

errors="$(grep -c '^psql:.*ERROR' "$work/restore.log" || true)"
log "psql reported $errors error line(s) during the restore"

# ── assert ───────────────────────────────────────────────────────────────────
scalar() {
    psql --quiet --no-psqlrc --tuples-only --no-align --command "$1" 2>/dev/null | tr -d '[:space:]'
}

missing=""
for table in users devices commands reminders refresh_tokens; do
    present="$(scalar "SELECT to_regclass('public.$table') IS NOT NULL")"
    [ "$present" = "t" ] || missing="$missing $table"
done

if [ -n "$missing" ]; then
    log "--- last 30 lines of the restore log ---"
    tail -n 30 "$work/restore.log" || true
    die "The restored database is missing:$missing"
fi

users="$(scalar 'SELECT count(*) FROM users')"
devices="$(scalar 'SELECT count(*) FROM devices')"
reminders="$(scalar 'SELECT count(*) FROM reminders')"

log "Restored: $users users, $devices devices, $reminders reminders"

[ "$users" -ge "$EXPECTED_MIN_USERS" ] \
    || die "Restored $users users, expected at least $EXPECTED_MIN_USERS. A backup that restores to an empty table is a backup that does not exist."

[ "$devices" -ge "$EXPECTED_MIN_DEVICES" ] \
    || die "Restored $devices devices, expected at least $EXPECTED_MIN_DEVICES."

# Credentials have to survive a restore or nobody can sign in afterwards, which
# is a restore that produced a database and not a service.
orphans="$(scalar 'SELECT count(*) FROM user_credentials c LEFT JOIN users u ON u.id = c.user_id WHERE u.id IS NULL')"
[ "$orphans" = "0" ] || die "$orphans credential rows have no user. The restore is not referentially whole."

log "PASS: the latest backup restores into a working database."
log "Next: pcconnect-migrate verify, against this same scratch database."

# =============================================================================
# Known gap (09 §3)
#
# BACKUP_SSH_SOURCE exists because the nightly job writes to a volume on the
# deploy host and nothing pushes those files off it — 02 §9 says "pushed
# off-host", and that part is not built. Until it is, a CI rehearsal needs an
# SSH key that can read /var/backups/pcconnect on the production host, which is
# a credential this repository deliberately does not have. Run the rehearsal on
# the host with BACKUP_LOCAL_DIR, or finish the off-host push first.
# =============================================================================
