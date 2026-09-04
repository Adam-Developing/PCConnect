# 09 — Implementation notes: where the build differs from the plan

This document exists so that nothing in the delivered system silently contradicts the
architecture that specified it. Every divergence is listed here with the reason, and every
one is also recorded in the ADR or the section it changes.

Three kinds of entry appear below:

| | Meaning |
|---|---|
| **Superseded** | A decision was replaced. There is an ADR. |
| **Corrected** | The document was wrong about the existing system, and implementing it revealed that. |
| **Deferred** | Specified, not built, and why. |

---

## 1. Superseded decisions

| What | Was | Is | ADR |
|---|---|---|---|
| Runtime and framework | Node 22 + TypeScript on Fastify 5 | ASP.NET Core on .NET 10 | [0009](adr/0009-implementation-platform.md) |
| Database | MySQL 8.4 | PostgreSQL 18 | [0009](adr/0009-implementation-platform.md) |
| Migration tooling | `dbmate` | Our own runner, same plain-SQL file format, plus checksums | [0009](adr/0009-implementation-platform.md) |
| Realtime transport | Socket.IO 4 + Valkey adapter | SignalR + StackExchange.Redis backplane | [0009](adr/0009-implementation-platform.md) |
| Access-token signature | EdDSA (Ed25519) | ES256 (ECDSA P-256) | [0009](adr/0009-implementation-platform.md) |
| Desktop client | Go + Wails v2, running as the user | .NET Windows service + WPF companion | [0012](adr/0012-client-technology.md) |
| Mobile client | Flutter (Android + iOS) | Kotlin + Jetpack Compose (Android) | [0012](adr/0012-client-technology.md) |

Two capabilities were added that no ADR covered, and each has one now:

| What | ADR |
|---|---|
| Passkeys / WebAuthn as a first-class credential | [0010](adr/0010-passkeys.md) |
| Risk-tiered step-up for destructive commands | [0011](adr/0011-risk-tiered-step-up.md) |

---

## 2. Corrections to the documents

These are places where implementing the specification revealed that the specification was
wrong about the system it described. The document has been corrected and the reason is
recorded here.

### 2.1 `checkinternet.php` returns `yes`, not `Pong`

[04 §5](04-api-contract.md) mapped `GET /api/pcconnect/checkinternet.php` to `"Pong"`. That
came from `api/api_spec.md`, which documents the **never-deployed** Node gateway's
`/v1/system/checkinternet`.

The installed VB.NET client does this (`PCClient.vb:380`):

```vb
If CheckInternetContent = "yes" Then          ' online
ElseIf CheckInternetContent = "no" Then       ' offline
Else                                          ' also treated as offline
```

Returning `"Pong"` would land in the third branch and grey out the connectivity indicator
on every installed client. The shim returns `"yes"`. The Android client only checks for
HTTP 200, so it is unaffected either way.

**The general rule this establishes:** where `api_spec.md` and an installed client
disagree, the client wins. `api_spec.md` describes an implementation that was never
deployed; the client is what is running on someone's PC tonight. There is a golden-file
test for this specific string.

### 2.2 `getreminder.php` was missing from the shim map

[04 §5](04-api-contract.md)'s table omitted `GET /api/pcclient/getreminder.php`. The VB
client calls it every 500 ms and parses `id`, `date`, `time` and `reminder` out of a flat
dictionary (`PCClient.vb:339-347`); it is what raises the reminder window. Without it the
installed client's reminders stop working entirely.

It is implemented, returning the next due reminder, and an empty object when there is none
— which is how v1 signalled "nothing due" (the client catches the parse failure).

### 2.3 `updaterequest.php` is a GET

The document lists it as `POST`. The VB client calls `httpClient.GetAsync(updateRequest)`.
The shim accepts both.

### 2.4 The legacy reminder id is an integer

[02 §3.1](02-data-architecture.md) is emphatic that auto-increment ids are never exposed.
The shim exposes `reminders.id` as `ID`, because `PCClient.vb` does
`Integer.Parse(ReminderJSON("id"))` and `completereminder.php` sends it back. A UUID breaks
the installed client at the point where it marks a reminder done.

