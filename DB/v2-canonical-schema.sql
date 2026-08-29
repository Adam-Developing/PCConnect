-- PCConnect v2 canonical PostgreSQL 18 schema.
-- Apply only through versioned migrations in implementation; this file is the
-- architecture baseline and must remain equivalent to the latest migration.

BEGIN;

CREATE EXTENSION IF NOT EXISTS citext;
CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TYPE platform_type AS ENUM ('windows', 'android', 'ios', 'macos', 'linux', 'web', 'unknown');
CREATE TYPE device_status AS ENUM ('online', 'offline', 'revoked');
CREATE TYPE device_capability AS ENUM ('lock', 'sleep', 'hibernate', 'sign_out', 'restart', 'shutdown', 'reminders');
CREATE TYPE command_type AS ENUM ('lock', 'sleep', 'hibernate', 'sign_out', 'restart', 'shutdown');
CREATE TYPE command_status AS ENUM ('queued', 'claimed', 'accepted', 'succeeded', 'failed', 'expired', 'cancelled');
CREATE TYPE command_failure_code AS ENUM ('no_interactive_session', 'unsupported', 'permission_denied', 'expired', 'local_replay', 'execution_failed');
CREATE TYPE reminder_target_mode AS ENUM ('all_devices', 'selected_devices');
CREATE TYPE reminder_delivery_status AS ENUM ('pending', 'available', 'displayed', 'dismissed', 'completed', 'expired');
CREATE TYPE enrollment_status AS ENUM ('pending', 'approved', 'exchanged', 'expired', 'denied');
CREATE TYPE token_state AS ENUM ('active', 'rotated', 'revoked', 'expired');
CREATE TYPE export_status AS ENUM ('queued', 'processing', 'ready', 'failed', 'expired');
CREATE TYPE deletion_status AS ENUM ('queued', 'processing', 'completed', 'failed');

CREATE TABLE users (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    username citext NOT NULL,
    email citext NOT NULL,
    display_name varchar(100) NOT NULL,
    date_of_birth date,
    marketing_opt_in boolean NOT NULL DEFAULT false,
    marketing_consent_at timestamptz,
    email_verified_at timestamptz,
    timezone varchar(100) NOT NULL DEFAULT 'Europe/London',
    timezone_assumed boolean NOT NULL DEFAULT false,
    account_state varchar(30) NOT NULL DEFAULT 'active'
        CHECK (account_state IN ('active', 'reset_required', 'deletion_pending', 'deleted', 'disabled')),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    deleted_at timestamptz,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT users_username_unique UNIQUE (username),
    CONSTRAINT users_email_unique UNIQUE (email),
    CONSTRAINT users_marketing_consent_consistent CHECK (marketing_opt_in = (marketing_consent_at IS NOT NULL)),
    CONSTRAINT users_deleted_state_consistent CHECK ((account_state = 'deleted') = (deleted_at IS NOT NULL))
);

CREATE TABLE password_credentials (
    user_id uuid PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    password_hash text,
    hash_algorithm varchar(30),
    hash_parameters jsonb,
    legacy_sha256 char(64),
    migrated_at timestamptz,
    changed_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT password_credential_present CHECK (password_hash IS NOT NULL OR legacy_sha256 IS NOT NULL),
    CONSTRAINT legacy_sha256_hex CHECK (legacy_sha256 IS NULL OR legacy_sha256 ~ '^[0-9a-fA-F]{64}$'),
    CONSTRAINT argon_metadata_consistent CHECK (
        (password_hash IS NULL AND hash_algorithm IS NULL AND hash_parameters IS NULL)
        OR (password_hash IS NOT NULL AND hash_algorithm = 'argon2id' AND hash_parameters IS NOT NULL)
    )
);

CREATE TABLE passkeys (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    credential_id bytea NOT NULL,
    public_key bytea NOT NULL,
    public_key_algorithm integer NOT NULL,
    sign_count bigint NOT NULL DEFAULT 0 CHECK (sign_count >= 0),
    transports text[] NOT NULL DEFAULT '{}',
    aaguid uuid,
    display_name varchar(100) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    last_used_at timestamptz,
    revoked_at timestamptz,
    CONSTRAINT passkeys_credential_id_unique UNIQUE (credential_id),
    CONSTRAINT passkeys_user_name_unique UNIQUE (user_id, display_name)
);

