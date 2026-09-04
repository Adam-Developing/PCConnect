# 05 — Real-Time Architecture

> This is the layer that makes PCConnect feel instant, and the layer where a mistake shuts down
> the wrong computer at the wrong moment.

---

## 1. Requirements

| # | Requirement | Why |
|---|---|---|
| R1 | Command reaches an online agent in **p95 < 500 ms** | The product promise. Polling every 5 s cannot deliver it. |
| R2 | A command is executed **at most once** | Executing `shutdown` twice is not idempotent from the user's point of view — the second one lands after reboot. |
| R3 | A command is **never** executed after its TTL | Fixes S2-03: a shutdown queued at 09:00 must not fire when the laptop opens at 18:00. |
| R4 | Correct behaviour when the socket is down | Users are on flaky Wi-Fi, behind captive portals, and asleep. |
| R5 | The user can see whether the PC is online **and** what happened to their command | Today the phone gets `{"message":"Success"}` meaning "written to a database row". |
| R6 | Survives an API restart and runs on more than one instance | Today all sessions live in a process-local object (S2-06). |

---

## 2. Transport

> **Superseded by [ADR-0009](adr/0009-implementation-platform.md):** SignalR over
> WebSocket, with the StackExchange.Redis (Valkey) backplane. Everything below is
> unchanged in substance — handshake authentication from token claims, per-user and
> per-device groups, a backplane so restarts and `--scale api=N` are non-destructive,
> and polling as the fallback rather than the mechanism.

**Socket.IO 4 over WebSocket, with the Redis/Valkey adapter.**

Both target clients already speak it (`socket_io_client` in Flutter, a hand-rolled Engine.IO
handshake in the Go agent), and it supplies reconnection with backoff, heartbeats, rooms, and
long-polling fallback for hostile networks — all of which would otherwise be rebuilt by hand.
Rationale and rejected alternatives: [ADR-0003](adr/0003-command-channel-transport.md).

The Valkey adapter is what turns the current single-process design into something that can be
restarted and scaled: room membership and cross-instance emits go through the pub/sub bus rather
than living in one Node heap.

```
   Agent ──wss──┐                            ┌── Valkey pub/sub ──┐
   Agent ──wss──┼──▶ api instance #1 ────────┤                    │
   Phone ──wss──┘                            │  rooms, presence,  │
                                             │  adapter fan-out   │
   Phone ──wss─────▶ api instance #2 ────────┤                    │
                                             └────────────────────┘
```

### 2.1 Handshake authentication

The current design authenticates the socket with a cookie set by a REST call, and looks it up in a
process-local map (`server.js:20`, `:90-100`). Two problems: the cookie is `secure:false`, and the
map is lost on restart.

Target: the token is presented **in the handshake auth payload**, verified as a JWT, and never
written to a cookie.

```jsonc
// client → server, on connect
{ "auth": { "token": "<access token>" } }
```

The server verifies the signature, checks expiry, checks the `jti` deny-list, and derives identity
from the claims. Nothing is self-asserted: the `did` claim decides which device room the socket
joins, so the socket cannot subscribe to a device it does not hold a credential for.

Because access tokens live 15 minutes and sockets live longer, the client refreshes over REST and
emits `auth.renew` with the new token. A socket whose token expires without renewal is disconnected
with `auth.expired`, and reconnects with a fresh one.

### 2.2 Rooms

| Room | Members | Carries |
|---|---|---|
| `device:{deviceId}` | the one agent for that device | `command.issued` |
| `user:{userId}` | every one of the user's phones, web sessions and agents | `device.presence`, `command.status`, `reminder.changed` |

A device token can join **only** `device:{its own did}`. A user token can join **only**
`user:{its own sub}`. Both are enforced server-side from claims, never from a client-supplied name.

---

## 3. Event catalogue

Server → client:

| Event | Room | Payload | Meaning |
|---|---|---|---|
| `command.issued` | `device:{id}` | `{id, type, params, expiresAt}` | Execute this if it is still fresh |
| `command.status` | `user:{id}` | `{id, deviceId, status, resultCode?, at}` | Progress for the issuing UI |
| `device.presence` | `user:{id}` | `{deviceId, isOnline, at}` | Online indicator |
| `reminder.changed` | `user:{id}` | `{type: created\|updated\|deleted, reminder, revision}` | Sync across the user's clients |
| `auth.expired` | socket | `{}` | Renew and reconnect |

Client → server:

