# 04 — API Contract

Machine-readable source of truth: [`openapi/pcconnect-v2.yaml`](openapi/pcconnect-v2.yaml)

---

## 1. The contract is generated, not written

> **As built.** The pipeline below is the same idea with different parts:
> the request and response **records** in `PCConnect.Core.Contracts` are the schema,
> ASP.NET Core generates OpenAPI 3.1 from them, and `tools/generate-openapi.sh`
> commits the result. CI regenerates and fails when the committed copy is stale, and
> `oasdiff` fails an unlabelled breaking change. The .NET clients bind to those records
> directly, so drift is a compile error rather than a runtime surprise.

The root cause of §00.3 — three incompatible API surfaces for the same operations — is that the
contract was prose. `api/api_spec.md` documents endpoints that exist in neither implementation, and
sits in the directory of the implementation it does not describe.

The fix is mechanical:

```
   Zod schema  ──▶  Fastify runtime validation   (requests AND responses)
        │
        ├───────▶  OpenAPI 3.1 document          (generated at build)
        │                 │
        │                 ├──▶ TypeScript client   (web dashboard)
        │                 ├──▶ Dart client         (Flutter, via openapi-generator)
        │                 └──▶ Go client           (desktop agent, via oapi-codegen)
        │
        └───────▶  Contract tests                 (Dredd / schemathesis in CI)
```

Consequences that matter:

- A handler that returns a field not in its response schema **fails in CI**, not in a client.
- Every client's model layer is regenerated from one document, so drift is impossible by construction.
- The OpenAPI document is committed; a PR that changes it shows the contract change in the diff.
- `oasdiff` runs in CI and **fails the build on a breaking change** unless the PR is labelled
  `api-breaking` and bumps the version.

---

## 2. Versioning and lifecycle

| Rule | Detail |
|---|---|
| Path versioning | `/v2/...`. Chosen over header negotiation because it is visible in logs, cacheable, and trivial for a Go/Dart/VB client to construct. |
| Additive is not breaking | New optional request fields and new response fields ship without a version bump. Clients must ignore unknown fields — stated in the spec and enforced by the generators. |
| Breaking requires a new version | Removing a field, narrowing a type, changing a status code, or tightening validation. |
| Deprecation window | Minimum **6 months** from the `Deprecation` header first appearing to removal, and never before legacy client traffic falls under 1%. |
| Announcement | `Deprecation: true` and `Sunset: <RFC 1123 date>` response headers, plus `GET /v2/meta/discovery`. |

`GET /v2/meta/discovery` is unauthenticated and is what makes the client sunset story work:

```jsonc
{
  "apiVersion": "2.3.0",
  "realtimeUrl": "wss://api.pcconnect.example/rt",
  "minimumSupportedClient": { "desktop": "5.0.0", "mobile": "8.0.0" },
  "recommendedClient":     { "desktop": "5.2.1", "mobile": "8.1.0" },
  "legacySunset": { "v1": "2027-03-01T00:00:00Z" },
  "capabilities": ["commands.ttl", "reminders.rrule", "devices.pairing"]
}
```

Clients call it at startup. Below `minimumSupportedClient` they show a blocking update prompt —
which is the mechanism that eventually lets the legacy PHP endpoints be switched off.

---

## 3. Conventions

| Aspect | Rule |
|---|---|
| Casing | `camelCase` in JSON. (The current API mixes `PCName`, `api_key`, `Reminder`, `sort_order` in one payload.) |
| Identifiers | UUIDv7 strings. Auto-increment ids are never exposed. |
| Timestamps | RFC 3339 UTC with `Z`. Never a local wall-clock string. |
| Collections | `{ "items": [...], "nextCursor": "..." }` — cursor pagination, never offset |
| Empty results | `200` with an empty `items` array, never `204` and never `null` |
| Partial update | `PATCH` with a partial body. `PUT` is not used. |
| Idempotency | `Idempotency-Key` header accepted on every `POST`/`PATCH`/`DELETE` |
| Correlation | `X-Request-Id` echoed on every response and present in every log line |

### 3.1 One error envelope

```jsonc
{
  "error": {
    "code": "device.not_paired",
    "message": "This device is not paired to your account.",
    "requestId": "01JZ8K9M4QW7XPYRV2N6THGDF3",
    "details": [ { "field": "deviceId", "issue": "not_found" } ]
  }
}
```

`code` is stable and documented; `message` is for humans and may change. Clients switch on `code`.

