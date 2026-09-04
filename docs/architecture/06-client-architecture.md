# 06 — Client Architecture

Four clients exist today across two generations. The target is **three**, each generated against
the same OpenAPI document.

Both production clients run on end-of-life runtimes (S3-10), which is the underlying reason this
table has two rows to retire rather than two to upgrade.

| Today | Target | Fate |
|---|---|---|
| `PCClient/` — VB.NET WinForms, .NET Framework 4.7.2 | — | Sunset after the Go agent reaches parity |
| `PCClientWails/` — Go + Wails + React | **Desktop agent** | Promoted; Wails pinned to v2 stable |
| `App/` — Java Android, `AsyncTask` | — | Sunset after Flutter reaches parity |
| `mobile_flutter/` — Flutter + Riverpod | **Mobile app** | Promoted; Android *and* iOS |
| — | **Web dashboard** | New; small, shares the generated TS client |

---

> **Superseded by [ADR-0012](adr/0012-client-technology.md).** The desktop client is a
> .NET Windows service plus a WPF companion, and the mobile client is Kotlin with
> Jetpack Compose. The most consequential change is §2.3's "runs as the user, not as a
> service": running as a service is what lets a command reach a PC that is on with
> nobody signed in, which is the case the product exists for. The privilege cost of
> that, and what bounds it, is stated plainly in the ADR.

## 1. What every client must do identically

These are not per-client choices. Divergence here is how the current system ended up with three
different login flows.

| Behaviour | Rule |
|---|---|
| Credential storage | OS keychain only — Windows Credential Manager, Android Keystore/EncryptedSharedPreferences, iOS Keychain. **Never** plain preferences or a config file. |
| Password handling | Send the plaintext over TLS. Clients no longer hash. |
| Token lifecycle | Access token in memory only; refresh token in the keychain; refresh on 401 once, then log out. |
| Base URL | Build-time default, runtime-overridable, **never** a hardcoded absolute constant. |
| Startup | Call `GET /v2/meta/discovery`; block with an update prompt if below `minimumSupportedClient`. |
| Time | Everything on the wire is UTC RFC 3339. Local rendering uses the user's IANA timezone. |
| Errors | Switch on `error.code`, never on `error.message`. |
| Offline | Queue mutations with client-generated ids; replay on reconnect. Safe because command issue is idempotent by id. |
| Models | **Generated** from `openapi/pcconnect-v2.yaml`. Hand-written DTOs are a build failure. |

---

## 2. Desktop agent (Go + Wails)

### 2.1 The Wails version question

`PCClientWails/go.mod` currently requires **both** `wails/v2 v2.12.0` and `wails/v3 v3.0.0-alpha.90`,
and `app/app.go` imports the v2 runtime (S2-10). Meanwhile six branches — `copilot/upgrade-wails-v3`,
`copilot/upgrade-wails-app-to-v3`, `jules-6699572618790866906-ccbd497f`,
`wails-v3-upgrade-and-reminder-…` — are each attempting the same v3 upgrade independently, none
merged (S3-03).

**Decision: pin v2.12 stable, remove the v3 dependency, and gate the v3 upgrade on GA.**
Full reasoning in [ADR-0006](adr/0006-desktop-client-technology.md). The short version: v3 is alpha
with an API that has moved under this project four times already, and shipping the security
migration matters more than shipping a new window manager. The one v3-only feature being chased —
a transparent fullscreen reminder window — is achievable on v2 with a borderless always-on-top
window and per-pixel alpha.

Consolidate the six branches into one `feat/desktop-agent` line in Phase 1.

### 2.2 Structure

```
desktop/
├─ cmd/agent/main.go            entry point, single-instance guard
├─ internal/
│  ├─ apiclient/                GENERATED from OpenAPI (oapi-codegen) + a thin retry wrapper
│  ├─ auth/                     device secret in Windows Credential Manager  [exists, keep]
│  ├─ pairing/                  pairing-code UI flow                         [new]
│  ├─ realtime/                 socket lifecycle + policy.go backoff         [exists, keep+jitter]
│  ├─ commands/executor.go      the allow-list — the last line of defence    [exists, keep]
│  ├─ reminders/                local scheduler, fullscreen window
│  ├─ cache/                    offline reminder cache                       [exists]
│  ├─ tray/                     system tray                                  [exists]
│  └─ updater/                  signed auto-update                           [new]
└─ frontend/                    React + TS + Vite, generated TS client
```

### 2.3 Security properties of the agent

The agent is the piece that actually executes power commands, so its own posture is load-bearing:

| Property | Implementation |
|---|---|
| No shell, ever | `exec.Command(argv[0], argv[1:]...)` with a fixed argv per command type. `executor.go` already does this and must not regress. |
| Reject by default | Unknown command type returns an error and acks `rejected`. |
| Freshness check | Compare `expiresAt` against server-anchored time, not the local wall clock. |
| Replay guard | Bounded LRU of executed command ids. |
| Credential isolation | Device secret in Credential Manager; never in `%APPDATA%` JSON. The current `store.go` session file must stop holding the API key. |
| Runs as the user | Not as a service, not elevated. `shutdown /s` and `LockWorkStation` need no elevation, and running unprivileged bounds the damage of an agent compromise. |
| Signed binaries | Authenticode; the updater verifies the signature before applying. |

### 2.4 The VB.NET client's sunset

`PCClient/` is not modified except for one release: a build that (a) calls `/v2/meta/discovery`,
(b) shows an in-app "PCConnect has moved — install the new client" prompt, and (c) links to the
installer. It continues working against the `/legacy/*` shim until its sunset date.

Rewriting a 1,800-line WinForms app that is already being replaced would be work spent twice.

---

## 3. Mobile (Flutter)

### 3.1 Why Flutter wins over the Java app

