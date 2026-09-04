# 00 — Current State Assessment

> Snapshot taken 2026-09-02 against branch `Mobile-App` (`0b47ea2`), cross-referenced with
> `main` (`e3a6c01`) and `PCConnect/main` (`17bd411`). Everything below is derived from the
> repository contents, not from the deployed environment.

---

## 1. What PCConnect is

A remote PC control and reminders product. A user signs up on the website, installs a Windows
agent on one or more PCs, and can then trigger power-state commands (`Shutdown`, `Restart`,
`Signout`, `Lock`, `Sleep`, `Hibernate`) and manage timed reminders from a phone. Reminders are
displayed full-screen on the PC.

Business-critical property: **the product's core primitive is remote code execution on a user's
personal computer.** Every security decision in this architecture follows from that.

---

## 2. Component inventory

| # | Component | Path | Stack | Branch presence | State |
|---|---|---|---|---|---|
| C1 | Legacy PHP API (deployed) | *not in repo* (`api_PHP_Deprecated/` is gitignored) | PHP + MySQLi | — | **Live. Serves both production clients.** |
| C2 | PHP front-controller API | `api/` | PHP 8, hand-rolled router | untracked on disk | **Broken — does not run** |
| C3 | Node gateway | `api_node/` | Express 5, mysql2, Socket.IO 4 | `main`, HEAD | Works; not deployed |
| C4 | PCClient (production desktop) | `PCClient/` | VB.NET WinForms, .NET Framework 4.7.2 | untracked on disk | **Live** |
| C5 | PCClientWails (next desktop) | `PCClientWails/` | Go 1.25, Wails v2+v3-alpha, React/TS/Vite | `main`, HEAD | Prototype |
| C6 | Android app (production mobile) | `App/` | Java 8, Activities, `AsyncTask`, OkHttp | worktree + HEAD | **Live (v7.2, code 702)** |
| C7 | Flutter app (next mobile) | `mobile_flutter/` | Flutter 3, Riverpod, Dio, socket_io_client | `main` only | Prototype |
| C8 | Marketing website | *not in repo* | PHP | — | **Live** (`pcconnect.adamkhattab.co.uk`) |
| C9 | Database | `DB/pcconnect.sql` | MySQL 8, InnoDB, `utf8mb3` | dump on disk (gitignored) | **Live** |

### 2.1 The dependency reality

```
                 ┌──────────────────────────────────────────┐
                 │  pcconnect.adamkhattab.co.uk (Cloudflare)│
                 └────────────────┬─────────────────────────┘
                                  │
       ┌──────────────────────────┴──────────────────────────┐
       │                                                     │
┌──────▼───────────────────┐                   ┌─────────────▼──────────────┐
│ C1  Legacy PHP endpoints │                   │ C8  Marketing site (PHP)   │
│  /api/login.php          │                   │  menupages, links,         │
│  /api/time.php           │                   │  feedback, mailing_list    │
│  /api/pcclient/*.php  x7 │                   └─────────────┬──────────────┘
│  /api/pcconnect/*.php x4 │                                 │
└──────┬───────────────────┘                                 │
       │                                                     │
   ┌───┴────────────┬─────────────────┐                      │
   │                │                 │                      │
┌──▼────────┐  ┌────▼─────┐    ┌──────▼──────┐               │
│ C4 VB.NET │  │ C6 Java  │    │ (browsers)  │               │
│  desktop  │  │  Android │    └─────────────┘               │
└───────────┘  └──────────┘                                  │
                                                             │
                    ┌────────────────────────────────────────┘
                    │
              ┌─────▼───────────────────────────────┐
              │ C9  MySQL 8  @ 130.162.164.140      │
              │  schema `pcconnect_new`             │
              └─────▲───────────────────────────────┘
                    │
       ┌────────────┴────────────┬──────────────────┐
       │                         │                  │
┌──────┴──────┐         ┌────────┴────────┐  ┌──────┴───────┐
│ C2 PHP FC   │         │ C3 Node gateway │  │ (dumps, ad-  │
│  (BROKEN)   │         │  REST + WS:3000 │  │  hoc scripts)│
└─────────────┘         └────────┬────────┘  └──────────────┘
                                 │  (not deployed)
                      ┌──────────┴──────────┐
                      │                     │
                ┌─────▼──────┐      ┌───────▼──────┐
                │ C5 Wails   │      │ C7 Flutter   │
                │  desktop   │      │  mobile      │
                └────────────┘      └──────────────┘
```

