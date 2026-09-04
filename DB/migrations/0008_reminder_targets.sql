-- =============================================================================
-- 0008 — Which PCs a reminder shows on
--
-- Until now a reminder belonged to an account and appeared on every screen
-- signed in to it. The clients offer "All PCs / Choose PCs", and a picker whose
-- choice the server ignores is worse than no picker, so the choice is stored.
--
-- A join table rather than a column: a reminder shows on zero or more devices,
-- and an array of ids in a jsonb column could not be a foreign key — a revoked
-- device would leave a dangling reference that nothing cleans up.
--
-- No rows for a reminder means "every PC", which is what every reminder written
-- before this migration meant and what the clients still send by default. That
-- is why this migration needs no backfill.
-- =============================================================================

-- migrate:up

CREATE TABLE reminder_devices (
  reminder_id           bigint       NOT NULL REFERENCES reminders(id) ON DELETE CASCADE,
  device_id             bigint       NOT NULL REFERENCES devices(id) ON DELETE CASCADE,

  created_at            timestamptz(3) NOT NULL DEFAULT now(),

  CONSTRAINT pk_reminder_devices PRIMARY KEY (reminder_id, device_id)
);

COMMENT ON TABLE reminder_devices IS
  'Which devices a reminder shows on. No rows means every device on the account.';

-- The read path: "the targets of these reminders", answered from the index.
CREATE INDEX ix_reminder_devices_reminder ON reminder_devices (reminder_id);
-- The other direction: "what shows on this PC", and the cascade when one is revoked.
CREATE INDEX ix_reminder_devices_device   ON reminder_devices (device_id);

-- migrate:down

DROP TABLE IF EXISTS reminder_devices;