CREATE TABLE sessions (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    family_id uuid NOT NULL DEFAULT uuidv7(),
    platform platform_type NOT NULL,
    client_name varchar(100) NOT NULL,
    client_version varchar(40) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    last_used_at timestamptz NOT NULL DEFAULT now(),
    sliding_expires_at timestamptz NOT NULL,
    absolute_expires_at timestamptz NOT NULL,
    revoked_at timestamptz,
    revoked_reason varchar(50),
    CONSTRAINT sessions_family_unique UNIQUE (family_id),
    CONSTRAINT sessions_expiry_order CHECK (sliding_expires_at <= absolute_expires_at),
    CONSTRAINT sessions_revoke_consistent CHECK ((revoked_at IS NULL) = (revoked_reason IS NULL))
);

CREATE INDEX sessions_user_active_idx ON sessions (user_id, last_used_at DESC) WHERE revoked_at IS NULL;

CREATE TABLE session_refresh_tokens (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    session_id uuid NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    token_hash bytea NOT NULL,
    state token_state NOT NULL DEFAULT 'active',
    issued_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    replaced_by_id uuid REFERENCES session_refresh_tokens(id),
    consumed_at timestamptz,
    CONSTRAINT session_refresh_token_hash_unique UNIQUE (token_hash),
    CONSTRAINT session_refresh_expiry CHECK (expires_at > issued_at)
);

CREATE UNIQUE INDEX session_one_active_refresh_idx ON session_refresh_tokens (session_id) WHERE state = 'active';

CREATE TABLE access_tokens (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    session_id uuid REFERENCES sessions(id) ON DELETE CASCADE,
    device_id uuid,
    token_hash bytea NOT NULL UNIQUE,
    issued_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz,
    CONSTRAINT access_token_subject_one CHECK ((session_id IS NOT NULL)::integer + (device_id IS NOT NULL)::integer = 1),
    CONSTRAINT access_token_ten_minute_max CHECK (expires_at <= issued_at + interval '10 minutes' AND expires_at > issued_at)
);

CREATE TABLE authentication_throttles (
    account_hash bytea NOT NULL,
    network_address inet NOT NULL,
    window_started_at timestamptz NOT NULL,
    attempts integer NOT NULL CHECK (attempts > 0),
    blocked_until timestamptz,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (account_hash, network_address)
);

CREATE INDEX authentication_throttles_cleanup_idx ON authentication_throttles(updated_at);

CREATE INDEX access_tokens_active_hash_idx ON access_tokens (token_hash, expires_at) WHERE revoked_at IS NULL;

CREATE TABLE email_tokens (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    purpose varchar(30) NOT NULL CHECK (purpose IN ('verify_email', 'reset_password', 'confirm_email_change')),
    token_hash bytea NOT NULL UNIQUE,
    pending_email citext,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    consumed_at timestamptz,
    CONSTRAINT email_token_expiry CHECK (expires_at > created_at)
);

CREATE TABLE email_outbox (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    user_id uuid REFERENCES users(id) ON DELETE SET NULL,
    template varchar(50) NOT NULL CHECK (template IN ('verify_email', 'reset_password', 'confirm_email_change')),
    payload_ciphertext bytea NOT NULL,
    payload_nonce bytea NOT NULL CHECK (octet_length(payload_nonce) = 12),
    payload_tag bytea NOT NULL CHECK (octet_length(payload_tag) = 16),
    encryption_key_id varchar(100) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    available_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    claimed_by uuid,
    claimed_until timestamptz,
    sent_at timestamptz,
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    last_error_code varchar(100),
    CONSTRAINT email_outbox_expiry CHECK (expires_at > created_at)
);

CREATE INDEX email_outbox_pending_idx ON email_outbox (available_at, created_at) WHERE sent_at IS NULL;

CREATE TABLE webauthn_challenges (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    user_id uuid REFERENCES users(id) ON DELETE CASCADE,
    session_id uuid REFERENCES sessions(id) ON DELETE CASCADE,
    purpose varchar(30) NOT NULL CHECK (purpose IN ('register', 'authenticate', 'step_up')),
    challenge_hash bytea NOT NULL UNIQUE,
    intent jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    consumed_at timestamptz,
    CONSTRAINT webauthn_challenge_five_minute_max CHECK (expires_at <= created_at + interval '5 minutes' AND expires_at > created_at)
);

