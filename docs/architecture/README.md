# PCConnect v2 architecture

**Status:** approved implementation baseline

**Last updated:** 26 August 2026

**Owners:** PCConnect engineering and operations

This directory is the authoritative architecture for PCConnect v2. It replaces
the incompatible PHP, Node/Socket.IO, Wails, Flutter, Android Java, and VB.NET
designs found in repository history. Legacy code remains only as migration
input; it does not define v2 behaviour.

## Decision hierarchy

When documents disagree, use this order:

1. Versioned machine-readable contracts in [`../../contracts`](../../contracts).
2. The canonical schema in [`../../DB/v2-canonical-schema.sql`](../../DB/v2-canonical-schema.sql).
3. Architecture decisions in [decisions.md](decisions.md).
4. The explanatory documents in this directory.
5. Legacy source and `api/api_spec.md` only as compatibility evidence.

Any change to a public enum, state transition, token lifetime, route, event, or
database invariant must update all affected artifacts in one pull request and
include a compatibility note.

## Architecture pack

| Document | Purpose |
|---|---|
| [system.md](system.md) | C4 context, container, deployment, module and client architecture |
| [protocols.md](protocols.md) | Authentication, enrollment, commands, realtime, reminders and IPC flows |
| [data-model.md](data-model.md) | Canonical ERD, ownership, encryption and retention model |
| [security.md](security.md) | Threat model, trust boundaries, secrets, encryption and privacy controls |
| [decisions.md](decisions.md) | Binding architecture decisions and rejected alternatives |
| [migration.md](migration.md) | Live-data discovery, importer, cutover, 60-day compatibility and rollback |
| [operations.md](operations.md) | VPS topology, delivery, SLOs, observability, backup and recovery |
| [test-matrix.md](test-matrix.md) | Contract, security, migration, resilience and client acceptance tests |
| [acceptance-status.md](acceptance-status.md) | Implemented evidence, unexecuted environmental gates and production blockers |

## Machine-readable artifacts

- `contracts/openapi-v2.json` — OpenAPI 3.1 REST contract.
- `contracts/realtime-v2.json` — AsyncAPI 3.0 SignalR event contract.
- `contracts/named-pipe-v1.schema.json` — Windows service/companion IPC envelope.
- `contracts/migration-manifest.schema.json` — importer reconciliation output.
- `DB/v2-canonical-schema.sql` — executable PostgreSQL schema.
- `DB/legacy-mapping.md` — discovery-driven legacy mapping rules.

Run `python contracts/check_contracts.py` from the repository root to validate
JSON syntax, local references, enum consistency, required security controls and
the SQL/contract vocabulary.

## Scope and platform policy

- Migration implementations: Windows agent/companion and Android controller.
- Future consumers: iOS controller and Linux/macOS agents use the same REST,
  realtime, enrollment, capability and state contracts.
- Production identity origin: `pcconnect.adamdeveloping.co.uk`.
- Production API: `https://api.pcconnect.adamdeveloping.co.uk/api/v2`.
- Legacy host: compatibility proxy only, with an absolute 60-day sunset.
- Initial deployment: one 4-vCPU/8-GB VPS; components remain independently
  deployable when scale requires more hosts.

## Non-negotiable invariants

- PostgreSQL is the source of truth; SignalR is a non-durable notification hint.
- No public API accepts password hashes or legacy API keys.
- No client can submit an arbitrary command or executable argument.
- Every command has an immutable event history, expiry and idempotency key.
- High-risk commands require a server-verified, intent-bound step-up grant.
- Reminder plaintext and credentials never appear in logs, audit payloads or
  migration reports.
- All external time values are UTC ISO-8601 instants plus an IANA timezone when
  local scheduling semantics matter.
- Compatibility code has a deployment-time sunset and cannot be re-enabled
  without a new security review and architecture decision.