`App/` is on `AsyncTask` (deprecated at API 30), Java 8, raw `HttpURLConnection` alongside OkHttp,
`printStackTrace` as its error strategy (S2-13), and hardcoded absolute URLs in six activities. It
is Android-only. `mobile_flutter/` is already structured correctly — feature-first layout, Riverpod,
Dio, `flutter_secure_storage`, `local_auth` — and gives iOS at no extra cost.
[ADR-0007](adr/0007-mobile-client-technology.md).

### 3.2 Structure

```
mobile/lib/
├─ core/
│  ├─ api/            GENERATED Dart client + Dio interceptors (auth, retry, request-id)
│  ├─ config/         resolved at runtime — NOT AppConfig.localIp = '192.168.0.113'
│  ├─ security/       flutter_secure_storage, biometric gate, cert pinning
│  ├─ realtime/       socket_io_client lifecycle
│  └─ offline/        mutation queue keyed by client-generated UUIDv7
└─ features/
   ├─ auth/           login, register, reset, biometric unlock
   ├─ devices/        list, presence, pairing-code entry, rename, revoke
   ├─ commands/       issue, live status, history
   ├─ reminders/      list, create, recurrence editor, complete
   └─ account/        profile, timezone, sessions, delete account
```

`AppConfig.localIp = '192.168.0.113'` shipping inside a release build is the concrete instance of
S3-08 and is deleted in the first Flutter change.

### 3.3 Mobile-specific security

- Biometric gate on app resume before any `command:issue` action (`local_auth` is already a
  dependency). Shutting down a PC from a stolen, unlocked phone should require a fingerprint.
- Refresh token in `flutter_secure_storage`; access token in memory, cleared on background.
- Certificate pinning on the primary domain with a documented backup pin and un-pin release path.
- Screenshot suppression on the reminder list (`FLAG_SECURE`) — reminder text can be private.

### 3.4 The Java app's sunset

One final release (`versionCode 703`): a discovery check and a blocking "install the new app"
screen. Then unpublished from the Play Store once the Flutter app is live and legacy traffic is
under 1%.

Note: `App/app/build.gradle.kts` declares `mysql:mysql-connector-java:8.0.27`. Nothing in the source
imports it, so no phone is opening a direct database connection — but the dependency ships in the
APK and should be removed regardless.

---

## 4. Web dashboard

Small on purpose: React + TS + Vite, the generated TypeScript client, one page for devices, one for
reminders, one for account and sessions. It exists mainly so that device pairing and session
revocation are reachable without a phone, and so the marketing site has somewhere to send people.

It is the only client that uses a browser session cookie, and that cookie authenticates the
dashboard's own routes — never `/v2/*`, which stays bearer-only. That separation is what keeps CSRF
structurally impossible on the API surface.

---

## 5. Client-server compatibility matrix

| Client | Auth | API | Realtime | Notes |
|---|---|---|---|---|
| Desktop agent (Go) | device secret → device token | `/v2` | Socket.IO, `device:{id}` | `command:receive`, `command:ack` only |
| Mobile (Flutter) | password → token pair | `/v2` | Socket.IO, `user:{id}` | `command:issue`, never `receive` |
| Web dashboard | password → token pair | `/v2` | Socket.IO, `user:{id}` | same as mobile |
| PCClient (VB.NET) | legacy SHA-256 → compat token | `/legacy` | none — polls | Until sunset |
| Android (Java) | legacy SHA-256 → compat token | `/legacy` | none — polls | Until sunset |

---

## 6. Release and update

| Client | Channel | Update mechanism | Signing |
|---|---|---|---|
| Desktop agent | GitHub Releases | In-app updater, signature-verified before apply | Authenticode |
| Mobile | Play Store + App Store | Store update; discovery gate forces it below minimum | Play App Signing / Apple |
| Web | Continuous | Cache-busted asset hashes | — |
| PCClient (legacy) | Existing MSI | Manual; prompted by the final release | Existing |
| Android (legacy) | Play Store | Manual; prompted by the final release | Existing keystore |

`minimumSupportedClient` in the discovery document is the lever that ends the legacy era. It is
raised only when the shim's Prometheus counter shows legacy traffic below 1% — a measurement, not a
date guess. See [ADR-0008](adr/0008-api-versioning-and-legacy-sunset.md).

---

## 7. Client testing

| Client | Layer | Tooling |
|---|---|---|
| Go agent | Unit — allow-list, backoff+jitter, freshness, replay guard | `go test`; extend the existing `policy_test.go` |
| Go agent | Integration — pairing, socket lifecycle, ack | testcontainers against a real API |
| Flutter | Unit + widget | `flutter_test`, `mocktail` |
| Flutter | Integration | `integration_test` against staging |
| Web | Component + e2e | Vitest, Playwright |
| All | Contract | Generated clients compile against the committed OpenAPI in CI |

The Go agent's `executor.go` allow-list deserves the strictest test in the codebase: for every
input outside the six known types, assert that **nothing is executed** — including inputs with
shell metacharacters, path separators, null bytes, and unicode case-folding tricks against the
existing `strings.EqualFold` match.


### Diagnostics on the phone

**Account → Logs** shows the app's own recent activity: each request as `METHOD /path -> status in Nms`, the realtime connection coming and going, and every error with its stable code. It exists because the app it replaces failed silently — a request that did not work left a blank screen, and the only way to see why was to plug the phone into a computer.

It records nothing secret: no tokens, no passwords, no device secrets, no request or response bodies, no reminder text. The buffer holds 400 entries and is cleared on sign-out ([09 §2.14](09-implementation-notes.md)).

---

Previous: [05 — Real-Time Architecture](05-realtime-architecture.md) · Next: [07 — Migration Plan](07-migration-plan.md)