This is confined to `/api/*`, reachable only with a `client_kind='legacy'` credential, and
it dies with the shim. The v2 surface exposes UUIDv7 only.

### 2.5 The legacy API key is imported, not re-minted

[04 §5](04-api-contract.md) says the shim mints a compatibility token at login. It does,
but the importer *also* carries the existing `users.api_key` across as a
`client_kind='legacy'` token (hashed).

Without that, every installed Android client is signed out at cutover: it caches the key in
SharedPreferences and only re-authenticates when a call fails. Importing it means the
cutover is invisible to those users, and the key becomes a session they can see and revoke
— which the permanent, unrevocable v1 key never was (S1-05).

### 2.6 The `utf8mb3 → utf8mb4` conversion does not happen

[02 §5.2](02-data-architecture.md) describes a table-by-table charset conversion with index
prefix hazards. On PostgreSQL there is nothing to convert: the database is UTF-8
throughout. S2-08 is closed by the engine choice. The section is retained as a record of
what the MySQL path would have required.

### 2.7 Push delivery needs its own confirmation

[01 §C-3](01-target-architecture.md) draws the lifecycle as `issued --claim--> delivered`, and
[05 §4.3](05-realtime-architecture.md) originally described the poll as the only thing that
marks a row `delivered`. Both were written from the poll's point of view.

Under the push-first model the two documents otherwise specify, that leaves a hole: a healthy
agent never polls, so every pushed command went `issued -> succeeded` (or `-> failed`) with
`delivered_at` null and no `delivered` audit row. The system worked; the record of it did not.
Three things quietly broke — the delivery SLO in [01 §8](01-target-architecture.md), measured
as `delivered / issued`, read near zero while delivery was in fact succeeding; the funnel in
[08 §4](08-platform-and-delivery.md) was missing its middle stage; and the partition narrative
in [05 §6](05-realtime-architecture.md) ("the command stays `delivered` and the ack retries")
described a state the push path never reached.

The fix is a `ConfirmDelivery(commandId)` hub method the agent calls on receipt, before it
executes. The confirmation is the agent's, not the server's: `SendAsync` returning proves only
that a frame was written to a socket, and marking delivery on that would be the same kind of
claim as the v1 client reporting "Success" because a row was written. It is best-effort in the
agent — a confirmation that fails to land costs a row in the funnel and must never stop the
command being executed and acked — and idempotent on the server, since a reconnecting agent
may confirm twice and a racing poll may have claimed the row already.

01 §C-3 and 05 §4.3 now show both routes into `delivered`.

---

### 2.8 Reconnecting is only half the recovery

[05 §6](05-realtime-architecture.md) says that when an API instance restarts "no session state was
in the process, so nothing is lost". That is true of the server and not of the clients: SignalR
replays nothing that was sent while a socket was down, so events emitted during the gap are gone.

Observed directly during verification. The API was restarted under a connected phone and a
connected agent; both reconnected, and the phone then sat showing a command as `issued` that the
server had already recorded as `failed`. Worse than the stale row is why it stayed stale — the
client had stopped polling, correctly, because `ShouldPoll` is false while the socket is healthy.
A reconnect therefore *ends* the recovery mechanism without having recovered anything.

Every client now performs a catch-up read as part of reconnecting, and *what* it re-reads is the
host's own decision rather than the transport's. The first version hard-coded the agent's answer
— claim pending commands — into the shared realtime client, which meant the WPF companion, whose
credential is a user token, asked a device-only endpoint for pending commands and was refused
`403` on every connect. The client now exposes a `RecoverState` delegate: the agent installs the
claim, the companion re-reads devices and reminders, the phone re-reads everything the screen
shows. In the agent this runs on all three paths — first connect, SignalR's own automatic
reconnect, and the fallback loop's manual one — because an agent that was stopped while a command
was issued would otherwise never see it. The read is idempotent: the claim returns only commands
still `issued`, and the agent drops ids it has already executed.

