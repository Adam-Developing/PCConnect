# Verification and acceptance matrix

Every row is an automated release gate unless marked as an operational exercise.
OS commands are represented by a fake executor in CI; destructive integration
tests run only on disposable, isolated Windows test machines.

| Area | Required scenarios | Evidence |
|---|---|---|
| Contracts | OpenAPI/AsyncAPI/JSON parse; local refs resolve; public enums equal SQL/IPC enums; examples validate | `python contracts/check_contracts.py` plus generated C#/Kotlin model compile |
| Schema | Empty PostgreSQL 18 apply; constraints/indexes exist; repeatable migrations; downgrade policy documented | Testcontainers integration suite |
| Passwords | Argon2 login; one-time SHA migration; wrong/disabled/reset-required accounts; migration transaction rollback | Identity integration tests |
| Sessions | Access expiry; refresh rotation; concurrent refresh; reuse revokes family; logout/session/device revocation | Identity integration and realtime tests |
| Passkeys | Registration/login; replayed/expired challenge; wrong RP/origin; UV-required step-up; revoked session | WebAuthn conformance vectors and API tests |
| Authorization | Cross-user IDs for every resource; device credential on controller route; revoked device; missing capability | Generated authorization matrix |
| Commands | All six types; risk policy; duplicate idempotency key; unsupported capability; offline/expired target | API and application tests |
| Delivery | Competing claims; 30-second lease; lost SignalR hint; duplicated event; agent crash; reconnect/cursor catch-up | PostgreSQL/Valkey integration tests |
| Executor | Fixed allowlist; no arbitrary args; accepted semantics; permission/no-session/replay failures | Windows fake executor and isolated VM tests |
| Named pipe | ACL/SID/PID checks; nonce/replay; unknown version/type; oversized/truncated frames | Windows IPC security tests |
| Reminders | One-shot/recurring; all/selected targets; DST gap/overlap; device added later; offline delivery/ack | Worker integration tests with fixed clocks |
| Encryption | AES-GCM round trip/tamper/AAD; master-key rotation/rewrap; missing/wrong key; no plaintext logs | Cryptography and observability tests |
| Android | Keystore wrapping; backup exclusion; API 24 password path; API 28+ passkey path; no offline command queue | Unit, emulator and instrumentation tests |
| Migration | Full/delta/rerun; stable mappings; collisions; orphans; malformed dates; checksums; no PII in manifest | Sanitized rehearsal reports |
| Sunset | Day 44 compatibility; day 45 legacy command denial; day 60 total credential/route denial | Clock-controlled compatibility suite |
| HTTP security | HTTPS redirect/HSTS/CORS/CSP/nosniff; body limits; problem details; no exception leakage | External staging smoke tests |
| Load (operational exercise) | 1,000 hubs, 50 RPS, 10 commands/s, auth memory pressure, outbox recovery | Staging-only k6 scripts and production-class VPS load report |
| Recovery (operational exercise) | Base backup + WAL + key restore; deletion tombstone replay; RPO/RTO measurement | Monthly isolated restore exercise |
| Release | Clean builds; tests; secret/dependency/container scans; SBOM/provenance; MSI/AAB signatures | Protected CI release job |

The repository's `Architecture contracts` workflow already runs the
dependency-free cross-contract validator, validates JSON Schema metaschemas and
applies the canonical DDL to a clean PostgreSQL 18 service. Product implementation
adds model generation and client/server compilation to this baseline.

The staging HTTP suite is `deploy/verify-staging.sh`. Capacity profiles are in
`tests/load/` and refuse to run without explicit staging-only environment
guards. Those profiles are release evidence only after execution on the target
VPS class; merely parsing the scripts is not a passed capacity gate.

## Contract acceptance examples

The generated C# and Kotlin models must round-trip at least:

- Password and passkey token responses without exposing refresh tokens in logs.
- A Windows device with every capability and a future macOS device with a
  subset, proving platform-neutral parsing.
- Command creation, claim, accepted and typed failure resources.
- Account-wide recurring reminder plus two device deliveries.
- Every realtime event with unknown additional payload fields ignored.
- RFC 9457 validation, authentication, rate-limit and conflict responses.

## Migration exit gates

- Two consecutive sanitized rehearsals yield identical stable IDs and counts.
- All quarantines/collisions have signed decisions; none are silently dropped.
- Cutover and rollback complete inside the maintenance budget.
- Legacy traffic is zero for seven days before route removal.
- Day-60 tests prove legacy API keys and SHA-shaped credentials cannot mint or
  use a v2 session.
