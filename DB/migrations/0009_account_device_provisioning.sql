-- =============================================================================
-- 0009 — A PC joins an account by signing in on that PC
--
-- The human-entered pairing-code flow is retired. A signed-in Windows companion
-- creates a device and passes an opaque, single-use ticket to its local agent.
-- =============================================================================

-- migrate:up

DELETE FROM auth_challenges WHERE purpose = 'device_pairing';
ALTER TABLE auth_challenges DROP CONSTRAINT ck_auth_challenges_purpose;
ALTER TABLE auth_challenges ADD CONSTRAINT ck_auth_challenges_purpose CHECK (
  purpose IN ('password_reset','email_verify','step_up')
);

CREATE TABLE device_provisionings (
  id                    bigint         GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  ticket_hash           bytea          NOT NULL,
  device_id             bigint         NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
  secret_wrapped        bytea          NULL,
  secret_kek_id         varchar(32)    NULL,
  secret_released_at    timestamptz(3) NULL,
  expires_at            timestamptz(3) NOT NULL,
  created_at            timestamptz(3) NOT NULL DEFAULT now(),

  CONSTRAINT uq_device_provisionings_ticket UNIQUE (ticket_hash)
);

CREATE INDEX ix_device_provisionings_expiry ON device_provisionings (expires_at);

-- Preserve tickets minted by the earlier provisioning implementation. Pending
-- human-code sessions deliberately do not migrate: that flow no longer exists.
INSERT INTO device_provisionings
  (ticket_hash, device_id, secret_wrapped, secret_kek_id, secret_released_at, expires_at, created_at)
SELECT poll_token_hash, device_id, secret_wrapped, secret_kek_id, secret_released_at, expires_at, created_at
  FROM device_pairings
 WHERE claimed_at IS NOT NULL
   AND device_id IS NOT NULL
   AND secret_wrapped IS NOT NULL
   AND secret_kek_id IS NOT NULL;

COMMENT ON TABLE device_provisionings IS
  'Single-use local-agent handoff created when a user signs in on a PC.';

-- migrate:down

ALTER TABLE auth_challenges DROP CONSTRAINT ck_auth_challenges_purpose;
ALTER TABLE auth_challenges ADD CONSTRAINT ck_auth_challenges_purpose CHECK (
  purpose IN ('password_reset','email_verify','device_pairing','step_up')
);

DROP TABLE device_provisionings;
