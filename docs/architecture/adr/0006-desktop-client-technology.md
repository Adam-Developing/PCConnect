# ADR-0006 — Desktop client technology and Wails version

**Status:** Accepted
**Date:** 2026-09-02
**Context docs:** [06 §2](../06-client-architecture.md)

## Context

Two desktop clients exist:

- `PCClient/` — VB.NET WinForms on **.NET Framework 4.7.2**, ~1,800 lines, in production. It polls
  in a `While True` loop (S2-14), stores a password-equivalent SHA-256 hash in `My.Settings`
  (S1-04), and hardcodes eight absolute endpoint URLs.
- `PCClientWails/` — Go 1.25 + Wails + React/TypeScript. Better structured: an allow-listed command
  executor, Windows Credential Manager integration, a tested backoff policy, a WebSocket client.

`PCClientWails` has a specific problem. `go.mod` requires **both** `wails/v2 v2.12.0` and
`wails/v3 v3.0.0-alpha.90`, while `app/app.go` imports the v2 runtime (S2-10). Four separate
branches — `copilot/upgrade-wails-v3`, `copilot/upgrade-wails-app-to-v3`,
`jules-6699572618790866906-ccbd497f`, `wails-v3-upgrade-and-reminder-…` — have each attempted the v3
upgrade independently and none has merged (S3-03). The feature being chased across all four is a
transparent fullscreen reminder window.

## Decision

**Promote `PCClientWails` (Go + Wails + React) to be the desktop agent. Pin Wails to v2.12 stable
and remove the v3 dependency. Treat the v3 upgrade as a separate project gated on v3 reaching GA.**

Consolidate the four branches into one `feat/desktop-agent` line. Implement the transparent
fullscreen reminder on v2 with a borderless, always-on-top, layered window.

`PCClient/` receives exactly one further release: a build that checks
`GET /v2/meta/discovery` and shows a blocking "install the new client" prompt.

## Options considered

### Which client to carry forward

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Go + Wails** (chosen) | Already substantially built; small single binary; excellent Windows API access; the command allow-list, Credential Manager integration and backoff policy already exist and are correct; cross-platform later | Wails ecosystem is smaller; the v3 situation needs resolving | **Chosen** |
| Keep and modernise VB.NET | No rewrite | .NET Framework 4.7.2 is a dead end; WinForms limits the fullscreen reminder; the newer client is already further along on the things that matter | Rejected |
| Rewrite in .NET 8 + WinUI 3 | First-class Windows; strong tooling | A full rewrite discarding working Go; Windows-only | Rejected |
| Tauri 2 (Rust) | Mature, active, excellent docs | Discards all existing Go; a new language for the maintainer | Rejected |
| Electron | Familiar web stack | ~150 MB for a tray app that mostly waits on a socket | Rejected |

### Wails v2 vs v3

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Pin v2.12 stable** (chosen) | Stable API; ships today; the migration's critical path is the security work, not the window manager | No v3 features; a future upgrade is deferred, not avoided | **Chosen** |
| Adopt v3-alpha now | Multi-window, better tray, a cleaner API | **Alpha.** Four attempts have already failed to land. An API that moves under the project during a security migration is an unacceptable schedule risk | Rejected |
| Keep both (status quo) | — | Two incompatible runtimes compiled into one binary. Not a strategy | Rejected |

## Consequences

**Positive**
- One desktop line of work instead of five. The four stalled branches stop consuming effort.
- The dependency graph becomes coherent; `go.mod` stops requiring two runtimes.
- The security migration is not blocked on a moving UI framework.
- The pieces already worth keeping — `commands/executor.go`, `realtime/policy.go`, `auth/auth.go` —
  carry forward unchanged.

**Negative**
- The fullscreen transparent reminder needs a v2 implementation rather than a v3 feature. Achievable
  with a borderless always-on-top window and per-pixel alpha, but it is real work rather than a
  framework gift.
- The v3 upgrade still has to happen eventually, and deferring it means doing it against a larger
  codebase. Accepted deliberately: doing it *now*, mid-security-migration, is worse.
- Wails v2 will eventually stop receiving fixes. The trigger for revisiting is below.

**Neutral**
- The React/TypeScript frontend is unaffected by the version choice; only the Go runtime bindings
  differ.

## Revisit when

- Wails v3 reaches a stable GA release **and** the migration has passed Phase 5. At that point the
  upgrade is a contained, well-tested project rather than a moving target.
- Wails v2 stops receiving security fixes, which would make the upgrade urgent rather than optional.
- macOS or Linux support becomes a product requirement, which would justify re-examining Tauri.