CREATE TABLE step_up_grants (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    session_id uuid NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    grant_hash bytea NOT NULL UNIQUE,
    authentication_method varchar(20) NOT NULL CHECK (authentication_method IN ('passkey', 'password')),
    intent varchar(30) NOT NULL CHECK (intent IN ('command', 'account_delete', 'data_export', 'device_revoke', 'security_change')),
    target_device_id uuid,
    command command_type,
    idempotency_key uuid NOT NULL,
    authenticated_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    consumed_at timestamptz,
    CONSTRAINT step_up_five_minute_max CHECK (expires_at <= created_at + interval '5 minutes' AND expires_at > created_at),
    CONSTRAINT step_up_command_binding CHECK (
        (intent = 'command' AND target_device_id IS NOT NULL AND command IS NOT NULL)
        OR (intent <> 'command' AND command IS NULL)
    )
);

CREATE TABLE devices (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    platform platform_type NOT NULL,
    display_name varchar(100) NOT NULL,
    display_name_normalized varchar(100) NOT NULL,
    agent_version varchar(40) NOT NULL,
    protocol_version integer NOT NULL CHECK (protocol_version >= 2),
    timezone varchar(100),
    capabilities device_capability[] NOT NULL DEFAULT '{}',
    status device_status NOT NULL DEFAULT 'offline',
    enrolled_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz,
    revoked_at timestamptz,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT devices_user_name_unique UNIQUE (user_id, display_name_normalized),
    CONSTRAINT devices_revoked_consistent CHECK ((status = 'revoked') = (revoked_at IS NOT NULL))
);

ALTER TABLE access_tokens ADD CONSTRAINT access_tokens_device_fk
    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE;
ALTER TABLE step_up_grants ADD CONSTRAINT step_up_target_device_fk
    FOREIGN KEY (target_device_id) REFERENCES devices(id) ON DELETE CASCADE;

CREATE INDEX devices_user_status_idx ON devices (user_id, status, display_name_normalized);
CREATE INDEX devices_presence_idx ON devices (status, last_seen_at) WHERE status <> 'revoked';

CREATE TABLE device_credentials (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    family_id uuid NOT NULL DEFAULT uuidv7(),
    created_at timestamptz NOT NULL DEFAULT now(),
    last_used_at timestamptz NOT NULL DEFAULT now(),
    sliding_expires_at timestamptz NOT NULL,
    absolute_expires_at timestamptz NOT NULL,
    revoked_at timestamptz,
    revoked_reason varchar(50),
    CONSTRAINT device_credential_family_unique UNIQUE (family_id),
    CONSTRAINT device_credential_expiry_order CHECK (sliding_expires_at <= absolute_expires_at),
    CONSTRAINT device_credential_revoke_consistent CHECK ((revoked_at IS NULL) = (revoked_reason IS NULL))
);

CREATE UNIQUE INDEX device_one_active_credential_idx ON device_credentials (device_id) WHERE revoked_at IS NULL;

CREATE TABLE device_refresh_tokens (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    credential_id uuid NOT NULL REFERENCES device_credentials(id) ON DELETE CASCADE,
    token_hash bytea NOT NULL UNIQUE,
    state token_state NOT NULL DEFAULT 'active',
    issued_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    replaced_by_id uuid REFERENCES device_refresh_tokens(id),
    consumed_at timestamptz
);

CREATE UNIQUE INDEX device_one_active_refresh_idx ON device_refresh_tokens (credential_id) WHERE state = 'active';

CREATE TABLE device_enrollments (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    device_code_hash bytea NOT NULL UNIQUE,
    user_code char(8) NOT NULL,
    requested_platform platform_type NOT NULL,
    requested_display_name varchar(100) NOT NULL,
    requested_agent_version varchar(40) NOT NULL,
    requested_protocol_version integer NOT NULL CHECK (requested_protocol_version >= 2),
    requested_timezone varchar(100),
    requested_capabilities device_capability[] NOT NULL DEFAULT '{}',
    status enrollment_status NOT NULL DEFAULT 'pending',
    poll_interval_seconds integer NOT NULL DEFAULT 5 CHECK (poll_interval_seconds >= 5),
    last_polled_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    approved_by_user_id uuid REFERENCES users(id) ON DELETE SET NULL,
    approved_at timestamptz,
    exchanged_device_id uuid REFERENCES devices(id) ON DELETE SET NULL,
    exchanged_at timestamptz,
    CONSTRAINT enrollment_user_code_unique UNIQUE (user_code),
    CONSTRAINT enrollment_ten_minute_max CHECK (expires_at <= created_at + interval '10 minutes' AND expires_at > created_at)
);

