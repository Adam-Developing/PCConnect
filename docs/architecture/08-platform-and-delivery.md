# 08 — Platform and Delivery

> Sizing note: PCConnect has roughly a thousand accounts and a handful of devices each, maintained
> by one person part-time. The platform is deliberately small. Every component below earns its place
> by removing a specific failure that exists today; nothing is here because it is standard practice
> at larger scale.

---

## 1. Runtime topology

One VM, one `docker compose` stack, four containers.

```
                         Cloudflare  (DNS · TLS edge · WAF · rate limit · bot mgmt)
                                        │
                        ═══════════════ VM ═══════════════════════════
                                        │
                                  ┌─────▼──────┐
                                  │   caddy    │  :80 :443  (the only published ports)
                                  │  ACME, h2  │
                                  └──┬──────┬──┘
                          /v2 /rt    │      │   /  (static site + dashboard)
                          /legacy    │      │
                                  ┌──▼──────────┐        ┌──────────────┐
                                  │     api     │        │  web (static)│
                                  │  Node 22 TS │        └──────────────┘
                                  │  Fastify    │
                                  │  Socket.IO  │
                                  └──┬───────┬──┘
                internal network only│       │internal network only
                            ┌────────▼──┐ ┌──▼────────┐
                            │  mysql 8.4│ │ valkey 8  │
                            │  NO host  │ │ NO host   │
                            │  port     │ │ port      │
                            └───────────┘ └───────────┘
                                        │
                        ═══════════════════════════════════════════
                                        │ nightly, age-encrypted
                                  ┌─────▼──────────┐
                                  │ off-host object│
                                  │ storage (backup)│
                                  └────────────────┘
```

**The single most important line in this diagram** is that MySQL has no published host port. Today
the database is reachable at a public IP (S1-02). Moving it behind the compose network converts
"anyone on the internet can attempt to authenticate to the database" into "you must already be
inside the VM".

### 1.1 Why not Kubernetes, serverless, or managed services

- **Kubernetes** — the operational surface exceeds the application's. One maintainer, one VM.
- **Serverless** — the product's core is a *persistent* WebSocket per device. Function runtimes are
  a poor fit and the cost model inverts.
- **Managed DB** — reasonable and worth revisiting; not required, and the migration is already
  changing enough at once. Revisit when backup/restore effort exceeds its price.

Compose scales to `--scale api=N` behind Caddy the moment it is needed, which is only possible
because sessions and presence moved to Valkey.

---

## 2. Environments

| | Local | Staging | Production |
|---|---|---|---|
| Data | Seeded fixtures | Anonymised production copy, refreshed monthly | Real |
| Secrets | `.env.local`, committed as SOPS-encrypted, dummy values | SOPS `.env.staging.enc` | SOPS `.env.production.enc` |
| Migrations | `dbmate up` on demand | Every merge to `main` | Manual approval gate |
| Deploy | `docker compose up` | Automatic on merge | Tag-triggered, manual approval |
| Purpose | Feature work | **Where every migration and cutover is rehearsed** | — |

Staging is the piece with the highest return here: it does not exist today (S3-07), so every change
is currently tested in production against real users' computers.

**Anonymisation** for the staging refresh is a scripted, tested step — emails rewritten to
`user{id}@staging.invalid`, names replaced, password hashes replaced with a known test Argon2id
hash, reminder ciphertext replaced with fixture text under a staging DEK. It runs as part of the
refresh job, not by hand.

---

## 3. Configuration and secrets

All server configuration is environment variables, validated by a Zod schema at boot. A missing or
malformed variable **fails startup** rather than surfacing as a 500 on the first request that needs
it — which is how `api/db_config.php` behaves today.

