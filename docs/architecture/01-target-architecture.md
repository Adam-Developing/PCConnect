# 01 — Target Architecture

---

## 1. Architectural goals

Ordered. Where they conflict, the earlier one wins.

| # | Goal | Why it is first / where it sits |
|---|---|---|
| G1 | **No credential or command path can be exercised by anyone but the account owner** | The product shuts down people's computers. Everything else is negotiable. |
| G2 | **One contract, machine-defined, shared by every client** | Contract drift across three API surfaces (§00.3) is the root cause of most current defects. |
| G3 | **Zero-downtime migration with the legacy clients still working** | Cannot force-update an installed MSI or a Play Store app. |
| G4 | **A solo maintainer can operate it** | One runtime, one database, one deployment unit, boring tools. |
| G5 | **Every schema and config change is reproducible** | Currently impossible; blocks staging, blocks rollback. |
| G6 | **Real-time by default, correct when offline** | Push is the product's differentiator; polling is the safety net, not the mechanism. |

**Explicit non-goals:** horizontal autoscaling, multi-region, microservices, multi-tenancy beyond
per-user isolation, real-time collaborative editing, an admin web console in v1.

---

## 2. Target system, at a glance

```
                            ┌───────────────────────────┐
                            │   Cloudflare (DNS, TLS,   │
                            │   WAF, rate limit, DDoS)  │
                            └─────────────┬─────────────┘
                                          │  HTTPS / WSS only
                         ┌────────────────▼─────────────────┐
                         │  Caddy  (reverse proxy, ACME)    │
                         └───┬──────────────┬───────────┬───┘
                             │              │           │
        ┌────────────────────▼───┐  ┌───────▼────────┐  │
        │  pcconnect-api         │  │ pcconnect-web  │  │
        │  Node 22 + TypeScript  │  │ static site +  │  │
        │  Fastify + Socket.IO   │  │ thin dashboard │  │
        │                        │  └────────────────┘  │
        │  /v2/*   REST          │                      │
        │  /rt     Socket.IO     │        ┌─────────────▼──────────────┐
        │  /legacy/* shim ───────┼───────▶│ legacy PHP (read-only      │
        │  /healthz /readyz      │        │ compat shim, sunset date)  │
        │  /metrics              │        └────────────────────────────┘
        └───┬───────────┬────────┘
            │           │
   ┌────────▼───┐  ┌────▼──────────┐        ┌──────────────────────────┐
   │ MySQL 8.4  │  │ Valkey/Redis  │        │ Object storage (backups, │
   │ private    │  │ sessions,     │        │ encrypted, off-host)     │
   │ network    │  │ presence,     │        └──────────────────────────┘
   │ only       │  │ rate limits,  │
   └────────────┘  │ command queue │
                   └───────────────┘
                            ▲
             ┌──────────────┼──────────────────┐
             │              │                  │
   ┌─────────┴──────┐ ┌─────┴────────┐ ┌───────┴────────┐
   │ Desktop agent  │ │ Mobile app   │ │ Web dashboard  │
   │ Go + Wails     │ │ Flutter      │ │ (same OpenAPI  │
   │ Windows first  │ │ Android+iOS  │ │  client)       │
   └────────────────┘ └──────────────┘ └────────────────┘
```

### 2.1 Deployment unit

A single `docker compose` stack on one VM. Four containers: `api`, `mysql`, `valkey`, `caddy`.
MySQL and Valkey bind to the compose-internal network only — **never** published to a host port
(this closes S1-02). The API is stateless; scaling is `docker compose up --scale api=N` behind
Caddy, which is possible only because sessions and presence move to Valkey (closes S2-06).

---

## 3. Logical architecture

### 3.1 Bounded contexts

Five, all inside one deployable. They are module boundaries, not services. Cross-context calls go
through an explicit exported interface, never a shared table.

```
pcconnect-api/src/
├─ platform/          cross-cutting: config, db, cache, logging, errors, crypto, ids
├─ identity/          users, credentials, sessions, tokens, password reset
├─ devices/           device registry, pairing, presence, heartbeat
├─ commands/          command lifecycle: issue → deliver → ack → expire, audit
├─ reminders/         reminders, recurrence, scheduling, encryption at rest
├─ notifications/     push fan-out (WS now; APNs/FCM later)
└─ interfaces/
   ├─ http/           Fastify routes, schema-first, generates the OpenAPI doc
   ├─ realtime/       Socket.IO namespace, handshake auth, rooms
   └─ legacy/         compatibility shim for C1 endpoints (has an expiry date)
```

