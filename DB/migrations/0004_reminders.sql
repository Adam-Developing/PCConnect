-- =============================================================================
-- 0004 — Reminders context
--
-- Closes S2-07 (no timezone), S2-08 (utf8mb3 could not hold an emoji - moot on
-- PostgreSQL, which is UTF-8 throughout), S2-09 (five recurrence columns that
-- no client reads), S1-06/S1-07 (the API key was the AES key, and CBC was
-- unauthenticated).
-- =============================================================================

-- migrate:up

CREATE TABLE reminders (
  id                    bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  public_id             uuid         NOT NULL DEFAULT uuidv7(),
  user_id               bigint       NOT NULL REFERENCES users(id) ON DELETE CASCADE,

  -- AES-256-GCM under the user's DEK: [12B nonce][ciphertext][16B tag].
  -- GCM replaces CBC so the ciphertext is authenticated (fixes S1-07).
  body_ciphertext       bytea        NOT NULL,
  body_dek_id           varchar(32)  NOT NULL,

  -- The single source of truth for "when". A UTC instant, always.
  due_at_utc            timestamptz(3) NOT NULL,
  -- Kept alongside so a recurrence can be re-expanded correctly across DST and
  -- so the UI can show the time the user actually typed.
  due_local_time        time         NOT NULL,
  timezone              varchar(64)  NOT NULL,

  -- RFC 5545 RRULE replaces the five Recurrence_* columns (fixes S2-09).
  rrule                 varchar(255) NULL,
  recurrence_until      timestamptz(3) NULL,

  is_completed          boolean      NOT NULL DEFAULT false,
  completed_at          timestamptz(3) NULL,
  -- Set when the scheduler has delivered this one-off reminder, so a worker
  -- restart does not notify the same person twice.
  notified_at           timestamptz(3) NULL,

  created_at            timestamptz(3) NOT NULL DEFAULT now(),
  updated_at            timestamptz(3) NOT NULL DEFAULT now(),
  deleted_at            timestamptz(3) NULL,

  CONSTRAINT uq_reminders_public_id UNIQUE (public_id),
  -- 12B nonce + 16B tag + at least one byte of ciphertext.
  CONSTRAINT ck_reminders_ciphertext CHECK (octet_length(body_ciphertext) >= 29),
  CONSTRAINT ck_reminders_completed  CHECK (
    (is_completed AND completed_at IS NOT NULL) OR (NOT is_completed AND completed_at IS NULL))
);

COMMENT ON COLUMN reminders.body_ciphertext IS 'AES-256-GCM under the per-user DEK: [12B nonce][ciphertext][16B tag]';
COMMENT ON COLUMN reminders.body_dek_id     IS 'Which DEK version encrypted this row; enables lazy rekeying';
COMMENT ON COLUMN reminders.timezone        IS 'IANA tz captured at creation; the reminder fires at the users local time, not the servers (S2-07)';

-- The hot path: "this user's upcoming reminders".
CREATE INDEX ix_reminders_user_due  ON reminders (user_id, is_completed, due_at_utc)
  WHERE deleted_at IS NULL;
-- The scheduler path: "everything due in the next tick".
CREATE INDEX ix_reminders_due_sweep ON reminders (due_at_utc)
  WHERE deleted_at IS NULL AND is_completed = false AND notified_at IS NULL;

-- Materialised occurrences of recurring reminders. Expanding an RRULE on every
-- scheduler tick does not scale; a rolling horizon does, and it lets a user
-- complete or skip one occurrence without touching the series.
CREATE TABLE reminder_occurrences (
  id                    bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  reminder_id           bigint       NOT NULL REFERENCES reminders(id) ON DELETE CASCADE,
  occurs_at_utc         timestamptz(3) NOT NULL,
  status                text         NOT NULL DEFAULT 'pending',
  notified_at           timestamptz(3) NULL,
  completed_at          timestamptz(3) NULL,

  CONSTRAINT uq_reminder_occurrence UNIQUE (reminder_id, occurs_at_utc),
  CONSTRAINT ck_reminder_occurrences_status CHECK (status IN ('pending','notified','completed','skipped'))
);

CREATE INDEX ix_reminder_occurrences_sweep ON reminder_occurrences (occurs_at_utc)
  WHERE status = 'pending';

-- migrate:down

DROP TABLE IF EXISTS reminder_occurrences;
DROP TABLE IF EXISTS reminders;
