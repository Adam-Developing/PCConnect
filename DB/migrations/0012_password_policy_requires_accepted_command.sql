-- =============================================================================
-- 0012 — A disabled command cannot require a password
-- =============================================================================

-- migrate:up

-- Clean up choices saved by versions that treated the two switches as
-- independent. An empty allow-list retains its legacy meaning of "allow all".
UPDATE devices
   SET password_required_commands = (
     SELECT COALESCE(jsonb_agg(entry.value), '[]'::jsonb)
       FROM jsonb_array_elements(password_required_commands) AS entry(value)
      WHERE allowed_commands @> jsonb_build_array(entry.value)
   )
 WHERE allowed_commands <> '[]'::jsonb;

ALTER TABLE devices ADD CONSTRAINT ck_devices_password_requires_allowed CHECK (
  allowed_commands = '[]'::jsonb OR password_required_commands <@ allowed_commands
);

-- migrate:down

ALTER TABLE devices DROP CONSTRAINT ck_devices_password_requires_allowed;