**Dependency rule:** `interfaces → contexts → platform`. Contexts never import from `interfaces`;
contexts import each other only via the narrow interface each exports (`identity` exports
`verifyAccessToken`, `devices` exports `assertDeviceOwnedBy`, etc.). This eliminates the circular
`routes.js ↔ server.js` import (S2-12) structurally rather than with a try/catch.

### 3.2 Context responsibilities

| Context | Owns | Exposes | Does **not** |
|---|---|---|---|
| **identity** | `users`, `user_credentials`, `refresh_tokens`, `password_resets` | `authenticate()`, `verifyAccessToken()`, `issueTokenPair()`, `revokeFamily()` | know what a device or a command is |
| **devices** | `devices`, `device_credentials`, `device_pairings` | `assertDeviceOwnedBy(userId, deviceId)`, `presence()`, `heartbeat()` | execute anything |
| **commands** | `commands`, `command_events` | `issue()`, `claim()`, `ack()`, `expireDue()` | choose *how* a command reaches a device |
| **reminders** | `reminders`, `reminder_occurrences` | `list()`, `create()`, `complete()`, `dueBetween()` | send notifications |
| **notifications** | delivery fan-out only | `deliver(userId, event)` | own any table |

---

## 4. The five architectural changes that matter

Everything else in this document is detail hanging off these.

### C-1 · Identity replaces bearer keys

The static `users.api_key` is retired. Three distinct credential types, each with its own lifetime,
scope and revocation path:

| Credential | Holder | Lifetime | Scope | Storage |
|---|---|---|---|---|
| **Access token** (JWT, EdDSA) | any client | 15 min | `user:*` or `device:*` claims | memory only |
| **Refresh token** (opaque, 256-bit) | any client | 30 days, rotating, reuse-detected | mint access tokens | hashed (SHA-256) at rest; OS keychain on client |
| **Device secret** | one PC agent | until revoked | `device:execute` for **that device id only** | hashed (Argon2id) at rest; Windows Credential Manager on client |

A leaked mobile refresh token can no longer shut down a PC: it cannot mint `device:execute`.
Full detail: [03 — Security Architecture](03-security-architecture.md).

### C-2 · Devices are paired, not asserted

`PCName` as a self-asserted, auto-registering header (S1-08) is replaced by an explicit pairing
handshake: the agent requests a pairing code, the user confirms it in the mobile app or web
dashboard, and the server issues a device id plus device secret. `PCName` becomes a mutable display
label with no security meaning.

### C-3 · Commands become an append-only, expiring lifecycle

The mutable `pcnames.Request`/`Value` mailbox (S2-03, S2-04, S2-05) is replaced by an append-only
`commands` table with an explicit state machine and a **mandatory TTL**:

```
  issued ──claim─────────▶ delivered ──ack:ok────▶ succeeded
     │   └──push+confirm──▶     │
     │                          └──ack:error──────▶ failed
     │
     ├──ttl elapsed────────────────────────────────▶ expired
     └──user cancels───────────────────────────────▶ cancelled
```

There are two routes into `delivered` because there are two ways a command reaches an agent.
A poll claims it; a pushed command is confirmed received by the agent over the same socket it
arrived on ([05 §4.3](05-realtime-architecture.md)). Both write a `delivered` event, and both
are driven by the agent rather than by the server's own send — a write to a socket is not
evidence that anything received it.

`expires_at` defaults to **120 seconds** for power commands. A command that was not delivered
inside its window is never executed — which is the fix for "the shutdown I sent this morning fired
when I opened my laptop tonight". Every transition writes a `command_events` row: destructive
actions get an audit trail.

### C-4 · Encryption is decoupled from authentication

The API-key-as-AES-key design (S1-06, S1-07) is replaced by envelope encryption: a per-user Data
Encryption Key, wrapped by a Key Encryption Key held outside the database, with **AES-256-GCM**
(authenticated) replacing AES-256-CBC. Rotating a credential no longer destroys data.
Honest scope of this control is stated in [ADR-0004](adr/0004-reminder-encryption-model.md).

### C-5 · The contract is the schema

Every route is defined by a Zod schema. Fastify validates request and response against it at
runtime and emits OpenAPI 3.1 from it at build time. The Flutter and TypeScript clients are
**generated** from that document in CI. A breaking change fails the build rather than reaching a
client. This is what stops §00.3 from happening again.

