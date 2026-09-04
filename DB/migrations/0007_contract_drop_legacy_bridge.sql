-- =============================================================================
-- 0007 — CONTRACT: drop the legacy bridge
--
-- DESTRUCTIVE. This is the one-way door of the migration (07 Phase 4.9). It
-- runs only when the runner is invoked with --allow-destructive, and only after
-- the guards below pass.
--
-- The guards are not advisory. Dropping the bridge while a reminder is still
-- unencrypted, or while an account still holds a v1 password hash that no
-- client can upgrade, is unrecoverable without a restore. The migration raises
-- and rolls back rather than proceeding.
-- =============================================================================

-- migrate:up

DO $guard$
DECLARE
  unmigrated_reminders bigint;
  legacy_credentials   bigint;
  unresolved_exceptions bigint;
BEGIN
  -- Guard 1 (the dangerous one, ADR-0004): every reminder must already be
  -- AES-256-GCM under a DEK. Before envelope encryption existed the API key was
  -- the AES key, so dropping the bridge with plaintext-less rows destroys them.
  SELECT count(*) INTO unmigrated_reminders
  FROM reminders
  WHERE body_ciphertext IS NULL OR octet_length(body_ciphertext) < 29;

  IF unmigrated_reminders > 0 THEN
    RAISE EXCEPTION 'REFUSING to contract: % reminder(s) are not re-encrypted under a DEK. See 02 section 7.', unmigrated_reminders;
  END IF;

  -- Guard 2: no account may be stranded on a hash no client can upgrade.
  -- Legacy accounts must first be moved to pending_verification with a reset
  -- email sent (07 Phase 6.5).
  SELECT count(*) INTO legacy_credentials
  FROM user_credentials c
  JOIN users u ON u.id = c.user_id
  WHERE c.algo = 'legacy_sha256_unsalted'
    AND u.status <> 'pending_verification'
    AND u.deleted_at IS NULL;

  IF legacy_credentials > 0 THEN
    RAISE EXCEPTION 'REFUSING to contract: % account(s) still hold a legacy hash and are not pending_verification.', legacy_credentials;
  END IF;

  -- Guard 3: nothing unexplained was left behind by the import.
  SELECT count(*) INTO unresolved_exceptions FROM migration_exceptions;
  IF unresolved_exceptions > 0 THEN
    RAISE EXCEPTION 'REFUSING to contract: % unresolved migration exception(s). Review migration_exceptions.', unresolved_exceptions;
  END IF;
END
$guard$;

DROP INDEX IF EXISTS uq_reminders_legacy_id;
DROP INDEX IF EXISTS uq_devices_legacy_pcid;
DROP INDEX IF EXISTS uq_users_legacy_id;

ALTER TABLE reminders DROP COLUMN legacy_id;
ALTER TABLE devices   DROP COLUMN legacy_pcid;
ALTER TABLE users     DROP COLUMN legacy_user_id;

DROP TABLE migration_exceptions;
DROP TABLE migration_state;

-- migrate:down

-- Reversing the contract restores the columns but not their values: the mapping
-- is only recoverable from a backup taken before this migration ran. That
-- asymmetry is why this migration is separately approved.
ALTER TABLE users     ADD COLUMN legacy_user_id integer NULL;
ALTER TABLE devices   ADD COLUMN legacy_pcid    integer NULL;
ALTER TABLE reminders ADD COLUMN legacy_id      integer NULL;

CREATE UNIQUE INDEX uq_users_legacy_id     ON users     (legacy_user_id) WHERE legacy_user_id IS NOT NULL;
CREATE UNIQUE INDEX uq_devices_legacy_pcid ON devices   (legacy_pcid)    WHERE legacy_pcid    IS NOT NULL;
CREATE UNIQUE INDEX uq_reminders_legacy_id ON reminders (legacy_id)      WHERE legacy_id      IS NOT NULL;

CREATE TABLE migration_state (
  entity                text         PRIMARY KEY,
  last_legacy_id        bigint       NOT NULL DEFAULT 0,
  rows_imported         bigint       NOT NULL DEFAULT 0,
  rows_skipped          bigint       NOT NULL DEFAULT 0,
  last_error            text         NULL,
  started_at            timestamptz(3) NOT NULL DEFAULT now(),
  updated_at            timestamptz(3) NOT NULL DEFAULT now(),
  completed_at          timestamptz(3) NULL
);

CREATE TABLE migration_exceptions (
  id                    bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  entity                text         NOT NULL,
  legacy_id             bigint       NULL,
  reason                text         NOT NULL,
  detail                jsonb        NULL,
  occurred_at           timestamptz(3) NOT NULL DEFAULT now()
);

CREATE INDEX ix_migration_exceptions_entity ON migration_exceptions (entity, occurred_at DESC);
