# Security and privacy architecture

## Trust boundaries and assets

| Boundary | Untrusted input | Protected assets |
|---|---|---|
| Internet → Caddy/API | All headers, bodies, tokens and hub frames | accounts, commands, reminders, availability |
| API/worker → PostgreSQL | application queries and migrations | credentials, PII, durable state, audit trail |
| API/worker → Valkey | cache/rate-limit/backplane data | availability and session revocation latency |
| Cloud → Windows service | typed command IDs and resources | power/session-control capability |
| Service → companion | named-pipe messages and caller identity | interactive user session and reminder plaintext |
| Android storage | restored files, rooted-device access | refresh credential and cached PII |
| CI/release | source, dependencies and signing requests | publisher identity and distributable binaries |

High-value assets are service secrets, password verifiers, refresh credentials,
passkey public-key records, reminder encryption keys/plaintext, signing keys,
command authority and the audit trail.

## Authentication and authorization

- Public authentication errors are generic and RFC 9457-compatible. Store only
  keyed token hashes and constant-time compare credential material.
- Rate-limit login/reset by normalized account and risk-scored source IP;
  enrollment by device code and IP; commands by actor and target device.
  Progressive backoff never permanently locks an account through unauthenticated
  requests.
- Authorization is resource-based. Every account, session, device, command,
  reminder, target, delivery and export query includes owner identity in the
  database predicate; identifiers alone never authorize access.
- Step-up grants are single-use database records. The server validates
  authentication method/reference, `auth_time`, session, target and intent.
  Android `BiometricPrompt` may unlock a passkey or local secret but is not an
  authentication assertion by itself.
- Device credentials authorize agent endpoints for one device only. They cannot
  create commands, manage accounts or read another device's reminders.

## Command boundary

- Validate command enums in OpenAPI, application code, database constraints,
  Windows IPC schema and the final executor.
- Persist before notifying, claim before executing and record acceptance before
  initiating a potentially terminating action.
- Never deserialize a command into a shell string. Native executors receive a
  closed enum and construct fixed OS API calls internally.
- The named pipe uses a fixed name containing the protocol major version,
  service-created ACLs, verified client process token/SID, length-prefixed UTF-8
  JSON, a 64 KiB maximum frame, request IDs and a challenge nonce. Reject unknown
  message types and protocol versions.

## Secrets and cryptography

- Production secrets enter as Docker secrets or host files in a root-controlled
  directory, mounted read-only and owned by the consuming non-root container
  UID where local Compose cannot remap file ownership. No `.env`, database password, CAPTCHA/API secret, encryption key or
  signing key belongs in source, images, logs or CI artifacts.
- Maintain separate keys for token hashing, reminder wrapping, ASP.NET Data
  Protection and backup encryption. Give each an ID and rotation runbook.
- Persist the ASP.NET Data Protection key ring outside ephemeral containers and
  encrypt it at rest. Blue/green instances share the same protected ring.
- Use TLS 1.2+ externally, HSTS after all owned subdomains are HTTPS-ready,
  origin allowlists rather than wildcard CORS, explicit preflight handling,
  request/body limits and safe response headers.
- Use a protected code-signing service/certificate for Windows and Play App
  Signing for Android. CI receives signing authority only in protected release
  jobs.

## Data minimization and lifecycle

- Do not log tokens, hashes, passkey challenges, reminder text, verification
  codes, full email addresses or raw IP addresses. Use correlation IDs and
  pseudonymous actor/device IDs.
- Security audit payloads use an allowlisted schema and append-only storage.
- User export is asynchronous, encrypted, authenticated and short-lived.
  Account deletion immediately revokes sessions and devices, then the worker
  deletes user content and records a non-identifying tombstone needed to prove
  completion.
- Backups follow their documented expiry; deletion cannot rewrite historical
  immutable backup media immediately. Restored backups must replay deletion
  tombstones before service exposure.
- The legacy hosted database is encrypted/read-only after cutover, retained for
  at most 90 days after the compatibility sunset, then destroyed with evidence.

## Required security gates

- Threat-model review for changes to identity, enrollment, commands, IPC,
  encryption, compatibility or release signing.
- Secret, dependency, static-analysis, manifest/permission and container-image
  scans on every pull request; unresolved high/critical findings block release.
- Cross-tenant authorization and token-replay tests run against a real disposable
  PostgreSQL instance.
- Quarterly restore exercise, credential rotation exercise and command abuse
  review during the migration year; at least annually thereafter.
