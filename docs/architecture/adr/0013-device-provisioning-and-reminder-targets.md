# ADR-0013 — Signing in adds the PC, and a reminder can name its PCs

**Status:** Accepted
**Date:** 2026-09-04
**Amends:** [ADR-0012](0012-client-technology.md) (the named pipe carries "two verbs and
nothing else"), [ADR-0011](0011-risk-tiered-step-up.md) (unchanged in substance; the
passkey method it allows for is now implemented on Android)
**Context docs:** [03](../03-security-architecture.md), [06](../06-client-architecture.md),
[09 §3](../09-implementation-notes.md)

## Context

The client redesign drew three things the system could not do, and the honest first
response was to record them rather than fake them ([09 §3](../09-implementation-notes.md)).
This ADR is the second response: building them.

1. **"A PC joins the account by signing in on it."** Adding a PC meant reading an
   eight-character code off its screen and typing it into a phone. The design has no code
   anywhere, because signing in on the PC *is* the proof that the person at the keyboard
   owns the account — the code only ever existed to carry that proof from a machine
   nobody was signed in on to one that was.

2. **"A reminder shows on the PCs you choose."** Reminders belonged to an account and
   appeared on every screen signed in to it. `CreateReminderRequest` had no targeting,
   `ReminderResponse` returned none, and the worker fanned each due reminder out per user.

3. **"A fingerprint confirms a destructive command."** The server had accepted a passkey
   in place of a password for step-up since it was written — `StepUpService.VerifyAsync`
   has a `passkey` branch. The Android app never registered one, so the challenge only
   ever came back offering `password`, and the confirm dialog could only ever type.

## Decision

### 1. A signed-in companion provisions the PC it is running on

`POST /v2/devices/provision` takes an authenticated user and creates the device, its
credential, and a **provisioning ticket**. The local agent redeems the ticket through
`POST /v2/devices/provision/complete`.

The old human-entered code flow and its `pair/start`, `pair/claim`, and `pair/poll`
endpoints are removed. Signing in on the PC is the only way to add it to an account.

**The companion never holds the device secret.** It holds a ticket. The agent redeems it
and writes the secret to Credential Manager under `CRED_PERSIST_LOCAL_MACHINE`, which is
the whole reason the two-process split exists: a process in the user's session cannot
write a credential the LocalSystem service will read back.

**The pipe gains a second direction and two verbs.** ADR-0012 fixed the session pipe at
two verbs carrying nothing derived from an HTTP body, and that pipe is unchanged. This is
a second pipe, `PCConnect.Provisioning`, running the other way — companion to agent —
carrying `WHOAMI` and `PROVISION <ticket>`.

What that widening costs, and what bounds it:

- The ticket **is** derived from an HTTP body, which ADR-0012's pipe deliberately excluded.
  It is an opaque single-use token with a ten-minute life that the server minted for an
  authenticated user, and the agent does nothing with it but hand it back to the server.
  There is no path from its contents to a command, a path or a process.
- The pipe's ACL is SYSTEM and **`InteractiveSid`** — not `Users`. A service account or a
  remote session on this machine cannot reach it, and the flow only makes sense for
  somebody sitting at the PC.
- **The agent refuses to provision when it is already registered.** Moving a PC to another
  account stays a deliberate removal by its current owner followed by a sign-in on that PC.
- `WHOAMI` returns a device id to any interactive user. A device id is not a credential —
  it is a public identifier its owner already sees in the app — and it is what stops the
  companion guessing which device it is by machine name, which was a label and never an
  identity (S1-08).

### 2. Reminders name the devices they show on

A `reminder_devices` join table ([0008](../../../DB/migrations/0008_reminder_targets.sql)),
`deviceIds` on the create, update and response contracts, and the same list on
`ReminderDueEvent`.

**No rows means every device.** That is what every reminder written before this migration
meant, what the clients still send by default, and why the migration needs no backfill.
Null and "all of them" are the same thing all the way out to the client; an empty list is
rejected, because "no PCs at all" is a reminder nobody would ever see.

