# Architecture decisions

These compact ADRs are binding. A superseding decision must name the replaced
ADR, migration impact and contract versions affected.

## ADR-001 — Modular monolith with API and worker processes

**Decision:** ASP.NET Core/.NET 10 hosts a modular monolith. HTTP/SignalR and
background work are separate processes built from the same solution.

**Why:** one VPS does not justify distributed-service failure modes, while
module/table ownership and an outbox preserve a later extraction path.

**Rejected:** restoring incomplete PHP; restoring the in-memory Node gateway;
microservices before operational scale requires them.

## ADR-002 — PostgreSQL is authoritative; realtime is advisory

**Decision:** every state change commits to PostgreSQL before an outbox event is
published. SignalR events contain identifiers and versions only.

**Consequence:** clients must implement cursor catch-up and idempotency; a lost
hub message changes latency, not correctness.

## ADR-003 — Opaque, rotating session credentials

**Decision:** use short opaque access tokens and rotating refresh-token families
with reuse detection. Device families are scoped to one device.

**Rejected:** account-wide permanent API keys and client-stored password hashes.

## ADR-004 — Optional passkeys, mandatory server step-up for high risk

**Decision:** password login remains available after secure migration; passkeys
are optional for normal login. High-risk commands and account/security changes
require a recent server-verified WebAuthn or password step-up bound to intent.

## ADR-005 — Durable command ledger

**Decision:** append-only command events plus a current projection replace the
single mutable pending-request string. Claims, leases, expiry and idempotency are
server-enforced.

## ADR-006 — Windows service plus per-user companion

**Decision:** the always-on service owns cloud connectivity and machine actions;
the companion owns interactive-session actions and reminder UI. They communicate
over a versioned, ACL-protected named pipe.

**Rejected:** a tray-only agent that cannot operate before sign-in; a privileged
UI process; arbitrary command forwarding.

## ADR-007 — Native Android, contract-driven future clients

**Decision:** implement Android in Kotlin/Compose and represent platform support
through capabilities. Do not share UI code merely to anticipate iOS.

## ADR-008 — Envelope encryption for reminders

**Decision:** server-side AES-256-GCM envelope encryption enables multi-device
sync, recurrence and recovery while limiting database/back-up disclosure.

**Rejected:** API-key-derived encryption, plaintext application fields and v2
end-to-end encryption without a recoverable multi-device key design.

## ADR-009 — Strangler migration with a hard sunset

**Decision:** after a final live-data import, the v2 database becomes the system
of record and legacy paths translate into it for 60 days. Day 45 disables legacy
command creation; day 60 removes legacy credentials/routes.

**Rejected:** indefinite dual-write, production migration from the repository
dump, and a big-bang fresh-account reset.

## ADR-010 — Canonical domains

**Decision:** `pcconnect.adamdeveloping.co.uk` is the WebAuthn RP ID and
`api.pcconnect.adamdeveloping.co.uk` is the API origin. The old domain is only a
compatibility edge and cannot register passkeys.

## ADR-011 — UTC storage with explicit scheduling timezone

**Decision:** instants use UTC `timestamptz`; recurring reminder intent retains
an IANA timezone and local start. Legacy naive values assume `Europe/London` and
are marked for user confirmation.

## ADR-012 — Expand/contract releases on one VPS

**Decision:** Caddy switches blue/green stateless API slots. Database migrations
are additive until every running/revertible image no longer uses the old shape.
After write cutover, rollback is application rollback/roll-forward, not database
restoration over accepted writes.

## ADR-013 — Separate reminder text and key-wrap authentication contexts

**Decision:** reminder text AES-GCM associated data contains a format version,
reminder ID and owner ID. The AES-GCM operation that wraps the per-row data key
uses the wrapping-key ID as its associated data. `text_aad_version` is persisted
with the reminder.

**Why:** the original baseline required the text associated data to contain the
wrapping-key ID while also requiring master-key rotation to rewrap only the data
key. Those requirements are cryptographically incompatible: changing the key
ID changes the text associated data and invalidates its tag. Separate contexts
preserve ownership binding and key-version substitution protection while making
rewrap-only rotation possible.

## ADR-014 — Persist reminder-creation idempotency

**Decision:** v2-created reminders persist `creation_session_id` and
`idempotency_key` as a unique pair. The pair is nullable only for imported
legacy reminders.

**Why:** `openapi-v2.json` requires `Idempotency-Key` on reminder creation, but
the original schema had no durable deduplication key. Relying on an in-memory or
cache-only key would permit duplicate reminders after failover or expiry.

## ADR-015 — Bind passkey authentication to a client descriptor

**Decision:** `PasskeyAuthenticationOptionsRequest` requires a
`ClientDescriptor`. The descriptor is persisted with the five-minute ceremony
state and used if the assertion succeeds.

**Why:** the original contract returned a session token pair from passkey
authentication but supplied no platform, client name or version, even though
all session columns are required and the architecture relies on session
inventory and revocation. Guessing from `User-Agent` would be lossy and would
break non-browser native clients. This is a pre-release corrective contract
change; generated clients must be regenerated together.

## ADR-016 — Encrypt transactional email hand-off in PostgreSQL

**Decision:** verification, reset and email-change tokens are committed with an
encrypted `email_outbox` message in the same transaction that creates the token.
The worker claims mail with a lease and sends through configured SMTP. Recipient
and plaintext token are encrypted under a dedicated versioned key and are never
written to logs.

