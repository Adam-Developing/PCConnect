-- =============================================================================
-- 0006 — Legacy bridge  (EXPAND stage of the strangler migration)
--
-- The v1 system is MySQL; the v2 canonical store is PostgreSQL (ADR-0009). The
-- two engines cannot dual-write inside one transaction, so the bridge is an
-- idempotent replicated import keyed on the legacy primary keys, plus the state
-- table that makes that import resumable and auditable.
--
-- Everything in this migration is dropped again by 0007, which is destructive
-- and separately approved (07 section Phase 4.9).
-- =============================================================================

-- migrate:up

ALTER TABLE users     ADD COLUMN legacy_user_id integer NULL;
ALTER TABLE devices   ADD COLUMN legacy_pcid    integer NULL;
ALTER TABLE reminders ADD COLUMN legacy_id      integer NULL;

CREATE UNIQUE INDEX uq_users_legacy_id     ON users     (legacy_user_id) WHERE legacy_user_id IS NOT NULL;
CREATE UNIQUE INDEX uq_devices_legacy_pcid ON devices   (legacy_pcid)    WHERE legacy_pcid    IS NOT NULL;
CREATE UNIQUE INDEX uq_reminders_legacy_id ON reminders (legacy_id)      WHERE legacy_id      IS NOT NULL;

COMMENT ON COLUMN users.legacy_user_id IS 'v1 users.id. Bridge column; dropped at contract (0007).';
COMMENT ON COLUMN devices.legacy_pcid  IS 'v1 pcnames.PCID. Bridge column; dropped at contract (0007).';
COMMENT ON COLUMN reminders.legacy_id  IS 'v1 reminders.ID. Bridge column; dropped at contract (0007).';

-- Resumable, auditable import state. One row per (entity, batch) checkpoint, so
-- an interrupted backfill restarts from the last committed high-water mark
-- rather than from the beginning.
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

COMMENT ON TABLE migration_state IS 'High-water marks for the MySQL to PostgreSQL import; makes the backfill idempotent and resumable (07 Phase 4.3)';

-- Rows the import could not map cleanly. Nothing is silently dropped: an
-- unparseable recurrence or a duplicate email lands here with its reason, and
-- the verification gate counts it.
CREATE TABLE migration_exceptions (
  id                    bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  entity                text         NOT NULL,
  legacy_id             bigint       NULL,
  reason                text         NOT NULL,
  detail                jsonb        NULL,
  occurred_at           timestamptz(3) NOT NULL DEFAULT now()
);

CREATE INDEX ix_migration_exceptions_entity ON migration_exceptions (entity, occurred_at DESC);

-- migrate:down

DROP TABLE IF EXISTS migration_exceptions;
DROP TABLE IF EXISTS migration_state;
DROP INDEX IF EXISTS uq_reminders_legacy_id;
DROP INDEX IF EXISTS uq_devices_legacy_pcid;
DROP INDEX IF EXISTS uq_users_legacy_id;
ALTER TABLE reminders DROP COLUMN IF EXISTS legacy_id;
ALTER TABLE devices   DROP COLUMN IF EXISTS legacy_pcid;
ALTER TABLE users     DROP COLUMN IF EXISTS legacy_user_id;
