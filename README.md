# PCConnect v2

PCConnect v2 securely sends a fixed set of remote state commands to enrolled
computers and delivers encrypted reminders across a user's devices. This
repository contains the replacement .NET 10 service, PostgreSQL 18 schema,
Windows service/WPF companion, Kotlin/Compose Android app, 60-day compatibility
adapter, migration tooling, deployment topology, and release gates.

The architecture pack in [`docs/architecture`](docs/architecture/README.md),
the public files in [`contracts`](contracts), and
[`DB/v2-canonical-schema.sql`](DB/v2-canonical-schema.sql) are authoritative.
The old PHP, Java Android, and VB.NET trees are migration references only; they
are not part of a v2 release.

## Repository map

- `src/PCConnect.Api` — authenticated REST API and SignalR hubs.
- `src/PCConnect.Worker` — command expiry, reminders, outbox, email, retention,
  export, and deletion jobs.
- `src/PCConnect.Infrastructure` — PostgreSQL persistence, Argon2id, WebAuthn,
  opaque session rotation, encryption, and compatibility services.
- `src/PCConnect.Windows.Agent` and `src/PCConnect.Windows.Companion` — Windows
  Service plus least-privilege interactive WPF process.
- `AndroidV2` — Kotlin/Jetpack Compose client with Room, Credential Manager,
  Android Keystore, WorkManager, SignalR hints, and REST recovery.
- `tools/PCConnect.Migration` — dry-run/full/delta strangler migration importer.
- `deploy` — digest-pinned Compose topology, staging gates, telemetry, encrypted
  backup/WAL archive, and isolated restore rehearsal.
- `tests` — unit, PostgreSQL integration, contract, and Windows IPC/executor
  verification.

## Build and test

The repository pins .NET SDK `10.0.101`, NuGet lock files, Gradle `8.11.1`, and
its wrapper checksum.

For prerequisites, a local backend walkthrough, every client and tool, release
artifacts, containers, tests, and staging deployment, see
[`docs/RUNNING_AND_BUILDING.md`](docs/RUNNING_AND_BUILDING.md).

```text
dotnet restore PCConnect.slnx --locked-mode
dotnet build PCConnect.slnx -c Release --no-restore
dotnet test tests/PCConnect.UnitTests/PCConnect.UnitTests.csproj -c Release --no-build --no-restore
dotnet test tests/PCConnect.IntegrationTests/PCConnect.IntegrationTests.csproj -c Release --no-build --no-restore
dotnet test tests/PCConnect.WindowsTests/PCConnect.WindowsTests.csproj -c Release --no-build --no-restore
python contracts/check_contracts.py
AndroidV2/gradlew -p AndroidV2 testDebugUnitTest lintDebug assembleDebug --no-daemon
```

Set `PCCONNECT_TESTCONTAINERS=1` to execute the PostgreSQL 18 integration suite;
it otherwise performs only environment-independent migration quarantine tests.
No integration test executes real destructive operating-system commands.

## Configuration and secrets

No runtime or signing secret belongs in source, an image, a log, or an artifact.
The API and worker read independent Docker secret files through .NET key-per-file
configuration. Copy `deploy/config.example.env` outside the repository, replace
every placeholder, and follow [`deploy/README.md`](deploy/README.md). Android and
Windows signing credentials are accepted only by the protected release workflow.

## Migration and release safety

The migration is a 60-day strangler transition, not an in-place overwrite. Run
two sanitized dry-run rehearsals, an isolated staging full/delta/rerun exercise,
and the acceptance matrix before requesting cutover. Day 45 disables legacy
controller command creation and day 60 rejects all legacy credentials/routes.

Production database changes, credential rotation, deployment, signing-key use,
and destructive command tests remain operator-controlled. The included
production deployment entry point intentionally refuses to proceed without an
explicit approval token and change ticket. Staging verification and a tested
rollback window are mandatory first.