---

## 5. Runtime and technology selections

> **Superseded in part.** The table below is the selection as designed. Four rows
> changed during implementation — runtime, database, realtime and both clients — and
> the current selection is in [ADR-0009](adr/0009-implementation-platform.md) and
> [ADR-0012](adr/0012-client-technology.md), summarised in
> [09 §1](09-implementation-notes.md). Every *property* this table was chosen for is
> preserved; the products differ.
>
> | Layer | As built |
> |---|---|
> | Backend runtime | ASP.NET Core on .NET 10 |
> | HTTP framework | Minimal APIs; the contract generated from the same records the handlers return |
> | Real-time | SignalR with a StackExchange.Redis (Valkey) backplane |
> | Database | PostgreSQL 18 |
> | Migrations | Versioned plain SQL, applied by `pcconnect-migrate`, with applied-file checksums |
> | Desktop client | .NET Windows service + WPF companion |
> | Mobile client | Kotlin + Jetpack Compose (Android) |
> | Auth | Argon2id + ES256 access tokens + rotating refresh, **plus passkeys and step-up** |

| Layer | Selection | Alternative rejected | ADR |
|---|---|---|---|
| Backend runtime | **Node 22 LTS + TypeScript 5** | PHP 8.3 (no shared types with clients); Go (would discard `api_node`) | [0001](adr/0001-backend-runtime-and-framework.md) |
| HTTP framework | **Fastify 5 + `fastify-type-provider-zod`** | Express 5 (no schema-first OpenAPI); NestJS (too heavy for one maintainer) | [0001](adr/0001-backend-runtime-and-framework.md) |
| Auth | **Argon2id + JWT access / rotating opaque refresh** | Session cookies only (no native client story); Auth0 (cost, GPL-3 project) | [0002](adr/0002-authentication-and-session-model.md) |
| Real-time | **Socket.IO 4 with Redis adapter** | Raw WebSocket (must rebuild reconnect/rooms); SSE (no client→server channel) | [0003](adr/0003-command-channel-transport.md) |
| Encryption | **AES-256-GCM envelope, per-user DEK** | Field-level AES-CBC (status quo); full E2EE (breaks server-side scheduling) | [0004](adr/0004-reminder-encryption-model.md) |
| Database | **MySQL 8.4 LTS, `utf8mb4_0900_ai_ci`** | Postgres (migration cost with no payoff at this size) | [0005](adr/0005-database-and-migration-tooling.md) |
| Migrations | **`dbmate` — versioned plain SQL** | Prisma Migrate / Knex (couples schema history to the app runtime) | [0005](adr/0005-database-and-migration-tooling.md) |
| Cache / sessions | **Valkey 8** (Redis-compatible, BSD) | In-process map (status quo, S2-06); Redis 7.4 (RSALv2 licence) | [0003](adr/0003-command-channel-transport.md) |
| Desktop client | **Go + Wails v2.12 (stable) now; v3 on GA** | Wails v3-alpha now (S2-10); Tauri (rewrite); WinUI (loses cross-platform) | [0006](adr/0006-desktop-client-technology.md) |
| Mobile client | **Flutter** — retire the Java Android app | Kotlin rewrite (Android only, more work) | [0007](adr/0007-mobile-client-technology.md) |
| Legacy sunset | **Shim + versioned deprecation with forced update** | Big-bang cutover (breaks installed clients) | [0008](adr/0008-api-versioning-and-legacy-sunset.md) |

---

## 6. Cross-cutting concerns

### 6.1 Configuration

No absolute URL is ever compiled into a client (fixes S3-08). Clients resolve their backend from a
build-time constant that is overridable at runtime, and discover capability from a
`GET /v2/meta/discovery` document (versions, WS URL, minimum supported client build, sunset dates).
Server config is environment variables only, validated by a Zod schema at boot — the process
refuses to start on a missing or malformed variable rather than failing at first request.

### 6.2 Errors

One error envelope everywhere, replacing the four current formats
(bare text, `{error,message}`, `{success,data}`, raw arrays):

```jsonc
{
  "error": {
    "code": "device.not_paired",      // stable, machine-readable, documented
    "message": "This device is not paired to your account.",
    "requestId": "01JZ8K9M...",       // correlates with server logs
    "details": [ /* optional, field-level */ ]
  }
}
```

Codes are namespaced by context and enumerated in the OpenAPI document. Clients switch on `code`,
never on `message`.

### 6.3 Idempotency