CREATE TABLE device_authorized_sids (
    device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    windows_sid varchar(184) NOT NULL,
    display_label varchar(100),
    authorized_at timestamptz NOT NULL DEFAULT now(),
    revoked_at timestamptz,
    PRIMARY KEY (device_id, windows_sid),
    CONSTRAINT windows_sid_shape CHECK (windows_sid LIKE 'S-1-%')
);

CREATE TABLE device_sid_candidates (
    device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    windows_sid varchar(184) NOT NULL,
    display_label varchar(100),
    observed_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    PRIMARY KEY (device_id, windows_sid),
    CONSTRAINT candidate_windows_sid_shape CHECK (windows_sid LIKE 'S-1-%'),
    CONSTRAINT candidate_sid_expiry CHECK (expires_at > observed_at AND expires_at <= observed_at + interval '1 day')
);

CREATE INDEX device_sid_candidates_expiry_idx ON device_sid_candidates(expires_at);

CREATE TABLE commands (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    target_device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    actor_session_id uuid REFERENCES sessions(id) ON DELETE SET NULL,
    actor_legacy_credential_id uuid,
    type command_type NOT NULL,
    status command_status NOT NULL DEFAULT 'queued',
    idempotency_key uuid NOT NULL,
    step_up_grant_id uuid REFERENCES step_up_grants(id) ON DELETE SET NULL,
    issued_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    claimed_by_instance_id uuid,
    claimed_until timestamptz,
    accepted_at timestamptz,
    finished_at timestamptz,
    failure_code command_failure_code,
    updated_at timestamptz NOT NULL DEFAULT now(),
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT commands_actor_session_idempotency_unique UNIQUE (actor_session_id, idempotency_key),
    CONSTRAINT commands_exactly_one_actor CHECK ((actor_session_id IS NULL) <> (actor_legacy_credential_id IS NULL)),
    CONSTRAINT commands_expiry_window CHECK (expires_at > issued_at AND expires_at <= issued_at + interval '5 minutes'),
    CONSTRAINT commands_claim_consistent CHECK ((claimed_by_instance_id IS NULL) = (claimed_until IS NULL)),
    CONSTRAINT commands_failure_consistent CHECK ((status = 'failed') = (failure_code IS NOT NULL))
);

CREATE INDEX commands_device_pending_idx ON commands (target_device_id, issued_at) WHERE status IN ('queued', 'claimed');
CREATE INDEX commands_user_recent_idx ON commands (user_id, issued_at DESC);
CREATE INDEX commands_user_updated_idx ON commands (user_id, updated_at DESC, id DESC);
CREATE INDEX commands_expiry_idx ON commands (expires_at) WHERE status IN ('queued', 'claimed');

CREATE TABLE command_events (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    command_id uuid NOT NULL REFERENCES commands(id) ON DELETE CASCADE,
    sequence integer NOT NULL CHECK (sequence > 0),
    from_status command_status,
    to_status command_status NOT NULL,
    actor_kind varchar(20) NOT NULL CHECK (actor_kind IN ('controller', 'agent', 'worker', 'compatibility', 'system')),
    actor_id uuid,
    failure_code command_failure_code,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    metadata jsonb NOT NULL DEFAULT '{}',
    CONSTRAINT command_events_sequence_unique UNIQUE (command_id, sequence)
);

