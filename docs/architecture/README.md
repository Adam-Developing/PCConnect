# PCConnect — Modernisation & Migration Architecture

> Complete architecture for moving PCConnect from its current state — three parallel API
> generations, four clients, a schema with no timezone and a credential that doubles as an
> encryption key — to a single secure, contract-driven, operable system, **without breaking the
> installed clients that are carrying production traffic today.**

**Status:** Implemented · **Version:** 2.0 · **Date:** 2026-09-02

> **The system described here is built.** Four decisions changed during
> implementation — the runtime, the database, the realtime transport and both
> clients — and two capabilities were added that no ADR covered. Every one of them
> is recorded in an ADR and summarised in
> [09 — Implementation notes](09-implementation-notes.md). Read that before
> trusting a technology name in 01 §5.
**Assessed against:** branch `Mobile-App` (`0b47ea2`), cross-referenced with `main` (`e3a6c01`)
and `PCConnect/main` (`17bd411`)

---

## Read this first

If you read only one thing, read **[Phase 0 of the migration plan](07-migration-plan.md#phase-0--contain)**.

`api/db_config.php` holds the live production MySQL credentials in plaintext. It is **untracked but
not gitignored** — one `git add -A` from being published permanently. The host is a public IP, which
suggests the database is directly reachable from the internet. `DB/pcconnect.sql` sits alongside it
with roughly ten thousand rows of real user PII. None of the rest of this architecture matters until
that is contained.

---

## The documents

| # | Document | What it answers |
|---|---|---|
| 00 | [Current State Assessment](00-current-state.md) | What exists, what is broken, and a severity-ranked findings register |
| 01 | [Target Architecture](01-target-architecture.md) | The destination: goals, topology, bounded contexts, the five changes that matter |
| 02 | [Data Architecture](02-data-architecture.md) | Entity model, schema decisions, and the expand/contract migration |
| 03 | [Security Architecture](03-security-architecture.md) | Threat model, identity, command authorisation, cryptography, secrets |
| 04 | [API Contract](04-api-contract.md) | The v2 surface, versioning, error model, the legacy shim |
| 05 | [Real-Time Architecture](05-realtime-architecture.md) | Command delivery: transport, state machine, TTL, failure modes |
| 06 | [Client Architecture](06-client-architecture.md) | Desktop agent, mobile app, web dashboard, and sunsetting the old two |
| 07 | [Migration Plan](07-migration-plan.md) | Seven gated phases, risk register, definition of done |
| 08 | [Platform & Delivery](08-platform-and-delivery.md) | Hosting, environments, secrets, CI/CD, observability, runbooks |
| 09 | [Implementation Notes](09-implementation-notes.md) | Every place the build differs from the plan, and why |

### Decision records

| ADR | Decision |
|---|---|
| [0001](adr/0001-backend-runtime-and-framework.md) | Node 22 + TypeScript on Fastify — one backend, contract generated from schemas |
| [0002](adr/0002-authentication-and-session-model.md) | Argon2id + JWT access / rotating refresh + separate device credentials |
| [0003](adr/0003-command-channel-transport.md) | Socket.IO with a Valkey adapter; append-only commands with a mandatory TTL |
| [0004](adr/0004-reminder-encryption-model.md) | AES-256-GCM envelope encryption, per-user DEK — and why not end-to-end |
| [0005](adr/0005-database-and-migration-tooling.md) | Stay on MySQL 8.4; adopt `dbmate` plain-SQL migrations |
| [0006](adr/0006-desktop-client-technology.md) | Go + Wails, pinned to v2 stable; v3 deferred to GA |
| [0007](adr/0007-mobile-client-technology.md) | Flutter for Android and iOS; retire the Java app |
| [0008](adr/0008-api-versioning-and-legacy-sunset.md) | Path versioning, a legacy shim, and a measurement-gated sunset |
| [0009](adr/0009-implementation-platform.md) | **.NET 10 + PostgreSQL 18 + SignalR** — supersedes 0001, 0005 and 0003's transport |
| [0010](adr/0010-passkeys.md) | Passkeys as a first-class credential — the revisit trigger 0002 named |
| [0011](adr/0011-risk-tiered-step-up.md) | Risk-tiered step-up: a session alone cannot power a machine off |
| [0012](adr/0012-client-technology.md) | **.NET service + WPF companion; Kotlin on Android** — supersedes 0006 and 0007 |
| [template](adr/0000-template.md) | For the next decision |

### Machine-readable artifacts

| Artifact | Purpose |
|---|---|
| [`openapi/pcconnect-v2.yaml`](openapi/pcconnect-v2.yaml) | **The contract**, generated from the running API by `tools/generate-openapi.sh`. CI fails when the committed copy is stale. 39 paths. |
| [`openapi/pcconnect-v2.design.yaml`](openapi/pcconnect-v2.design.yaml) | The original hand-written specification, kept as design intent. Not the contract. |
| [`schema/v2_target_schema.sql`](schema/v2_target_schema.sql) | Target DDL as originally designed for MySQL, annotated with the finding each decision closes. The executed schema is `db/migrations/*.sql` (PostgreSQL); see [ADR-0009](adr/0009-implementation-platform.md). |
| [`schema/migration_v1_to_v2.sql`](schema/migration_v1_to_v2.sql) | The expand/backfill/verify/contract sequence, with the guard that prevents destroying every reminder |

---

## The problem in one page

PCConnect lets someone shut down their PC from their phone. That makes remote code execution on a
personal computer its core primitive — and today that primitive is guarded by a single permanent,
unscoped, unrotatable bearer token, derived from a password that the *client* hashes with unsalted
SHA-256 before sending.

Around that sit three consequences:

**Three API generations.** The live PHP endpoints (not in this repository) serve 100% of traffic.
A PHP front-controller in `api/` cannot execute — four of its classes are zero-byte files. A working
Node gateway in `api_node/` serves nobody. The only written spec documents endpoints that no
implementation provides.

**Two client generations.** A VB.NET WinForms client on .NET Framework 4.7.2 and a Java Android app
on deprecated `AsyncTask` carry all production traffic. A Go/Wails desktop client and a Flutter
mobile app are prototypes that have never connected to production. Neither generation can talk to
the other's backend.

**A schema that causes bugs.** No timezone anywhere, so reminders fire at UK time for users in
Kolkata and Lima. `utf8mb3`, so reminder text cannot hold an emoji. And a single mutable row used as
a command mailbox, so a shutdown queued at 09:00 executes when the laptop opens at 18:00.

---

## The answer in one page

**Five changes carry the architecture.** Everything else is detail hanging off them.

| | Change | Closes |
|---|---|---|
| **C-1** | Three credentials — password (Argon2id, server-side), access token (15 min, scoped), refresh token (rotating, reuse-detected) — plus a separate per-device secret. No single credential does everything any more. | S1-03, S1-04, S1-05 |
| **C-2** | Devices are **paired** with a user-confirmed code, not auto-registered from a self-asserted header. | S1-08 |
| **C-3** | Commands become an append-only lifecycle with a **mandatory TTL** and per-command acknowledgement, replacing the mutable mailbox. | S2-03, S2-04, S2-05 |
| **C-4** | Encryption is decoupled from authentication: AES-256-GCM envelope encryption with a per-user data key, so rotating a credential no longer destroys data. | S1-06, S1-07 |
| **C-5** | The contract **is** the schema. Zod → runtime validation → OpenAPI → generated clients. Contract drift becomes a build failure. | §00.3 |

**And the migration keeps everything running.** A `/legacy/*` shim reproduces the old wire format
over the new services, so backend work is fully decoupled from MSI builds and Play Store reviews.
Legacy support ends when a Prometheus counter says legacy traffic is under 1% — a measurement, not a
date.

---

## How to use this

**To understand the system:** 00 → 01 → the ADRs that interest you.

**To start work:** 07 Phase 0, today. Then 07 Phase 1. The first two phases contain no
architecture — they are containment and reproducibility, and everything else depends on them.

**To make a decision that is not covered here:** copy [`adr/0000-template.md`](adr/0000-template.md).
An ADR with no honest "Negative" section and no "Revisit when" trigger is not a decision, it is a
preference.

**To check something is still true:** every finding in 00 cites the file and line it came from.
Re-derive rather than trusting the summary — this assessment is a snapshot of one day.

---

## Traceability

Every finding in the register maps to a control, and every control maps to a phase.

| Where | Mapping |
|---|---|
| [03 §10](03-security-architecture.md#10-findings--controls-traceability) | S1 findings → security controls |
| [01 §8](01-target-architecture.md#8-what-gets-deleted) | What gets deleted, and when |
| [07 §4](07-migration-plan.md#4-definition-of-done) | Definition of done |

---

## Open questions for the maintainer

These are decisions the architecture leaves to you, flagged where they arise:

1. **Hosting.** The design assumes the existing Oracle Cloud VM. A managed MySQL would remove most
   of the backup and restore burden for a monthly cost — worth weighing at Phase 1
   ([ADR-0005](adr/0005-database-and-migration-tooling.md)).
2. **iOS.** Flutter makes it nearly free in engineering terms, but an Apple Developer account is an
   annual cost and App Store review is an ongoing one ([ADR-0007](adr/0007-mobile-client-technology.md)).
3. **Legacy sunset backstop.** The plan proposes 12 months, after which remaining legacy accounts get
   a forced password reset. That is a product call as much as a technical one
   ([ADR-0008](adr/0008-api-versioning-and-legacy-sunset.md)).
4. **Command TTL default.** 120 seconds is a judgement call, tunable against the observed expiry
   ratio ([ADR-0003](adr/0003-command-channel-transport.md)).