**The two production clients (C4, C6) talk exclusively to C1 — an API surface that does not exist
in this repository.** Both modernisation efforts (C3+C5+C7) form a complete parallel stack that
nothing in production uses. This is the single most important fact about the current state: there
are two disconnected systems, and the modern one has never carried traffic.

---

## 3. API surface drift

Three incompatible contracts exist for the same operations.

| Operation | C1 legacy (live) | C2 PHP FC | C3 Node |
|---|---|---|---|
| Login | `POST /api/login.php` → **bare API key as text/plain** | `POST /auth/login` → `{success,data.api_key}` | `POST /auth/login` → `{success,data.api_key}` |
| List devices | `GET /api/pcconnect/PCNames.php` → `{PCNames:[…]}` | `GET /v1/devices` | `GET /v1/devices` |
| Push command | `POST /api/pcconnect/exchange.php` | `POST /v1/devices/requests/exchange` | same + Socket.IO push |
| Poll command | `GET /api/pcclient/findrequests.php` | `GET /v1/devices/requests` | same |
| Clear command | `POST /api/pcclient/updaterequest.php` | `POST /v1/devices/requests/clear` | same |
| Heartbeat | `POST /api/pcclient/updatepctimedatabase.php` | *missing* | *missing* |
| Server time | `GET /api/time.php` | *missing* | *missing* |
| List reminders | `GET /api/pcclient/listreminders.php` | `GET /v1/reminders` | `GET /v1/reminders` |
| Create reminder | `POST /api/pcclient/reminder.php` | *missing* | `POST /v1/reminders` |
| Complete reminder | `POST /api/pcclient/completereminder.php` | *missing* | `POST /v1/reminders/:id/complete` |
| Add PC | `POST /api/pcclient/addpc.php` | `POST /v1/devices` | `POST /v1/devices` |
| Connectivity | `GET /api/pcconnect/checkinternet.php` | `GET /v1/system/checkinternet` | same |
| Profile | — | *missing* | `GET/PUT /v1/account/profile` |
| Signup | `signup.php` (on disk, untracked) | declared "Not Implemented" | *missing* |
| Session/WS auth | — | — | `POST /v1/auth/session` (cookie) |

`api/api_spec.md` — the only tracked file under `api/` — documents the **C3 Node** contract while
sitting inside the **C2 PHP** directory, and describes endpoints (`GET/POST /v1/devices/time`) that
exist in neither. The written spec is authoritative for nothing.

---

## 4. Findings register

Severity: **S1** = exploitable now, user harm; **S2** = serious defect or blocks modernisation;
**S3** = maintainability / cost of change.

### S1 — Security