CREATE TABLE reminders (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    creation_session_id uuid REFERENCES sessions(id) ON DELETE SET NULL,
    creation_legacy_credential_id uuid,
    idempotency_key uuid,
    target_mode reminder_target_mode NOT NULL,
    timezone varchar(100) NOT NULL,
    timezone_assumed boolean NOT NULL DEFAULT false,
    local_start timestamp without time zone NOT NULL,
    recurrence_rule varchar(1000),
    text_ciphertext bytea NOT NULL,
    text_nonce bytea NOT NULL CHECK (octet_length(text_nonce) = 12),
    text_tag bytea NOT NULL CHECK (octet_length(text_tag) = 16),
    wrapped_data_key bytea NOT NULL,
    wrapping_key_id varchar(100) NOT NULL,
    text_aad_version smallint NOT NULL DEFAULT 1 CHECK (text_aad_version = 1),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    deleted_at timestamptz,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT reminders_creation_actor_consistent CHECK (
        (creation_session_id IS NULL AND creation_legacy_credential_id IS NULL AND idempotency_key IS NULL)
        OR (num_nonnulls(creation_session_id, creation_legacy_credential_id) = 1 AND idempotency_key IS NOT NULL)
    ),
    CONSTRAINT reminders_creation_idempotency_unique UNIQUE (creation_session_id, idempotency_key),
    CONSTRAINT reminders_rrule_shape CHECK (recurrence_rule IS NULL OR recurrence_rule LIKE 'FREQ=%')
);

CREATE INDEX reminders_user_active_idx ON reminders (user_id, updated_at DESC) WHERE deleted_at IS NULL;
CREATE UNIQUE INDEX reminders_legacy_creation_idempotency_idx
    ON reminders (creation_legacy_credential_id, idempotency_key)
    WHERE creation_legacy_credential_id IS NOT NULL;

CREATE TABLE reminder_targets (
    reminder_id uuid NOT NULL REFERENCES reminders(id) ON DELETE CASCADE,
    device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (reminder_id, device_id)
);

CREATE TABLE reminder_occurrences (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    reminder_id uuid NOT NULL REFERENCES reminders(id) ON DELETE CASCADE,
    occurrence_at timestamptz NOT NULL,
    local_occurrence timestamp without time zone NOT NULL,
    timezone_offset_seconds integer NOT NULL,
    generated_at timestamptz NOT NULL DEFAULT now(),
    cancelled_at timestamptz,
    CONSTRAINT reminder_occurrence_unique UNIQUE (reminder_id, occurrence_at)
);

CREATE INDEX reminder_occurrences_due_idx ON reminder_occurrences (occurrence_at) WHERE cancelled_at IS NULL;

CREATE TABLE reminder_deliveries (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    legacy_numeric_id bigint GENERATED BY DEFAULT AS IDENTITY UNIQUE,
    occurrence_id uuid NOT NULL REFERENCES reminder_occurrences(id) ON DELETE CASCADE,
    device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    status reminder_delivery_status NOT NULL DEFAULT 'pending',
    available_at timestamptz,
    acknowledged_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT reminder_delivery_unique UNIQUE (occurrence_id, device_id),
    CONSTRAINT reminder_ack_consistent CHECK ((status IN ('displayed', 'dismissed', 'completed')) = (acknowledged_at IS NOT NULL))
);

CREATE INDEX reminder_deliveries_device_pending_idx ON reminder_deliveries (device_id, created_at) WHERE status IN ('pending', 'available');

CREATE TABLE outbox_messages (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    event_type varchar(100) NOT NULL,
    aggregate_type varchar(50) NOT NULL,
    aggregate_id uuid NOT NULL,
    aggregate_version bigint NOT NULL CHECK (aggregate_version > 0),
    payload jsonb NOT NULL,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    available_at timestamptz NOT NULL DEFAULT now(),
    claimed_until timestamptz,
    claimed_by uuid,
    published_at timestamptz,
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    last_error_code varchar(100)
);

CREATE INDEX outbox_pending_idx ON outbox_messages (available_at, occurred_at) WHERE published_at IS NULL;

CREATE TABLE audit_events (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    event_type varchar(100) NOT NULL,
    user_id uuid REFERENCES users(id) ON DELETE SET NULL,
    actor_kind varchar(30) NOT NULL CHECK (actor_kind IN ('user', 'device', 'compatibility', 'worker', 'system')),
    actor_id uuid,
    target_type varchar(50),
    target_id uuid,
    outcome varchar(20) NOT NULL CHECK (outcome IN ('success', 'denied', 'failed')),
    correlation_id varchar(100) NOT NULL,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    metadata jsonb NOT NULL DEFAULT '{}'
);

CREATE INDEX audit_events_user_time_idx ON audit_events (user_id, occurred_at DESC);
CREATE INDEX audit_events_security_idx ON audit_events (event_type, occurred_at DESC);

