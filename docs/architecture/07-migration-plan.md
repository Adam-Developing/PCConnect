# 07 — Migration Plan

Strangler-fig migration in seven phases. Phases are **gated, not scheduled**: each advances when its
exit criteria are met, not when its estimate elapses. Durations below are for sequencing intuition
and assume one part-time maintainer.

```
 P0 CONTAIN      P1 FOUNDATION   P2 BACKEND      P3 IDENTITY     P4 DATA        P5 CLIENTS     P6 DECOMMISSION
 ░░░ 1 week      ▒▒▒▒ 2-3 wks    ▓▓▓▓▓ 4-6 wks   ████ 3-4 wks    ████ 3-4 wks   █████ 6-8 wks  ▓▓ 2 wks
 secrets         repo, CI,       api-v2 behind   Argon2id,       expand/        Flutter GA,    legacy off,
 rotated,        migrations,     /v2 + legacy    token pairs,    contract to    agent GA,      old tables
 DB private,     staging         shim; parity    device pairing  v2 schema      sunset old     dropped
 backups                         contract tests                                 clients
                                                                                       ▲
   ─────────── production traffic still on legacy PHP ──────────────────────────────┘ cutover here
```

**Reversibility.** Every phase before P6 is reversible by reverting the application: the legacy
schema and endpoints remain live and written throughout. P6 is the only one-way door, which is why
it is gated on measured client traffic rather than a date.

> **One change as built.** Because v1 is MySQL and v2 is PostgreSQL
> ([ADR-0009](adr/0009-implementation-platform.md)), the point of no return moves earlier: after
> **cutover** in P4, writes land in PostgreSQL and the MySQL copy goes stale. Before cutover,
> rollback is still a proxy change. Cutover therefore happens after the verification gates have
> been at zero for a soak, with a verified backup taken first, and out of hours.

---

## Phase 0 — Contain

**Goal:** stop the bleeding. Nothing else starts until this is done. No architecture improves a
system whose production database password is sitting in a working tree.

| # | Action | Done when |
|---|---|---|
| 0.1 | Add to `.gitignore`: `api/db_config.php`, `**/config.json`, `*.env*`, `DB/*.sql`, `PCClient/.vs/`, `PCClient/**/bin/`, `PCClient/**/obj/`, `PCClient/PCClient Setup*/`, `App/build/`, `App/.gradle/` (S3-04) | `git status --porcelain` shows no sensitive or build path |
| 0.2 | `git log --all -S'<the password>'` and `git log --all -- api/db_config.php` to confirm the credential never entered history | Both empty (verified: they are) |
| 0.3 | **Rotate the MySQL password.** It has lived unencrypted on a workstation; treat it as compromised | New credential in use; old one revoked |
| 0.4 | Bind MySQL to a private interface; replace `'pcconnect_new'@'%'` with a host-scoped grant; close port 3306 at the firewall | `nmap -p3306 130.162.164.140` shows filtered |
| 0.5 | Encrypt or delete `DB/pcconnect.sql` (≈10k rows of real PII) | Not present in plaintext on any workstation |
| 0.6 | Move `App/Key Store/PCConnectKey.jks` to the password manager; keep an offline copy | Removed from the working tree |
| 0.7 | Nightly `mysqldump`, age-encrypted, off-host, plus one **verified restore** into a scratch container | Restore succeeds; row counts match |
| 0.8 | Add `gitleaks` as a pre-commit hook and a CI check | A planted test secret fails the check |

**Exit gate:** 0.3, 0.4, 0.7 and 0.8 all complete. **Risk of skipping:** total data loss or full
compromise, at any moment, with no recovery path.

---

## Phase 1 — Foundation

**Goal:** make change safe and reproducible before changing anything that matters.

