# Running and building PCConnect v2

This guide covers every active part of PCConnect v2: the .NET API and worker,
database migrations, Windows agent and companion, Android app, migration tool,
containers, installer, tests, and the staging deployment. Run commands from the
repository root unless a section says otherwise.

The `api/` PHP tree and the `App/` Java Android tree are migration references.
They are retired, are not built, and must not be deployed. The active Android
application is `AndroidV2/`.

## What you need

| Component | Required tools | Supported host |
| --- | --- | --- |
| .NET services and tools | .NET SDK 10.0.101 (selected by `global.json`) | Windows, Linux, or macOS |
| Local PostgreSQL integration tests | Docker with a running Linux-container engine | Windows, Linux, or macOS |
| Windows applications and MSI | Windows, .NET SDK 10.0.101; WiX is restored by the installer project | Windows x64 |
| Android | JDK 17, Android SDK platform 36, Android build-tools 35.0.0 | Windows, Linux, or macOS |
| Contract/release checks | Python 3.13 | Windows, Linux, or macOS |
| Staging deployment | Linux, Docker Compose v2, `curl`, and `openssl` | Linux server |
| Backup/restore scripts | The tools listed in `deploy/README.md`, including `age` and `rclone` | Linux server |
| Load tests | k6 and an approved staging environment | Any k6-supported host |

Check the main tools:

```powershell
dotnet --version
python --version
docker version
docker compose version
java -version
```

The .NET version should resolve to `10.0.101`, and Java should report version
17. Install Android Studio if you want the Android SDK, emulator, and IDE in one
package.

## Build everything used in normal development

Restore is locked to the committed NuGet lock files:

```powershell
dotnet restore PCConnect.slnx --locked-mode
dotnet build PCConnect.slnx -c Release --no-restore
python contracts/check_contracts.py
python tools/check_android_security.py
python tools/check_release_hygiene.py
```

Build and check the Android debug application separately:

```powershell
./AndroidV2/gradlew.bat -p AndroidV2 testDebugUnitTest lintDebug assembleDebug --no-daemon
```

On Linux or macOS, replace `./AndroidV2/gradlew.bat` with
`./AndroidV2/gradlew`. The debug APK is written to
`AndroidV2/app/build/outputs/apk/debug/app-debug.apk`.

## Run the backend locally

The API and worker require PostgreSQL plus six independent 32-byte application
keys. Valkey is optional in code, but run it locally to exercise SignalR fanout
and worker outbox publishing. The following is a disposable development setup;
do not reuse its password or generated keys outside your machine.

### Quick start on Windows

Start Docker Desktop and wait until its engine is ready, then run:

```powershell
./tools/local-dev/Start-PCConnectLocal.ps1
```

The launcher creates persistent development-only keys under the ignored
`artifacts/local-dev/` directory, starts PostgreSQL and Valkey, applies EF Core
migrations, starts the API and worker, waits for readiness, and writes their
logs and process IDs under the same directory. Stop the stack without deleting
its database using:

```powershell
./tools/local-dev/Stop-PCConnectLocal.ps1
```

The manual procedure below explains and reproduces what the launcher does.

### 1. Start PostgreSQL and Valkey

```powershell
docker run --name pcconnect-dev-postgres -e POSTGRES_DB=pcconnect -e POSTGRES_USER=pcconnect -e POSTGRES_PASSWORD=pcconnect_dev -p 5432:5432 -d postgres:18
docker run --name pcconnect-dev-valkey -p 6379:6379 -d valkey/valkey:latest valkey-server --save "" --appendonly no
```

On later runs, restart the existing containers instead:

```powershell
docker start pcconnect-dev-postgres pcconnect-dev-valkey
```

Wait until PostgreSQL is ready:

```powershell
docker exec pcconnect-dev-postgres pg_isready -U pcconnect -d pcconnect
```

### 2. Set local configuration in each backend terminal

.NET maps double underscores in environment-variable names to configuration
colons. Run this block once in PowerShell and keep that terminal open: these are
process-local values.