| Status | Used for |
|---|---|
| 400 | Malformed or schema-invalid request |
| 401 | Missing, expired, or invalid token |
| 403 | Valid token, insufficient scope, or resource not owned |
| 404 | Resource does not exist **or** is not visible to this caller — 403 and 404 are deliberately indistinguishable across ownership boundaries, so the API is not an existence oracle |
| 409 | Conflict: duplicate device name, idempotency key replayed with a different body |
| 410 | Endpoint removed after its sunset date |
| 422 | Semantically invalid (e.g. `rrule` that parses but never fires) |
| 429 | Rate limited; always carries `Retry-After` |
| 5xx | Never carries an internal message or stack trace |

---

## 4. Endpoint surface

`✓` = implemented in `api_node` today (possibly under a different shape) · `+` = new

### Auth — `/v2/auth`

| Method | Path | Scope | Notes |
|---|---|---|---|
| `POST` | `/login` | — | ✓ Takes a **plaintext password** over TLS. Also accepts `legacyPasswordHash` during migration ([02 §6](02-data-architecture.md)). Returns a token pair. |
| `POST` | `/refresh` | — | + Rotates. Reuse of a revoked token revokes the family. |
| `POST` | `/logout` | authenticated | + Revokes the presented refresh token. |
| `POST` | `/logout-all` | `account:manage` | + Revokes every session; used after a suspected compromise. |
| `POST` | `/register` | — | + Replaces the untracked `signup.php`. Email verification required. |
| `POST` | `/password/forgot` | — | + Always returns 202 regardless of whether the account exists. |
| `POST` | `/password/reset` | — | + Consumes a single-use challenge; revokes all sessions. |
| `POST` | `/email/verify` | — | + |

### Devices — `/v2/devices`

| Method | Path | Scope | Notes |
|---|---|---|---|
| `GET` | `` | `device:read` | ✓ Returns objects with `id`, `displayName`, `isOnline`, `lastSeenAt`, `allowedCommands`. (Today: a bare array of name strings.) |
| `GET` | `/{deviceId}` | `device:read` | + |
| `PATCH` | `/{deviceId}` | `device:manage` | + Rename; change `allowedCommands`. |
| `DELETE` | `/{deviceId}` | `device:manage` | + Revokes the device credential and cascades. |
| `POST` | `/pair/start` | — | + Agent-initiated. Returns a pairing code. |
| `POST` | `/pair/claim` | `device:manage` | + User-initiated. Confirms the code. |
| `POST` | `/pair/poll` | — | + Agent collects `deviceId` + `deviceSecret`, **once**. |
| `POST` | `/token` | — | + Agent exchanges `deviceId` + `deviceSecret` for a device access token. |
| `POST` | `/{deviceId}/heartbeat` | `command:receive` | + Coalesced; the fallback when the socket is down. |

`POST /v2/devices` (blind creation from a header) does **not** exist. Devices come into being only
through pairing.

### Commands — `/v2/commands`

| Method | Path | Scope | Notes |
|---|---|---|---|
| `POST` | `` | `command:issue` | ✓ replaces `/requests/exchange`. Body carries a **client-generated** `id`, `deviceId`, `type`, optional `params`, optional `ttlSeconds` (default 120, max 600). |
| `GET` | `` | `command:issue` | + History for the user, cursor-paginated. |
| `GET` | `/{commandId}` | `command:issue` | + Live status; how a phone shows "delivered / executed". |
| `POST` | `/{commandId}/cancel` | `command:issue` | + Only from `issued`. |
| `GET` | `/pending` | `command:receive` | ✓ replaces `/requests`. **Device tokens only.** Returns unexpired, undelivered commands; marks them `delivered`. |
| `POST` | `/{commandId}/ack` | `command:ack` | ✓ replaces `/requests/clear`. Body `{outcome, resultCode?, resultMessage?}`. Per-command, not "clear everything". |

The shift from "clear the mailbox" to "acknowledge command X" is what makes the two delivery paths
(socket push and poll) safely reconcilable — S2-05.

### Reminders — `/v2/reminders`

| Method | Path | Scope | Notes |
|---|---|---|---|
| `GET` | `` | `reminder:read` | ✓ Cursor-paginated; filters `from`, `to`, `completed`. Times are UTC RFC 3339 plus the originating timezone. |
| `POST` | `` | `reminder:write` | ✓ `{body, dueAt, timezone, rrule?}` |
| `GET` | `/{reminderId}` | `reminder:read` | + |
| `PATCH` | `/{reminderId}` | `reminder:write` | ✓ (was `PUT`) |
| `DELETE` | `/{reminderId}` | `reminder:write` | + Soft delete. |
| `POST` | `/{reminderId}/complete` | `reminder:write` | ✓ Body `{completed, occurrenceAt?}` — `occurrenceAt` completes one occurrence of a series rather than the whole series. |

