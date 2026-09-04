# ADR-0009 — Implementation platform: .NET 10, PostgreSQL 18, SignalR

**Status:** Accepted
**Date:** 2026-09-02
**Supersedes:** [ADR-0001](0001-backend-runtime-and-framework.md) (runtime and framework),
[ADR-0005](0005-database-and-migration-tooling.md) (engine and migration tooling), and the
transport half of [ADR-0003](0003-command-channel-transport.md)
**Context docs:** [01 §5](../01-target-architecture.md), [02](../02-data-architecture.md),
[05 §2](../05-realtime-architecture.md), [08 §1](../08-platform-and-delivery.md)

## Context

ADR-0001 chose Node 22 + TypeScript on Fastify, ADR-0003 chose Socket.IO with a Valkey
adapter, and ADR-0005 chose MySQL 8.4 with `dbmate`. Those three decisions shared one
premise, stated in ADR-0001's context: *"Consolidating to one implementation is not
optional; the question is which"*, and the answer leaned on reusing the working
`api_node` gateway.

Two things changed after those ADRs were written.

**The `api_node` gateway is no longer in the working tree.** The prototype whose reuse
justified "port, don't rewrite" has been removed. The migration cost that ADR-0001 weighed
against a Go rewrite — *"discards the whole `api_node` implementation including the working
Socket.IO room model"* — no longer applies, because there is nothing left to discard.

**The maintainer has directed the implementation at .NET.** The stack for the delivered
system is ASP.NET Core on .NET 10 for the API and worker, PostgreSQL 18 as the canonical
database, SignalR for realtime, Kotlin/Jetpack Compose on Android, and a .NET Windows
service with a WPF companion on the desktop.

This ADR records that decision, its consequences, and what it costs — rather than letting
the implementation quietly contradict three accepted ADRs, which is exactly the drift
those ADRs exist to prevent.

## Decision

**ASP.NET Core on .NET 10, PostgreSQL 18, and SignalR, with Valkey retained for cache,
rate limits, presence and the realtime backplane.**

| Layer | Was (ADR-0001/0003/0005) | Is | Why the change is safe |
|---|---|---|---|
| Runtime | Node 22 + TypeScript 5 | .NET 10 | Same shape of application; a compiled, statically typed runtime with first-class async |
| HTTP | Fastify 5 + Zod | ASP.NET Core minimal APIs + records | The contract is still generated from the code that serves it (C-5) |
| Realtime | Socket.IO 4 + Valkey adapter | SignalR + `Microsoft.AspNetCore.SignalR.StackExchangeRedis` | Same design: handshake-authenticated, group-based, backplane-scaled |
| Database | MySQL 8.4 | PostgreSQL 18 | Every schema decision in 02 survives; several get *better* primitives |
| Migrations | `dbmate` | Our own plain-SQL runner, same file format | ADR-0005's actual property — versioned plain SQL, not tool-generated — is kept |
| Password hashing | Argon2id (`argon2` npm) | Argon2id (`Konscious.Security.Cryptography`) | Unchanged; parameters unchanged |
| Access tokens | EdDSA (Ed25519) | ES256 (ECDSA P-256) | See "Signing algorithm" below |

Everything in [03 — Security Architecture](../03-security-architecture.md) is unchanged.
The credential model, the eight checks on a command, the TTL, the envelope encryption and
the threat model are properties of the design, not of the runtime.

## What PostgreSQL changes, concretely

The move from MySQL is the largest of these and it is worth being specific about, because
[02](../02-data-architecture.md) was written against MySQL's constraints.