```powershell
$env:ConnectionStrings__Postgres = 'Host=localhost;Port=5432;Database=pcconnect;Username=pcconnect;Password=pcconnect_dev'
$env:Realtime__ValkeyConnection = 'localhost:6379,abortConnect=false'
$env:Security__TokenHashingKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:Security__LegacyCredentialHashingKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:Security__ActiveReminderKeyId = 'v1'
$env:Security__ReminderWrappingKeys__v1 = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:Security__ActiveEmailKeyId = 'v1'
$env:Security__EmailEncryptionKeys__v1 = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:Security__DeletionTombstoneKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:Security__ExportEncryptionKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:Security__WebAuthnRpId = 'localhost'
$env:Security__WebAuthnOrigins__0 = 'http://localhost:5080'
$env:Http__AllowedOrigins__0 = 'http://localhost:5080'
$env:Exports__Directory = "$PWD/artifacts/dev/exports"
```

Launch the migrator, API, and worker from this shell or from child terminals it
opens so they inherit the same values. If you use unrelated terminals, copy the
generated values across instead of rerunning the block. If the API and worker
use different keys, tokens, reminders, email addresses, exports, and deletion
records will not interoperate. For repeat development, store these values in a
local secret manager or an untracked environment file, never in
`appsettings.json` or Git.

The OpenTelemetry exporters default to a collector on `localhost:4317`. A
missing local collector can produce exporter warnings but does not replace the
required PostgreSQL configuration.

### 3. Create or update the database

First inspect pending EF Core migrations, then explicitly apply them:

```powershell
dotnet run --project tools/PCConnect.DatabaseMigrator --configuration Release
dotnet run --project tools/PCConnect.DatabaseMigrator --configuration Release -- --apply
```

The first command is deliberately a dry run. `DB/v2-canonical-schema.sql` is an
architecture reference; do not execute it directly instead of the migrator.

### 4. Run the API

