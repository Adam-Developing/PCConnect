-- =============================================================================
-- 0002 — Devices context
--
-- `PCName` stops being an identity claim (S1-08). A device exists only because
-- a user confirmed a pairing code, and authorisation is by authenticated
-- device id — never by a header the caller chose.
-- =============================================================================

-- migrate:up

CREATE TABLE devices (
  id                    bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  public_id             uuid         NOT NULL DEFAULT uuidv7(),
  user_id               bigint       NOT NULL REFERENCES users(id) ON DELETE CASCADE,

  -- Purely a display label. It carries no security meaning.
  display_name          varchar(128) NOT NULL,

  platform              text         NOT NULL DEFAULT 'windows',
  os_version            varchar(64)  NOT NULL DEFAULT '',
  agent_version         varchar(32)  NOT NULL DEFAULT '',

  -- Per-device capability allow-list, intersected server-side with the user's
  -- policy. The agent enforces its own allow-list independently (defence in depth).
  allowed_commands      jsonb        NOT NULL DEFAULT '["shutdown","restart","signout","lock","sleep","hibernate"]'::jsonb,

  status                text         NOT NULL DEFAULT 'active',
  paired_at             timestamptz(3) NOT NULL DEFAULT now(),
  last_seen_at          timestamptz(3) NULL,

  created_at            timestamptz(3) NOT NULL DEFAULT now(),
  updated_at            timestamptz(3) NOT NULL DEFAULT now(),
  revoked_at            timestamptz(3) NULL,

  CONSTRAINT uq_devices_public_id UNIQUE (public_id),
  CONSTRAINT uq_devices_user_name UNIQUE (user_id, display_name),
  CONSTRAINT ck_devices_status    CHECK (status IN ('active','revoked','suspended')),
  -- Platform-neutral from day one: the API and schema already admit the clients
  -- that do not exist yet (01 section 1, G-platform).
  CONSTRAINT ck_devices_platform  CHECK (platform IN ('windows','macos','linux','android','ios','other')),
  CONSTRAINT ck_devices_allowed   CHECK (jsonb_typeof(allowed_commands) = 'array')
);

COMMENT ON COLUMN devices.public_id        IS 'UUIDv7; the device id in the API and in the did token claim';
COMMENT ON COLUMN devices.allowed_commands IS 'e.g. ["lock","sleep"] - omit "shutdown" to disable it for this device';
COMMENT ON COLUMN devices.last_seen_at     IS 'Durable heartbeat, coalesced to at most one write per minute; live presence lives in the cache';

CREATE INDEX ix_devices_user_status ON devices (user_id, status);
CREATE INDEX ix_devices_last_seen   ON devices (last_seen_at);

ALTER TABLE refresh_tokens
  ADD CONSTRAINT fk_refresh_tokens_device
  FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE;

-- The agent's own credential, independent of the user's password and tokens.
CREATE TABLE device_credentials (
  device_id             bigint       PRIMARY KEY REFERENCES devices(id) ON DELETE CASCADE,
  secret_hash           text         NOT NULL,
  rotated_at            timestamptz(3) NULL,
  created_at            timestamptz(3) NOT NULL DEFAULT now()
);

COMMENT ON COLUMN device_credentials.secret_hash IS 'Argon2id PHC of the device secret. The plaintext is shown once, at pairing.';

-- Short-lived pairing handshake. Replaces auto-registration of any PCName.
CREATE TABLE device_pairings (
  id                    bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  public_id             uuid         NOT NULL DEFAULT uuidv7(),
  code_hash             bytea        NOT NULL,
  poll_token_hash       bytea        NOT NULL,
  requested_name        varchar(128) NOT NULL,
  platform              text         NOT NULL DEFAULT 'windows',

  claimed_by_user_id    bigint       NULL REFERENCES users(id) ON DELETE CASCADE,
  device_id             bigint       NULL REFERENCES devices(id) ON DELETE SET NULL,

  -- The device secret is generated at claim time, held here encrypted under the
  -- KEK, and released to the agent exactly once by pair/poll. Storing it wrapped
  -- rather than in plaintext keeps a database dump from yielding a live secret
  -- during the ten-minute pairing window.
  secret_wrapped        bytea        NULL,
  secret_kek_id         varchar(32)  NULL,
  secret_released_at    timestamptz(3) NULL,

  expires_at            timestamptz(3) NOT NULL,
  claimed_at            timestamptz(3) NULL,
  attempts              integer      NOT NULL DEFAULT 0,
  created_at            timestamptz(3) NOT NULL DEFAULT now(),

  CONSTRAINT uq_device_pairings_code  UNIQUE (code_hash),
  CONSTRAINT uq_device_pairings_poll  UNIQUE (poll_token_hash),
  CONSTRAINT ck_device_pairings_plat  CHECK (platform IN ('windows','macos','linux','android','ios','other'))
);

CREATE INDEX ix_device_pairings_expiry ON device_pairings (expires_at);

-- migrate:down

ALTER TABLE refresh_tokens DROP CONSTRAINT IF EXISTS fk_refresh_tokens_device;
DROP TABLE IF EXISTS device_pairings;
DROP TABLE IF EXISTS device_credentials;
DROP TABLE IF EXISTS devices;