```
                     repo (safe to commit)
             ┌──────────────────────────────────┐
             │ .env.production.enc  (SOPS+age)  │
             │ .sops.yaml           (rules)     │
             │ age public key                   │
             └──────────────┬───────────────────┘
                            │ decrypt at deploy
                     ┌──────▼──────────────┐        age PRIVATE key:
                     │ container env vars  │◀────── deploy host + password manager only
                     └─────────────────────┘        (never in the repo, never in CI logs)
```

| Variable | Purpose |
|---|---|
| `DATABASE_URL` | MySQL DSN (internal hostname) |
| `VALKEY_URL` | Cache/session/pubsub |
| `JWT_PRIVATE_KEY_JWKS` / `JWT_PUBLIC_KEY_JWKS` | Ed25519 signing keys, rotatable |
| `KEK_CURRENT_ID`, `KEK_KEY_K1`, `KEK_KEY_K2` | Envelope keys. Two named slots so rotation is not a flag day; the slot name is the id stored in `users.dek_kek_id`, so a rotation fills the empty slot and never refills the full one |
| `ARGON2_MEMORY_KIB`, `ARGON2_TIME_COST` | Tuned per host; asserted at boot to be ≥ the OWASP floor |
| `CORS_ALLOWED_ORIGINS` | Explicit allow-list (S1-11) |
| `SENTRY_DSN`, `LOG_LEVEL` | Observability |
| `LEGACY_SUNSET_AT` | Drives the `Sunset` header and discovery |

SOPS is chosen over a hosted secret manager because it needs no running service, versions secrets
alongside the code as reviewable diffs, and costs nothing — the right shape for one maintainer.

---

## 4. CI/CD

### 4.1 Pipeline

```
 push / PR
    │
    ├─▶ lint · typecheck · unit tests            (all components, in parallel)
    ├─▶ gitleaks                                  ── blocking
    ├─▶ npm audit --audit-level=high              ── blocking
    ├─▶ govulncheck · trivy                       ── blocking
    ├─▶ licence check vs the GPL-3.0 allow-list   ── blocking
    ├─▶ generate OpenAPI → oasdiff vs the last tag
    │      └─ breaking change without an `api-breaking` label ⇒ FAIL
    ├─▶ regenerate TS / Dart / Go clients → assert no uncommitted diff
    └─▶ integration tests (testcontainers: MySQL + Valkey)
           ├─ authorisation matrix
           ├─ contract tests (schemathesis)
           └─ legacy shim golden files
                │
          merge to main
                │
    ├─▶ build + push images (SHA-tagged, SBOM attached)
    ├─▶ deploy to staging · dbmate up · smoke tests
    │
          tag v*
                │
    └─▶ manual approval ─▶ dbmate up (prod) ─▶ rolling deploy ─▶ smoke ─▶ auto-rollback on failure
```

### 4.2 Migration safety in CI

Every migration PR must satisfy:

- `dbmate up` then `dbmate rollback` then `dbmate up` succeeds on a scratch database
- Applies cleanly against a **restored production backup**, not just an empty schema
- A destructive migration (`DROP`, `ALTER … DROP COLUMN`) requires the `destructive-migration`
  label and a linked rollback plan
- The reminder guard runs before any migration touching `users.api_key`
  (see [07 Phase 3](07-migration-plan.md))

### 4.3 Client release

| Client | Trigger | Steps |
|---|---|---|
| Desktop agent | Tag `desktop-v*` | Cross-build, Authenticode sign, NSIS installer, GitHub Release, update manifest |
| Mobile | Tag `mobile-v*` | Build AAB + IPA, sign (keystore from a CI secret), upload to internal → beta → production tracks |
| Web | Merge to `main` | Build, hash assets, deploy alongside `api` |

---

## 5. Observability

### 5.1 Signals

| Signal | Tool | Retention |
|---|---|---|
| Logs | `pino` JSON → Loki (or the compose logging driver early on) | 14 days |
| Metrics | `prom-client` → Prometheus → Grafana | 90 days |
| Errors | Sentry — API, agent, mobile, web; release-tagged, source-mapped | 90 days |
| Uptime | External probe on `/readyz` from outside the VM | 1 year |
| Audit | `security_events` + `command_events` in MySQL | 90 days |

