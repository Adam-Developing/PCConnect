# ADR-0002 — Authentication and session model

**Status:** Accepted
**Date:** 2026-09-02
**Context docs:** [03 §2](../03-security-architecture.md), [02 §6](../02-data-architecture.md)

## Context

The current model has four compounding defects:

- **S1-03** — passwords are hashed **client-side** with unsalted SHA-256 (`Login.vb:21-28`,
  `LoginActivity.java:182`) and the server compares for equality. The stored hash *is* a working
  credential; no server-side hardening can help while the client does the hashing.
- **S1-05** — `users.api_key` is a permanent, unscoped bearer token with no expiry and no
  revocation path.
- **S1-04** — that key and the password hash are persisted in cleartext client storage
  (`My.Settings.Password`, Android `SharedPreferences`).
- **S1-09** — the same single credential authorises *remote power commands on the user's PC*.

One leaked value therefore grants permanent, unrevocable, full control including shutdown. About a
thousand accounts hold an unsalted SHA-256 that cannot be reversed into a plaintext password, so any
replacement needs a migration path that does not lock those users out.

## Decision

Three distinct credentials with different lifetimes and scopes:

1. **Password** — sent as plaintext over TLS, verified server-side with **Argon2id**
   (m=19 MiB, t=2, p=1, tuned to ~250 ms).
2. **Access token** — JWT signed **EdDSA (Ed25519)**, 15-minute lifetime, scope claims, memory only.
3. **Refresh token** — opaque 256-bit, 30 days, **rotating with family-based reuse detection**,
   SHA-256-hashed at rest, stored in the OS keychain.

Plus a **device credential**: a per-device secret, Argon2id-hashed at rest, exchanged for a device
access token carrying only `command:receive` and `command:ack`.

Legacy hashes migrate by **upgrade-on-next-login** ([02 §6](../02-data-architecture.md)).

## Options considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **JWT access + rotating opaque refresh** (chosen) | Stateless verification on the hot path; short exposure window; per-credential scoping; reuse detection turns silent theft into a detectable event; standard OAuth 2.1 shape | Two token types to implement; revocation before expiry needs a `jti` deny-list | **Chosen** |
| Opaque tokens with a server-side session store | Instant revocation; simplest mental model | A cache lookup on every request; Socket.IO handshake auth becomes stateful again — which is the S2-06 failure being removed | Rejected |
| Session cookies only | Simplest for the web dashboard | Native Go/Dart clients handle cookies poorly; reintroduces CSRF surface; the current `secure:false` cookie (S1-12) is precisely this path done badly | Rejected for the API; retained for the dashboard's own routes |
| Keep the API key, add expiry | Smallest change | Leaves a single credential authorising both reading reminders and shutting down a PC; does not fix S1-03 or S1-06 | Rejected |
| External IdP (Auth0, Clerk) | Offloads the hard parts | Cost; a hard dependency for a GPL-3 hobby-scale product; migrating ~1k unsalted hashes into a hosted IdP is its own project; device credentials would still be bespoke | Rejected |

**Why Argon2id over bcrypt:** memory-hardness resists GPU and ASIC attack; it is the OWASP and
RFC 9106 first recommendation; bcrypt's 72-byte input truncation is a footgun. Cost: it is
CPU- *and* memory-expensive on a small VM, so it runs in a worker pool with tuned parameters.

**Why Ed25519 over RS256:** small signatures, fast verification, no curve or padding parameters to
get wrong, and pinning the algorithm at the verifier removes the `alg` confusion class of bug.

## Consequences

**Positive**
- A leaked mobile refresh token cannot execute a command; it lacks `command:receive`. A leaked device
  secret cannot read reminders or change the password, and is scoped to one PC. There is no longer a
  credential that does everything.
- 15-minute access tokens bound the damage of interception.
- Reuse detection makes token theft self-limiting and *observable*.
- A database dump yields no usable credential: Argon2id passwords, SHA-256 token hashes, Argon2id
  device secrets.
- Users get a real session list and can revoke individual sessions.

**Negative**
- **Every client must ship a change before any account can be upgraded**, because while a client
  pre-hashes, the server never sees the real password. Accounts on un-updated clients stay on the
  legacy hash until the sunset date, then get a forced reset.
- Argon2id costs ~250 ms and ~19 MiB per login on a 2-vCPU VM. Login is rare; a worker pool and
  per-IP rate limiting keep it from being a denial-of-service lever.
- Token refresh, rotation, deny-lists and reuse detection are real code with real edge cases, and
  they need the strictest tests in the codebase.
- Clock skew between the API and clients now matters for `exp` validation; a 60-second leeway is
  allowed and NTP is required on the host.

**Neutral**
- The legacy shim mints a long-lived compatibility token in `refresh_tokens` with
  `client_kind='legacy'`, so old clients keep working through one code path that is measurable and
  has an end date.

## Revisit when

- Passkeys/WebAuthn become practical across Windows, Android, iOS and web for this product — they
  would remove the password from the model entirely and should then supersede this ADR.
- The `jti` deny-list in Valkey becomes a hot spot, which would argue for shortening the access
  token lifetime instead.
