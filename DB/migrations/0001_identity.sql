-- =============================================================================
-- 0001 — Identity context
--
-- PostgreSQL 18 · UTC everywhere · see docs/architecture/02-data-architecture.md
--
-- Conventions (ADR-0009 restates these for PostgreSQL):
--   * Surrogate PK : bigint GENERATED ALWAYS AS IDENTITY, column `id`
--   * Public id    : uuid holding a UUIDv7 (PostgreSQL 18 `uuidv7()`), column
--                    `public_id`, UNIQUE. Auto-increment ids are never exposed:
--                    they leak the user count and invite enumeration.
--   * Timestamps   : timestamptz(3), suffix `_at`, always UTC (closes S2-07)
--   * Enums        : text + CHECK, never a native enum type (adding a value to
--                    a CHECK is a catalogue-only change)
--   * Secrets      : digests only; never a reversible or plaintext value
-- =============================================================================

-- migrate:up

CREATE TABLE users (
  id                    bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  public_id             uuid         NOT NULL DEFAULT uuidv7(),

  email                 varchar(320) NOT NULL,
  email_normalised      varchar(320) NOT NULL,
  is_email_verified     boolean      NOT NULL DEFAULT false,

  username              varchar(64)  NOT NULL,
  username_normalised   varchar(64)  NOT NULL,

  display_name          varchar(255) NOT NULL DEFAULT '',
  timezone              varchar(64)  NOT NULL DEFAULT 'Etc/UTC',
  locale                varchar(16)  NOT NULL DEFAULT 'en-GB',

  status                text         NOT NULL DEFAULT 'active',
  is_marketing_opt_in   boolean      NOT NULL DEFAULT false,

  -- Envelope encryption (ADR-0004): the DEK is generated per user and stored
  -- wrapped by a KEK that lives outside the database.
  dek_wrapped           bytea        NULL,
  dek_kek_id            varchar(32)  NULL,

  created_at            timestamptz(3) NOT NULL DEFAULT now(),
  updated_at            timestamptz(3) NOT NULL DEFAULT now(),
  deleted_at            timestamptz(3) NULL,

  CONSTRAINT uq_users_public_id UNIQUE (public_id),
  CONSTRAINT uq_users_email     UNIQUE (email_normalised),
  CONSTRAINT uq_users_username  UNIQUE (username_normalised),
  CONSTRAINT ck_users_status    CHECK (status IN ('active','suspended','pending_verification')),
  CONSTRAINT ck_users_timezone  CHECK (length(timezone) > 0)
);

COMMENT ON COLUMN users.public_id   IS 'UUIDv7 - the only user id the API exposes';
COMMENT ON COLUMN users.timezone    IS 'IANA tz id; authoritative for rendering reminder local times (closes S2-07)';
COMMENT ON COLUMN users.dek_wrapped IS 'AES-256-GCM(KEK, user DEK). NULL until the first encrypted write.';
COMMENT ON COLUMN users.deleted_at  IS 'Soft delete; hard delete runs 30d later via the retention job';

CREATE INDEX ix_users_deleted_at ON users (deleted_at) WHERE deleted_at IS NOT NULL;

-- Credentials live apart from the profile: different access pattern, different
-- sensitivity, and it keeps password material out of every SELECT * FROM users.
CREATE TABLE user_credentials (
  user_id               bigint       PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,

  algo                  text         NOT NULL DEFAULT 'argon2id',
  password_hash         text         NOT NULL,

  must_rehash           boolean      NOT NULL DEFAULT false,
  password_changed_at   timestamptz(3) NOT NULL DEFAULT now(),

  failed_attempts       integer      NOT NULL DEFAULT 0,
  locked_until          timestamptz(3) NULL,

  created_at            timestamptz(3) NOT NULL DEFAULT now(),
  updated_at            timestamptz(3) NOT NULL DEFAULT now(),

  CONSTRAINT ck_user_credentials_algo CHECK (algo IN ('argon2id','legacy_sha256_unsalted'))
);

COMMENT ON COLUMN user_credentials.password_hash IS 'Argon2id PHC string, or a lowercase SHA-256 hex digest while algo=legacy_sha256_unsalted (02 section 6)';
COMMENT ON COLUMN user_credentials.must_rehash   IS 'Forces a legacy account through the upgrade-on-login path';

-- Rotating refresh tokens with family-based reuse detection (ADR-0002).
-- Only the SHA-256 of the token is stored: a database dump yields nothing usable.
CREATE TABLE refresh_tokens (
  id                    bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  token_hash            bytea        NOT NULL,
  family_id             uuid         NOT NULL,

  user_id               bigint       NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  device_id             bigint       NULL,          -- FK added in 0002 (devices does not exist yet)

  client_kind           text         NOT NULL,
  client_version        varchar(32)  NOT NULL DEFAULT '',
  user_agent            varchar(255) NOT NULL DEFAULT '',
  ip_first_seen         inet         NULL,

  issued_at             timestamptz(3) NOT NULL DEFAULT now(),
  expires_at            timestamptz(3) NOT NULL,
  last_used_at          timestamptz(3) NULL,
  revoked_at            timestamptz(3) NULL,
  revoked_reason        text         NULL,

  CONSTRAINT uq_refresh_tokens_hash   UNIQUE (token_hash),
  CONSTRAINT ck_refresh_tokens_client CHECK (client_kind IN ('desktop_agent','mobile','web','legacy')),
  CONSTRAINT ck_refresh_tokens_reason CHECK (revoked_reason IS NULL OR revoked_reason IN
    ('rotated','logout','logout_all','reuse_detected','password_change','device_revoked','admin','expired')),
  CONSTRAINT ck_refresh_tokens_hashlen CHECK (octet_length(token_hash) = 32)
);