| Event | From | Payload | Meaning |
|---|---|---|---|
| `command.delivered` | device | `{id}` | Received over the socket; marks the row `delivered` |
| `command.ack` | device | `{id, outcome, resultCode?, resultMessage?}` | Executed, failed, or rejected |
| `device.heartbeat` | device | `{agentVersion, osVersion}` | Liveness; coalesced |
| `auth.renew` | any | `{token}` | Replace the socket's credential in place |

Every event carries the same envelope: `{v: 1, id, at, data}`. `v` allows the event schema to
evolve without breaking older agents.

---

## 4. Command delivery — the state machine

```
                       POST /v2/commands
                              │
                              ▼
                        ┌──────────┐
                  ┌─────│  ISSUED  │─────┐
                  │     └────┬─────┘     │
   ttl elapsed    │          │           │  user cancels
        │         │   push / claim       │        │
        ▼         │          ▼           │        ▼
   ┌─────────┐    │   ┌────────────┐     │  ┌───────────┐
   │ EXPIRED │◀───┘   │ DELIVERED  │     └─▶│ CANCELLED │
   └─────────┘        └──────┬─────┘        └───────────┘
        ▲                    │
        │              agent acks
        │           ┌────────┴────────┐
        │           ▼                 ▼
        │    ┌────────────┐    ┌───────────┐
        └────│ SUCCEEDED  │    │  FAILED   │
   (ttl wins if     └────────────┘    └───────────┘
    no ack arrives)
```

Every transition writes a `command_events` row inside the same transaction as the status update.
For destructive actions on a personal computer, "who asked for this, when, and did it happen" must
be answerable.

### 4.1 The happy path, end to end

```
 Phone                    API                       Valkey            Agent
   │                       │                          │                 │
   │─ POST /v2/commands ──▶│                          │                 │
   │  {id (client UUIDv7), │                          │                 │
   │   deviceId, type,     │ 1 authenticate           │                 │
   │   ttlSeconds:120}     │ 2 scope command:issue    │                 │
   │                       │ 3 device owned by sub    │                 │
   │                       │ 4 type ∈ allowedCommands │                 │
   │                       │ 5 rate budget            │                 │
   │                       │                          │                 │
   │                       │─ INSERT commands (issued)│                 │
   │                       │─ INSERT command_events ──│                 │
   │◀─ 201 {status:issued}─│                          │                 │
   │                       │─ emit device:{id} ──────▶│──── push ──────▶│
   │                       │                          │                 │ 6 agent allow-list
   │                       │                          │                 │ 7 now < expiresAt?
   │                       │                          │                 │ 8 id already seen?
   │                       │◀──────── command.ack ────────────────────  │
   │                       │─ UPDATE succeeded        │                 │ execute argv
   │◀── command.status ────│─ INSERT command_events   │                 │
   │    {succeeded}        │                          │                 │
```

Steps 1–5 are server-side; 6–8 are agent-side and independent. A compromised server still cannot
make an agent run something outside its own six-entry allow-list, and cannot make it run a stale
command.

### 4.2 The agent is offline

The command sits in `issued`. Two outcomes, both correct:

- **Agent returns inside the TTL** → on connect it calls `GET /v2/commands/pending`, receives the
  command, executes it, acks. The phone sees `succeeded`.
- **TTL elapses first** → the sweep marks it `expired`, emits `command.status`, and the phone shows
  "not delivered — your PC was offline". The command is never executed.

This is the behaviour change that fixes S2-03. Today there is no third state: the row sits at
`Value=1` forever and fires whenever the PC next looks.

### 4.3 Both paths fire at once

If the push and a poll race, both may hand the agent the same command. Three things make that safe:

1. `GET /v2/commands/pending` marks rows `delivered` in the same statement that selects them
   (`UPDATE … WHERE status='issued' AND expires_at > NOW()` then read back), so a second poll does
   not re-serve it. The push does the same through `ConfirmDelivery(id)`, which the agent invokes
   on receipt: the same guarded update, narrowed to one command, so whichever path arrives first
   marks the row and the other becomes a no-op. Neither marks delivery on the server's own send;
   only the agent saying it holds the command counts.
2. The agent keeps a bounded LRU of executed command ids and drops duplicates (check 8).
3. The ack is per-command and idempotent — a second ack on a terminal command returns 409 and
   changes nothing.

This is the reconciliation the current dual-write design lacks entirely (S2-05).

---

## 5. Fallback polling

