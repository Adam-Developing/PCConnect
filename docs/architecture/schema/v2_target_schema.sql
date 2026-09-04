-- =============================================================================
-- PCConnect — Target schema v2
-- MySQL 8.4 LTS · InnoDB · utf8mb4_0900_ai_ci
--
-- This is the *destination* state, presented as one readable document, grouped
-- by bounded context for review rather than by dependency order (refresh_tokens
-- forward-references devices). It is NOT executed as-is: it is decomposed into
-- ordered dbmate migrations under db/migrations/ and reached via the
-- expand/contract sequence in 02-data-architecture.md §5.
--
-- Conventions
--   * Surrogate PK  : BIGINT UNSIGNED AUTO_INCREMENT, column `id`
--   * Public id     : BINARY(16) holding a UUIDv7, column `public_id`,
--                     UNIQUE. Never expose auto-increment ids in the API —
--                     they leak user counts and invite enumeration.
--   * Timestamps    : DATETIME(3) in UTC. Suffix `_at`. Never TIMESTAMP
--                     (2038 + implicit session-timezone conversion).
--   * Booleans      : TINYINT(1) UNSIGNED, prefix `is_`, explicit DEFAULT
--   * Enums         : VARCHAR + CHECK, not ENUM (ENUM changes need a table
--                     rebuild; CHECK constraints do not)
--   * FKs           : always named, always with an explicit ON DELETE rule
--   * Money/secrets : never stored in plaintext; see column comments
-- =============================================================================

SET NAMES utf8mb4;
SET time_zone = '+00:00';

-- =============================================================================
-- IDENTITY
-- =============================================================================

