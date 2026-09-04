#!/bin/sh
# =============================================================================
# Nightly backup: dump, encrypt, retain, prune.
#
# The dump is encrypted with `age` before it touches the disk, using a public
# key that is safe to commit; the private key lives offline and in the
# maintainer's password manager (03 §7).
#
# The restore rehearsal is not here — it is a CI job (02 §9), because a restore
# that is only ever described in a runbook is a restore that has never worked.
# =============================================================================

set -eu

BACKUP_DIR="/var/backups/pcconnect"
RETENTION_DAYS="${BACKUP_RETENTION_DAYS:-30}"

log() {
    echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) $*"
}

if [ -z "${BACKUP_AGE_RECIPIENT:-}" ]; then
    log "FATAL: BACKUP_AGE_RECIPIENT is not set. Refusing to write an unencrypted dump."
    exit 1
fi

# age is baked into the image (see backup.Dockerfile). This service has no route
# to the internet, so it cannot install anything at run time — by design.
if ! command -v age >/dev/null 2>&1; then
    log "FATAL: age is not installed in this image. Refusing to write an unencrypted dump."
    exit 1
fi

mkdir -p "$BACKUP_DIR"

run_backup() {
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    target="$BACKUP_DIR/pcconnect-$stamp.sql.age"

    log "Dumping to $target"

    # pg_dump takes its own snapshot and is consistent without blocking writers,
    # so it needs no equivalent of mysqldump's --single-transaction.
    if pg_dump --no-owner --no-privileges \
        | age --recipient "$BACKUP_AGE_RECIPIENT" --output "$target"; then

        size="$(wc -c < "$target")"

        # A dump that is suspiciously small is a failed dump that exited zero.
        if [ "$size" -lt 4096 ]; then
            log "FAILED: $target is only $size bytes"
            rm -f "$target"
            return 1
        fi

        log "Wrote $target ($size bytes)"
    else
        log "FAILED: pg_dump or age returned non-zero"
        rm -f "$target"
        return 1
    fi

    log "Pruning backups older than $RETENTION_DAYS days"
    find "$BACKUP_DIR" -name 'pcconnect-*.sql.age' -mtime "+$RETENTION_DAYS" -delete

    log "Backups on disk: $(find "$BACKUP_DIR" -name 'pcconnect-*.sql.age' | wc -l)"
}

# One at start-up so a fresh deployment has a backup within the minute rather
# than within the day, then one every 24 hours.
while true; do
    run_backup || log "Backup failed; will retry on the next cycle"
    sleep 86400
done
