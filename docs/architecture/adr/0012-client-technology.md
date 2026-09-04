# ADR-0012 — Client technology: .NET service + WPF companion, Kotlin on Android

**Status:** Accepted
**Date:** 2026-09-02
**Supersedes:** [ADR-0006](0006-desktop-client-technology.md) (Go + Wails),
[ADR-0007](0007-mobile-client-technology.md) (Flutter), and [06 §2.3](../06-client-architecture.md)'s
"runs as the user, not as a service"
**Context docs:** [06](../06-client-architecture.md), [ADR-0009](0009-implementation-platform.md)

## Context

ADR-0006 chose Go + Wails v2 for the desktop agent, on the strength of promoting the
existing `PCClientWails` prototype. ADR-0007 chose Flutter for mobile, on the strength of
promoting the existing `mobile_flutter` prototype and getting iOS nearly free.

Both prototypes have since been removed from the working tree, so the "promote what
exists" argument that carried both ADRs no longer has a subject. With
[ADR-0009](0009-implementation-platform.md) moving the backend to .NET, the desktop client
can share the contract types and the client SDK with the server it talks to — which is a
stronger version of the "one language" benefit ADR-0006 was reaching for by pairing Go with
a Go backend it never got.

The maintainer has directed the clients at a .NET Windows service with a WPF companion, and
a Kotlin/Jetpack Compose Android app.

## Decision

**Windows: a .NET Windows service (`PCConnect.Agent`) plus a WPF companion
(`PCConnect.Companion`). Android: Kotlin with Jetpack Compose. Both bind to the shared
contract types in `PCConnect.Core.Contracts` (Windows) or to types generated from the same
OpenAPI document (Android).**

### Why the Windows client is two processes

This is the part that contradicts [06 §2.3](../06-client-architecture.md) most directly,
which said:

> **Runs as the user** — Not as a service, not elevated. `shutdown /s` and
> `LockWorkStation` need no elevation, and running unprivileged bounds the damage of an
> agent compromise.

That reasoning is sound and the trade-off is real. Running as a service was chosen anyway,
because it buys something the user-session agent cannot:

| | User-session agent (06 §2.3) | Windows service (chosen) |
|---|---|---|
| Receives a command when nobody is signed in | **No** | Yes |
| Receives a command at the lock screen | **No** | Yes |
| Survives the user signing out | No | Yes |
| Starts before anyone logs in | No | Yes |
| Compromise blast radius | The user's account | **SYSTEM** |
| Can `LockWorkStation` / sign out | Yes | **No** — session 0 has no interactive session |

"Shut down my PC from my phone" is the product. An agent that only works while someone is
already signed in fails at the moment the feature is most useful: the machine was left on
and the user has gone out.

The last row is why there are two processes rather than one. `LockWorkStation` and
`ExitWindowsEx` act on an interactive session, which session 0 does not have. So:

- **`PCConnect.Agent`** (service, LocalSystem) holds the device credential, keeps the
  realtime connection, and performs `shutdown`, `restart`, `sleep` and `hibernate` through
  Win32 — `InitiateSystemShutdownExW` and `SetSuspendState`, never a child process and
  never a command line.