---

### 2.9 A KEK rotation had no way to finish

[ADR-0004](adr/0004-reminder-encryption-model.md) and `deploy/.env.example` both describe
rotation as a two-key state: the new KEK becomes current, and the previous one stays configured
"until every data key has been rewrapped". Nothing rewrapped them.

The consequence is quiet and permanent. New users get data keys under the new KEK; every user who
existed before the rotation keeps a data key wrapped with the old one, forever. The previous key
can therefore never be removed, and removing it — which the documentation implies is the end of
the procedure — destroys those users' reminders with no recovery path.

`pcconnect-migrate rewrap-deks` is the missing step. It unwraps each stale data key with the KEK
that wrapped it and rewraps it with the current one, one user per statement, guarded on the old
wrapper so a concurrent write wins rather than being clobbered. Only the wrapper changes: the
data key underneath is the same bytes, so nothing is re-encrypted and an interrupted run leaves a
mixture the system already serves correctly. `--status` reports the split and says whether the
previous key is safe to remove yet; the command exits non-zero while any key still needs it.

A data key it cannot unwrap is skipped, counted and named, not thrown on. The first version
aborted at the first such row, which meant one user whose KEK had been lost blocked the rotation
for everybody else — and, because each pass re-selected the same rows, that loop could not
terminate either. It now pages by id, so progress is made even where a row cannot be, and reports
"N of them are wrapped with 'x', which is not configured — restore it." The distinction the
operator needs is between a rotation that is *stalled* and one that is *finished except for rows
nobody can read*; those are different problems and the output says which it is.

Nothing is written for a row that cannot be read: silently re-keying a DEK it cannot unwrap would
turn a recoverable mistake into an unrecoverable one. Covered by `KeyRotationTests` — including
the case of a stranded key alongside a healthy one, which is not a contrived fixture but exactly
the shape of a database that has been imported from v1. The whole procedure is
[runbook §5](../runbook.md).

---

### 2.10 The WPF companion's theme was wired in a way that silently did nothing

Found by looking at the running window rather than by reading the code, which is the only way
these could have been found: every one of them compiles, and none of them throws.

- `<Style TargetType="Window">` in application resources never applied. WPF keys an implicit
  style on the element's exact runtime type, so a style targeting `Window` does not reach
  `MainWindow` or `StepUpWindow`. Both kept the default white background while the cards inside
  them were painted dark. The style is now keyed and referenced explicitly, which cannot fail
  quietly.
- `Heading` and `Caption` did not set `BasedOn`, so they inherited neither the foreground nor
  `TextWrapping`. Headings rendered near-black on near-black, and the sign-in explanation was
  clipped mid-word instead of wrapping. Worst of these was the step-up dialog, where the prompt
  naming the consequence — "Shut down this PC?" — was the unreadable text.
- The implicit `TextBlock` style set `Foreground`, which beats inheritance. A button's own
  foreground was therefore ignored by its label, so `DangerButton` had no visible effect and
  "Shut down" looked exactly like "Lock" — the one distinction [ADR-0011](adr/0011-risk-tiered-step-up.md)
  exists to make. The colour is now set once on the window and inherits.
- `TabItem`, `ListBoxItem`, `ComboBox`, `DatePicker` and `CheckBox` were left on the stock
  Windows light chrome: unreadable tab headers, a selection that greyed out when the list lost
  focus, and a white date field in a dark card.
- The connection badge read `IsConnected` once, just after connecting, and showed that answer for
  the rest of the session — displaying "Reconnecting" over a healthy socket. The realtime client
  now raises `ConnectionStateChanged`, which is what the Android client already did with a
  `StateFlow`.
- The command buttons were enabled with no PC selected. The send was guarded, so nothing was
  sent, but silently: pressing "Shut down" did nothing at all, which reads as a broken
  application rather than as a precondition. They are now disabled until a PC is selected.
- Two different buttons both read "Sign out" — one signs out of PCConnect on this machine, the
  other signs the user out of Windows on the remote one. They are now "Sign out of PCConnect" and
  "Sign out of Windows".

