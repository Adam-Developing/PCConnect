# 03 — Security Architecture

> PCConnect's core feature is executing power commands on a stranger's personal computer on the
> strength of an HTTP header. This document is not a compliance exercise; it is the part of the
> architecture that decides whether the product is safe to run.

---

## 1. Threat model

### 1.1 Assets, ranked

| # | Asset | Worst case |
|---|---|---|
| A1 | The ability to execute a command on a user's PC | Attacker shuts down / restarts / locks a victim's machine at will; data loss from forced shutdown during work |
| A2 | User passwords | Credential stuffing into the victim's email, bank, everything — users reuse passwords |
| A3 | Reminder content | Discloses schedule, health, personal plans |
| A4 | Account PII (name, email, DOB, IP history) | Doxxing, phishing material, GDPR breach |
| A5 | Service availability | Product unusable; user PCs unmanageable |

### 1.2 Adversaries

| ID | Adversary | Capability |
|---|---|---|
| T1 | Remote unauthenticated attacker | Can reach every public endpoint; can enumerate; can replay |
| T2 | Credential thief | Has one leaked credential — a token, a DB row, a client config file |
| T3 | Malicious or curious network position | Can observe or modify traffic if TLS is absent or downgradable |
| T4 | Malware on the user's PC | Reads the agent's local storage |
| T5 | Attacker with database read access | SQL injection, a leaked dump, a stolen backup |
| T6 | Hostile website in the victim's browser | Can issue cross-origin requests carrying the victim's cookies |
| T7 | Malicious authenticated user | Tries to reach another user's devices or reminders |

### 1.3 Current posture against them

| | T1 | T2 | T3 | T4 | T5 | T6 | T7 |
|---|---|---|---|---|---|---|---|
| **Today** | Partial — TLS + Cloudflare, but no rate limiting, no lockout | **Fails** — a leaked API key is permanent, unscoped, full control incl. A1 | Partial — TLS on the PHP host; `secure:false` cookie on the Node path | **Fails** — password-equivalent hash in `user.config` / SharedPreferences | **Fails catastrophically** — a DB read yields working logins for every user *and* the AES keys for every reminder | **Fails** — `origin:true` with `credentials:true` | Partial — queries are scoped by `UserID`, but `PCName` is self-asserted so device scoping is weak |
| **Target** | Mitigated | Mitigated — short-lived, scoped, revocable | Mitigated — TLS 1.3, HSTS, pinning | Mitigated — OS keychain, device-scoped secret | Mitigated — Argon2id, hashed tokens, DEK outside the DB | Mitigated — origin allow-list, no cookie auth on the API | Mitigated — device authorisation by authenticated `device_id` |

### 1.4 Out of scope, stated honestly

