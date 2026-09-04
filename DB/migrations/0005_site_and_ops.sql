-- =============================================================================
-- 0005 — Website/support tables and operational tables
--
-- `links` and `menupages` were duplicates of each other; both become nav_items.
-- `apikeys`, `requests`, `time` and `code` are dead and are not carried across.
-- =============================================================================

-- migrate:up

CREATE TABLE nav_items (
  id                    bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  label                 varchar(128) NOT NULL,
  url                   varchar(512) NOT NULL,
  placement             text         NOT NULL,
  sort_order            integer      NOT NULL DEFAULT 0,
  is_external           boolean      NOT NULL DEFAULT false,
  is_visible            boolean      NOT NULL DEFAULT true,

  CONSTRAINT ck_nav_items_placement CHECK (placement IN ('header','footer','both')),
  CONSTRAINT uq_nav_items_url UNIQUE (url, placement)
);

CREATE INDEX ix_nav_items_placement ON nav_items (placement, sort_order) WHERE is_visible;

CREATE TABLE feedback (
  id                    bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  user_id               bigint       NULL REFERENCES users(id) ON DELETE SET NULL,
  name                  varchar(255) NOT NULL DEFAULT '',
  email                 varchar(320) NOT NULL DEFAULT '',
  body                  text         NOT NULL,
  rating                smallint     NULL,
  submitted_ip          inet         NULL,
  created_at            timestamptz(3) NOT NULL DEFAULT now(),

  CONSTRAINT ck_feedback_rating CHECK (rating IS NULL OR rating BETWEEN 1 AND 5)
);

CREATE INDEX ix_feedback_user    ON feedback (user_id);
CREATE INDEX ix_feedback_created ON feedback (created_at DESC);

-- Consent-tracked, so an unsubscribe is provable.
CREATE TABLE mailing_list (
  id                    bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  email_normalised      varchar(320) NOT NULL,
  user_id               bigint       NULL REFERENCES users(id) ON DELETE SET NULL,
  subscribed_at         timestamptz(3) NOT NULL DEFAULT now(),
  unsubscribed_at       timestamptz(3) NULL,
  unsubscribe_token     bytea        NOT NULL,
  consent_source        varchar(32)  NOT NULL DEFAULT 'website',

  CONSTRAINT uq_mailing_list_email UNIQUE (email_normalised),
  CONSTRAINT uq_mailing_list_token UNIQUE (unsubscribe_token),
  CONSTRAINT ck_mailing_list_token CHECK (octet_length(unsubscribe_token) = 32)
);

-- Durable idempotency for state-changing endpoints. Hot lookups hit the cache;
-- this table is the crash-safe record.
CREATE TABLE idempotency_keys (
  id                    bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  scope                 varchar(64)  NOT NULL,
  idempotency_key       varchar(255) NOT NULL,
  user_id               bigint       NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  request_hash          bytea        NOT NULL,
  response_status       smallint     NULL,
  response_body         jsonb        NULL,
  created_at            timestamptz(3) NOT NULL DEFAULT now(),
  expires_at            timestamptz(3) NOT NULL,

  CONSTRAINT uq_idempotency UNIQUE (scope, user_id, idempotency_key),
  CONSTRAINT ck_idempotency_hash CHECK (octet_length(request_hash) = 32)
);

COMMENT ON COLUMN idempotency_keys.request_hash IS 'Detects the same key replayed with a different body (409)';

CREATE INDEX ix_idempotency_expiry ON idempotency_keys (expires_at);

-- migrate:down

DROP TABLE IF EXISTS idempotency_keys;
DROP TABLE IF EXISTS mailing_list;
DROP TABLE IF EXISTS feedback;
DROP TABLE IF EXISTS nav_items;
