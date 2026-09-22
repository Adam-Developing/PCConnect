-- =============================================================================
-- 0010 — Remove storage for the retired human pairing-code flow
--
-- This is deliberately separate from 0009. Deployments can adopt account-based
-- provisioning without a destructive-migration flag, then remove the obsolete
-- table during a controlled migration run with --allow-destructive.
-- =============================================================================

-- migrate:up

DROP TABLE device_pairings;

-- migrate:down

CREATE TABLE device_pairings (
  id                    bigint         GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  public_id             uuid           NOT NULL DEFAULT uuidv7(),
  code_hash             bytea          NOT NULL,
  poll_token_hash       bytea          NOT NULL,
  requested_name        varchar(128)   NOT NULL,
  platform              text           NOT NULL DEFAULT 'windows',
  claimed_by_user_id    bigint         NULL REFERENCES users(id) ON DELETE CASCADE,
  device_id             bigint         NULL REFERENCES devices(id) ON DELETE SET NULL,
  secret_wrapped        bytea          NULL,
  secret_kek_id         varchar(32)    NULL,
  secret_released_at    timestamptz(3) NULL,
  expires_at            timestamptz(3) NOT NULL,
  claimed_at            timestamptz(3) NULL,
  attempts              integer        NOT NULL DEFAULT 0,
  created_at            timestamptz(3) NOT NULL DEFAULT now(),
  CONSTRAINT uq_device_pairings_code UNIQUE (code_hash),
  CONSTRAINT uq_device_pairings_poll UNIQUE (poll_token_hash),
  CONSTRAINT ck_device_pairings_plat CHECK (platform IN ('windows','macos','linux','android','ios','other'))
);

CREATE INDEX ix_device_pairings_expiry ON device_pairings (expires_at);