| 02 says | On PostgreSQL 18 |
|---|---|
| `BINARY(16)` holding a UUIDv7, because InnoDB clusters on the PK and a random UUID PK causes page splits | Native `uuid` type, and `uuidv7()` is built in. Heap storage means the ordering argument does not apply, but UUIDv7 is kept: it is still better for the index, and the public ids stay time-ordered |
| `VARCHAR` + `CHECK` rather than `ENUM`, because adding an `ENUM` value rebuilds the table | `text` + `CHECK`. Same reasoning, and adding a value is a catalogue-only change here too |
| `DATETIME(3)`, never `TIMESTAMP`, because of 2038 and implicit session-timezone conversion | `timestamptz(3)`. No 2038 problem, and the conversion behaviour is explicit and correct |
| §5.2 `utf8mb3 → utf8mb4`, table by table, watching index prefix limits | **Moot.** PostgreSQL is UTF-8 throughout. S2-08 — "reminder text cannot hold an emoji" — is closed by the choice of engine, with no conversion step and no index-length hazard |
| Composite indexes ordered equality-then-range | Same, plus **partial indexes**: the two `_sweep` indexes now cover only rows that can still transition, which is smaller and faster |
| `JSON` columns for `params` and `allowed_commands` | `jsonb`, with `jsonb_typeof` CHECK constraints so a malformed value cannot be stored at all |
| The command claim reads then updates | `UPDATE … FOR UPDATE SKIP LOCKED` in a single CTE, so two agents polling concurrently cannot be served the same command |

One genuinely new capability is used: the `ck_commands_stepup` CHECK constraint makes
"a destructive command cannot exist without a recorded step-up" an invariant the database
enforces, not just the service ([ADR-0011](0011-risk-tiered-step-up.md)).

## Signing algorithm: EdDSA → ES256

[ADR-0002](0002-authentication-and-session-model.md) specified EdDSA (Ed25519), for
*"small signatures, fast verification, no curve or padding parameters to get wrong, and
pinning the algorithm at the verifier"*.

.NET 10 has no in-box Ed25519 signer. The options were a native libsodium binding
(`NSec`), which adds a platform-specific native dependency to a solo-maintained
deployment, or ES256 (ECDSA P-256), which is in the base class library.

**ES256 is used.** Every property ADR-0002 actually named is preserved: 64-byte
signatures, fast verification, an algorithm pinned at the verifier so the `alg:none` and
RS256-confusion class of bug cannot occur, and no RSA padding to get wrong. What is lost
is Ed25519's freedom from nonce-reuse footguns in the signer — mitigated by the signer
being the platform's own implementation rather than ours.

Revisit if .NET gains an in-box Ed25519 signer.

## Migration tooling: `dbmate` → our own runner

ADR-0005 chose `dbmate` for a stated reason: *"versioned plain SQL"* that does not couple
schema history to the application runtime, with the alternative rejected because
*"Prisma Migrate / Knex couples schema history to the app runtime"*.

The runner in `src/PCConnect.DbMigrator` keeps that property exactly:

- The migrations are plain `.sql` files in `db/migrations`, in `dbmate`'s own
  `-- migrate:up` / `-- migrate:down` format. They can be read, reviewed and applied by
  hand in an incident with `psql`.
- The runner reads them, tracks applied versions in `schema_migrations`, and does nothing
  else. No model, no scaffolding, no code generation.
- It **checksums** each applied migration and refuses to proceed when an applied file has
  been edited — a property `dbmate` does not have, and the one that stops a staging
  database silently ceasing to match production.
- Destructive migrations require `--allow-destructive`, so the contract step of
  [07 Phase 4.9](../07-migration-plan.md) cannot run by accident.

What is dropped is one external binary to install on every machine and in CI.

## The two-engine migration

ADR-0005 assumed the migration stayed on one engine, which is what made
[02 §5](../02-data-architecture.md)'s expand → dual-write → backfill → cutover → contract
sequence possible: both shapes live in one database and one transaction can write both.

Across two engines that is not available. The sequence becomes:

```
 EXPAND          SYNC              VERIFY           CUTOVER          CONTRACT
 v2 schema in    idempotent,       V1-V10 return    traffic moves    MySQL
 PostgreSQL      resumable         zero for 24h     to PostgreSQL    decommissioned;
 (empty)         MySQL→PG import                                     bridge columns
                 on a timer                                          dropped
```

- **MySQL stays authoritative until cutover.** The v1 PHP endpoints keep serving and keep
  writing; the import replicates forwards on a timer. Rollback before cutover is a proxy
  change, exactly as [07 Phase 2](../07-migration-plan.md) describes, because nothing has
  been taken away from MySQL.
- **The import is idempotent and resumable**, keyed on the legacy primary keys, with
  high-water marks in `migration_state`. Interrupting it and running it again picks up
  where it stopped.