| # | Action | Detail |
|---|---|---|
| 1.1 | Repository layout | `apps/api`, `apps/desktop`, `apps/mobile`, `apps/web`, `db/migrations`, `docs/`. One repo; the components ship together. |
| 1.2 | Branch consolidation | Merge or close `copilot/upgrade-wails-v3`, `copilot/upgrade-wails-app-to-v3`, `jules-*`, `wails-v3-upgrade-*` into one `feat/desktop-agent` (S3-03). Remove the `wails/v3` requirement from `go.mod` (S2-10). |
| 1.3 | Migration tooling | Adopt `dbmate`. `db/migrations/00000000000001_baseline.sql` captures the **current production** schema exactly — dumped from production, not from `DB/pcconnect.sql`, which is a different generation (S2-02). |
| 1.4 | Staging environment | A second VM (or a second compose project) with an anonymised copy of production. First time any change can be tested off production (S3-07). |
| 1.5 | CI | GitHub Actions: lint, typecheck, test, `gitleaks`, `npm audit`, `govulncheck`, `trivy`, build. Blocking on `main` — closes S3-01 (no CI, no tests). |
| 1.6 | Baseline observability | `pino` structured logs, `/healthz`, `/readyz`, `/metrics`, Sentry on all components (S3-09). Deployed against the **existing** Node service so there is a before-picture. |
| 1.7 | Delete the dead PHP front-controller | `api/` (S2-01) — four zero-byte classes that cannot run. |

**Exit gate:** a schema change can be applied to staging and rolled back by a CI run; a monthly
restore rehearsal job is green; branch count is down to `main` plus active work.

---

## Phase 2 — Backend consolidation

**Goal:** one backend, serving both the new contract and the old wire format, with no client change.

| # | Action | Detail |
|---|---|---|
| 2.1 | Port `api_node` JS → TypeScript on Fastify | Behaviour-preserving. Fixes S2-11 (`const { crypto }`) and S2-12 (circular import) structurally. |
| 2.2 | Zod schemas per route; generate `openapi/pcconnect-v2.yaml` from them | The document in this repo becomes generated output, not prose |
| 2.3 | Implement `/v2/*` per [04](04-api-contract.md) against the **existing** schema | Command TTL and the append-only table land here even though the old columns still exist |
| 2.4 | Implement `/legacy/*` shim reproducing C1 byte-for-byte | Golden-file tests captured from the live PHP responses first |
| 2.5 | Move Socket.IO auth into the handshake; add the Valkey adapter | Fixes S2-06; makes restarts non-destructive |
| 2.6 | CORS allow-list; bearer-only API; security headers | Fixes S1-11, S1-12 |
| 2.7 | Rate limiting in Valkey | Per [03 §6](03-security-architecture.md) |
| 2.8 | Authorisation test matrix | For every resource: owner 2xx / other user 404 / wrong scope 403 / no token 401 |
| 2.9 | **Shadow traffic** | Mirror production requests at the new service, compare responses against PHP, alert on divergence. Read-only paths first. |
| 2.10 | Cut over | Point `pcconnect.adamkhattab.co.uk/api/*` at the shim. Legacy clients notice nothing. |

**Exit gate:** shadow comparison shows zero unexplained divergence for 7 days; the shim serves 100%
of legacy traffic for 72 h with error rates at or below the PHP baseline; rollback is a DNS or proxy
change tested at least once.

**Rollback:** repoint the proxy at PHP. The database has not changed.

---

## Phase 3 — Identity and security cutover

**Goal:** close every S1 finding. This is the phase the whole plan exists for.

| # | Action | Ordering constraint |
|---|---|---|
| 3.1 | `user_credentials` table; move hashes; `algo='legacy_sha256_unsalted'` | Before 3.2 |
| 3.2 | Argon2id verification path + upgrade-on-login ([02 §6](02-data-architecture.md)) | Before 3.3 |
| 3.3 | Ship clients that send the **plaintext** password | Nothing can be upgraded until clients stop pre-hashing |
| 3.4 | Token pairs: `refresh_tokens`, rotation, family reuse detection | After 3.2 |
| 3.5 | Scopes on tokens; `command:issue` and `command:receive` made disjoint | After 3.4 |
| 3.6 | Device pairing: `devices`, `device_credentials`, `device_pairings`; pairing UI in agent and app | After 3.5 |
| 3.7 | Password policy on signup, change **and** reset (fixes S1-10) | Any time after 3.2 |
| 3.8 | Envelope encryption: KEK in the secret manager, per-user DEK, AES-256-GCM | Before 3.9 |
| 3.9 | Re-encrypt every reminder ([02 §7](02-data-architecture.md)) | **Must complete before `users.api_key` is dropped in P4** |
| 3.10 | `security_events` for every auth decision | Any time |