Reuse the policy already written and tested in `PCClientWails/internal/realtime/policy.go` — it is
correct and should be carried forward, not rewritten:

```go
BaseFallbackInterval = 5 * time.Second
MaxFallbackInterval  = 30 * time.Second
NextFallbackInterval // doubles, capped
ShouldPoll(socketHealthy bool) bool { return !socketHealthy }
```

Extensions needed:

- **Jitter.** Add ±20% randomisation. Without it, a server restart makes every agent in the fleet
  reconnect on the same tick — a self-inflicted thundering herd.
- **A connected agent does not poll at all.** `ShouldPoll` already encodes this; the important part
  is that it stays true as the agent grows features.
- **Heartbeat coalescing.** At most one durable `last_seen_at` write per device per minute; live
  presence is a Valkey key with a 90-second TTL refreshed by the socket.

---

## 6. Presence

| Layer | Mechanism | Truth for |
|---|---|---|
| Live | Valkey `presence:device:{id}` with a 90 s TTL, refreshed by socket heartbeats | "Is it online *now*" |
| Durable | `devices.last_seen_at`, written at most once a minute | "When did I last see it" |

On connect and disconnect the API emits `device.presence` to `user:{userId}`. On a **disconnect**
the key is deleted immediately rather than waiting for the TTL, so the phone's indicator goes grey
in about a second instead of a minute and a half.

Presence is advisory. The phone may show a device as online and the command may still expire — which
is exactly why the command carries a TTL and the UI reports the terminal status rather than
optimistically claiming success. The current API's `{"message":"Success"}` on a DB write is a lie the
user has no way to detect.

---

## 7. Backpressure and abuse

| Risk | Control |
|---|---|
| A client floods `command.ack` | Per-socket event rate limit; disconnect after repeated breach |
| Reconnect storm after a deploy | Randomised reconnect delay client-side; Caddy connection limit server-side |
| Socket exhaustion | Max 5 concurrent sockets per user, 1 per device; oldest evicted |
| Large payloads | 16 KB max frame; anything larger closes the socket |
| Slow consumer | Bounded per-socket send buffer; drop and disconnect rather than growing the heap |

---

## 8. Failure modes and expected behaviour

| Failure | Behaviour |
|---|---|
| API instance restarts | Sockets reconnect with backoff; rooms rebuild from claims; no session state was in the process. Nothing is lost **server**-side, but a socket replays nothing that was sent while it was down, so every client performs a catch-up read as part of reconnecting — the agent re-claims pending commands, the phone re-reads devices, commands and reminders. Reconnecting without that read leaves a client showing whatever was true when it dropped, and no longer polling because it now looks healthy |
| Valkey unavailable | Push fan-out degrades to per-instance only; agents fall back to polling; `/readyz` fails so the deploy halts. Commands are still durable in MySQL. |
| MySQL unavailable | Command issue returns 503; existing sockets stay up; no command is silently dropped |
| Agent's clock is wrong | Server timestamps are authoritative; the agent compares `expiresAt` against a **server-anchored** monotonic offset, not the local wall clock |
| Network partition mid-execution | Command stays `delivered`; the agent's ack retries on reconnect; if the TTL passes with no ack it is reported `expired` — the UI never claims an unconfirmed success |
| Duplicate ack | 409, no state change |

The clock row matters: an agent whose clock is hours fast would otherwise treat every command as
expired, and one whose clock is slow would execute stale ones. The agent computes
`skew = serverTime - localTime` at connect and evaluates freshness against that.

---

## 9. Observability

| Metric | Type | Alert |
|---|---|---|
| `pcconnect_commands_issued_total{type}` | counter | — |
| `pcconnect_commands_expired_total{type}` | counter | **expired/issued > 10% over 15 min** |
| `pcconnect_command_delivery_seconds` | histogram | p95 > 1 s |
| `pcconnect_command_stale_executions_total` | counter | **any non-zero value** |
| `pcconnect_ws_connections{clientKind}` | gauge | drops > 50% in 5 min |
| `pcconnect_ws_auth_failures_total` | counter | spike |
| `pcconnect_presence_flaps_total{deviceId}` | counter | sustained flapping |

`command_stale_executions_total` should be structurally impossible. It is instrumented anyway,
because "impossible" and "unobserved" are different claims, and this one would mean a computer was
shut down without its owner asking.

---

Previous: [04 — API Contract](04-api-contract.md) · Next: [06 — Client Architecture](06-client-architecture.md)