CREATE INDEX ix_refresh_tokens_family ON refresh_tokens (family_id);
CREATE INDEX ix_refresh_tokens_user   ON refresh_tokens (user_id) WHERE revoked_at IS NULL;
CREATE INDEX ix_refresh_tokens_expiry ON refresh_tokens (expires_at);
CREATE INDEX ix_refresh_tokens_device ON refresh_tokens (device_id) WHERE device_id IS NOT NULL;

-- Replaces `verifications` + `verificationtypes`. Single-use, expiring, hashed.
CREATE TABLE auth_challenges (
  id                    bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  user_id               bigint       NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  purpose               text         NOT NULL,
  code_hash             bytea        NOT NULL,
  expires_at            timestamptz(3) NOT NULL,
  consumed_at           timestamptz(3) NULL,
  attempts              integer      NOT NULL DEFAULT 0,
  requested_ip          inet         NULL,
  detail                jsonb        NULL,
  created_at            timestamptz(3) NOT NULL DEFAULT now(),

  CONSTRAINT uq_auth_challenges_hash    UNIQUE (code_hash),
  CONSTRAINT ck_auth_challenges_purpose CHECK (purpose IN
    ('password_reset','email_verify','device_pairing','step_up')),
  CONSTRAINT ck_auth_challenges_hashlen CHECK (octet_length(code_hash) = 32)
);

CREATE INDEX ix_auth_challenges_user   ON auth_challenges (user_id, purpose) WHERE consumed_at IS NULL;
CREATE INDEX ix_auth_challenges_expiry ON auth_challenges (expires_at);

-- Passkeys / WebAuthn (ADR-0010). A passkey is a first-class account credential,
-- not a second factor bolted onto the password.
CREATE TABLE webauthn_credentials (
  id                    bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  public_id             uuid         NOT NULL DEFAULT uuidv7(),
  user_id               bigint       NOT NULL REFERENCES users(id) ON DELETE CASCADE,

  credential_id         bytea        NOT NULL,
  public_key_cose       bytea        NOT NULL,
  signature_counter     bigint       NOT NULL DEFAULT 0,
  aaguid                uuid         NULL,
  transports            text         NOT NULL DEFAULT '',
  is_backup_eligible    boolean      NOT NULL DEFAULT false,
  is_uv_capable         boolean      NOT NULL DEFAULT true,

  display_name          varchar(128) NOT NULL DEFAULT '',
  created_at            timestamptz(3) NOT NULL DEFAULT now(),
  last_used_at          timestamptz(3) NULL,
  revoked_at            timestamptz(3) NULL,

  CONSTRAINT uq_webauthn_credential_id UNIQUE (credential_id),
  CONSTRAINT uq_webauthn_public_id     UNIQUE (public_id)
);

COMMENT ON COLUMN webauthn_credentials.signature_counter IS 'Monotonic authenticator counter; a non-increasing value on a counter-using authenticator indicates a cloned credential';

CREATE INDEX ix_webauthn_user ON webauthn_credentials (user_id) WHERE revoked_at IS NULL;

-- In-flight WebAuthn ceremonies. Challenges are single-use and short-lived, and
-- are bound to the ceremony type so a registration challenge cannot be replayed
-- into an authentication.
CREATE TABLE webauthn_challenges (
  id                    bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  challenge             bytea        NOT NULL,
  ceremony              text         NOT NULL,
  user_id               bigint       NULL REFERENCES users(id) ON DELETE CASCADE,
  expires_at            timestamptz(3) NOT NULL,
  consumed_at           timestamptz(3) NULL,
  created_at            timestamptz(3) NOT NULL DEFAULT now(),

  CONSTRAINT uq_webauthn_challenge UNIQUE (challenge),
  CONSTRAINT ck_webauthn_ceremony  CHECK (ceremony IN ('registration','authentication','step_up'))
);

CREATE INDEX ix_webauthn_challenges_expiry ON webauthn_challenges (expires_at);

-- Every authentication decision. IPs are purged on the same 90-day retention as
-- command_events (03 section 8).
CREATE TABLE security_events (
  id                    bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  user_id               bigint       NULL REFERENCES users(id) ON DELETE SET NULL,
  event                 varchar(48)  NOT NULL,
  outcome               text         NOT NULL,
  source_ip             inet         NULL,
  user_agent            varchar(255) NOT NULL DEFAULT '',
  detail                jsonb        NULL,
  occurred_at           timestamptz(3) NOT NULL DEFAULT now(),

  CONSTRAINT ck_security_events_outcome CHECK (outcome IN ('success','failure'))
);

CREATE INDEX ix_security_events_user  ON security_events (user_id, occurred_at DESC);
CREATE INDEX ix_security_events_event ON security_events (event, occurred_at DESC);

-- migrate:down

DROP TABLE IF EXISTS security_events;
DROP TABLE IF EXISTS webauthn_challenges;
DROP TABLE IF EXISTS webauthn_credentials;
DROP TABLE IF EXISTS auth_challenges;
DROP TABLE IF EXISTS refresh_tokens;
DROP TABLE IF EXISTS user_credentials;
DROP TABLE IF EXISTS users;