**Exit gate:** all S1 findings closed and verified by test; `SELECT algo, COUNT(*) FROM
user_credentials` shows a falling legacy count; V2 (unmigrated reminders) returns zero; an
independent security review of the auth paths is complete.

**The critical ordering hazard:** dropping `users.api_key` before 3.9 finishes destroys every
reminder irrecoverably, because the API key *is* the decryption key today (S1-06). A guard
migration asserts `SELECT COUNT(*) FROM reminders WHERE body_ciphertext IS NULL = 0` and aborts
otherwise.

---

## Phase 4 — Data migration

**Goal:** reach the v2 schema by expand/contract, with no client downtime.

Per-table sequence and gates: [02 §5](02-data-architecture.md).

| # | Action |
|---|---|
| 4.1 | **Expand** — create v2 tables and columns; `devices.legacy_pcid` bridges to `pcnames.PCID` |
| 4.2 | **Dual write** — the API writes both shapes; V5 divergence query runs continuously |
| 4.3 | **Backfill** — batched, resumable, idempotent; users → devices → reminders → nav/feedback |
| 4.4 | **Verify** — V1–V5 return zero for 24 consecutive hours |
| 4.5 | `utf8mb3 → utf8mb4` conversion, table by table ([02 §5.2](02-data-architecture.md)) — fixes S2-08 |
| 4.6 | Timezone backfill: `users.timezone` from `verifications.Current` where available, else `Europe/London`; recompute `reminders.due_at_utc` — fixes S2-07 |
| 4.7 | `Recurrence_*` → `rrule` where parseable; unparseable logged and nulled — fixes S2-09 |
| 4.8 | **Cutover** — reads move to v2 tables |
| 4.9 | **Contract** — drop `pcnames.Request/.Value/.Time`, `users.api_key`, `users.DateOfBirth`, `reminders.Recurrence_*`; drop `apikeys`, `requests`, `time`, `code`; merge `links`+`menupages` → `nav_items` |

**Exit gate:** verification queries at zero for 7 days after cutover; a restore rehearsal against
the v2 schema succeeds; 4.9 blocked until the P3 guard passes.

**Rollback:** through 4.8, revert the application — old columns still exist and are still written.
After 4.9, restore from backup. That asymmetry is why 4.9 is last and separately approved.

---

## Phase 5 — Client convergence

**Goal:** every user on a modern client, so the legacy surface can be switched off.

| # | Action |
|---|---|
| 5.1 | Desktop agent to parity: pairing, commands with TTL and ack, reminders, fullscreen window, tray, signed auto-update |
| 5.2 | Flutter app to parity: pairing-code entry, command issue with live status, reminders with recurrence, biometric gate, sessions |
| 5.3 | Web dashboard: devices, reminders, account, sessions |
| 5.4 | Beta with real users on both platforms; fix what the telemetry shows |
| 5.5 | GA: Play Store, App Store, signed Windows installer |
| 5.6 | **Final legacy releases** — PCClient and the Java app get one build each that checks discovery and shows a blocking "please install the new client" prompt |
| 5.7 | Publish sunset dates on the website, in-app, and by email |
| 5.8 | Watch `pcconnect_legacy_requests_total{endpoint}` fall |

**Exit gate:** legacy traffic **below 1% of requests for 14 consecutive days**, and at least 6 months
elapsed since the first `Deprecation` header. Both, not either.

---

## Phase 6 — Decommission

