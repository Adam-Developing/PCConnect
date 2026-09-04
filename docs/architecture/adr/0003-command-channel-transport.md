# ADR-0003 — Command channel transport and delivery semantics

**Status:** Accepted
**Date:** 2026-09-02
**Context docs:** [05](../05-realtime-architecture.md), [02 §3.2-3.3](../02-data-architecture.md)

## Context

Commands reach a PC by two mechanisms that do not agree with each other:

- **The mailbox** — `UPDATE pcnames SET Value=1, Request=?` (`routes.js:150`). A single mutable row
  per device. A second command overwrites an undelivered first (S2-04). `Value=1` persists
  indefinitely, so a `Shutdown` issued at 09:00 executes when the laptop is opened at 18:00 (S2-03).
- **The push** — `PushManager.pushCommand` emits over Socket.IO to `user_{id}_pc_{pcId}`
  (`server.js:63-70`). Nothing reconciles the two: an agent may execute from the push and a poll may
  serve the same command again (S2-05).

Socket sessions live in a process-local object (`server.js:20`) that is never swept and is lost on
restart (S2-06), so the service cannot be restarted without dropping every device and cannot run more
than one instance.

There is also no record of who issued a command or whether it ran. For an operation that powers off
someone's computer, that is the gap that matters most.

## Decision

**Socket.IO 4 over WebSocket with the Valkey adapter**, authenticated in the **handshake** by access
token, delivering an **append-only command lifecycle with a mandatory TTL and per-command
acknowledgement**.

- `commands` is append-only with a client-generated UUIDv7 id and an explicit state machine
  (`issued → delivered → succeeded|failed`, or `expired|cancelled`).
- `expires_at` defaults to 120 s. A command not delivered inside its window is never executed.
- The agent acks a **specific command id** with an outcome, replacing "clear the mailbox".
- Every transition writes a `command_events` row in the same transaction.
- HTTP polling remains as a fallback only, governed by the existing
  `internal/realtime/policy.go` backoff plus jitter.

## Options considered

### Transport

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Socket.IO + Valkey adapter** (chosen) | Both target clients already speak it; reconnection, heartbeats, rooms and long-polling fallback come free; the adapter makes restarts and scale-out non-destructive | Protocol overhead vs raw WS; the Go client currently hand-rolls the Engine.IO handshake | **Chosen** |
| Raw WebSocket | Minimal; no framework | Reconnect, backoff, heartbeat, rooms and fan-out all rebuilt by hand — that is the bulk of what Socket.IO provides, reimplemented worse | Rejected |
| Server-Sent Events | Trivial server side; survives proxies well | Unidirectional — no channel for `command.ack` or heartbeats without a second mechanism | Rejected |
| MQTT | Purpose-built for device fan-out; QoS levels | A whole broker to operate; no browser story without a bridge; overkill at ~1k devices | Rejected |
| Push only (FCM/APNs/WNS) | No persistent connection; battery-friendly | Best-effort with unbounded latency — incompatible with a p95 < 500 ms target; a third-party dependency in the critical path for shutting down a PC | Rejected as primary; retained as a future wake-up hint for a sleeping mobile app |
| Long polling only | Simplest; works everywhere | Latency and server load are exactly what the product is trying to escape (S2-14) | Rejected as primary; kept as fallback |

### Delivery semantics

| Option | Verdict |
|---|---|
| At-most-once, TTL-bounded, per-command ack (**chosen**) | An unexecuted shutdown is a mild annoyance; a spurious one loses the user's work. Bias to not executing. |
| At-least-once with agent idempotency | The agent's replay guard already gives this within its LRU window, but the *server* must not assume it — a command executed twice after an agent restart is a real risk |
| Exactly-once | Not achievable across a network partition; claiming it would be false comfort |

## Consequences

**Positive**
- Stale destructive commands become impossible (S2-03). The TTL is a security control as well as a
  correctness one: it bounds how long a replayed command stays useful.
- Multiple pending commands are representable; nothing is silently overwritten (S2-04).
- Push and poll reconcile through the per-command ack and the `delivered` marking (S2-05).
- Restarting the API no longer drops every device (S2-06); `--scale api=N` becomes possible.
- Every power command has an audit trail: who, when, from which client, and what happened.
- The phone can show real status — `delivered`, `succeeded`, `expired` — instead of the current
  `{"message":"Success"}`, which only means "a database row was written".

**Negative**
- Valkey becomes a runtime dependency. Its failure degrades push to per-instance fan-out and pushes
  agents onto polling; commands stay durable in MySQL, but `/readyz` fails and deploys halt.
- `commands` and `command_events` grow unboundedly without the 90-day retention job. That job is
  part of the design, not an afterthought.
- A TTL introduces a new user-visible failure mode: "your PC was offline, the command was not
  delivered". This is *correct* but must be explained well in the UI, or users will read it as a bug.
- Choosing 120 s is a judgement call. Too short and a briefly-flaky connection loses a legitimate
  command; too long and staleness returns. It is configurable per command (5–600 s) so the default
  can be tuned against the observed expiry ratio.

**Neutral**
- The existing room naming (`user_{id}_pc_{pcId}`) is kept in spirit as `device:{deviceId}` and
  `user:{userId}`, now derived from token claims rather than a client-supplied `PCName`.

## Revisit when

- The expiry ratio stays above 10% with healthy agents — the default TTL is then wrong.
- Mobile background delivery becomes a requirement, at which point FCM/APNs join as a wake-up hint
  that triggers a socket connect, still without entering the trust path.
