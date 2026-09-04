# ADR-0008 — API versioning and legacy sunset

**Status:** Accepted
**Date:** 2026-09-02
**Context docs:** [04 §2, §5](../04-api-contract.md), [07 Phase 5-6](../07-migration-plan.md)

## Context

Two installed client generations cannot be force-updated: an MSI-distributed VB.NET desktop client
and a Play Store Android app. Both speak the legacy PHP surface (C1) exclusively — a surface that
does not exist in this repository (§00.2).

Meanwhile three incompatible contracts describe the same operations (§00.3), and the only written
spec, `api/api_spec.md`, documents endpoints that no implementation provides. Prose contracts have
demonstrably failed here.

Any cutover strategy must therefore keep installed clients working for an unknown period, while not
letting the legacy surface become permanent — which is exactly how three generations came to coexist.

## Decision

1. **Path versioning** — `/v2/...`.
2. **The contract is generated** from server-side Zod schemas into `openapi/pcconnect-v2.yaml`; every
   client's model layer is generated from that document; `oasdiff` fails CI on an unlabelled breaking
   change.
3. **A `/legacy/*` shim** reproduces the C1 wire format byte-for-byte, implemented as a thin
   translation over the v2 services with **no database access of its own**.
4. **Sunset is gated on measurement, not on a date.** The shim is removed only when
   `pcconnect_legacy_requests_total` is below 1% of requests for 14 consecutive days **and** at least
   6 months have passed since the first `Deprecation` header.
5. **`GET /v2/meta/discovery`** publishes `minimumSupportedClient`, and clients below it show a
   blocking update prompt. This is the lever that ends the legacy era.

## Options considered

### Versioning scheme

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Path (`/v2/`)** (chosen) | Visible in logs and metrics; cacheable; trivial to construct from Go, Dart and VB; obvious in a bug report | Duplicated route trees across versions | **Chosen** |
| `Accept` header negotiation | Purist REST; one URL per resource | Invisible in logs; easy for a client to get wrong; painful in VB.NET | Rejected |
| Query parameter | Simple | Cache-hostile; easy to omit accidentally | Rejected |
| No versioning, never break | No version management | Not credible — the auth model is changing fundamentally | Rejected |

### Legacy strategy

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Shim over v2 services** (chosen) | One implementation of every rule; legacy clients unaffected; every legacy request is measurable | Shim code to write and keep faithful | **Chosen** |
| Keep the PHP running alongside | Zero shim work | Two implementations of every rule, diverging — the current failure, preserved; two hosts, two deployments | Rejected |
| Big-bang cutover | Clean | Breaks every installed client instantly. Unacceptable for software that manages people's computers | Rejected |
| Server-side translating proxy | Language-agnostic | Another moving part; still needs the translation logic written | Rejected |

## Consequences

**Positive**
- Backend work is fully decoupled from client releases. Without the shim, every backend change would
  wait on an MSI build and a Play Store review.
- One implementation of authorisation, rate limiting and command policy, exercised by both surfaces —
  so a fix applies everywhere at once.
- Every legacy request increments a labelled counter, so the sunset decision is made from data.
  `pcconnect_legacy_requests_total{endpoint}` also shows precisely *which* legacy endpoints still
  matter, which sequences their individual removal.
- Generated clients make the §00.3 drift structurally impossible to repeat.

**Negative**
- The shim carries the old weak model forward for a while. Notably `addpc.php` must keep
  auto-registering a device from a self-asserted name (S1-08), because legacy clients have no pairing
  flow. Mitigation: that path is confined to the shim, writes a `security_events` row on every use,
  is rate-limited, and dies with the shim. It is the one accepted residual risk in the design, and it
  is bounded and observable.
- Legacy clients keep using the compatibility token minted from an unsalted SHA-256, so those
  accounts cannot be upgraded to Argon2id until their users move
  ([02 §6](../02-data-architecture.md)).
- A measurement-gated sunset has no guaranteed end date. Mitigation: the blocking update prompt in
  the final legacy releases, the discovery `minimumSupportedClient` lever, and a hard backstop —
  at 12 months, remaining legacy accounts are moved to `pending_verification` with a password-reset
  email rather than being silently cut off.
- Golden-file fidelity tests must be captured from the **live** PHP responses before it is switched
  off. Once the PHP host is gone, that reference is unrecoverable.

**Neutral**
- `/v1` is never published as a supported surface. The legacy prefix is `/legacy`, which makes it
  obvious in logs and dashboards that a request came from an old client rather than an old version
  of the new API.

## Revisit when

- Legacy traffic plateaus above 1% for more than 12 months, at which point the backstop applies and
  this ADR is superseded by a forced-migration decision.
- A second major version (`/v3`) is needed — the same shim pattern should be reused, and this ADR
  extended rather than replaced.