**Why:** the canonical model previously created hashed tokens but had no durable
way to hand the corresponding plaintext token to a mail provider. Sending before
commit could deliver unusable tokens; sending after commit without an outbox
could lose mail on process failure. The short-lived encrypted outbox closes both
failure windows without reusing reminder or token-hashing keys.

## ADR-017 — Add authoritative agent reminder recovery

**Decision:** agents list available reminder deliveries through a cursor-based
REST resource. `ReminderChanged` remains an advisory hint only.

**Why:** the original realtime contract required REST catch-up after reconnect,
but OpenAPI exposed only delivery acknowledgement. A lost SignalR hint therefore
made an otherwise durable reminder undiscoverable.

## ADR-018 — Retain deletion job completion without retaining identity

**Decision:** `account_deletion_jobs.user_id` becomes null when the user row is
deleted, while the completed job and its keyed, non-reversible tombstone remain.
The tombstone references the job.

**Why:** the original `ON DELETE CASCADE` relationship deleted the job at the
same moment as the account, making the required completed status impossible to
record or prove. `ON DELETE SET NULL` preserves operational evidence without
retaining a recoverable user identifier.

## ADR-019 — Legacy command creation cannot bypass step-up

**Decision:** during days 0–44 the compatibility controller may create only the
idempotent `lock` command. Legacy attempts to create `sleep`, `hibernate`,
`sign_out`, `restart`, or `shutdown` are denied with `migration_required` from
day 0. All legacy controller command creation ends at day 45; legacy agents may
continue consuming already-authorized v2 commands until day 60. Compatibility
commands identify their legacy credential as the actor and have their own
durable idempotency constraint; they do not mint a v2 session.

**Why:** ADR-004 requires a server-verified step-up grant bound to session,
device, command type and idempotency key. Existing legacy clients have neither
the ceremony nor a v2 session and cannot be retrofitted server-side without
turning possession of a long-lived API key into an authorization bypass. The
earlier phrase “legacy controllers cannot create commands at day 45” incorrectly
implied that every command remained available before then. Restricting the
transition to lock preserves the only safe, idempotent operation while making
the security boundary explicit.

## ADR-020 — Explicit Windows SID candidate approval

**Decision:** after verifying a companion process token, PID, active session and
claimed SID locally, the device agent submits a short-lived SID candidate. The
account owner reviews it through a controller and authorizes it with an
intent-bound `security_change` step-up grant. Only then may the service send
interactive commands or reminder plaintext to that SID. Revocation uses the
same step-up boundary. Candidate and authorization APIs are device-owned and
never accept a SID as proof of identity.

**Why:** the baseline correctly said device enrollment must not implicitly
authorize a Windows SID and included `device_authorized_sids`, but supplied no
state or protocol for reaching an authorized row. Automatically trusting the
first interactive user would disclose reminders and permit session actions on
a shared computer. The candidate workflow fills that authorization gap without
elevating the companion or trusting payload claims.

## ADR-021 — Preserve legacy reminder writes without granting a v2 session

**Decision:** through day 59, an imported legacy credential with the explicit
`reminder_create` scope may create an encrypted, all-device, one-shot reminder.
The adapter accepts only the historical date/time/text form, interprets the
wall-clock value in the owner's stored IANA time zone, marks the time zone as
assumed, records the legacy credential as creator and persists a five-second
content-bound idempotency key. Legacy reminder polling and completion operate on
durable v2 deliveries; a generated numeric alias is exposed only at the adapter
boundary. Day 60 revokes the route with all other legacy access.

**Why:** reminder creation is useful non-destructive legacy functionality and
the strangler plan says the adapter translates legacy writes rather than writing
old tables. Returning `410` from day 0 would silently narrow that promise and
unnecessarily lose functionality. A separate creator field and idempotency
constraint preserve auditability without minting a session or weakening the v2
controller contract. The ambiguous historical local timestamp remains visibly
marked for user review after migration.

## ADR-022 — Bind Android passkeys to the signed application

**Decision:** Caddy serves `/.well-known/assetlinks.json` from the WebAuthn RP
host using an explicitly configured Android application ID and signing
certificate SHA-256 fingerprint. The API accepts both the owned HTTPS origin and
the corresponding `android:apk-key-hash:` native origin. Preflight validates the
public identifier formats, staging smoke verifies the association document, and
the protected release derives both fingerprints from the actual release
certificate and fails on mismatch. Android's API base URL is a validated HTTPS
build input so a staging-signed client targets staging without source changes.

**Why:** the earlier architecture configured only an HTTPS WebAuthn origin and
did not publish Digital Asset Links. Android Credential Manager assertions are
origin-bound to the application signing certificate, so an otherwise correct
server ceremony would fail on a real device—or be weakened by accepting an
unverified native origin. The certificate fingerprint is public configuration;
the signing key remains confined to protected release tooling.

## ADR-023 — Complete email account actions through verified Android links

**Decision:** verification, password-reset and device-enrollment URLs use the WebAuthn RP
host and place the one-time token in the URL fragment. The signed Android app
claims only `/verify-email` and `/reset-password` through the same verified
Digital Asset Links relationship, consumes verification tokens online, and
shows the reset-password form before revoking all sessions. The enrollment link
opens the controller's device-code approval surface. Caddy serves a
token-agnostic fallback page when the app is absent; it never reflects or logs
the fragment. The controller also exposes registration and generic-response
forgot-password flows.

**Why:** the API and encrypted email outbox existed, but the generated links
previously targeted routes with no owning UI, making verification and recovery
unusable. Query-string tokens would also be more likely to enter edge logs and
referrer data. A fragment reaches the verified native application without being
sent in the HTTP request, while the API remains the only authority that can
consume it.