In a configured terminal:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://localhost:5080'
dotnet run --project src/PCConnect.Api --configuration Release --no-build
```

Check it from another terminal:

```powershell
Invoke-RestMethod http://localhost:5080/api/v2/health/live
Invoke-RestMethod http://localhost:5080/api/v2/health/ready
Invoke-RestMethod http://localhost:5080/api/v2/version
```

`live` proves that the process is serving requests; `ready` also proves that
PostgreSQL is reachable and the canonical migration has been applied.

### 5. Run the worker

Open a child terminal that inherited the backend configuration (or copy the
same values into another terminal), and run:

```powershell
dotnet run --project src/PCConnect.Worker --configuration Release --no-build
```

The worker owns command expiry, reminders, presence, outbox delivery, email,
account deletion, exports, retention, and operational metrics. SMTP settings in
`src/PCConnect.Worker/appsettings.json` are blank by default; configure
`Email__Smtp__Host`, `Email__Smtp__Username`, `Email__Smtp__Password`, and
`Email__Smtp__FromAddress` only when testing actual email delivery.

To remove the disposable database and cache later, stop the processes and run:

```powershell
docker rm -f pcconnect-dev-postgres pcconnect-dev-valkey
```

That final command permanently deletes the data in those two named development
containers.

## Run the Windows applications

The Windows agent, companion, and Windows tests require Windows. The agent can
execute the product's fixed set of operating-system state commands, so use a
test Windows machine or VM when exercising commands end to end.

### Development run

With the local API running, run the agent and companion in separate interactive
terminals. The credential-path override keeps local enrollment separate from an
installed production agent:

```powershell
$env:PCConnect__ApiBaseUrl = 'http://localhost:5080'
$env:PCConnect__CredentialPath = "$env:LOCALAPPDATA/PCConnect/dev-device.dat"
$env:PCConnect__PipeName = 'pcconnect-agent-dev'
dotnet run --project src/PCConnect.Windows.Agent --configuration Release --no-build
```

```powershell
$env:PCConnect__PipeName = 'pcconnect-agent-dev'
dotnet run --project src/PCConnect.Windows.Companion --configuration Release --no-build
```

The companion opens a graphical sign-in screen. Log in with a PCConnect account;
it creates, approves, and exchanges the device enrollment automatically, then
passes only the resulting device credential to the agent over the authenticated
local named-pipe protocol. The password is never stored. The installed agent
normally runs as a Windows Service; the console run is the easier development
loop.

If setup was interrupted after the device credential was protected but before
the Windows identity was authorized, reopening both applications displays a
graphical **Finish connecting this PC** sign-in screen. Completing that screen
recovers the existing device instead of creating a duplicate enrollment.

The development pipe-name override prevents the local Agent from colliding with
an installed PCConnect Windows Service. Both processes must use the same value.

After enrollment, the installed companion starts quietly at Windows sign-in.
Use the PCConnect Start menu shortcut to view its connection status; reminders
can still bring the window forward when they are delivered.

### Publish the Windows binaries

```powershell
dotnet publish src/PCConnect.Windows.Agent/PCConnect.Windows.Agent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:RestoreLockedMode=true -o artifacts/windows/agent
dotnet publish src/PCConnect.Windows.Companion/PCConnect.Windows.Companion.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:RestoreLockedMode=true -o artifacts/windows/companion
```

Outputs are under `artifacts/windows/agent/` and
`artifacts/windows/companion/`.

### Build the MSI

Publish both applications first, then build the WiX project:

```powershell
dotnet build installer/PCConnect.Windows.Setup/PCConnect.Windows.Setup.wixproj -c Release -p:RestoreLockedMode=true -p:AgentPublishDir="$PWD/artifacts/windows/agent" -p:CompanionPublishDir="$PWD/artifacts/windows/companion"
```

The MSI is produced under
`installer/PCConnect.Windows.Setup/bin/x64/Release/`. Local executables and MSI
packages are unsigned test artifacts. Only the protected release workflow may
sign and promote them; see `installer/README.md`.

## Run and package Android

Open `AndroidV2/` (not `App/`) in Android Studio, select the `app` run
configuration, and use an API 24-or-later emulator or device. The command-line
equivalent is:

```powershell
./AndroidV2/gradlew.bat -p AndroidV2 assembleDebug --no-daemon
adb install -r AndroidV2/app/build/outputs/apk/debug/app-debug.apk
```

The build defaults to the production API. To build against staging, set an
HTTPS endpoint ending exactly in `/api/v2/` and its matching WebAuthn relying
party host before building:

```powershell
$env:PCCONNECT_ANDROID_API_BASE_URL = 'https://staging-api.example.com/api/v2/'
$env:PCCONNECT_ANDROID_RP_HOST = 'staging.example.com'
./AndroidV2/gradlew.bat -p AndroidV2 clean assembleDebug --no-daemon
```

For a fully local debug session, forward the Android device's loopback port to
the local API and build the debug-only loopback URL into the APK:

```powershell
adb reverse tcp:5080 tcp:5080
$env:PCCONNECT_ANDROID_DEBUG_API_BASE_URL = 'http://localhost:5080/api/v2/'
./AndroidV2/gradlew.bat -p AndroidV2 clean assembleDebug --no-daemon
adb install -r AndroidV2/app/build/outputs/apk/debug/app-debug.apk
```

The main/release network policy still rejects all cleartext traffic. The debug
resource permits cleartext only for `localhost` and `127.0.0.1`; arbitrary HTTP
hosts are rejected at build time. `adb reverse` must remain active while the
app uses this local endpoint. Passkeys and verified App Links still require a
proper HTTPS RP host, but password login, enrollment, device control, recovery,
and reminders can be exercised locally.

Run JVM tests and lint:

```powershell
./AndroidV2/gradlew.bat -p AndroidV2 testDebugUnitTest lintDebug --no-daemon
```

Run device/emulator instrumentation tests while a device is visible to `adb`:

```powershell
adb devices
./AndroidV2/gradlew.bat -p AndroidV2 connectedDebugAndroidTest --no-daemon
```

For a signed release AAB, all four signing variables are mandatory:

```powershell
$env:PCCONNECT_ANDROID_KEYSTORE = 'C:\secure\pcconnect-upload.jks'
$env:PCCONNECT_ANDROID_STORE_PASSWORD = '<from-secret-store>'
$env:PCCONNECT_ANDROID_KEY_ALIAS = '<upload-key-alias>'
$env:PCCONNECT_ANDROID_KEY_PASSWORD = '<from-secret-store>'
$env:PCCONNECT_ANDROID_API_BASE_URL = 'https://api.example.com/api/v2/'
$env:PCCONNECT_ANDROID_RP_HOST = 'example.com'
./AndroidV2/gradlew.bat -p AndroidV2 testDebugUnitTest lintRelease bundleRelease --no-daemon
jarsigner -verify AndroidV2/app/build/outputs/bundle/release/app-release.aab
```

The AAB is written to
`AndroidV2/app/build/outputs/bundle/release/app-release.aab`. Signing identity,
Digital Asset Links, and the WebAuthn Android origin must agree before rollout;
the protected release workflow performs those checks.

## Build the Linux container images

Each Dockerfile uses the repository root as its build context:

```powershell
docker build -f src/PCConnect.Api/Dockerfile -t pcconnect-api:dev .
docker build -f src/PCConnect.Worker/Dockerfile -t pcconnect-worker:dev .
docker build -f tools/PCConnect.DatabaseMigrator/Dockerfile -t pcconnect-migrator:dev .
```

Release builds must override the Dockerfile base-image arguments with approved
digest-pinned .NET images, then publish the resulting application images by
digest. The protected `.github/workflows/release.yml` workflow is the reference
implementation.

## Run the legacy-data migration tool

The importer consumes a protected JSON snapshot and writes a reconciliation
manifest. It never connects to the legacy source. Even a dry run needs two
independent Base64 32-byte checksum/credential keys:

```powershell
$env:PCCONNECT_MIGRATION_CHECKSUM_KEY = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:PCCONNECT_LEGACY_CREDENTIAL_HASHING_KEY = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet run --project tools/PCConnect.Migration --configuration Release -- --snapshot C:\secure\rehearsal.legacy-snapshot.json --manifest artifacts\migration-manifest-rehearsal.json --mode dry_run --source-system hosted-v1 --compat-sunset 2026-11-01T00:00:00Z
```

`full` and `delta` additionally require:

```powershell
$env:PCCONNECT_MIGRATION_TARGET = '<target Npgsql connection string>'
$env:PCCONNECT_MIGRATION_REMINDER_KEY_ID = 'v1'
$env:PCCONNECT_MIGRATION_REMINDER_KEY = '<Base64 32-byte target reminder key>'
```

Do not move to `full` or `delta` without the rehearsal and approval gates in
`tools/PCConnect.Migration/README.md`. Snapshots contain sensitive data and must
remain outside Git.

## Run all automated checks

After the locked restore and Release build:

```powershell
dotnet test tests/PCConnect.UnitTests/PCConnect.UnitTests.csproj -c Release --no-build --no-restore --collect:"XPlat Code Coverage"
dotnet test tests/PCConnect.IntegrationTests/PCConnect.IntegrationTests.csproj -c Release --no-build --no-restore
dotnet test tests/PCConnect.WindowsTests/PCConnect.WindowsTests.csproj -c Release --no-build --no-restore
python contracts/check_contracts.py
python tools/check_android_security.py
python tools/check_release_hygiene.py
./AndroidV2/gradlew.bat -p AndroidV2 testDebugUnitTest lintDebug assembleDebug --no-daemon
```

By default, the integration project runs only environment-independent migration
quarantine tests. Enable its PostgreSQL 18 Testcontainers suite when Docker is
running:

```powershell
$env:PCCONNECT_TESTCONTAINERS = '1'
dotnet test tests/PCConnect.IntegrationTests/PCConnect.IntegrationTests.csproj -c Release --no-build --no-restore
```

The Windows test project must run on Windows. Its executor verification does
not invoke real destructive operating-system actions.

Load tests are staging-only and fail closed without both
`PCCONNECT_ENVIRONMENT=staging` and
`PCCONNECT_LOAD_APPROVED=STAGING_ONLY`. See `tests/load/README.md` for the
synthetic credentials and commands required by each k6 scenario.

## Deploy the complete staging topology

`deploy/compose.yaml` is a hardened staging/production topology, not a quick
local-development Compose file. It requires digest-pinned images, file-backed
secrets, Linux ownership and permissions, HTTPS hostnames, persistent state
directories, Caddy, PostgreSQL, Valkey, OpenTelemetry, Prometheus, Pushgateway,
and node-exporter.

On the target Linux host:

1. Copy `deploy/config.example.env` outside the repository and replace every
   placeholder.
2. Create the secret and state files with the exact modes and numeric owners in
   `deploy/README.md`.
3. Verify the host and Compose configuration.
4. Run the staging deployment, which performs a migration dry run, applies the
   migration, brings up the inactive API slot, switches Caddy, starts the
   worker, and runs the fail-closed smoke suite.

```sh
./deploy/preflight.sh /root/pcconnect-staging.env
./deploy/deploy-staging.sh /root/pcconnect-staging.env
```

For a manual smoke rerun:

```sh
set -a
. /root/pcconnect-staging.env
set +a
./deploy/verify-staging.sh "https://$PCCONNECT_API_HOST"
```

Production deployment, slot switching, recurring backups, and restore
rehearsals are operator-controlled procedures with additional approval gates.
Follow `deploy/README.md`; do not adapt the local disposable-container commands
for production.

## Artifact map

| Artifact | Location |
| --- | --- |
| .NET build outputs | Each project's `bin/Release/` directory |
| Windows agent publish | `artifacts/windows/agent/` |
| Windows companion publish | `artifacts/windows/companion/` |
| Windows MSI | `installer/PCConnect.Windows.Setup/bin/x64/Release/` |
| Android debug APK | `AndroidV2/app/build/outputs/apk/debug/app-debug.apk` |
| Android release AAB | `AndroidV2/app/build/outputs/bundle/release/app-release.aab` |
| Android lint report | `AndroidV2/app/build/reports/lint-results-debug.html` |
| Test results | Each test project's `TestResults/` directory |

## Common failures

- **The requested .NET SDK was not found:** install SDK 10.0.101 and confirm
  `dotnet --version` from the repository root.
- **`NU1004` or lock-file mismatch:** package declarations changed without an
  intentional lock-file update. Normal builds should keep `--locked-mode`.
- **NuGet vulnerability data cannot be loaded:** restore needs access to
  `https://api.nuget.org`; this repository treats restore warnings as errors.
- **API says a security key must be Base64 or 32 bytes:** rerun the key-generation
  block and make sure the API and worker received the same values.
- **Readiness returns 503:** PostgreSQL is unavailable or the EF migration was
  not applied. Run the database migrator dry run, then `--apply`.
- **Android says `JAVA_HOME` is missing:** install/select JDK 17 in Android
  Studio or set `JAVA_HOME` to that JDK.
- **Android rejects the API URL:** it must use HTTPS and end in `/api/v2/`.
  A debug build may instead use loopback HTTP through
  `PCCONNECT_ANDROID_DEBUG_API_BASE_URL` and `adb reverse` as described above.
- **MSI build says a publish input is missing:** publish both Windows
  applications to the expected artifact directories before building WiX.
- **Compose preflight refuses the environment:** this is intentional. Correct
  the image digests, secrets, ownership, state directories, hosts, or Android
  signing metadata reported by `preflight.sh`.
