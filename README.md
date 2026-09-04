# PCConnect

Change your computer's state from your phone, and set reminders you will actually see — because
they appear on the screen you are sitting in front of.

Screenshots: <https://pcconnect.adamkhattab.co.uk/screenshots.html> ·
Downloads: <https://pcconnect.adamkhattab.co.uk/download.html>

---

## Version 2

PCConnect v2 is a rebuild of the whole system: one API, one database, one realtime channel, and
clients that no longer poll. The architecture, the reasoning and every decision that changed
along the way are in [`docs/architecture/`](docs/architecture/README.md); this file is how to run
it.

What is materially different from v1:

- **Passwords are Argon2id**, not client-side SHA-256 with no salt. Legacy hashes are upgraded on
  the owner's next successful sign-in, so nobody has to be told to reset anything.
- **Passkeys** (WebAuthn) as a first-class credential, not a second factor bolted on.
- **A device is something you pair, not something you name.** Claiming a PC needs a code shown on
  that PC and confirmed by the signed-in owner.
- **Commands expire.** Every command carries a TTL, an audit trail and a real outcome, so "the
  shutdown I sent this morning" cannot fire tonight and "Success" cannot mean "a row was written".
- **Shutting down, restarting, signing out and hibernating need a fresh confirmation** — a
  password or a passkey, seconds old, single use.
- **Reminders are encrypted with a key that is not the API key**, envelope-wrapped with AES-256-GCM
  under a key encryption key that is not in the database.
- **Push, not polling.** SignalR carries commands; polling is the fallback when the socket is
  unhealthy, and a client that reconnects re-reads what it missed.
- **The installed v1 clients keep working** while all of that is true, through a compatibility
  shim that speaks the old wire format and counts every request so the shutoff is a measurement
  rather than a date.

---

## Layout

```
src/     PCConnect.Api          ASP.NET Core 10 — HTTP API and the SignalR hub
         PCConnect.Worker       command expiry, reminder materialisation and delivery
         PCConnect.Core         domain types and contracts, no dependencies
         PCConnect.Infrastructure   database, crypto, realtime, jobs
         PCConnect.Client       the .NET client both Windows apps use
         PCConnect.DbMigrator   `pcconnect-migrate` — schema, verification gates, KEK rewrap
         PCConnect.LegacyMigrator  `pcconnect-import` — the v1 MySQL → v2 PostgreSQL import
clients/ windows/PCConnect.Agent      the Windows service that receives commands
         windows/PCConnect.Companion  the WPF app that shows reminders and pairs a PC
         android/                     Kotlin + Jetpack Compose
db/      migrations/ verification/ legacy/
deploy/  docker-compose.yml and everything the VPS needs
docs/    architecture/ and runbook.md
tools/   generate-openapi.sh, restore-rehearsal.sh
```

`PCClient/`, `App/` and `api/` are the v1 system. They are still here because the migration plan
([07](docs/architecture/07-migration-plan.md)) removes them at the end, not at the start.

---

## Running it locally

You need .NET 10 and Docker.

```bash
# A database and a cache to develop against.
docker run -d --name pcconnect-dev-pg -e POSTGRES_PASSWORD=postgres \
  -p 55432:5432 postgres:18-alpine
docker run -d --name pcconnect-dev-valkey -p 56379:6379 valkey/valkey:8-alpine

# Schema.
PCCONNECT_DATABASE__CONNECTIONSTRING="Host=localhost;Port=55432;Database=pcconnect;Username=postgres;Password=postgres" \
  dotnet run --project src/PCConnect.DbMigrator -- up

# The API. In Development it generates a signing key and a KEK in memory and says
# so — nothing encrypted survives a restart, which is the point.
dotnet run --project src/PCConnect.Api --urls http://localhost:5080
```

Then `http://localhost:5080/v2/meta/discovery` answers, and `/openapi/v1.json` is the contract.

### The agent

```bash
PCCONNECT_AGENT_AGENT__BASEADDRESS=http://localhost:5080 \
  dotnet run --project clients/windows/PCConnect.Agent
```

It prints a pairing code. Type that into the phone app or the companion, and the PC is linked.
The device secret goes into Windows Credential Manager; it crosses the wire exactly once.

### The Android app

```bash
cd clients/android && ./gradlew assembleDebug
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

The debug build points at `http://10.0.2.2:5080`, which is the host machine as seen from an
emulator.

---

## Tests

```bash
dotnet test tests/PCConnect.UnitTests/PCConnect.UnitTests.csproj          # 198
dotnet test tests/PCConnect.IntegrationTests/PCConnect.IntegrationTests.csproj  # 112
cd clients/android && ./gradlew test
```

The integration tests start real PostgreSQL 18 and MySQL 8.4 containers through Testcontainers —
no in-memory database stands in for the thing being tested. They cover the authorisation matrix,
the command lifecycle, refresh-token reuse detection, the legacy shim's wire format, the v1
import, and KEK rotation.

---

## Deploying

```bash
cd deploy
cp .env.example .env && ./env/generate-secrets.sh   # fills in what it can
docker compose run --rm migrate
docker compose up -d
```

Caddy terminates TLS and gets its own certificates. PostgreSQL and Valkey are on an
`internal: true` network and publish no ports at all.

Everything operational — deploying, rolling back, restoring, rotating keys, and what to do when
something is wrong — is in **[docs/runbook.md](docs/runbook.md)**.

---

## Migrating from v1

The v1 data lives in MySQL. The import is idempotent and resumable, and reads the legacy database
with read-only credentials:

```bash
dotnet run --project src/PCConnect.LegacyMigrator -- dry-run   # reads everything, writes nothing
dotnet run --project src/PCConnect.LegacyMigrator -- import
dotnet run --project src/PCConnect.LegacyMigrator -- verify
```

`verify` runs the gates in [`db/verification/checks.sql`](db/verification/checks.sql). Every one
must return zero before the next stage. The phased plan — and the point at which each old
component is switched off — is [07](docs/architecture/07-migration-plan.md).

---

## Where the decisions are

- [Architecture index](docs/architecture/README.md) — start here
- [ADRs](docs/architecture/adr/) — including the four decisions that changed during
  implementation and why
- [Implementation notes](docs/architecture/09-implementation-notes.md) — every place the built
  system and the written architecture disagreed, what was changed, and what is deliberately not
  built yet
- [Runbook](docs/runbook.md)

---

## v1 release notes

Kept because they are the record of what the installed clients do, and those clients are still
carrying traffic until the shim is switched off.

**PCClient 4.5** — bug fix for the exit and logout buttons after they moved into the settings
panel.

**PCClient 4.0** — multi-PC support, so one account can manage several machines rather than
needing an account each. The settings menu gained reminder text and background colour, for eye
strain.

**PCClient 3.0** — rewritten in VB.NET, with a control panel that shows and adds reminders
without needing the phone.

---

## Licence

GPL-3.0. See [`gpl-3.0.rtf`](gpl-3.0.rtf).