- **T4 with full administrative malware on the PC.** An attacker with SYSTEM can shut the machine
  down without PCConnect. Device-scoped secrets in the Credential Manager limit *lateral* damage
  (they cannot reach the user's other PCs or reminders); they do not stop local compromise.
- **A malicious server operator.** Reminders are encrypted with keys the server can access. This
  protects against T5 (dump / backup / injection), not against a compromised application host. True
  E2EE is a deliberate non-goal — see [ADR-0004](adr/0004-reminder-encryption-model.md).

---

## 2. Identity and credentials

### 2.1 Three credentials, three lifetimes

> **Four, as built.** [ADR-0010](adr/0010-passkeys.md) adds passkeys as a first-class
> credential — a passkey can sign in on its own and can satisfy step-up with user
> verification. The password remains as the recovery path. This is the revisit trigger
> ADR-0002 named.

```
   ┌──────────────┐  password (Argon2id verified server-side)
   │     User     │──────────────────────────┐
   └──────────────┘                          ▼
                                  ┌─────────────────────┐
                                  │  POST /v2/auth/login │
                                  └──────────┬──────────┘
                                             │ issues
                        ┌────────────────────┴────────────────────┐
                        ▼                                         ▼
             ┌────────────────────┐                    ┌─────────────────────┐
             │  Access token      │                    │  Refresh token      │
             │  JWT, EdDSA        │                    │  opaque 256-bit     │
             │  15 minutes        │                    │  30 days, rotating  │
             │  memory only       │                    │  OS keychain        │
             │  scope: user:*     │                    │  family reuse-detect│
             └────────────────────┘                    └─────────────────────┘

   ┌──────────────┐  pairing code (user-confirmed, 10 min, single use)
   │  PC Agent    │──────────────────────────┐
   └──────────────┘                          ▼
                                  ┌──────────────────────┐
                                  │ POST /v2/devices/pair │
                                  └──────────┬───────────┘
                                             │ issues
                                             ▼
                                  ┌──────────────────────────┐
                                  │  Device secret           │
                                  │  256-bit, until revoked  │
                                  │  Windows Credential Mgr  │
                                  │  scope: device:execute   │
                                  │         for THIS id only │
                                  └──────────────────────────┘
```

**The property that matters:** a stolen mobile refresh token can list devices and *issue* commands
(it holds `command:issue`), but cannot *receive or execute* them. A stolen device secret can receive
and execute commands for one PC, but cannot read reminders, change the password, or reach the user's
other PCs. There is no longer a single credential that does everything — which is precisely what
`users.api_key` is today (S1-05).

### 2.2 Access token claims

```jsonc
{
  "iss": "https://api.pcconnect.example",
  "sub": "01JZ8K9M4QW7XPYRV2N6THGDF3",   // users.public_id
  "aud": "pcconnect-api",
  "exp": 1780000900, "iat": 1780000000,
  "jti": "01JZ8K9M...",                   // for the deny-list
  "cid": "mobile",                        // client kind
  "scp": ["reminder:read","reminder:write","device:read","command:issue"],
  "did": null                             // devices set this to devices.public_id
}
```

Signed **ES256 (ECDSA P-256)** as built — ADR-0002 specified EdDSA (Ed25519) and .NET 10
has no in-box signer for it; the properties that mattered are preserved and the substitution
is recorded in [ADR-0009](adr/0009-implementation-platform.md). Originally specified as EdDSA (Ed25519) — 64-byte signatures, no curve-parameter footguns, no
`alg:none`/RS256-confusion class of bug because the verifier pins the algorithm. Keys are rotated
via a JWKS document with an overlap window; `kid` selects the key.

15-minute lifetime means revocation is mostly handled by expiry. For the cases where that is too
slow (device revoked, reuse detected, password changed) a `jti` deny-list in Valkey with a 15-minute
TTL gives immediate revocation at O(1) cost and bounded memory.

### 2.3 Scopes

| Scope | Granted to | Permits |
|---|---|---|
| `reminder:read` / `reminder:write` | mobile, web, desktop-user session | own reminders only |
| `device:read` | mobile, web, desktop | list own devices, see presence |
| `device:manage` | mobile, web | rename, revoke, set `allowed_commands` |
| `command:issue` | mobile, web | issue a command to an owned device |
| `command:receive` | **device tokens only** | subscribe to own device's command stream |
| `command:ack` | **device tokens only** | acknowledge own device's commands |
| `account:manage` | mobile, web (step-up required) | change email/password, delete account |

`command:receive` and `command:issue` are deliberately disjoint. Nothing holds both.

### 2.4 Refresh token rotation with reuse detection

Every refresh mints a new token and revokes the presented one, keeping `family_id` constant. If a
**revoked** token is presented, that means a copy leaked and both parties are now using the chain:
the entire family is revoked immediately, a `security_events` row is written, and the user is
notified. This is the standard OAuth 2.1 recommendation and it turns silent token theft into a
detectable, self-limiting event.

### 2.5 Password handling

| | Today | Target |
|---|---|---|
| Hashing location | **Client** (S1-03) | Server only |
| Algorithm | Unsalted SHA-256 | **Argon2id** — m=19 MiB, t=2, p=1 (OWASP 2024 baseline), tuned to ~250 ms on the target VM |
| Comparison | `!==` string equality | Constant-time, inside the Argon2 verifier |
| Policy | Enforced only on signup, never on change (S1-10) | One `PasswordPolicy` module called by signup, change, and reset; min 12 chars, checked against the [Pwned Passwords](https://haveibeenpwned.com/API/v3#PwnedPasswords) k-anonymity range API |
| Failure handling | None | Exponential lockout per account **and** per source IP; generic error message; constant-ish response time on both branches to avoid a username oracle |

Removing the client-side hash is not optional and is not cosmetic: while the client hashes, the hash
*is* the password, so no server-side improvement can help. Migration path in
[02 §6](02-data-architecture.md).

### 2.6 Device pairing

```
 Agent                          Server                      User (mobile/web)
   │                              │                              │
   │─ POST /v2/devices/pair/start─▶                              │
   │   {requested_name, platform} │                              │
   │◀─ {pairing_code:"K7M2-9QXB", │                              │
   │    expires_in:600} ──────────│                              │
   │                              │                              │
   │  [displays code to the user] │                              │
   │                              │◀─ POST /v2/devices/pair/claim │
   │                              │   {code} + user access token │
   │                              │──────────────────────────────▶
   │                              │   creates devices row,       │
   │                              │   device_credentials row     │
   │─ POST /v2/devices/pair/poll ─▶                              │
   │◀─ {device_id, device_secret} │  (returned exactly once)     │
   │   [store in Credential Mgr]  │                              │
```

Properties: the code is user-confirmed (so `PCName` is no longer self-asserting, closing S1-08);
it is single-use and expires in 10 minutes; it is rate-limited and attempt-counted, so the 8-character
alphabet is not brute-forceable; and the device secret crosses the wire exactly once, at pairing.

---

## 3. Authorising a command

> **Extended by [ADR-0011](adr/0011-risk-tiered-step-up.md).** The five checks below
> are all satisfied by holding a valid access token, which is the right bar for
> locking a screen and not for powering a machine off. Commands now carry a risk tier,
> and the destructive tier — `shutdown`, `restart`, `signout`, `hibernate` — requires
> a fresh, single-use, server-verified step-up in addition to all five.

Five checks, in order, all server-side, before a command is ever issued:

```
1. AUTHENTICATE   valid, unexpired access token; jti not in the deny-list
2. SCOPE          token carries `command:issue`
3. OWNERSHIP      commands.device_id resolves to a device whose user_id == token sub
                  (a plain equality check on an authenticated id — not a header)
4. POLICY         command_type ∈ devices.allowed_commands
5. RATE           per-user and per-device budget; destructive types are stricter
```

Then, independently, on the agent:

```
6. AGENT ALLOW-LIST   commands/executor.go maps command_type → a fixed argv.
                      No shell. No interpolation. Reject-by-default.
7. FRESHNESS          reject if now > expires_at, even if the server sent it
8. IDEMPOTENCY        reject if this command public_id was already executed
```

Checks 6–8 exist because check 1–5 living only on the server means a server compromise is an
immediate RCE on every connected PC. The agent's allow-list is the last line and it does not trust
the server's word about *what* to run — only *whether* to run one of six known things.

**Command TTL** (`expires_at`, default 120 s) is a security control as much as a correctness one: it
bounds how long a stolen or replayed command remains useful.

---

## 4. Cryptography

| Purpose | Algorithm | Key management |
|---|---|---|
| Passwords | Argon2id (m=19 MiB, t=2, p=1) | n/a |
| Device secrets | Argon2id | n/a |
| Refresh tokens | SHA-256 of a 256-bit random value | n/a (hash only stored) |
| Pairing / reset codes | SHA-256 | single-use, TTL |
| Access tokens | Ed25519 (EdDSA) | JWKS, 90-day rotation, overlap window |
| Reminder text | **AES-256-GCM** | Envelope: per-user DEK, wrapped by a KEK held outside the DB |
| Backups | `age` (X25519 + ChaCha20-Poly1305) | Recipient key offline |
| Transport | TLS 1.3 | ACME via Caddy; HSTS `max-age=63072000; includeSubDomains; preload` |

### 4.1 Envelope encryption

```
  KEK  (32 bytes, env var / secret manager, NEVER in the database)
   │
   │ AES-256-GCM wrap
   ▼
  users.dek_wrapped ──unwrap──▶ DEK (per user, in memory only, cached briefly)
                                 │
                                 │ AES-256-GCM
                                 ▼
                       reminders.body_ciphertext = [12B nonce][ct][16B tag]
```

What this fixes:

- **S1-06** — rotating a user's credentials no longer destroys their data, because the credential
  and the data key are now different things.
- **S1-07** — GCM authenticates; CBC did not. Tampering with a ciphertext now fails loudly instead
  of producing garbage plaintext or a padding oracle.
- **T5** — a database dump alone yields no plaintext, because the KEK is not in the database.

`dek_kek_id` and `body_dek_id` exist so both layers can be rotated without a flag day: rewrap DEKs
under a new KEK without touching reminder rows; re-encrypt reminder rows under a new DEK lazily.

**Nonce discipline:** a random 96-bit nonce per encryption, never reused with the same key. At
GCM's birthday bound this is safe far beyond any plausible per-user reminder count.

---

## 5. Transport and browser security

| Control | Setting |
|---|---|
| TLS | 1.3 only; 1.2 with AEAD suites only if a client demands it |
| HSTS | `max-age=63072000; includeSubDomains; preload` |
| CORS | **Explicit origin allow-list** (the marketing site and the dashboard). Never `*`, never `origin:true` with credentials — this is S1-11 |
| API auth | `Authorization: Bearer` **only**. No cookie authentication on `/v2/*`, which makes CSRF structurally impossible on the API |
| Cookies | Only the web dashboard's own session, if any: `Secure`, `HttpOnly`, `SameSite=Lax` (fixes S1-12) |
| CSP | `default-src 'none'` on API responses; strict nonce-based policy on the dashboard |
| Other headers | `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Permissions-Policy` denying everything unused |
| Certificate pinning | Mobile client pins the primary domain, with a documented un-pin release path and a backup pin |

---

## 6. Rate limiting and abuse

Enforced in Valkey (sliding window), plus Cloudflare rules in front.

| Endpoint class | Limit | On breach |
|---|---|---|
| `POST /v2/auth/login` | 5 / 15 min per account **and** 20 / 15 min per IP | 429 + exponential account lockout |
| `POST /v2/auth/refresh` | 60 / hour per family | 429; reuse detection may revoke the family |
| `POST /v2/devices/pair/claim` | 5 / 10 min per user, 10 attempts per code | 429 + invalidate the code |
| `POST /v2/commands` | 30 / min per user; 10 / min per device; **3 / min** for `shutdown`/`restart` | 429 |
| Password reset request | 3 / hour per account, 10 / hour per IP | 429; response is identical whether or not the account exists |
| Everything else | 300 / min per token | 429 with `Retry-After` |

The destructive-command limit is deliberately tight. A legitimate user does not shut a PC down four
times a minute; an attacker with a stolen token wants to.

---

## 7. Secrets management

**Rule: no secret is ever a file in the repository.** S1-01 exists because `api/db_config.php`
breaks it, and that file is currently untracked *but not gitignored* — one `git add -A` from
permanent publication.

| Secret | Where it lives |
|---|---|
| DB password, KEK, JWT signing key, Sentry DSN, SMTP creds | Environment variables, injected at deploy |
| Source of those variables | **SOPS + age**-encrypted `.env.<environment>.enc` committed to the repo; the age private key lives only on the deploy host and in the maintainer's password manager |
| Android signing keystore | Off-repo, in the password manager; CI uses a base64 GitHub Actions secret |
| Backup encryption recipient | age public key in the repo (safe); private key offline |

SOPS is the right shape for a solo maintainer: encrypted secrets are versioned alongside the code
and reviewable as diffs, with no external secret-manager service to run or pay for.

**Immediate containment actions** (Phase 0, before any other work — see [07](07-migration-plan.md)):

1. Add `api/db_config.php`, `api/**/*.php`, `**/config.json`, `*.env`, `DB/*.sql` to `.gitignore`.
2. **Rotate the exposed MySQL password.** It has been in a working tree on a developer workstation;
   treat it as compromised regardless of whether it reached git.
3. Move MySQL off its public interface — bind to the Docker network only, and delete any
   `'user'@'%'` grant in favour of `'user'@'172.%'`.
4. `git log --all -S'HjhLKyhDpT4L8Fmi' -- '*'` to confirm it never entered history. If it did,
   rotate **and** rewrite with `git filter-repo`, then force-push and invalidate all clones.
5. Encrypt or delete `DB/pcconnect.sql` — it holds ~10k rows of real user PII (S1-14).
6. Add `gitleaks` to CI as a blocking check so this cannot recur.

---

## 8. Logging and privacy

Never logged, at any level: passwords, password hashes, tokens (access, refresh, device secret),
pairing codes, reset codes, DEKs, KEK, decrypted reminder text.

Always logged: `requestId`, `userId` (internal id, not email), `deviceId`, route, status, latency,
and for security events the outcome and source IP.

`security_events` records every authentication decision. IPs are stored as `VARBINARY(16)` and
purged on the same 90-day retention as `command_events`.

**GDPR posture:** lawful basis is contract performance for account and device data, consent for
marketing (tracked in `mailing_list` with a provable unsubscribe token). Data subject rights are
implemented as `GET /v2/account/export` and `DELETE /v2/account`, the latter soft-deleting
immediately and hard-deleting after 30 days, cascading to devices, commands and reminders — with a
test that asserts nothing survives.

---

## 9. Security in the delivery pipeline

| Gate | Tool | Blocking |
|---|---|---|
| Secret scanning | `gitleaks` | Yes |
| Dependency vulnerabilities | `npm audit --audit-level=high`, `govulncheck`, Dependabot | Yes for high/critical |
| Static analysis | `eslint` + `@typescript-eslint` with `no-floating-promises`; `semgrep` rules for auth bypass patterns | Yes |
| Licence compliance | `license-checker` against the GPL-3.0 allow-list | Yes |
| Container scanning | `trivy` on the built image | Yes for high/critical |
| Authorisation tests | A test suite asserting user A cannot reach user B's device, reminder, or command — one test per resource type | Yes |

That last one is the highest-value test in the codebase and does not exist today.

---

## 10. Findings → controls traceability

| Finding | Control | Where |
|---|---|---|
| S1-01 secrets in tree | SOPS + gitignore + gitleaks + rotation | §7 |
| S1-02 public MySQL | Private Docker network, host-restricted grants | §7, [01 §2.1](01-target-architecture.md) |
| S1-03 client-side SHA-256 | Server-side Argon2id + upgrade-on-login | §2.5, [02 §6](02-data-architecture.md) |
| S1-04 credential in client storage | OS keychain; refresh token is rotating, not password-equivalent | §2.1 |
| S1-05 static API key | Token pair with scopes and expiry | §2.1–2.3 |
| S1-06 API key as AES key | Envelope encryption, per-user DEK | §4.1 |
| S1-07 unauthenticated CBC | AES-256-GCM | §4 |
| S1-08 self-asserted PCName | Device pairing + authenticated `device_id` | §2.6, §3 |
| S1-09 RCE on a bearer token | Five server checks + three agent checks + TTL + tight rate limit | §3, §6 |
| S1-10 unenforced password policy | One `PasswordPolicy` module on all three paths | §2.5 |
| S1-11 permissive CORS | Explicit origin allow-list; bearer-only API | §5 |
| S1-12 `secure:false` cookie | No cookie auth on the API at all | §5 |
| S1-13 `strip_tags` on commands | Closed command vocabulary, structured params, no shell | §3, [02 §3.3](02-data-architecture.md) |
| S1-14 PII dump on disk | Encrypt or delete; add to gitignore | §7 |
| S1-15 keystore in tree | Password manager + CI secret | §7 |

---

Previous: [02 — Data Architecture](02-data-architecture.md) · Next: [04 — API Contract](04-api-contract.md)