| ID | Finding | Evidence | Impact |
|---|---|---|---|
| **S1-01** | Production DB credentials in plaintext in the working tree, **untracked but not gitignored** | `api/db_config.php` — host `130.162.164.140`, user `pcconnect_new`, password in clear | One `git add -A` publishes them permanently. Full read/write on all user data. |
| **S1-02** | MySQL appears to be reachable on a public IP | `api/db_config.php` host is a public Oracle Cloud address, not `localhost` | DB is directly attackable; credential compromise = total compromise |
| **S1-03** | Passwords hashed **client-side** with unsalted SHA-256; the hash is the credential | `Login.vb:21-28,94-102`; `LoginActivity.java:127,182`; `login.php` compares equality | The stored hash *is* password-equivalent. A DB read yields working logins for every user. No salt means rainbow-table recovery of the real passwords, which users reuse elsewhere. |
| **S1-04** | Password-equivalent hash persisted in cleartext client storage | `My.Settings.Password` (VB `user.config`); Android `SharedPrefManager` | Local malware or a stolen device yields a permanent credential |
| **S1-05** | `api_key` is a permanent, non-rotating, unscoped bearer token | `users.api_key`; every client sends `X-API-Key` | No expiry, no revocation path, no scope. Leak equals permanent full account takeover including remote shutdown. |
| **S1-06** | The API key doubles as the AES-256 data-encryption key for reminders | `helpers.js:70-110` — `createDecipheriv('aes-256-cbc', apiKey, iv)` | Key rotation is impossible without destroying all reminder data. Credential rotation and data confidentiality are fatally coupled. |
| **S1-07** | AES-256-**CBC** with no authentication tag | `helpers.js` `encryptString`/`decryptString` | Ciphertext is malleable; padding-oracle class attacks; no integrity guarantee on user data |
| **S1-08** | Device identity is a self-asserted, auto-registering header | `helpers.js:36-68` `requirePC` inserts any unknown `PCName` | Any holder of an API key can register arbitrary devices; `PCName` is not an authenticated identity |
| **S1-09** | Remote OS command execution gated only by the static API key | `routes.js:137-160` → `PushManager.pushCommand`; `executor.go:11-18`; `PCClient.vb:174-211` | The blast radius of S1-03/04/05 is *shutting down the victim's computer*, not just data disclosure |
| **S1-10** | Password change compares hashes with `!==` and accepts any client-supplied string as the new password | `routes.js:322-330` | No server-side password policy on the change path; `validatePassword` exists in `helpers.js` but is never called |
| **S1-11** | `Access-Control-Allow-Origin: *` and `cors({origin:true, credentials:true})` | `api/index.php:5`; `api_node/server.js:11` | `origin:true` reflects any origin *with credentials* — any website can drive the authenticated API in a victim's browser |
| **S1-12** | Session cookie issued with `secure:false` | `server.js:47` | Session token sent over plaintext HTTP |
| **S1-13** | Sanitisation is `strip_tags` / regex tag-stripping on a command string | `DeviceController.php:66`; `helpers.js:46` | Wrong control for the threat; commands need an allow-list, not HTML escaping |
| **S1-14** | Real user PII on disk in an unencrypted dump | `DB/pcconnect.sql` — ~10k rows: names, DOB, emails, password hashes, IPs | Gitignored, so not in history — but present on the workstation and in any backup of it |
| **S1-15** | Android signing keystore in the working tree | `App/Key Store/PCConnectKey.jks` | Gitignored, but loss or leak means loss of app-update authority |

### S2 — Correctness / blocking

| ID | Finding | Evidence |
|---|---|---|
| **S2-01** | The PHP front-controller cannot execute: four of its classes are **zero-byte files** | `api/src/Auth.php`, `Database.php`, `Router.php`, `Controllers/AuthController.php` are 0 lines; `ReminderController.php` is the two characters `}}`. `index.php` `require_once`s all of them. |
| **S2-02** | Schema drift: committed dump does not match the schema the code queries | Dump has `pcnames(PCID, Username TEXT, PCName)`. Code queries `pcnames.UserID`, `.Request`, `.Value`, `.Time` and `users.api_key` — none exist in the dump. |
| **S2-03** | Command mailbox has no TTL — stale destructive commands fire on reconnect | `pcnames.Value=1` persists until a client clears it; a `Shutdown` queued at 09:00 executes when the PC comes online at 18:00 |
| **S2-04** | Command mailbox is a mutable single row — no history, no idempotency, races | `UPDATE pcnames SET Value=1, Request=?` — a second command overwrites an undelivered first; no audit trail for destructive actions |
| **S2-05** | Dual write paths (DB mailbox + WS push) with no reconciliation | `routes.js:150-158` writes the row *and* pushes; a device on WS executes and clears, but a device that polls may execute the same command twice |
| **S2-06** | WS sessions in a process-local plain object, never swept | `server.js:20` `const sessionTokens = {}` — unbounded growth; all sessions lost on restart; cannot run more than one instance |
| **S2-07** | Reminder times are naive wall-clock with no timezone | `reminders.Time TIME`, `Date DATE`, no tz column; users observed in `Asia/Calcutta`, `America/Lima`, `America/Sao_Paulo`, `Europe/London` | Reminders fire at the wrong time for non-UK users |
| **S2-08** | `utf8mb3` charset throughout | every `CREATE TABLE` in `DB/pcconnect.sql` | Emoji and many scripts cannot be stored in reminder text |
| **S2-09** | Recurrence modelled as five loosely-typed columns, unused by any client | `reminders.Recurrence*`; no client reads them | Half-built feature with no valid recurrence semantics |
| **S2-10** | `go.mod` depends on **both** Wails v2.12 and v3-alpha.90 | `PCClientWails/go.mod:11-12`; `app/app.go` imports `wails/v2/pkg/runtime` | Two incompatible runtimes in one binary; v3 is alpha and API-unstable |
| **S2-11** | `require('crypto')` destructured incorrectly at `server.js:18` | `const { crypto } = require('crypto')` yields `undefined` — dead line, works only because line 42 re-requires correctly. Symptom of no linting. |
| **S2-12** | Circular import between `routes.js` and `server.js` worked around with try/catch | `routes.js:7-13` `getPushManager()` — push silently becomes a no-op if load order changes |
| **S2-13** | No error handling on the Android HTTP path beyond `printStackTrace` | `HttpRequestHelper.java:57-60`; `AsyncTask` deprecated since API 30 |
| **S2-14** | Busy-wait polling loop in the desktop client | `PCClient.vb` `Running()` — `While True` over HTTP; server load scales with device count times poll rate |