| # | Action |
|---|---|
| 6.1 | Raise `minimumSupportedClient` above every legacy build |
| 6.2 | `/legacy/*` returns `410 Gone` with a link to the installer |
| 6.3 | Delete the shim; delete `PCClient/` and `App/` from the default branch (tagged for history) |
| 6.4 | Unpublish the old Android app |
| 6.5 | Set remaining `legacy_sha256_unsalted` accounts to `pending_verification` and email a reset link — no silent lockout |
| 6.6 | Drop `pcnames`, `verifications`, `verificationtypes`, `legacy_pcid` |
| 6.7 | Decommission the PHP host; keep the marketing site |
| 6.8 | Archive `docs/architecture/00-current-state.md` as historical and rewrite it as the operations picture |

**Exit gate:** no `/legacy/*` request in 30 days; every S1/S2 finding closed; the runbook is current.

---

## 2. Risk register

| Risk | L | I | Mitigation | Owner phase |
|---|---|---|---|---|
| **Reminder data destroyed by dropping `api_key` before re-encryption completes** | Med | **Critical** | Guard migration asserting zero unmigrated rows; drop is a separate, separately-approved migration | P3/P4 |
| Users never update; legacy runs forever | High | Med | Gate on measurement not date; blocking update prompt in a final legacy release; keep the shim as long as it takes | P5 |
| Shim diverges from PHP and breaks installed clients | Med | High | Golden-file tests captured from live responses; shadow traffic comparison before cutover | P2 |
| Backfill corrupts or loses data | Low | **Critical** | Idempotent resumable batches; V1–V5 continuous verification; verified restore before starting | P4 |
| `utf8mb3→utf8mb4` breaks an index or locks a table | Med | Med | Smallest tables first; low-traffic window; shorten over-long indexed columns first; rehearse on staging | P4 |
| Auth rewrite locks users out | Med | High | Dual-accept login during transition; staged rollout; account-recovery path tested before release | P3 |
| Solo-maintainer bandwidth stalls the plan mid-migration | **High** | Med | Every phase is independently valuable and leaves the system in a shippable state; dual-write windows are tolerable indefinitely | all |
| Wails v3 churn re-consumes the schedule | Med | Med | Pin v2; v3 is a post-GA project ([ADR-0006](adr/0006-desktop-client-technology.md)) | P1/P5 |
| Cloudflare or the VM is a single point of failure | Med | Med | Documented restore-to-new-host runbook, rehearsed once | P1 |

---

## 3. Sequencing rationale

**Why security before the schema.** Every S1 finding is exploitable today. The schema is ugly but
not actively harming users. Doing data first would mean carrying the exposure for months longer.

**Why the shim before anything else changes.** It decouples backend work from client releases
entirely. Without it, every backend change waits on an MSI and a Play Store review.

**Why the clients last.** They are the slowest step (store review, user-driven installs) and they
depend on the API being final. Starting them earlier means rewriting them against a moving contract.

**Why decommissioning is a phase.** Deleted code is the only code with no bugs and no maintenance
cost. Left implicit, the legacy surface survives forever — which is exactly how three parallel API
generations came to exist.

---

## 4. Definition of done

The migration is complete when all of the following hold:

- [ ] Every S1 finding closed and covered by a regression test
- [ ] Every S2 finding closed or explicitly accepted with a written rationale
- [ ] One API contract; `oasdiff` gates breaking changes; every client model generated from it
- [ ] `git grep -iE '(password|secret|api[_-]?key)\s*=\s*["\x27]'` finds nothing
- [ ] Schema reproducible from `db/migrations` on an empty database
- [ ] Monthly restore rehearsal green for three consecutive months
- [ ] `pcconnect_command_stale_executions_total` has never left zero
- [ ] Zero `/legacy/*` requests for 30 days
- [ ] `PCClient/` and `App/` removed from the default branch
- [ ] Runbook covers deploy, rollback, restore, key rotation, and incident response

---

Previous: [06 — Client Architecture](06-client-architecture.md) · Next: [08 — Platform & Delivery](08-platform-and-delivery.md)
