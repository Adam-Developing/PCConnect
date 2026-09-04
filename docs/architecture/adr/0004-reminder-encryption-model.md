# ADR-0004 — Reminder encryption model

**Status:** Accepted
**Date:** 2026-09-02
**Context docs:** [03 §4](../03-security-architecture.md), [02 §7](../02-data-architecture.md)

## Context

Reminder text is encrypted today with `AES-256-CBC` where **the encryption key is the user's API
key** (`helpers.js:88` — `crypto.createDecipheriv('aes-256-cbc', apiKey, iv)`).

Two independent problems:

- **S1-06** — the credential and the data key are the same value. Rotating or revoking the API key
  makes every one of that user's reminders permanently undecryptable. Security hygiene and data
  preservation are in direct conflict, so in practice neither happens.
- **S1-07** — CBC provides confidentiality but no integrity. Ciphertext is malleable and the design
  is exposed to the padding-oracle family of attacks. `decryptString` even contains a
  backward-compatibility branch that speculatively base64-decodes the ciphertext body, which widens
  the oracle surface further.

[ADR-0002](0002-authentication-and-session-model.md) retires the API key. Doing so **before** the
data is re-keyed would destroy every reminder in the system.

## Decision

**Envelope encryption with AES-256-GCM.**

```
  KEK        32 bytes, from the secret manager, NEVER stored in the database
   │ AES-256-GCM wrap
   ▼
  users.dek_wrapped ──unwrap──▶ per-user DEK (memory only, briefly cached)
                                  │ AES-256-GCM, random 96-bit nonce per write
                                  ▼
                        reminders.body_ciphertext = [12B nonce][ciphertext][16B tag]
```

`users.dek_kek_id` and `reminders.body_dek_id` record which key version was used, so both layers
rotate incrementally rather than in a flag day.

## Options considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Envelope, per-user DEK, AES-256-GCM** (chosen) | Decouples credential rotation from data; authenticated encryption; per-user blast radius; both key layers rotatable independently | The server can still decrypt; two key layers to manage | **Chosen** |
| Single application-wide key, AES-256-GCM | Simplest | One key compromise exposes every user; no per-user rotation | Rejected |
| Keep API-key-as-DEK, switch CBC→GCM | Smallest change; fixes integrity | Leaves S1-06 entirely — the credential still cannot be rotated without data loss | Rejected |
| **True end-to-end encryption** (key derived from the user's password, server never holds it) | Strongest confidentiality; the server genuinely cannot read reminders | Breaks server-side scheduling and notification, which is the product's core feature; a forgotten password means permanent data loss with no reset; multi-device key sync is a hard project on its own | Rejected — see below |
| No encryption at all | Simplest; honest about the threat model | Loses the real protection against a leaked dump or backup (T5) | Rejected |
| Transparent database-level encryption (TDE) | Zero application change | Protects the files on disk only; a SQL-injection read or an application-level dump returns plaintext | Rejected as sufficient; complementary if the host supports it |

### On end-to-end encryption

E2EE was seriously considered and deliberately rejected. It is incompatible with the product as it
exists: the server must read reminder text to schedule and deliver notifications, and a client-side
scheduler cannot fire while every device is offline. It also converts "I forgot my password" from an
inconvenience into permanent, unrecoverable data loss for a consumer product with no support team.

Revisiting it would mean redesigning notifications as client-scheduled, which is a product decision
rather than a security one.

## Consequences

**Positive**
- Credential rotation becomes possible, which unblocks the whole of
  [ADR-0002](0002-authentication-and-session-model.md).
- GCM authenticates: tampering fails loudly instead of yielding attacker-influenced plaintext, and
  the padding-oracle class disappears along with the speculative base64 branch.
- A leaked database dump or backup yields no reminder plaintext, because the KEK is not in the
  database (mitigates T5).
- Per-user DEKs mean one compromised DEK exposes one user.

**Negative**
- **The migration is the single most dangerous step in the whole plan.** Every reminder must be
  decrypted with the old API key and re-encrypted under a DEK before the API key is dropped.
  Mitigation: a guard migration asserting `COUNT(*) WHERE body_ciphertext IS NULL = 0`, an idempotent
  resumable backfill, a sampled decrypt-and-compare verification, and a verified backup taken first
  ([02 §7](../02-data-architecture.md)).
- Losing the KEK loses every reminder. It must be in the password manager and in an offline copy, and
  the loss scenario belongs in the runbook.
- **This does not protect against a compromised application server.** Stated plainly because the
  alternative — implying reminders are private from the operator — would be misleading. The control
  is against dumps, backups and injection reads, not against host compromise.
- Two key layers add operational surface: KEK rotation rewraps DEKs; DEK rotation re-encrypts rows.
  The rewrap is `pcconnect-migrate rewrap-deks` and it is not optional — until it has run, the
  previous KEK cannot be retired, and retiring it early destroys the reminders of every user
  still wrapped with it ([09 §2.9](../09-implementation-notes.md), [runbook §5](../../runbook.md)).
  DEK rotation is not built: it would re-encrypt every row and no threat currently calls for it.

**Neutral**
- `VARBINARY(4096)` on `body_ciphertext` bounds reminder text at 2000 characters of plaintext with
  ample headroom for nonce, tag and multi-byte characters.

## Revisit when

- The product moves to client-scheduled notifications, which would make E2EE feasible and worth
  reopening.
- A managed KMS (cloud or hardware) becomes available and worth its cost — the envelope structure is
  already the right shape to adopt one without touching the data.