**Delivery stays per account, and the client filters.** A companion holds a user
credential and joins `user:{id}`, not `device:{id}`, so the worker cannot address one PC's
connection. The event carries its targets and each PC decides whether the reminder is for
the screen it is sitting on — which it can now do reliably, because `WHOAMI` tells it
which device it is.

The server still validates ownership on write: a reminder cannot name a device that is not
the caller's, or the id would come straight back out on the reminder it was written to. It
answers `422` with `reminder.target_unknown` — the same answer for "not yours" and "does
not exist", so the endpoint cannot be used to test ids for existence.

**Revoking a PC clears the targets naming it**, in `DeviceService.RevokeAsync` and not by
the foreign key. Revoking marks the device rather than deleting the row, so `ON DELETE
CASCADE` never fires; without the explicit delete a reminder would stay aimed at a machine
that will never show it again, and one that named only that machine would fire nowhere at
all. Clearing the targets sends it back to meaning every PC.

### 3. Android registers and asserts passkeys

`androidx.credentials` for both ceremonies, translating between Credential Manager's W3C
JSON and the server's typed contract. Both use base64url for every binary field, so it is
a reshaping and never a re-encoding.

With a passkey registered, the fingerprint **is** the confirmation: the authenticator
checks it and the server verifies the signature. Without one, the password is asked for and
a fingerprint is only a local gate. The password stays reachable in both cases, because a
sensor that will not read a wet finger must not be the only way to turn a computer off.

## Options considered

| Option | Verdict |
|---|---|
| **Companion provisions, agent redeems a ticket** (chosen) | Chosen. The secret never leaves the process that must hold it. |
| Companion fetches the secret and writes it for the agent | Rejected. `CRED_PERSIST_LOCAL_MACHINE` written from a user session lands in that user's vault, not the machine's; the service would never see it. It would also put a device secret in a process that has no business holding one. |
| Agent watches for an interactive logon and pairs itself | Rejected. The agent holds no user credential and must not; it cannot prove the account is the one signed in. |
| Reminder targets as a `jsonb` array on `reminders` | Rejected. It could not be a foreign key, so a deleted device would leave a dangling id nothing cleans up. (The foreign key does not do the work on a *revoke*, which is a soft delete — that is handled explicitly above — but it is what makes a dangling id impossible.) |
| Route due reminders to `device:{id}` groups | Rejected. The companion is a user connection; there is no device connection to send to. |

## Consequences

**Positive**

- Adding a PC is signing in on it, which is what both clients now say.
- A reminder can be aimed at one screen, and the client only shows what is for it.
- A destructive command can be confirmed with a fingerprint and no typing.
- The companion knows which device it is from the agent rather than by matching a name.

**Negative**

- **A second named pipe, and a token crossing it.** Smaller than the session pipe's
  attack surface but not zero, and it is a widening of a boundary ADR-0012 drew tight.
- **First-signed-in wins on a shared PC.** Same as the code flow, but now it happens
  without anybody deciding to do it, so it is easier to do by accident.
- **Passkeys need deployment work the app cannot do**: the relying party must serve
  `/.well-known/assetlinks.json` naming the app's package and signing fingerprint, and
  `WebAuthn:AllowedOrigins` must include `android:apk-key-hash:<...>`. Until both are in
  place the fingerprint path fails at the platform and the app falls back to the password.
- **A revoked PC widens the reminders that named it** rather than narrowing them. Someone
  who aimed a reminder at one PC and then revoked that PC gets it on every remaining PC.
  The alternative is a reminder that can never be seen, which is worse for a reminder.
- **Reminder targeting is enforced on the client.** The server decides who is *told*; the
  PC decides whether to *show*. A modified client could show a reminder aimed elsewhere.
  Reminder bodies are already readable by every client on the account, so this leaks
  nothing new — but it is a filter, not an authorisation boundary, and should not be
  mistaken for one.

**Neutral**

- A PC cannot be added remotely. Someone must sign in through the companion running on that PC.

## Revisit when

- A companion can hold a device identity of its own, at which point `WHOAMI` and the second
  pipe both disappear.
- Reminder targeting needs to be an authorisation boundary rather than a filter, which
  would mean per-device delivery and therefore a device-authenticated realtime connection
  for the companion.