---

### 2.11 The KEK slots could not express a rotation

Two problems in the deployment configuration, both found by trying to write §5 of the runbook as
something an operator could actually follow.

`deploy/env/generate-secrets.sh` emitted `KEK_CURRENT_ID=k$(date +%Y%m)` — `k202609` — while
`docker-compose.yml` defined the key under the fixed name `K1`. A deployment set up with this
project's own script therefore refused to start: `CurrentKekId 'k202609' has no matching key`.
Fail-closed, so nothing was at risk, but the documented first run did not work.

The second is the one that mattered. The slots were named `KEK_CURRENT` and `KEK_PREVIOUS`, which
invites the obvious rotation — move the current value into `KEK_PREVIOUS`, put the new key in
`KEK_CURRENT` — and that is silently destructive. The id recorded in `users.dek_kek_id` was `k1`
for everything, so after such a rotation the server would try to unwrap keys written under the old
`k1` with the new `k1` and fail authentication on every one of them. Users would lose access to
their reminders with no error that explains why.

The slots are now `KEK_KEY_K1` and `KEK_KEY_K2`, with `KEK_CURRENT_ID` naming which is current, so
the slot name *is* the id and rotation means filling the empty slot rather than refilling the full
one. The two alternate, and an id is never reused for a different key. The `migrate` service also
carries the keys now, because `rewrap-deks` runs through it and could not have run without them.

---

### 2.12 The Android app could not receive a single realtime event

Found by sending a command from the phone and watching the row stay on `issued` while the server
had already recorded `failed`.

The server sends each event as the envelope 05 §3 specifies — a JSON **object**, `{v, id, at,
data}`. The Kotlin client registered its handlers with `String::class.java`, telling the SignalR
Java client to read that object as a JSON string. Every event failed to deserialize inside the
library and was dropped before reaching any handler. Nothing threw, nothing logged, and the socket
stayed up: the badge said "Live" and not one push had ever arrived, for any event type, ever.

What hid it is that the app looks fine without realtime. Every screen re-reads on refresh, so the
list updated whenever the user pulled to refresh or switched tabs — the app behaved exactly like
the polling client it was built to replace, which is the one failure mode nobody would notice by
using it.

The handlers now take `JsonElement` (GSON parses any shape into one) and kotlinx decodes the app's
own `@Serializable` types from it, so there is still a single serializer for the contract. GSON is
declared explicitly in the version catalogue: it was already on the runtime classpath through
SignalR, but a transitive `implementation` dependency is not on the compile classpath.

With the fix the phone shows `issued → delivered → failed` as it happens, and a PC coming online
turns the indicator green without a refresh. `reminder.changed` is now handled too — it was in
the contract and in the .NET client, but the phone had no subscriber for it.

### 2.13 Two things the phone did with a wrong password

Both found by typing one deliberately, which is worth doing because it is the path a real person
takes most often on a destructive command.

**The client retried the password check.** The API client refreshed its token and re-sent any
request that came back 401. A refused step-up *is* a 401 (`auth.step_up_invalid`), so every
mistyped password was checked twice: two of the ten attempts an account gets in fifteen minutes,
and a refresh-token rotation, spent on a typo. The retry is now conditional on the error code
actually being about the token. Automatically retrying a credential check was the wrong default
in the first place.

**The dialog closed on refusal.** A wrong password dismissed the confirmation, dropped the typed
password and the pending command, and reported itself in a snackbar that was gone within seconds
— on the one flow in the app where the user most needs to know what happened. The dialog now stays
open with the error under the field and the password still in it, and closes only on success or
cancel.

The step-up field also lacked `KeyboardType.Password`, so the mask hid it on screen while the
keyboard still offered it in the suggestion strip and could learn it. The sign-in field already
had it.

### 2.14 The Android app now shows its own log

Not a correction to the architecture — an addition the architecture did not ask for, recorded here
because it changes what the app stores.

