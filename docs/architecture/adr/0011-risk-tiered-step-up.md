# ADR-0011 — Per-device step-up policy and destructive-command risk tiers

**Status:** Accepted; amended 2026-09-14 so each PC can choose which commands ask.
The passkey method it allows for is implemented on Android
as of [ADR-0013](0013-device-provisioning-and-reminder-targets.md) §3; before
that, only the password path existed on a client.
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

**Commands carry a risk tier, while each PC independently chooses which command types
require a fresh, single-use, server-verified confirmation of the human.** Destructive
commands are selected by default. Their risk tier always remains destructive, even when
the PC owner turns off password confirmation, so the tighter rate budget still applies.

```
     every command ─────▶ the five checks (03 §3)
            │
            ├─ listed in this PC's password policy ───▶ + a step-up token
            │
            └─ destructive risk tier ─────────────────▶ + a tighter rate budget
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

The service refuses a password-protected command without a redeemed token. The database
refuses one too, using the policy snapshot recorded when the command is issued:

```sql
CONSTRAINT ck_commands_stepup CHECK (
  NOT password_required OR step_up_verified_at IS NOT NULL)
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
| **Per-device policy with a server-verified step-up token** (chosen) | The check is on the server and follows the target PC's explicit settings; single-use means one confirmation authorises one action | Turning protection off trades convenience for less protection on that PC | **Chosen** |
| Client-side biometric gate only (06 §3.3 as written) | No API change; no extra round trip | Enforced by the client, so an attacker calling the API directly is unaffected. Protects the app, not the account | Rejected as the primary control; **kept as an additional local gate** on Android and in the WPF companion |
| Require step-up for every command | Uniform; nothing to classify | Locking a screen from a phone is the product's most-used action; a prompt every time would train people to approve reflexively — which is how confirmation dialogs stop working | Rejected |
| Re-authenticate fully (sign in again) for destructive commands | No new token type | Ends every session on the device; far more disruptive than a confirmation | Rejected |
| Shorter access-token lifetime instead | No new concept | 15 minutes is already short; making it 2 would multiply refreshes without changing the stolen-unlocked-phone case at all | Rejected |

## Consequences

**Positive**

- On PCs using the secure defaults, a stolen unlocked phone cannot shut down the PC: the
  token in memory is not sufficient.
- Every password-protected command carries a recorded confirmation and method, which makes the
  audit trail answer the question that matters after the fact.
- The invariant is enforced by the database as well as the service.
- The tighter budget bounds how fast a compromised session can do damage before the
  account owner notices.

**Negative**

- **Protection can be disabled per command.** This is an explicit convenience/security
  choice owned by the target PC and is visible in that PC's settings.
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

- Commands not listed in the target PC's password policy keep the same five checks and no
  step-up round trip.
- The step-up token is a JWT signed by the same key as an access token; it is
  distinguished by its `pur` claim and by being redeemed exactly once.

## Revisit when

- Telemetry shows users abandoning protected commands at the confirmation step, which
  would mean the prompt is costing more than it buys.
- Passkeys become universal on this user base, at which point the password branch of
  step-up could be dropped and the flow becomes a single biometric tap.
- The shim is deleted, at which point the `legacy_shim` exemption goes with it and the
  invariant becomes unconditional.
