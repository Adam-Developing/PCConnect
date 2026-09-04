# ADR-0001 — Backend runtime and framework

**Status:** Accepted
**Date:** 2026-09-02
**Context docs:** [00 §2](../00-current-state.md), [01 §5](../01-target-architecture.md)

## Context

Three backend implementations exist for the same product (finding §00.3):

- **C1** — the deployed PHP endpoints, not in this repository, serving 100% of production traffic.
- **C2** — `api/`, a PHP front-controller that **cannot run**: `Auth.php`, `Database.php`,
  `Router.php` and `AuthController.php` are zero-byte files and `index.php` requires all of them
  (S2-01).
- **C3** — `api_node/`, an Express 5 + Socket.IO + mysql2 service that works and is not deployed.

Contract drift between them is the root cause of most defects in the register. Consolidating to one
implementation is not optional; the question is which.

Constraints: one part-time maintainer; a persistent WebSocket per device is core to the product; the
clients are Go, Dart and TypeScript, so a shared type story has real value; installed legacy clients
must keep working through a shim.

## Decision

**Node 22 LTS + TypeScript 5, on Fastify 5 with `fastify-type-provider-zod`, retaining Socket.IO.**

Port `api_node` to TypeScript rather than rewriting it. Every route is defined by a Zod schema;
Fastify validates requests *and responses* against it at runtime and the OpenAPI 3.1 document is
generated from the same schemas at build time.

## Options considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Fastify + TS** (chosen) | Reuses the working `api_node` logic; schema-first gives OpenAPI generation for free, which directly attacks the drift problem; native `async` error handling; ~2× Express throughput; excellent Socket.IO integration | A port, not a no-op; Fastify's plugin/encapsulation model is a learning step | **Chosen** |
| Express 5 + TS + manual Zod | Smallest diff from today | OpenAPI must be hand-maintained — reintroducing exactly the prose-contract failure being fixed; response validation needs bespoke middleware | Rejected |
| Stay on PHP, finish C2 | Same host, same deployment as the marketing site | Would have to be written from scratch (the classes are empty); no shared types with Go/Dart/TS clients; long-lived WebSockets are awkward in PHP-FPM; would need a second runtime for realtime anyway | Rejected |
| Rewrite in Go | Single language with the desktop agent; excellent concurrency; single-binary deploy | Discards the whole `api_node` implementation including the working Socket.IO room model; largest schedule cost at the moment the priority is closing S1 findings | Rejected |
| NestJS | Batteries included, strong conventions | Heavy DI and decorator ceremony for ~30 endpoints and one maintainer | Rejected |

## Consequences

**Positive**
- One backend. The contract becomes generated output, and `oasdiff` can fail the build on a breaking
  change ([04 §1](../04-api-contract.md)).
- Response validation means a handler cannot return an undocumented field.
- TypeScript's compiler catches the whole class of bug that `const { crypto } = require('crypto')`
  (S2-11) belongs to.
- The module structure ([01 §3.1](../01-target-architecture.md)) removes the `routes.js ↔ server.js`
  circular import (S2-12) structurally rather than with a try/catch.

**Negative**
- A build step now exists where none did. Deployment gains a compile stage.
- Node's single-threaded model means a CPU-bound operation blocks the loop — relevant because
  Argon2id is deliberately expensive. Mitigated by running Argon2 in a worker pool and tuning
  `ARGON2_MEMORY_KIB` against measured p99 login latency.
- PHP hosting knowledge accumulated on this project stops applying to the API.

**Neutral**
- The marketing site stays PHP; only the API moves.

## Revisit when

- Argon2id or another CPU-bound workload cannot be kept off the event loop without contortion —
  then a Go rewrite becomes worth its cost.
- The team grows past one person and a stronger convention framework starts paying for itself.