The v1 Android app failed silently: a request that did not work left a blank screen, so the only
way to find out why was to attach `adb logcat`. **Account → Logs** now shows what the app has been
doing — every request as `METHOD /path -> status in Nms`, the realtime connection coming and
going, and every error with its stable code — with Copy for pasting into a report and Clear.

It holds no secrets: no tokens, no passwords, no device secrets, no request or response bodies, no
reminder text. The buffer is bounded at 400 entries and is cleared on sign-out, because it names
devices and belongs to the session that produced it rather than to whoever signs in next.

---

---

## 3. The client redesign

Both clients were rebuilt against a single design — the same palette, type scale,
radii and icon set on Windows and Android — replacing the dark, control-per-row
screens the first implementation shipped with. The visual system lives in
`clients/windows/PCConnect.Companion/Resources/Theme.xaml` and
`clients/android/.../ui/Theme.kt`, and the icons in both are the same Material
Icons Outlined geometry, generated once into a WPF `ResourceDictionary` and a set
of Android vector drawables so a command is drawn with the same glyph on either
client.

Five things about that design could not be built exactly as drawn. Each is
recorded here rather than approximated in the UI, because a control that lies
about what it does is worse than one that is missing.

| Drawn | Built | Why |
|---|---|---|
| A PC appears on the account by signing in on it | The pairing code stays, in the empty state and under the PC list | The device credential belongs to the agent, which runs as LocalSystem in session 0; the companion runs as the user and cannot write a credential the service can read ([ADR-0012](adr/0012-client-technology.md)). Making the code disappear would need the pairing handshake to move into the agent, not the UI. |
| A reminder chooses which PCs it shows on | The picker appears only when discovery advertises `reminders.targets`; otherwise every reminder shows on every PC | `CreateReminderRequest` has no device targeting and `ReminderResponse` returns none, so today the server shows every reminder everywhere. The field is sent as an additive `deviceIds`, and the client reads it back through `Reminder.showsOn`, so the UI needs no change when the server grows the capability. |
| A fingerprint confirms a destructive command *instead of* a password | The password is always asked for; the fingerprint is a local gate in front of it | Step-up is verified server-side and takes a password ([ADR-0011](adr/0011-risk-tiered-step-up.md)). Replacing it needs a passkey assertion the server accepts in place of one — the endpoints exist, the Android app does not register passkeys yet. |
| "Snooze 10 min" on the full-screen reminder | Implemented, entirely on the PC showing it | There is no snooze on the wire. The window comes back in ten minutes; the reminder is untouched, so it still fires at its own time on every other screen, and a snooze does not survive restarting the app. |
| A repeat with several times a day is one reminder | One series per time | `BYHOUR` and `BYMINUTE` multiply out: "10:30 and 15:45" in a single rule expands to four occurrences a day, not two. Each time is saved as its own series, which is what the words mean and what `RecurrenceExpander` already handles. The sheet says so. |

Two further gaps are cosmetic and noted so nobody mistakes them for oversights.
The design is set in IBM Plex Sans and IBM Plex Mono; neither ships with Windows
or Android, so both clients carry the design's scale on the platform faces
(Segoe UI and Cascadia Mono; Roboto and its monospace). And the desktop calendars
filter by clicking a day rather than by dragging a range.

The commands table in desktop Settings writes `allowedCommands` for this PC. Its
second column, "asks for password", is deliberately read-only: which commands
require a step-up is the server's policy, and a switch that could not turn the
requirement off would be a lie about what it does.

---

## 4. Deferred

