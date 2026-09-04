# ADR-0010 — Passkeys as a first-class credential

**Status:** Accepted
**Date:** 2026-09-02
**Extends:** [ADR-0002](0002-authentication-and-session-model.md)
**Context docs:** [03 §2](../03-security-architecture.md)

## Context

ADR-0002 closed with an explicit trigger:

> **Revisit when** — Passkeys/WebAuthn become practical across Windows, Android, iOS and
> web for this product — they would remove the password from the model entirely and should
> then supersede this ADR.

That condition is met. Windows Hello, Android's Credential Manager and iOS Keychain all
ship platform authenticators, and every browser this product's dashboard targets supports
WebAuthn level 2.

It matters more here than for most products. PCConnect's password is the credential that
authorises **remote code execution on a personal computer**, and roughly a thousand
accounts currently hold an unsalted client-side SHA-256 (S1-03) that the user chose years
ago and has probably reused. A phishing-resistant credential is worth more to this product
than to a note-taking app.

## Decision

**WebAuthn passkeys are a first-class credential for an account, alongside the password —
not a second factor bolted onto it.**

- A passkey can **sign in on its own**: `POST /v2/auth/passkeys/assert/start` then
  `/assert/finish` returns a normal token pair. The credential identifies the account, so
  the caller does not have to reveal a username to an anonymous endpoint.
- A passkey can **satisfy step-up** for a destructive command
  ([ADR-0011](0011-risk-tiered-step-up.md)), and when it does, user verification is
  **required** — a PIN, fingerprint or face, not merely possession of the device.
- The password **stays**. It is the recovery path, and removing it would strand every
  user whose only authenticator is a phone they might lose.

### What is implemented

Registration and assertion are implemented directly against `System.Formats.Cbor` and
`System.Security.Cryptography`, not against a WebAuthn library.

| Check | Where |
|---|---|
| `clientDataJSON.type` matches the ceremony | `WebAuthnService.ParseClientData` |
| Challenge matches the one issued, compared in constant time | same |
| Origin is in an **exact** allow-list — never a suffix match, which would accept `evil-pcconnect.example` | same |
| `rpIdHash` equals SHA-256 of the configured RP id | `VerifyRpIdHash` |
| User presence flag set; user verification required for step-up | `CompleteAssertionAsync` |
| Signature verified over `authenticatorData ‖ SHA-256(clientDataJSON)` | `VerifySignature` |
| Signature counter is monotonic where the authenticator uses one | `CompleteAssertionAsync` |
| Challenges are single-use, expiring, and bound to their ceremony type | `webauthn_challenges`, `ConsumeChallengeAsync` |

**Algorithms:** ES256 and RS256. Ed25519 (`-8`) is refused at registration with a message
that says why, for the same reason as [ADR-0009](0009-implementation-platform.md): .NET 10
has no in-box Ed25519 verifier, and a native dependency is not worth it for the small
number of authenticators that prefer it. Refusing at registration is deliberate — the
alternative is accepting a credential that cannot be used to sign in.

**Attestation is requested as `none` and is not verified.** This is a consumer product
with no authenticator allow-list, so an attestation statement would be collected and
ignored; collecting it would imply a check that is not happening.

## Options considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Passkey as a first-class credential, password retained** (chosen) | Phishing-resistant sign-in; a natural step-up factor; no user is stranded | Two authentication paths to keep correct and to test | **Chosen** |
| Passkey as a second factor only | Simpler mental model | Keeps the password on the critical path for every sign-in, so the phishing exposure the passkey exists to remove stays | Rejected |
| Passkey replaces the password entirely | The cleanest end state; ADR-0002's stated ambition | A lost sole authenticator is a permanently lost account for a consumer product with no support desk | Rejected for now; revisit when a second passkey is routinely enrolled |
| A WebAuthn library (`Fido2NetLib`) | Less code to own; attestation verification available | A dependency in the authentication path for a feature that is ~250 lines of well-specified checks, when attestation — the hard part — is deliberately not being verified | Rejected; revisit if attestation is ever needed |
| Nothing — keep passwords only | No new code | Leaves the strongest available control on the table for the credential that authorises a shutdown | Rejected |

## Consequences

**Positive**

- A passkey cannot be phished: the credential is bound to the RP id, so a look-alike
  domain cannot obtain an assertion for this one.
- A passkey cannot be replayed from a database dump: `webauthn_credentials` holds a public
  key, and a dump of it authenticates nobody.
- Step-up with user verification means "someone holding the unlocked phone" is not enough
  to power off a PC — the check that makes ADR-0011 meaningful on mobile.
- The counter check turns a cloned authenticator into a detected, refused event rather
  than a silent one.

**Negative**

- **Two sign-in paths to keep correct**, and the WebAuthn one is easy to get subtly wrong.
  It is covered by tests, but the checks in the table above are the security boundary, and
  a missing one is not visible in behaviour.
- **~250 lines of protocol code we now own**, including CBOR parsing of attacker-supplied
  input. It is bounded and total — every branch either matches or refuses — but it is
  ours.
- **A lost sole passkey falls back to the password**, so the password's strength still
  matters. The policy applies to it unchanged.
- The RP id is a deployment-wide setting. Changing the domain invalidates every registered
  passkey; that belongs in the runbook alongside key rotation.

**Neutral**

- `webauthn_challenges` grows and is purged by the retention job with the other
  short-lived challenges.
- Nothing about the token model changes: a passkey assertion mints the same token pair a
  password does.

## Revisit when

- Users routinely enrol a second passkey, which would make removing the password from the
  model realistic rather than reckless.
- Attestation becomes necessary — for example if a policy ever requires hardware-bound
  authenticators — at which point a library becomes worth its dependency.
- .NET gains an in-box Ed25519 verifier, and the `-8` refusal can be dropped.