CREATE TABLE legacy_id_map (
    source_system varchar(50) NOT NULL,
    entity_type varchar(50) NOT NULL,
    legacy_id varchar(255) NOT NULL,
    v2_id uuid NOT NULL,
    source_row_checksum char(64) NOT NULL CHECK (source_row_checksum ~ '^[0-9a-f]{64}$'),
    imported_at timestamptz NOT NULL DEFAULT now(),
    last_reconciled_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (source_system, entity_type, legacy_id),
    CONSTRAINT legacy_map_v2_unique UNIQUE (source_system, entity_type, v2_id)
);

CREATE TABLE legacy_compat_credentials (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    credential_hash bytea NOT NULL UNIQUE,
    permitted_routes text[] NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz,
    CONSTRAINT legacy_compat_expiry_required CHECK (expires_at > created_at)
);

ALTER TABLE commands ADD CONSTRAINT commands_legacy_actor_fk
    FOREIGN KEY (actor_legacy_credential_id) REFERENCES legacy_compat_credentials(id)
    ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
CREATE UNIQUE INDEX commands_actor_legacy_idempotency_unique
    ON commands(actor_legacy_credential_id,idempotency_key) WHERE actor_legacy_credential_id IS NOT NULL;
ALTER TABLE reminders ADD CONSTRAINT reminders_legacy_actor_fk
    FOREIGN KEY (creation_legacy_credential_id) REFERENCES legacy_compat_credentials(id)
    ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;

CREATE TABLE data_export_jobs (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    status export_status NOT NULL DEFAULT 'queued',
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    claimed_at timestamptz,
    storage_reference text,
    failure_code varchar(100),
    CONSTRAINT export_expiry CHECK (expires_at > created_at)
);

CREATE TABLE account_deletion_jobs (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    user_id uuid REFERENCES users(id) ON DELETE SET NULL,
    status deletion_status NOT NULL DEFAULT 'queued',
    requested_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    failure_code varchar(100)
);

CREATE TABLE deletion_tombstones (
    subject_digest bytea PRIMARY KEY,
    deleted_at timestamptz NOT NULL,
    deletion_job_id uuid NOT NULL UNIQUE REFERENCES account_deletion_jobs(id) ON DELETE RESTRICT,
    restore_replay_version integer NOT NULL DEFAULT 1
);

CREATE FUNCTION reject_immutable_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% is append-only', TG_TABLE_NAME;
END;
$$;

CREATE TRIGGER command_events_immutable
    BEFORE UPDATE OR DELETE ON command_events
    FOR EACH ROW EXECUTE FUNCTION reject_immutable_change();
CREATE TRIGGER audit_events_immutable
    BEFORE UPDATE OR DELETE ON audit_events
    FOR EACH ROW EXECUTE FUNCTION reject_immutable_change();
CREATE TRIGGER deletion_tombstones_immutable
    BEFORE UPDATE OR DELETE ON deletion_tombstones
    FOR EACH ROW EXECUTE FUNCTION reject_immutable_change();

CREATE FUNCTION validate_command_transition() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status = OLD.status THEN
        RETURN NEW;
    END IF;

    IF NOT (
        (OLD.status = 'queued' AND NEW.status IN ('claimed', 'expired', 'cancelled')) OR
        (OLD.status = 'claimed' AND NEW.status IN ('queued', 'accepted', 'failed', 'expired')) OR
        (OLD.status = 'accepted' AND NEW.status IN ('succeeded', 'failed'))
    ) THEN
        RAISE EXCEPTION 'illegal command transition: % -> %', OLD.status, NEW.status;
    END IF;

    NEW.row_version := OLD.row_version + 1;
    NEW.updated_at := now();
    RETURN NEW;
END;
$$;

CREATE TRIGGER commands_state_machine
    BEFORE UPDATE OF status ON commands
    FOR EACH ROW EXECUTE FUNCTION validate_command_transition();

COMMENT ON COLUMN password_credentials.legacy_sha256 IS 'Migration-only verifier. Never accepted as a v2 request credential and cleared after upgrade/reset.';
COMMENT ON COLUMN legacy_compat_credentials.expires_at IS 'Immutable migration sunset, exactly 60 days after production cutover.';
COMMENT ON TABLE outbox_messages IS 'Durable notification source; SignalR events may be duplicated or lost after publication.';
COMMENT ON TABLE command_events IS 'Immutable command transition history; metadata is allowlisted and contains no credential or reminder plaintext.';

COMMIT;