| What | Where specified | Why not built | What would be needed |
|---|---|---|---|
| Web dashboard | [06 §4](06-client-architecture.md) | The two clients that carry traffic are the desktop and the phone; the dashboard exists mainly so pairing is reachable without a phone, which the WPF companion now also does | A small React app against the generated contract |
| iOS client | [ADR-0007](adr/0007-mobile-client-technology.md) | No Apple Developer account; listed as an open question in the README rather than a commitment | An account, and a client — the API is already platform-neutral |
| Sentry | [01 §6.5](01-target-architecture.md) | Needs a DSN, which is an account decision | `SENTRY_DSN` and the SDK package |
| SMTP delivery | [03 §8](03-security-architecture.md) | Needs credentials for a provider. `IEmailSender` is implemented against the log so reset and verification flows work end to end locally and in staging | An SMTP implementation and credentials in `.env` |
| Push wake-up (FCM/APNs) | [ADR-0003](adr/0003-command-channel-transport.md), "Revisit when" | Explicitly a future item, not part of the trust path | A Firebase project |
| Staging environment | [07 Phase 1.4](07-migration-plan.md) | Needs a second VM or compose project and a host to put it on | Infrastructure; the compose stack is the same file |
| Signed installers and Play Store release | [06 §6](06-client-architecture.md) | Needs an Authenticode certificate and the Play signing key | Certificates; the CI wiring reads both from secrets already |

---

## 5. Where each architecture control ended up

A reader coming from the architecture documents can find every named control here.

| Control | Document | Implementation |
|---|---|---|
| Argon2id, server-side, upgrade-on-login | 03 §2.5, 02 §6 | `Argon2PasswordHasher`, `IdentityService.VerifyPasswordAsync` |
| Token pair, rotation, family reuse detection | 03 §2.4 | `IdentityService.RefreshAsync` |
| Scopes; issue and receive disjoint | 03 §2.3 | `Scopes`, `CallerIdentity.Require` |
| Device pairing | 03 §2.6 | `DeviceService.StartPairingAsync` / `ClaimPairingAsync` / `PollPairingAsync` |
| The five server checks on a command | 03 §3 | `CommandService.IssueAsync`, in order, with comments naming each |
| The three agent checks (allow-list, freshness, replay) | 03 §3 | `CommandExecutor.ExecuteAsync` |
| Mandatory TTL and the expiry sweep | ADR-0003 | `CommandTtl`, `CommandService.ExpireDueAsync`, `CommandExpiryJob` |
| Command state machine | 05 §4 | `CommandStateMachine` |
| Envelope encryption, AES-256-GCM | ADR-0004 | `EnvelopeEncryptor`, `ReminderService.LoadDataKeyAsync` |
| Rate limits | 03 §6 | `RateBudgets`, `RateLimiter` |
| CORS allow-list, bearer-only, security headers | 03 §5 | `Program.cs` |
| One error envelope | 04 §3.1 | `ErrorEnvelopeHandler`, `ErrorCodes` |
| Cursor pagination | 04 §3 | `Cursor` |
| UTC everywhere, IANA timezone per user | 01 §6.4 | `timestamptz`, `reminders.timezone`, `RecurrenceExpander` |
| RFC 5545 recurrence | 02 §3 | `RecurrenceExpander`, `reminder_occurrences` |
| Presence in the cache, durable heartbeat coalesced | 05 §6 | `PresenceTracker`, `DeviceService.HeartbeatAsync` |
| Fallback polling with jitter | 05 §5 | `FallbackPollingPolicy` (.NET and Kotlin) |
| Retention and GDPR erasure | 03 §8 | `RetentionJob`, asserted by `AccountLifecycleTests` |
| Verification gates | 02 §5.1 | `db/verification/checks.sql`, `VerificationJob`, `pcconnect-migrate verify` |
| Legacy shim with `Deprecation`/`Sunset` and a counter | 04 §5 | `LegacyShim`, `CommandMetrics.LegacyRequest` |
| Secret containment | 03 §7 | `.gitignore`, `.gitleaks.toml`, `.git-hooks/pre-commit`, `deploy/.env.example` |

---

## 6. The OpenAPI document

`openapi/pcconnect-v2.yaml` is now **generated** from the running API, per
[04 §1](04-api-contract.md)'s "the contract is generated, not written". CI regenerates it
and fails if the committed copy differs.

The original hand-written specification is preserved as
`openapi/pcconnect-v2.design.yaml`. It is the design intent, with prose the generated
document cannot carry, and it is the right thing to read to understand *why* the surface
looks as it does. It is no longer the contract.

---

Previous: [08 — Platform & Delivery](08-platform-and-delivery.md)