- **Nothing is silently dropped.** A row that will not map — an unparseable recurrence, a
  duplicate email, a reminder that will not decrypt — is written to `migration_exceptions`
  with its reason, and gate V9 counts those as a failure.
- **The one-way door moves.** In the single-engine plan, contract was the point of no
  return. Here it is *cutover*: after it, writes land in PostgreSQL and MySQL is stale.
  The mitigation is the same in kind — a verified backup first, and a rehearsed rollback —
  but the window is measured in the time it takes to notice, so cutover happens after a
  soak with verification at zero, and out of hours.

The bridge columns (`users.legacy_user_id`, `devices.legacy_pcid`, `reminders.legacy_id`)
and the guards that protect them are in migrations `0006` and `0007`.

## Options considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **.NET 10 + PostgreSQL 18 + SignalR** (chosen) | One language across API, worker, Windows service and companion; compiled and statically typed; PostgreSQL's partial indexes, `jsonb` and `SKIP LOCKED` fit the command and sweep queries better than MySQL's; SignalR is first-party and needs no separate client library on .NET | Three accepted ADRs are superseded at once; the migration becomes cross-engine; no in-box Ed25519 | **Chosen** |
| Node 22 + Fastify + MySQL, as ADR-0001/0005 | No ADRs superseded | The `api_node` code that justified the reuse argument is gone, so this is now a rewrite too — with a second language for the Windows client on top | Rejected |
| .NET but stay on MySQL 8.4 | Keeps ADR-0005; single-engine expand/contract survives | Keeps `utf8mb3` conversion (S2-08) as a live migration step with index-length hazards; no partial indexes for the sweeps; `SKIP LOCKED` exists but the surrounding ergonomics are worse | Rejected — the engine change removes a whole migration stage rather than adding one |
| .NET with raw WebSockets instead of SignalR | No framework in the realtime path | Reconnection, backoff, heartbeats, groups and backplane fan-out all rebuilt by hand — the same argument ADR-0003 used to reject raw WebSocket over Socket.IO | Rejected |

## Consequences

**Positive**

- One language and one toolchain for the API, the worker, the Windows service, the WPF
  companion and the shared client SDK. The contract records in `PCConnect.Core.Contracts`
  are referenced directly by the .NET clients, so a field that changes shape fails to
  compile rather than failing on a user's machine.
- S2-08 is closed by the choice of engine rather than by a risky online schema change.
- The sweep queries get partial indexes, and the command claim gets `SKIP LOCKED`.
- The database enforces two invariants the service also enforces: a destructive command
  has a step-up, and a reminder ciphertext is at least nonce + tag long.
- SignalR's backplane is the same idea as Socket.IO's Valkey adapter, so 05's scaling and
  restart properties are unchanged.

**Negative**

- **Three ADRs are superseded**, which is a real cost in reader trust: anyone who read
  0001, 0003 and 0005 now has to read this too. That is why this document restates what
  changed rather than merely announcing a new stack.
- **The migration is cross-engine**, which loses transactional dual-write and moves the
  one-way door from contract to cutover. §"The two-engine migration" above is the honest
  account of that.
- **No in-box Ed25519.** ES256 is a good substitute, not an identical one.
- **PostgreSQL is a new operational surface** for a maintainer whose experience is MySQL:
  different backup tooling, different `EXPLAIN` output, different failure modes. The
  runbook covers dump, restore and rehearsal; the rest is learning.
- The `.NET` runtime images are larger than a Node image (~200 MB vs ~120 MB). Irrelevant
  on one VM; noted so it is not a surprise.

**Neutral**

- Valkey stays, in the same role: cache, rate-limit windows, presence, the access-token
  deny list, and the realtime backplane.
- Caddy, the compose topology, the secret handling and the CI gates are unchanged
  from [08](../08-platform-and-delivery.md).

## Revisit when

- .NET gains an in-box Ed25519 signer — then the ES256 substitution in ADR-0002 should be
  revisited.
- The maintainer stops working in .NET, at which point the "one language everywhere"
  benefit that justifies most of this evaporates.
- PostgreSQL's operational burden on one VM exceeds a managed instance's price, which is
  the same open question ADR-0005 left for MySQL.
