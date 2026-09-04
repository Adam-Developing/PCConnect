-- =============================================================================
-- 0003 — Commands context  (append-only; replaces pcnames.Request / .Value)
--
-- Closes S2-03 (stale commands), S2-04 (a second command overwrote the first),
-- S2-05 (push and poll could not be reconciled).
-- =============================================================================

-- migrate:up

CREATE TABLE commands (
  id                    bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,

  -- Client-generated UUIDv7. This is what makes retry safe: a replayed issue
  -- with the same public_id returns the existing command, not a second shutdown.
  public_id             uuid         NOT NULL,

  device_id             bigint       NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
  issued_by_user_id     bigint       NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  issued_by_client      text         NOT NULL,

  command_type          text         NOT NULL,
  params                jsonb        NULL,

  -- Risk tier drives step-up authentication and the rate budget (ADR-0011).
  risk_tier             text         NOT NULL DEFAULT 'standard',
  step_up_verified_at   timestamptz(3) NULL,
  step_up_method        text         NULL,

  status                text         NOT NULL DEFAULT 'issued',

  issued_at             timestamptz(3) NOT NULL DEFAULT now(),
  -- The fix for S2-03. A command not delivered inside its window is never run.
  expires_at            timestamptz(3) NOT NULL,
  delivered_at          timestamptz(3) NULL,
  acked_at              timestamptz(3) NULL,
  terminal_at           timestamptz(3) NULL,

  result_code           varchar(32)  NULL,
  result_message        varchar(255) NULL,

  CONSTRAINT uq_commands_public_id UNIQUE (public_id),
  CONSTRAINT ck_commands_status CHECK (status IN
    ('issued','delivered','succeeded','failed','expired','cancelled')),
  CONSTRAINT ck_commands_type CHECK (command_type IN
    ('shutdown','restart','signout','lock','sleep','hibernate')),
  CONSTRAINT ck_commands_client CHECK (issued_by_client IN
    ('mobile','web','desktop','legacy_shim')),
  CONSTRAINT ck_commands_risk CHECK (risk_tier IN ('standard','destructive')),
  CONSTRAINT ck_commands_params CHECK (params IS NULL OR jsonb_typeof(params) = 'object'),
  CONSTRAINT ck_commands_ttl CHECK (expires_at > issued_at),
  -- A destructive command may not exist without a recorded step-up. Enforced by
  -- the database as well as the service, because this is the invariant that
  -- decides whether a stolen phone can power off a machine (ADR-0011).
  CONSTRAINT ck_commands_stepup CHECK (
    risk_tier <> 'destructive' OR step_up_verified_at IS NOT NULL)
);

COMMENT ON COLUMN commands.public_id   IS 'Client-generated UUIDv7; the idempotency key for command issue';
COMMENT ON COLUMN commands.params      IS 'Structured, e.g. {"delaySeconds":10}. Never a shell string (S1-13).';
COMMENT ON COLUMN commands.expires_at  IS 'Mandatory TTL. A command past this instant is never executed (S2-03).';

-- The hot path: "give me this device's undelivered, unexpired commands".
CREATE INDEX ix_commands_device_pending ON commands (device_id, status, expires_at);
CREATE INDEX ix_commands_user_recent    ON commands (issued_by_user_id, issued_at DESC);
-- The sweep path: only rows that can still transition are worth scanning.
CREATE INDEX ix_commands_expiry_sweep   ON commands (expires_at)
  WHERE status IN ('issued','delivered');

-- Audit trail. These are destructive actions on someone's computer; every
-- transition is recorded and retained for 90 days.
CREATE TABLE command_events (
  id                    bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  command_id            bigint       NOT NULL REFERENCES commands(id) ON DELETE CASCADE,
  event                 text         NOT NULL,
  actor                 text         NOT NULL,
  actor_ip              inet         NULL,
  detail                jsonb        NULL,
  occurred_at           timestamptz(3) NOT NULL DEFAULT now(),

  CONSTRAINT ck_command_events_event CHECK (event IN
    ('issued','claimed','delivered','acked','failed','expired','cancelled','rejected','stale_execution')),
  CONSTRAINT ck_command_events_actor CHECK (actor IN ('user','device','system'))
);

CREATE INDEX ix_command_events_command   ON command_events (command_id, occurred_at);
CREATE INDEX ix_command_events_retention ON command_events (occurred_at);

-- migrate:down

DROP TABLE IF EXISTS command_events;
DROP TABLE IF EXISTS commands;