### 5.2 The dashboard that matters

Six panels, in this order:

1. **Command funnel** — issued → delivered → acked, and the expiry ratio
2. **Command latency** — p50/p95/p99 issue-to-ack
3. **Auth** — login success/failure, token reuse detections, lockouts
4. **Realtime** — connected sockets by client kind, reconnect rate
5. **Legacy** — `pcconnect_legacy_requests_total{endpoint}`, the number that decides Phase 6
6. **Golden signals** — request rate, error rate, p95 latency, DB pool saturation

### 5.3 Alerts

Alerting on everything means alerting on nothing. Five rules, each with a runbook link:

| Alert | Condition | Severity |
|---|---|---|
| Command delivery broken | `expired / issued > 10%` over 15 min | **Page** |
| Stale execution | `pcconnect_command_stale_executions_total > 0` | **Page** — a computer was shut down without its owner asking |
| Service down | `/readyz` failing 3 min | **Page** |
| Credential attack | Auth failure rate > 10× the 7-day baseline over 10 min | Notify |
| Backup failed | Nightly job failed, or the monthly restore rehearsal failed | Notify |

---

## 6. Runbooks

Living in `docs/runbooks/`, each written as a numbered procedure someone can follow at 03:00.

| Runbook | Covers |
|---|---|
| `deploy.md` | Normal deploy, migration gate, smoke checks |
| `rollback.md` | Image rollback; when a migration makes rollback unsafe and what to do instead |
| `restore.md` | Restore from backup to a fresh host; the rehearsed path |
| `rotate-keys.md` | KEK rotation (rewrap DEKs), JWT key rotation (JWKS overlap), DB password rotation |
| `incident-credential-leak.md` | Revoke sessions, force reset, rotate, notify — the drill for another S1-01 |
| `incident-stale-command.md` | Triage for a non-zero stale-execution counter |
| `legacy-sunset.md` | The measured checklist for turning the shim off |

### 6.1 Recovery objectives

| | Target | Mechanism |
|---|---|---|
| RPO | 24 h initially; 5 min once binlog shipping is on | Nightly dump; then binlogs |
| RTO | 1 h | Rehearsed restore-to-new-host |
| Backup verification | **Monthly, in CI** | Restore into a scratch container, run the [02 §5.1](02-data-architecture.md) verification queries, assert row counts |

The monthly automated rehearsal is the difference between having backups and having recovery.

---

## 7. Cost and capacity

| Resource | Sizing | Note |
|---|---|---|
| VM | 2 vCPU / 4 GB / 80 GB | Current Oracle Cloud shape is adequate |
| MySQL | ~2 GB working set | The whole dataset is small; keep the buffer pool above it |
| Valkey | 256 MB, `maxmemory-policy allkeys-lru` | Sessions, presence, rate limits, idempotency |
| Sockets | ~1 device + ~1 phone per active user | Well inside one Node process; Valkey adapter is for restarts and scale-out headroom, not for load today |
| Egress | Trivial | Payloads are hundreds of bytes |

Capacity is not a constraint at this size. The stack is designed for *operability and safety*, and
horizontal scale is a property that falls out of the session and presence changes rather than a
goal in itself.

---

## 8. Delivery hygiene

| Practice | Rule |
|---|---|
| Trunk-based | Short-lived branches off `main`; no long-running forks (S3-03) |
| Conventional commits | Drives the changelog and version bumps |
| Every PR | Passes CI, updates docs when behaviour changes, notes the migration impact |
| Dependencies | Dependabot weekly; security updates same-day |
| Dead code | Removed, not commented out — the current codebase carries large commented blocks (`PCClient.vb:70-88`) |
| ADRs | Every architecturally significant decision gets one before implementation |

---

Previous: [07 — Migration Plan](07-migration-plan.md) · Index: [README](README.md)