CREATE TABLE users (
  id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id         BINARY(16)      NOT NULL COMMENT 'UUIDv7, the only id exposed by the API',

  email             VARCHAR(320)    NOT NULL,
  email_normalised  VARCHAR(320)    NOT NULL COMMENT 'lower(trim(email)); the uniqueness key',
  is_email_verified TINYINT(1) UNSIGNED NOT NULL DEFAULT 0,

  username          VARCHAR(64)     NOT NULL,
  username_normalised VARCHAR(64)   NOT NULL COMMENT 'lower(username); the uniqueness key',

  display_name      VARCHAR(255)    NOT NULL DEFAULT '',
  timezone          VARCHAR(64)     NOT NULL DEFAULT 'Etc/UTC'
                      COMMENT 'IANA tz id. Authoritative for rendering reminder local times (fixes S2-07)',
  locale            VARCHAR(16)     NOT NULL DEFAULT 'en-GB',

  status            VARCHAR(16)     NOT NULL DEFAULT 'active',
  is_marketing_opt_in TINYINT(1) UNSIGNED NOT NULL DEFAULT 0,

  -- Envelope encryption (see ADR-0004). The DEK is generated per user and
  -- stored wrapped by the KEK, which lives outside the database.
  dek_wrapped       VARBINARY(255)  NULL COMMENT 'AES-256-GCM(KEK, user DEK). NULL until first encrypted write.',
  dek_kek_id        VARCHAR(32)     NULL COMMENT 'Which KEK version wrapped this DEK; enables KEK rotation',

  created_at        DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  updated_at        DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
  deleted_at        DATETIME(3)     NULL COMMENT 'Soft delete. Hard delete runs 30d later via retention job.',

  PRIMARY KEY (id),
  UNIQUE KEY uq_users_public_id  (public_id),
  UNIQUE KEY uq_users_email      (email_normalised),
  UNIQUE KEY uq_users_username   (username_normalised),
  KEY        ix_users_deleted_at (deleted_at),
  CONSTRAINT ck_users_status CHECK (status IN ('active','suspended','pending_verification'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- Credentials live apart from the profile: different access pattern, different
-- sensitivity, and it keeps password material out of every `SELECT * FROM users`.
CREATE TABLE user_credentials (
  user_id           BIGINT UNSIGNED NOT NULL,

  algo              VARCHAR(24)     NOT NULL DEFAULT 'argon2id'
                      COMMENT 'argon2id | legacy_sha256_unsalted (migration only, see 02 §6)',
  password_hash     VARBINARY(255)  NOT NULL COMMENT 'PHC string. Never a raw digest.',

  -- Forces every legacy account through the upgrade-on-login path.
  must_rehash       TINYINT(1) UNSIGNED NOT NULL DEFAULT 0,
  password_changed_at DATETIME(3)   NOT NULL DEFAULT CURRENT_TIMESTAMP(3),

  failed_attempts   INT UNSIGNED    NOT NULL DEFAULT 0,
  locked_until      DATETIME(3)     NULL COMMENT 'Exponential lockout; also enforced in Valkey for speed',

  created_at        DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  updated_at        DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),

  PRIMARY KEY (user_id),
  CONSTRAINT fk_user_credentials_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
  CONSTRAINT ck_user_credentials_algo CHECK (algo IN ('argon2id','legacy_sha256_unsalted'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- Rotating refresh tokens with family-based reuse detection (ADR-0002).
-- Only the SHA-256 of the token is stored: a database dump yields nothing usable.
CREATE TABLE refresh_tokens (
  id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  token_hash        BINARY(32)      NOT NULL COMMENT 'SHA-256 of the opaque 256-bit token',
  family_id         BINARY(16)      NOT NULL COMMENT 'Constant across a rotation chain; reuse revokes the whole family',

  user_id           BIGINT UNSIGNED NOT NULL,
  device_id         BIGINT UNSIGNED NULL COMMENT 'Set for agent sessions; NULL for mobile/web user sessions',

  client_kind       VARCHAR(16)     NOT NULL COMMENT 'desktop_agent | mobile | web',
  client_version    VARCHAR(32)     NOT NULL DEFAULT '',
  user_agent        VARCHAR(255)    NOT NULL DEFAULT '',
  ip_first_seen     VARBINARY(16)   NULL COMMENT 'INET6_ATON. For the user-visible session list.',

  issued_at         DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  expires_at        DATETIME(3)     NOT NULL,
  last_used_at      DATETIME(3)     NULL,
  revoked_at        DATETIME(3)     NULL,
  revoked_reason    VARCHAR(32)     NULL COMMENT 'rotated | logout | reuse_detected | password_change | admin',

  PRIMARY KEY (id),
  UNIQUE KEY uq_refresh_tokens_hash (token_hash),
  KEY        ix_refresh_tokens_family (family_id),
  KEY        ix_refresh_tokens_user (user_id, revoked_at),
  KEY        ix_refresh_tokens_expiry (expires_at),
  CONSTRAINT fk_refresh_tokens_user   FOREIGN KEY (user_id)   REFERENCES users(id)   ON DELETE CASCADE,
  CONSTRAINT fk_refresh_tokens_device FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE,
  CONSTRAINT ck_refresh_tokens_client CHECK (client_kind IN ('desktop_agent','mobile','web'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- Replaces `verifications` + `verificationtypes`. Single-use, expiring, hashed.
CREATE TABLE auth_challenges (
  id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  user_id           BIGINT UNSIGNED NOT NULL,
  purpose           VARCHAR(32)     NOT NULL COMMENT 'password_reset | email_verify | device_pairing',
  code_hash         BINARY(32)      NOT NULL COMMENT 'SHA-256 of the code; the code itself is never stored',
  expires_at        DATETIME(3)     NOT NULL,
  consumed_at       DATETIME(3)     NULL,
  attempts          INT UNSIGNED    NOT NULL DEFAULT 0,
  requested_ip      VARBINARY(16)   NULL,
  created_at        DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),

  PRIMARY KEY (id),
  UNIQUE KEY uq_auth_challenges_hash (code_hash),
  KEY        ix_auth_challenges_user (user_id, purpose, consumed_at),
  KEY        ix_auth_challenges_expiry (expires_at),
  CONSTRAINT fk_auth_challenges_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
  CONSTRAINT ck_auth_challenges_purpose CHECK (purpose IN ('password_reset','email_verify','device_pairing'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- =============================================================================
-- DEVICES
-- =============================================================================

CREATE TABLE devices (
  id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id         BINARY(16)      NOT NULL COMMENT 'UUIDv7; this is the device id in the API and in JWT claims',
  user_id           BIGINT UNSIGNED NOT NULL,

  -- Purely a display label now. It carries no security meaning: authorisation
  -- is by device_id from an authenticated device credential (fixes S1-08).
  display_name      VARCHAR(128)    NOT NULL,

  platform          VARCHAR(16)     NOT NULL DEFAULT 'windows',
  os_version        VARCHAR(64)     NOT NULL DEFAULT '',
  agent_version     VARCHAR(32)     NOT NULL DEFAULT '',

  -- Per-device capability allow-list, intersected server-side with the user's
  -- policy. The agent enforces its own allow-list independently: defence in depth.
  allowed_commands  JSON            NOT NULL
                      COMMENT 'e.g. ["lock","sleep"] — omit "shutdown" to disable it for this device',

  status            VARCHAR(16)     NOT NULL DEFAULT 'active',
  paired_at         DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  last_seen_at      DATETIME(3)     NULL COMMENT 'Heartbeat. Authoritative presence lives in Valkey; this is the durable fallback.',

  created_at        DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  updated_at        DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
  revoked_at        DATETIME(3)     NULL,

  PRIMARY KEY (id),
  UNIQUE KEY uq_devices_public_id (public_id),
  UNIQUE KEY uq_devices_user_name (user_id, display_name),
  KEY        ix_devices_user_status (user_id, status),
  KEY        ix_devices_last_seen (last_seen_at),
  CONSTRAINT fk_devices_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
  CONSTRAINT ck_devices_status   CHECK (status IN ('active','revoked','suspended')),
  CONSTRAINT ck_devices_platform CHECK (platform IN ('windows','macos','linux'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- The agent's own credential, independent of the user's password and tokens.
CREATE TABLE device_credentials (
  device_id         BIGINT UNSIGNED NOT NULL,
  secret_hash       VARBINARY(255)  NOT NULL COMMENT 'Argon2id PHC of the device secret. Plaintext is shown once, at pairing.',
  rotated_at        DATETIME(3)     NULL,
  created_at        DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),

  PRIMARY KEY (device_id),
  CONSTRAINT fk_device_credentials_device FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- Short-lived pairing handshake. Replaces auto-registration of any PCName.
CREATE TABLE device_pairings (
  id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  code_hash         BINARY(32)      NOT NULL COMMENT 'SHA-256 of the 8-char user-visible pairing code',
  requested_name    VARCHAR(128)    NOT NULL,
  platform          VARCHAR(16)     NOT NULL DEFAULT 'windows',

  claimed_by_user_id BIGINT UNSIGNED NULL,
  device_id         BIGINT UNSIGNED NULL COMMENT 'Set once the pairing completes',

  expires_at        DATETIME(3)     NOT NULL COMMENT 'Ten minutes from issue',
  claimed_at        DATETIME(3)     NULL,
  attempts          INT UNSIGNED    NOT NULL DEFAULT 0,
  created_at        DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),

  PRIMARY KEY (id),
  UNIQUE KEY uq_device_pairings_code (code_hash),
  KEY        ix_device_pairings_expiry (expires_at),
  CONSTRAINT fk_device_pairings_user   FOREIGN KEY (claimed_by_user_id) REFERENCES users(id)   ON DELETE CASCADE,
  CONSTRAINT fk_device_pairings_device FOREIGN KEY (device_id)          REFERENCES devices(id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- =============================================================================
-- COMMANDS  (append-only; replaces pcnames.Request / pcnames.Value)
-- =============================================================================

CREATE TABLE commands (
  id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,

  -- Client-generated UUIDv7. This is what makes retry safe: a replayed issue
  -- with the same public_id is a no-op, not a second shutdown.
  public_id         BINARY(16)      NOT NULL,

  device_id         BIGINT UNSIGNED NOT NULL,
  issued_by_user_id BIGINT UNSIGNED NOT NULL,
  issued_by_client  VARCHAR(16)     NOT NULL COMMENT 'mobile | web | desktop | legacy_shim',

  command_type      VARCHAR(32)     NOT NULL
                      COMMENT 'Closed vocabulary, validated server-side AND agent-side',
  params            JSON            NULL COMMENT 'e.g. {"delaySeconds":10}. Never a shell string.',

  status            VARCHAR(16)     NOT NULL DEFAULT 'issued',

  issued_at         DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  -- The fix for S2-03. A command not delivered inside its window is never run.
  expires_at        DATETIME(3)     NOT NULL,
  delivered_at      DATETIME(3)     NULL,
  acked_at          DATETIME(3)     NULL,
  terminal_at       DATETIME(3)     NULL,

  result_code       VARCHAR(32)     NULL,
  result_message    VARCHAR(255)    NULL,

  PRIMARY KEY (id),
  UNIQUE KEY uq_commands_public_id (public_id),
  -- The hot path: "give me this device's undelivered, unexpired commands"
  KEY ix_commands_device_pending (device_id, status, expires_at),
  KEY ix_commands_user_recent    (issued_by_user_id, issued_at),
  KEY ix_commands_expiry_sweep   (status, expires_at),
  CONSTRAINT fk_commands_device FOREIGN KEY (device_id)         REFERENCES devices(id) ON DELETE CASCADE,
  CONSTRAINT fk_commands_user   FOREIGN KEY (issued_by_user_id) REFERENCES users(id)   ON DELETE CASCADE,
  CONSTRAINT ck_commands_status CHECK (status IN
    ('issued','delivered','succeeded','failed','expired','cancelled')),
  CONSTRAINT ck_commands_type CHECK (command_type IN
    ('shutdown','restart','signout','lock','sleep','hibernate'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- Audit trail. These are destructive actions on someone's computer; every
-- transition is recorded and retained for 90 days.
CREATE TABLE command_events (
  id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  command_id        BIGINT UNSIGNED NOT NULL,
  event             VARCHAR(24)     NOT NULL COMMENT 'issued|claimed|delivered|acked|failed|expired|cancelled',
  actor             VARCHAR(24)     NOT NULL COMMENT 'user | device | system',
  actor_ip          VARBINARY(16)   NULL,
  detail            JSON            NULL,
  occurred_at       DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),

  PRIMARY KEY (id),
  KEY ix_command_events_command (command_id, occurred_at),
  KEY ix_command_events_retention (occurred_at),
  CONSTRAINT fk_command_events_command FOREIGN KEY (command_id) REFERENCES commands(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- =============================================================================
-- REMINDERS
-- =============================================================================

CREATE TABLE reminders (
  id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id         BINARY(16)      NOT NULL,
  user_id           BIGINT UNSIGNED NOT NULL,

  -- AES-256-GCM under the user's DEK: [12B nonce][ciphertext][16B tag].
  -- GCM replaces CBC so the ciphertext is authenticated (fixes S1-07).
  body_ciphertext   VARBINARY(4096) NOT NULL,
  body_dek_id       VARCHAR(32)     NOT NULL COMMENT 'Which DEK version encrypted this row; enables rekeying',

  -- The single source of truth for "when". UTC instant, always.
  due_at_utc        DATETIME(3)     NOT NULL,
  -- Kept alongside so a recurrence can be re-expanded correctly across DST
  -- and so the UI can show the time the user actually typed.
  due_local_time    TIME            NOT NULL,
  timezone          VARCHAR(64)     NOT NULL COMMENT 'IANA tz captured at creation',

  -- RFC 5545 RRULE replaces the five Recurrence_* columns (fixes S2-09).
  rrule             VARCHAR(255)    NULL COMMENT 'e.g. FREQ=WEEKLY;BYDAY=MO,WE;UNTIL=20270101T000000Z',
  recurrence_until  DATETIME(3)     NULL,

  is_completed      TINYINT(1) UNSIGNED NOT NULL DEFAULT 0,
  completed_at      DATETIME(3)     NULL,

  created_at        DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  updated_at        DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
  deleted_at        DATETIME(3)     NULL,

  PRIMARY KEY (id),
  UNIQUE KEY uq_reminders_public_id (public_id),
  -- The hot path: "this user's upcoming reminders"
  KEY ix_reminders_user_due (user_id, is_completed, due_at_utc),
  -- The scheduler path: "everything due in the next tick"
  KEY ix_reminders_due_sweep (due_at_utc, is_completed, deleted_at),
  CONSTRAINT fk_reminders_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- Materialised occurrences of recurring reminders. Expanding an RRULE on every
-- scheduler tick does not scale; a rolling 90-day horizon does, and it lets a
-- user complete or skip one occurrence without touching the series.
CREATE TABLE reminder_occurrences (
  id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  reminder_id       BIGINT UNSIGNED NOT NULL,
  occurs_at_utc     DATETIME(3)     NOT NULL,
  status            VARCHAR(16)     NOT NULL DEFAULT 'pending',
  notified_at       DATETIME(3)     NULL,
  completed_at      DATETIME(3)     NULL,

  PRIMARY KEY (id),
  UNIQUE KEY uq_reminder_occurrence (reminder_id, occurs_at_utc),
  KEY ix_reminder_occurrences_sweep (status, occurs_at_utc),
  CONSTRAINT fk_reminder_occurrences_reminder FOREIGN KEY (reminder_id) REFERENCES reminders(id) ON DELETE CASCADE,
  CONSTRAINT ck_reminder_occurrences_status CHECK (status IN ('pending','notified','completed','skipped'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- =============================================================================
-- WEBSITE / SUPPORT  (consolidates links + menupages; keeps feedback, mailing list)
-- =============================================================================

CREATE TABLE nav_items (
  id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  label             VARCHAR(128)    NOT NULL,
  url               VARCHAR(512)    NOT NULL,
  placement         VARCHAR(16)     NOT NULL COMMENT 'header | footer | both',
  sort_order        INT             NOT NULL DEFAULT 0,
  is_external       TINYINT(1) UNSIGNED NOT NULL DEFAULT 0,
  is_visible        TINYINT(1) UNSIGNED NOT NULL DEFAULT 1,

  PRIMARY KEY (id),
  KEY ix_nav_items_placement (placement, is_visible, sort_order),
  CONSTRAINT ck_nav_items_placement CHECK (placement IN ('header','footer','both'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


CREATE TABLE feedback (
  id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  user_id           BIGINT UNSIGNED NULL COMMENT 'NULL for anonymous submissions',
  name              VARCHAR(255)    NOT NULL DEFAULT '',
  email             VARCHAR(320)    NOT NULL DEFAULT '',
  body              TEXT            NOT NULL,
  rating            TINYINT UNSIGNED NULL COMMENT 'Was free text; now constrained 1-5',
  submitted_ip      VARBINARY(16)   NULL,
  created_at        DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),

  PRIMARY KEY (id),
  KEY ix_feedback_user (user_id),
  KEY ix_feedback_created (created_at),
  CONSTRAINT fk_feedback_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE SET NULL,
  CONSTRAINT ck_feedback_rating CHECK (rating IS NULL OR rating BETWEEN 1 AND 5)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- Consent-tracked, so an unsubscribe is provable. `users.is_marketing_opt_in`
-- covers registered users; this table covers standalone signups.
CREATE TABLE mailing_list (
  id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  email_normalised  VARCHAR(320)    NOT NULL,
  user_id           BIGINT UNSIGNED NULL,
  subscribed_at     DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  unsubscribed_at   DATETIME(3)     NULL,
  unsubscribe_token BINARY(32)      NOT NULL COMMENT 'SHA-256 of the one-click unsubscribe token',
  consent_source    VARCHAR(32)     NOT NULL DEFAULT 'website',

  PRIMARY KEY (id),
  UNIQUE KEY uq_mailing_list_email (email_normalised),
  UNIQUE KEY uq_mailing_list_token (unsubscribe_token),
  CONSTRAINT fk_mailing_list_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- =============================================================================
-- OPERATIONS
-- =============================================================================

-- Durable idempotency for state-changing endpoints. Hot lookups hit Valkey;
-- this table is the crash-safe record.
CREATE TABLE idempotency_keys (
  id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  scope             VARCHAR(64)     NOT NULL COMMENT 'route identifier',
  idempotency_key   VARCHAR(255)    NOT NULL,
  user_id           BIGINT UNSIGNED NOT NULL,
  request_hash      BINARY(32)      NOT NULL COMMENT 'Detects the same key reused with a different body',
  response_status   SMALLINT UNSIGNED NULL,
  response_body     JSON            NULL,
  created_at        DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  expires_at        DATETIME(3)     NOT NULL,

  PRIMARY KEY (id),
  UNIQUE KEY uq_idempotency (scope, user_id, idempotency_key),
  KEY ix_idempotency_expiry (expires_at),
  CONSTRAINT fk_idempotency_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- Security-relevant events that are not command executions: logins, failures,
-- token reuse, pairings, revocations, password changes.
CREATE TABLE security_events (
  id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  user_id           BIGINT UNSIGNED NULL,
  event             VARCHAR(48)     NOT NULL,
  outcome           VARCHAR(16)     NOT NULL COMMENT 'success | failure',
  source_ip         VARBINARY(16)   NULL,
  user_agent        VARCHAR(255)    NOT NULL DEFAULT '',
  detail            JSON            NULL,
  occurred_at       DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),

  PRIMARY KEY (id),
  KEY ix_security_events_user (user_id, occurred_at),
  KEY ix_security_events_event (event, occurred_at),
  CONSTRAINT fk_security_events_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE SET NULL,
  CONSTRAINT ck_security_events_outcome CHECK (outcome IN ('success','failure'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- =============================================================================
-- TABLES DELETED RELATIVE TO v1
--
--   apikeys              orphaned; superseded by refresh_tokens + device_credentials
--   requests             dead; superseded by pcnames.Request, now by commands
--   time                 dead; superseded by pcnames.Time, now by devices.last_seen_at
--   code                 three placeholder rows, no reader
--   links                duplicate of menupages; both merged into nav_items
--   menupages            merged into nav_items
--   verifications        replaced by auth_challenges (hashed, single-use)
--   verificationtypes    replaced by auth_challenges.purpose CHECK constraint
--   pcnames              split into devices + device_credentials + commands
--
-- COLUMNS DELETED
--   users.api_key                  replaced by the token model (ADR-0002)
--   users.Password (in users)      moved to user_credentials.password_hash
--   users.DateOfBirth              collected but never used; dropping reduces PII surface
--   reminders.Recurrence*  (x5)    replaced by reminders.rrule
-- =============================================================================