Every state-changing endpoint accepts an `Idempotency-Key` header. Keys are stored in Valkey for 24
hours against the response. Command issue is idempotent by construction (client-generated UUIDv7
command id), which is what makes the offline-queue replay in the mobile and desktop clients safe.

### 6.4 Time

The server is the sole authority on time. All instants are stored and transmitted as UTC RFC 3339.
Any local-time rendering is a client concern, driven by an IANA timezone stored on the user record.
This closes S2-07 and removes the need for the `GET /api/time.php` clock-sync endpoint entirely.

### 6.5 Observability

| Signal | Mechanism | Key items |
|---|---|---|
| Logs | `pino` JSON to stdout, collected by the container runtime | one line per request with `requestId`, `userId`, `deviceId`, latency, outcome |
| Metrics | `prom-client` on `/metrics`, scraped by Prometheus | command issue/deliver/ack/expire counters, WS connections gauge, auth failure rate, DB pool saturation, p50/p95/p99 latency |
| Errors | Sentry (self-hosted or free tier), server and clients | release-tagged, source-mapped |
| Health | `/healthz` (process alive) and `/readyz` (DB + Valkey reachable) | drives Caddy and deploy gating |

The four alerts that matter, in priority order: auth-failure spike (credential stuffing), command
expiry ratio above 10% (delivery broken), `/readyz` failing, WS connection count collapse.

### 6.6 Data protection

- **In transit:** TLS 1.3 only. HSTS with preload. Certificate pinning on the mobile client for the
  primary domain, with a documented un-pin release path.
- **At rest:** reminder text encrypted per §C-4. Passwords Argon2id. Refresh tokens SHA-256. Device
  secrets Argon2id. Full-disk encryption on the VM.
- **Backups:** nightly `mysqldump`, age-encrypted, shipped off-host, 30-day retention, and a
  **restore rehearsal that runs monthly in CI against a scratch database** — an untested backup is
  not a backup.
- **Retention:** `command_events` 90 days; expired refresh tokens purged at 30 days; user deletion
  cascades and is verified by a test.

---

## 7. Quality attribute targets

Measurable, and used as the exit gates in [07 — Migration Plan](07-migration-plan.md).

| Attribute | Target | Measured by |
|---|---|---|
| Command latency (issue → agent executes, both online) | p95 < 500 ms | server span + agent ack timestamp |
| Command delivery success (agent online) | > 99.5% | `delivered / issued` |
| Stale command execution | **0** | commands executed after `expires_at` — alarms at any non-zero value |
| API availability | 99.5% monthly | uptime probe against `/readyz` |
| Reconnect after network loss | < 30 s | agent telemetry; bounded by `policy.go` backoff |
| Cold start of the desktop agent to connected | < 5 s | agent telemetry |
| Deploy → rollback | < 5 min | rehearsed, documented in the runbook |
| Test coverage on `identity` + `commands` | > 85% lines, 100% of the auth decision paths | CI gate |
| Known S1 findings open | **0** before Phase 5 | findings register |

---

## 8. What gets deleted

Deletion is a deliverable. Tracked in [07 §7](07-migration-plan.md).

| Item | Replaced by | Removed at |
|---|---|---|
| `api/` (broken PHP front-controller, S2-01) | `pcconnect-api` | Phase 1 |
| `api/db_config.php` | env vars + secret manager | **Phase 0** |
| Legacy `/api/pcclient/*.php`, `/api/pcconnect/*.php` | `/v2/*` | Phase 6 |
| `users.api_key` | token pair + device credentials | Phase 3 |
| `pcnames.Request`, `.Value`, `.Time` | `commands`, `device_presence` | Phase 4 |
| Tables `apikeys`, `requests`, `time`, `code` | — (dead: S3-05 orphaned/placeholder, S3-06 superseded by `pcnames` columns) | Phase 4 |
| Table `links` (duplicate of `menupages`) | `nav_items` | Phase 4 |
| `reminders.Recurrence*` (5 columns, S2-09) | `rrule` (RFC 5545) | Phase 4 |
| `PCClient/` VB.NET client | Go/Wails agent | Phase 6 |
| `App/` Java Android client | Flutter app | Phase 6 |
| Branches `copilot/*`, `jules-*`, `wails-v3-*` (S3-03) | one `feat/desktop-agent` line | Phase 1 |

---

Previous: [00 — Current State](00-current-state.md) · Next: [02 — Data Architecture](02-data-architecture.md)
