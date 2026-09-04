# ADR-0011 — Risk-tiered step-up for destructive commands

**Status:** Accepted
**Date:** 2026-09-02
**Extends:** [ADR-0002](0002-authentication-and-session-model.md),
[ADR-0003](0003-command-channel-transport.md)
**Context docs:** [03 §3](../03-security-architecture.md), [05 §4](../05-realtime-architecture.md)

## Context

[03 §3](../03-security-architecture.md) lists five server-side checks before a command is
issued: authenticate, scope, ownership, policy, rate. All five are satisfied by *holding a
valid access token*. That is the right bar for locking a screen. It is not the right bar
for powering a machine off.

The gap is concrete. A phone that is unlocked and unattended for thirty seconds holds a
`command:issue` token that is good for another fifteen minutes. Under the five checks, that
is enough to shut down the owner's PC, losing whatever is unsaved on it. Nothing in the
design distinguishes "lock my screen", which is recoverable in one keypress, from "shut
down", which is not.

[06 §3.3](../06-client-architecture.md) gestured at this — *"Biometric gate on app resume
before any `command:issue` action"* — but as a client-side control. A control the client
enforces is a control an attacker with a token does not have to satisfy: it protects
against someone using the app, not against someone using the API.

## Decision

**Commands carry a risk tier, and the destructive tier requires a fresh, single-use,
server-verified confirmation of the human.**

```
                          risk_tier
     lock, sleep  ────▶   standard      ─────────────▶  the five checks
                                                        (03 §3)

  shutdown, restart,      destructive   ─────────────▶  the five checks
  signout, hibernate                                    + a step-up token
                                                        + a tighter rate budget
```

### The tiers

| Tier | Commands | Why |
|---|---|---|
| `standard` | `lock`, `sleep` | Recoverable in one keypress. Nothing is lost. |
| `destructive` | `shutdown`, `restart`, `signout`, `hibernate` | Ends the session or the power state. Unsaved work is gone, and the user may be sitting at the machine. |

`hibernate` is in the destructive tier deliberately: it is recoverable, but it takes a
machine off the network for as long as it takes someone to walk back to it, which for a
remote-control product is the same practical harm as a shutdown.

### The token

`POST /v2/auth/step-up/start` returns the methods this account can satisfy — a passkey
assertion when one is enrolled ([ADR-0010](0010-passkeys.md)), otherwise the password.
`POST /v2/auth/step-up/verify` exchanges the proof for a step-up token that is:

- **short-lived** — five minutes;
- **single-use** — redeemed from the cache on the first command that presents it;
- **bound to the account** — `sub` must equal the caller's;
- **not an access token** — it carries `pur=step_up`, and the caller resolver refuses a
  step-up token presented as a session. Without that check, holding a session would
  satisfy step-up and the control would be decorative.

Redemption records `step_up_verified_at` and `step_up_method` on the command, so the audit
trail answers "was this confirmed, and how" and not merely "who asked".

### Enforced twice

The service refuses a destructive command without a redeemed token. The database refuses
one too:

```sql
CONSTRAINT ck_commands_stepup CHECK (
  risk_tier <> 'destructive' OR step_up_verified_at IS NOT NULL)
```

The constraint exists because this is the invariant that decides whether a stolen phone
can power off a machine, and a service-layer bug should not be able to violate it.
Verification gate V6 counts violations continuously.

### The rate budget

Destructive commands get their own budget — three per minute per user, against thirty for
commands generally. A legitimate user does not shut a PC down four times a minute; an
attacker with a stolen token wants to. The budget is consumed **before** the step-up check,
so a probing attacker exhausts it rather than getting free attempts.

## Options considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Risk tiers with a server-verified step-up token** (chosen) | The check is on the server, so it binds an attacker with a token as well as a user with the app; single-use means one confirmation authorises one action | An extra round trip and an extra prompt for the commands people use most on a bad day | **Chosen** |
| Client-side biometric gate only (06 §3.3 as written) | No API change; no extra round trip | Enforced by the client, so an attacker calling the API directly is unaffected. Protects the app, not the account | Rejected as the primary control; **kept as an additional local gate** on Android and in the WPF companion |
| Require step-up for every command | Uniform; nothing to classify | Locking a screen from a phone is the product's most-used action; a prompt every time would train people to approve reflexively — which is how confirmation dialogs stop working | Rejected |
| Re-authenticate fully (sign in again) for destructive commands | No new token type | Ends every session on the device; far more disruptive than a confirmation | Rejected |
| Shorter access-token lifetime instead | No new concept | 15 minutes is already short; making it 2 would multiply refreshes without changing the stolen-unlocked-phone case at all | Rejected |

## Consequences

**Positive**

- A stolen unlocked phone can no longer shut down the owner's PC: the token in memory is
  not sufficient, and the step-up requires a passkey assertion with user verification or
  the password.
- Every destructive command carries a recorded confirmation and method, which makes the
  audit trail answer the question that matters after the fact.
- The invariant is enforced by the database as well as the service.
- The tighter budget bounds how fast a compromised session can do damage before the
  account owner notices.

**Negative**

- **An extra prompt on the commands people use in a hurry.** Someone leaving the house who
  wants to shut a PC down now types a password or presents a fingerprint. That is a real
  cost, paid every time, against a risk that materialises rarely.
- **The legacy shim cannot present a step-up token.** The installed VB.NET and Java clients
  have no concept of one, so the shim marks those commands `step_up_method='legacy_shim'`.
  That is a hole, it is confined to the shim, every such command is attributable, and it
  closes when the shim does ([ADR-0008](0008-api-versioning-and-legacy-sunset.md)). It is
  recorded here rather than hidden in the code.
- **Two more endpoints and a cache dependency** in the authentication path. If the cache
  is unavailable, step-up tokens cannot be redeemed and destructive commands fail closed —
  correct, but it means Valkey's availability now affects a user-visible action.
- Classifying commands is a judgement. `hibernate` being destructive and `sleep` not is a
  line drawn on how long the machine is unreachable, and reasonable people could draw it
  elsewhere.

**Neutral**

- Standard commands are unchanged: same five checks, same latency, same UX.
- The step-up token is a JWT signed by the same key as an access token; it is
  distinguished by its `pur` claim and by being redeemed exactly once.

## Revisit when

- Telemetry shows users abandoning destructive commands at the confirmation step, which
  would mean the prompt is costing more than it buys.
- Passkeys become universal on this user base, at which point the password branch of
  step-up could be dropped and the flow becomes a single biometric tap.
- The shim is deleted, at which point the `legacy_shim` exemption goes with it and the
  invariant becomes unconditional.
