-- =============================================================================
-- 0011 — Per-device password confirmation policy
--
-- Each PC owns the list of commands that need a fresh password/passkey check.
-- The default preserves the previous behaviour for existing installations.
-- =============================================================================

-- migrate:up

ALTER TABLE devices
  ADD COLUMN password_required_commands jsonb NOT NULL
    DEFAULT '["shutdown","restart","signout","hibernate"]'::jsonb,
  ADD CONSTRAINT ck_devices_password_required
    CHECK (jsonb_typeof(password_required_commands) = 'array');

ALTER TABLE commands
  ADD COLUMN password_required boolean NOT NULL DEFAULT false;

UPDATE commands
   SET password_required = true
 WHERE step_up_verified_at IS NOT NULL;

ALTER TABLE commands DROP CONSTRAINT ck_commands_stepup;
ALTER TABLE commands ADD CONSTRAINT ck_commands_stepup CHECK (
  NOT password_required OR step_up_verified_at IS NOT NULL
);

COMMENT ON COLUMN devices.password_required_commands IS
  'Command types that require fresh password/passkey confirmation for this device.';
COMMENT ON COLUMN commands.password_required IS
  'Snapshot of the target device confirmation policy when this command was issued.';

-- migrate:down

ALTER TABLE commands DROP CONSTRAINT ck_commands_stepup;
ALTER TABLE commands ADD CONSTRAINT ck_commands_stepup CHECK (
  risk_tier <> 'destructive' OR step_up_verified_at IS NOT NULL
) NOT VALID;
ALTER TABLE commands DROP COLUMN password_required;

ALTER TABLE devices
  DROP CONSTRAINT ck_devices_password_required,
  DROP COLUMN password_required_commands;