### Account — `/v2/account`

| Method | Path | Scope | Notes |
|---|---|---|---|
| `GET` | `/profile` | authenticated | ✓ |
| `PATCH` | `/profile` | `account:manage` | ✓ Password change is **not** here — it lives at `/auth/password/change` and requires the current password. |
| `GET` | `/sessions` | `account:manage` | + Active refresh-token families with device, IP and last-use, so a user can see and end sessions. |
| `DELETE` | `/sessions/{familyId}` | `account:manage` | + |
| `GET` | `/export` | `account:manage` | + GDPR data export. |
| `DELETE` | `` | `account:manage` | + Soft delete now, hard delete in 30 days. |

### Meta — `/v2/meta`

| Method | Path | Notes |
|---|---|---|
| `GET` | `/discovery` | + §2 |
| `GET` | `/healthz` | + Process liveness. |
| `GET` | `/readyz` | + DB and Valkey reachable. |
| `GET` | `/time` | + Replaces `time.php`. Retained only for client clock-skew diagnostics — never for scheduling, because scheduling is UTC end to end. |

`GET /v1/system/checkinternet` returning the string `"Pong"` is dropped. Connectivity is a
transport-layer fact; `/healthz` covers it with a real body.

---

## 5. Legacy compatibility shim

`/legacy/*` re-implements the exact C1 wire format so the installed VB.NET and Java clients keep
working. It is a thin translation layer over the v2 services — it does **not** get its own database
access, so there is one implementation of every rule.

| Legacy endpoint | Shim behaviour |
|---|---|
| `POST /api/login.php` | Verifies via `identity`, returns a **legacy-format API key** minted as a long-lived, `command:issue`-only compatibility token stored in `refresh_tokens` with `client_kind='legacy'`. Bare `text/plain` response preserved. |
| `GET /api/pcconnect/PCNames.php` | `{PCNames:[...]}` from `devices` |
| `POST /api/pcconnect/exchange.php` | Issues a command with a **server-generated** id and the default TTL |
| `GET /api/pcclient/findrequests.php` | `GET /v2/commands/pending`, flattened to the legacy string |
| `GET`/`POST` `/api/pcclient/updaterequest.php` | Acks every delivered command for the device. The installed client uses **GET**; both are accepted |
| `POST /api/pcclient/updatepctimedatabase.php` | Heartbeat |
| `GET /api/pcclient/listreminders.php` | Reminder list in the legacy array shape |
| `POST /api/pcclient/reminder.php` | Create reminder |
| `POST /api/pcclient/completereminder.php` | Complete reminder |
| `POST /api/pcclient/addpc.php` | **Auto-pairs** a device for legacy clients only, and writes a `security_events` row each time. This is the one place the old weak model survives; it dies with the shim. |
| `GET /api/pcclient/getreminder.php` | The next due reminder as `{id,date,time,reminder}`; an empty object when there is none. **Added in implementation** — the VB client polls it every 500 ms and it is what raises the reminder window ([09 §2.2](09-implementation-notes.md)) |
| `GET /api/time.php` | `{time:"HH:MM:SS"}` |
| `GET /api/pcconnect/checkinternet.php` | **`"yes"`, not `"Pong"`.** `PCClient.vb:380` treats anything else as offline; `"Pong"` came from `api_spec.md`, which describes a gateway that was never deployed ([09 §2.1](09-implementation-notes.md)) |

Every shim response carries `Deprecation: true` and `Sunset: <date>`, and every shim request
increments a labelled Prometheus counter. That counter is the data that decides when the shim can be
deleted — not a guess. See [ADR-0008](adr/0008-api-versioning-and-legacy-sunset.md).

---

## 6. Contract testing

| Layer | Tool | Asserts |
|---|---|---|
| Schema conformance | `schemathesis` against the OpenAPI doc | Every documented response shape is actually produced, including error paths |
| Breaking-change detection | `oasdiff` vs the previous tag | No silent breaking change |
| Authorisation matrix | Hand-written integration suite | For every resource: owner 2xx, other user 404, no-scope 403, no-token 401 |
| Legacy shim fidelity | Golden-file tests capturing real C1 responses | Byte-level compatibility for the installed clients |
| Client generation | CI regenerates Dart/Go/TS clients | Generation succeeds and the diff is committed |

The authorisation matrix is the suite that would have caught S1-08.

---

Previous: [03 — Security Architecture](03-security-architecture.md) · Next: [05 — Real-Time Architecture](05-realtime-architecture.md)