### S3 — Maintainability

| ID | Finding |
|---|---|
| **S3-01** | No CI, no automated tests. `api_node/run_tests.js`, `test2.js`, `test_all_endpoints.js` are ad-hoc scripts; `package.json` `test` script exits 1. Android has only the generated `ExampleUnitTest`. |
| **S3-02** | No database migration tooling. Schema evolved by hand plus one-off Python scripts (`DB/refactor_3nf.py`, `remove_userid_3nf.py`, `upload_remote.py` — all now deleted). No way to reproduce a schema. |
| **S3-03** | Six git branches attempting the same Wails v3 upgrade (`copilot/*` x2, `jules-*`, `wails-v3-upgrade-*`), none merged. Work is being redone in parallel. |
| **S3-04** | Build output, IDE state, installers and a `.msi` are untracked-but-unignored in `PCClient/`; `App/build/` and `App/.gradle/` are on disk. Repo hygiene prevents a clean `git add`. |
| **S3-05** | Duplicate tables `links` and `menupages` hold near-identical navigation data. `code` table contains three placeholder rows reading `INSERT CODE HERE FROM THE CODE OUTPUT`. `apikeys` table is orphaned (superseded by `users.api_key`), and its rows have empty usernames. |
| **S3-06** | Dead tables `requests` and `time` remain, superseded by columns on `pcnames`. |
| **S3-07** | No environments. One database, one host, no staging. Every change is tested in production. |
| **S3-08** | Hardcoded absolute base URLs compiled into every client (`AppConfig.localIp = '192.168.0.113'` shipped in the Flutter client). No configuration layer. |
| **S3-09** | No observability: no structured logs, metrics, tracing, error reporting, or health checks beyond `/ping`. |
| **S3-10** | Two production clients on end-of-life runtimes (.NET Framework 4.7.2 WinForms; Android Java 8 with `AsyncTask`). |

---

## 5. What is worth keeping

Not everything needs replacing. These are sound and should be carried forward:

- **The Socket.IO push model** (`server.js:60-138`). Rooms keyed `user_{id}_pc_{pcId}`, presence
  tracking, and `device_status` broadcast are the right shape. The auth around it is what is wrong.
- **The fallback-polling policy** in `PCClientWails/internal/realtime/policy.go` — exponential
  backoff 5s to 30s, poll only when the socket is unhealthy. Small, tested, correct.
- **The command allow-list** in `PCClientWails/internal/commands/executor.go` — enumerated
  invocations, case-insensitive match, reject-by-default. This is the correct enforcement point.
- **Windows Credential Manager integration** (`internal/auth/auth.go` via `wincred`) — the right
  place for a device secret.
- **The Flutter client's structure** — feature-first layout, `flutter_secure_storage`, `local_auth`,
  Riverpod. A good foundation.
- **`users` / `pcnames` / `reminders` relational core** — the entity model is right; the column
  types, keys and charset are not.

---

## 6. Constraints this architecture must respect

1. **Solo maintainer.** Operational complexity is a first-order cost. No Kubernetes, no service mesh,
   no polyglot backend.
2. **~1,000+ existing user accounts** with credentials that cannot be silently migrated — password
   upgrade must happen on next successful login.
3. **Two installed production clients that cannot be force-updated instantly.** The VB.NET desktop
   client is distributed by MSI; the Android app by Play Store. Legacy endpoints must keep working
   through a deprecation window.
4. **Destructive-action product.** Any auth regression can shut down a stranger's computer. Security
   changes ship before feature changes.
5. **GPL-3.0 licensed** (`gpl-3.0.rtf`) — dependency licences must remain compatible.

---

Next: [01 — Target Architecture](01-target-architecture.md)