- **`PCConnect.Companion`** (WPF, the user's session) is what a person interacts with:
  signing in, pairing, issuing commands, seeing reminders in a full-screen window. It also
  serves `lock` and `signout` for the service over a named pipe restricted to SYSTEM and
  the interactive user.
- The pipe carries **two verbs and nothing else**. Nothing derived from an HTTP body
  crosses it, so widening the service's privilege does not widen what an attacker who
  controls the server can ask for.

**The privilege cost is accepted and mitigated, not waved away.** An agent compromise is a
SYSTEM compromise. What bounds it:

- The executor has no shell path at all — the v1 client shelled out to `shutdown.exe` with
  an interpolated string; this one calls Win32 directly with a fixed argument per command
  type, so there is no code path from a network payload to a process.
- The command vocabulary is six closed values, rejected by default, and the agent's
  allow-list is independent of the server's.
- The device credential is scoped to `command:receive` and `command:ack` for one device,
  and lives in Windows Credential Manager rather than a JSON file (S1-04).
- Freshness and replay are checked on the agent, so a compromised *server* still cannot
  make an agent run something outside those six things or run a stale command.

### Why Kotlin rather than Flutter

ADR-0007's decisive argument was that `mobile_flutter/` already existed and gave iOS free.
It no longer exists, so the comparison is between two greenfield apps.

| | Flutter | Kotlin + Compose (chosen) |
|---|---|---|
| iOS | Nearly free | A separate app, not written |
| Platform credential storage | Via a plugin | Android Keystore directly |
| Biometric prompt | Via a plugin | `androidx.biometric` directly |
| SignalR client | No official Dart client; hand-rolled or third-party | Official `com.microsoft.signalr` |
| Toolchain | Dart SDK plus the Android SDK | The Android SDK the project already needs |

Losing free iOS is the real cost, and it is a product decision the maintainer has made:
there is no Apple Developer account today, and ADR-0007 listed that as an open question
rather than a settled commitment. The API is platform-neutral — the device `platform`
column already admits `ios`, `macos` and `linux` — so an iOS client remains a client to
write, not an architecture to change.

## Options considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Service + WPF companion; Kotlin on Android** (chosen) | Commands work with nobody signed in; one language shared with the backend; platform APIs used directly | Service runs as SYSTEM; two Windows processes and an IPC channel; no iOS | **Chosen** |
| Single user-session .NET agent (06 §2.3 as written) | Unprivileged; one process; no IPC | Cannot receive a command at the lock screen or with nobody signed in — the case the product exists for | Rejected |
| Service only, no companion | One process | Cannot lock or sign out at all; no reminder window; no pairing UI | Rejected |
| Go + Wails, per ADR-0006 | Keeps that ADR | The prototype it promoted is gone; a second language and toolchain for one client; the same session-0 problem, unsolved | Rejected |
| Flutter, per ADR-0007 | Keeps that ADR; iOS nearly free | The prototype it promoted is gone; iOS is not funded; plugins between the app and every platform API it needs | Rejected |

## Consequences

**Positive**

- A command reaches a PC that is on but has nobody signed in, which is the case the
  product is for.
- The Windows clients bind to `PCConnect.Core.Contracts` directly: a server field that
  changes shape breaks the build rather than a user's machine.
- The two v1 accessibility affordances survive — the reminder window's configurable
  colours, and the tray behaviour where closing the window leaves PCConnect running.
- The full-screen transparent reminder window, which six abandoned Wails v3 branches were
  chasing (S3-03), is native to WPF and needs no framework upgrade.
- Android uses the Keystore and `androidx.biometric` without a plugin layer.

**Negative**

- **The service runs as LocalSystem.** This is the significant one. An agent compromise is
  a SYSTEM compromise, where 06 §2.3's design bounded it to one user account. The
  mitigations above reduce the paths into it; they do not change what happens if one is
  found.
- **Two processes and a named pipe**, which is more moving parts than one agent, and a new
  failure mode: with the companion not running, `lock` and `signout` fail honestly rather
  than silently — but they do fail.
- **No iOS client**, which ADR-0007 would have given nearly free.
- **Windows-only desktop.** Go/Wails was cross-platform in principle; .NET is too, but the
  executor is Win32 and a macOS or Linux agent would need its own.
- Two ADRs superseded, with the same reader cost noted in ADR-0009.

**Neutral**

- The fallback-polling policy from `internal/realtime/policy.go` is carried forward in both
  clients, with the jitter [05 §5](../05-realtime-architecture.md) asks for — the
  assessment identified it as small, tested and correct, and it survives the language
  change intact.
- The old VB.NET and Java clients are untouched. They keep working against the legacy shim
  until the sunset gate in [ADR-0008](0008-api-versioning-and-legacy-sunset.md) is met.

## Revisit when

- An Apple Developer account exists and iOS becomes fundable, at which point a shared
  mobile framework becomes worth reconsidering — the API will not need to change.
- Windows offers a supported way for a session-0 service to act on an interactive session,
  which would remove the companion from the command path.
- A macOS or Linux agent is wanted, at which point the executor's platform boundary is the
  only part that needs new code.
