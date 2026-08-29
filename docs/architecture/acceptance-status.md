# PCConnect v2 acceptance status

This register records implementation evidence without treating an unexecuted
environmental exercise as a pass. It must be updated with immutable CI run links,
staging reports and approvals before a production migration is proposed.

## Repository-verifiable controls

| Area | Implemented evidence | Current verification |
|---|---|---|
| Contracts/schema | OpenAPI, realtime and IPC contracts; canonical PostgreSQL 18 DDL; embedded EF migration; cross-contract validator | Contract validator passes locally. The complete 12-scenario integration suite passed locally against disposable PostgreSQL 18.3 Testcontainers, including empty-schema apply and security constraints. Protected CI must reproduce it. |
| Passwords/sessions | Argon2id 64 MiB/3/1, atomic legacy upgrade, generic failures, rotating opaque tokens, reuse-family revocation, logout/revocation | Unit tests and PostgreSQL 18 integration tests pass locally, including upgrade rollback, wrong/disabled/reset-required states, sequential and concurrent rotation/reuse, logout, and user/device credential invalidation. |
| Passkeys/step-up | Fido2NetLib WebAuthn registration/login, five-minute single-use challenges, HTTPS plus signing-bound Android origins, RP-host Digital Asset Links, UV-required intent-bound passkey step-up, password fallback | Code and contract compile; preflight/release gates validate the public Android signing identity. Real authenticator/conformance vectors and wrong-origin/replay ceremonies remain a protected staging/device gate. |
| Authorization/devices | Separate controller/device policies, ownership-filtered resources, device-code enrolment, credential families, revocation and explicit Windows SID authorization | The local PostgreSQL API matrix passes role separation, cross-user device/command IDs, capability denial, enrolment and revocation checks. |
| Commands/delivery | Durable ledger/events/outbox, idempotency, expiry, 30-second claim lease, claim ownership, local replay key, legal transition state machine, REST recovery | Unit policy tests and the PostgreSQL lifecycle scenario pass locally: duplicate creation, recovery, competing claims, replay mismatch and duplicate terminal acknowledgement. |
| Reminders/encryption | AES-GCM envelope encryption, versioned AAD, rewrap rotation, one-shot/recurrence/DST, selected/all delivery, offline recovery/ack | Crypto and DST tests pass. The local PostgreSQL worker scenario passes incapable/offline/selected/late-enrolled delivery, plaintext recovery and duplicate acknowledgement checks. |
| Compatibility/migration | Exact legacy adapter routes, keyed legacy credentials, day-45/day-60 policy, deterministic full/delta/rerun importer, quarantine/collision manifest | Clock policy and malformed dry-run tests pass locally. PostgreSQL full/delta/rerun stable-mapping and malformed/orphan quarantine scenarios pass locally. PHP entrypoints fail closed with 410. |
| Windows | .NET 10 service, WPF companion, DPAPI stores, authenticated length-prefixed named pipe, SID/PID/session controls, replay cache and fixed executor | Seven IPC/executor unit tests and clean builds pass. Self-contained EXEs and MSI build locally; full ICE and Authenticode are protected Windows CI gates. |
| Android | Kotlin/Compose, Retrofit/Room, Keystore-wrapped session storage, AES-GCM/Keystore-encrypted reminder cache, SignalR plus REST recovery, account/enrollment/device/SID/passkey management, verified email/reset App Links, API 24 password and API 28 passkey gating | Unit/lint/debug/release-R8 builds passed locally with an ephemeral throwaway key. AndroidJUnitRunner passed all three Keystore/encrypted-cache tests on a read-only API 37 emulator; protected CI still runs the required API 24/28 matrix. Real release-origin passkey ceremonies remain a staging gate. |
| HTTP/operations | Trusted-proxy boundary, HTTPS/HSTS/security headers/CORS/body limit, JSON logs, OTel, alerts, digest-only deployment references, blue/green scripts | Static/YAML/Compose checks pass. `verify-staging.sh` performs the external HTTP assertions only in staging. |
| Release/supply chain | Locked dependencies, pinned actions, release-base digest variables, Gitleaks/Trivy, SBOM, provenance, MSI/AAB/container signing and verification | Workflow and hygiene validation pass locally. Protected credentials, official base digests and a hosted release run are still required. |

## Required external evidence before production

The following are deliberately not marked complete because the repository
cannot manufacture their trust material or target-environment results:

1. A successful protected CI run with PostgreSQL Testcontainers, both Android
   emulators, full WiX ICE validation, live dependency/secret/container scans,
   and artifact provenance.
2. Existing Android application signing authority (or an approved Play App
   Signing key upgrade/lineage), a trusted Windows code-signing certificate and
   timestamp service, and verified digest pins for official .NET/platform images.
3. A sanitized read-only inventory of the actual legacy schema and two approved
   full/delta rehearsals with identical mappings, resolved collision decisions,
   measured cutover/rollback time, and no production writes.
4. Staging smoke evidence, the 1,000-hub/50-RPS/10-command-per-second k6 reports,
   Argon2 memory-pressure results, and an isolated Windows VM executor run that
   never targets a real user machine.
5. An isolated base-backup, WAL, encrypted key restore and deletion-tombstone
   replay report proving the 15-minute RPO and four-hour RTO.
6. Explicit production approval, a change ticket, fresh backup evidence,
   rollback ownership and the full 60-day traffic/adoption record. Production
   deployment, credential rotation and database mutation remain prohibited
   without that approval.

The presence of a local ignored legacy JKS or database dump is not signing or
migration authority. Neither is read, moved, deleted, uploaded or used by the v2
tooling. Historical secret containment and any Git-history rewrite remain an
owner/security operation rather than an automated modernization step.
