[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$stateRoot = Join-Path $repositoryRoot 'artifacts\local-dev'
$configurationPath = Join-Path $stateRoot 'runtime.json'
$processPath = Join-Path $stateRoot 'processes.json'
$logRoot = Join-Path $stateRoot 'logs'

New-Item -ItemType Directory -Force -Path $stateRoot, $logRoot | Out-Null

function New-PCConnectRandomBytes {
    param([Parameter(Mandatory = $true)][int]$Count)

    $bytes = New-Object byte[] $Count
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) }
    finally { $generator.Dispose() }
    return $bytes
}

try {
    docker info *> $null
}
catch {
    throw 'Docker Desktop is not running. Start Docker Desktop, wait for the engine to become ready, then rerun this script.'
}
if ($LASTEXITCODE -ne 0) {
    throw 'Docker Desktop is not running. Start Docker Desktop, wait for the engine to become ready, then rerun this script.'
}

$containers = @(docker ps -a --format '{{.Names}}')
if (-not (Test-Path -LiteralPath $configurationPath) -and $containers -contains 'pcconnect-dev-postgres') {
    throw "The pcconnect-dev-postgres container already exists, but $configurationPath is missing. Remove that development container and its data volume, or restore the matching runtime.json before continuing."
}
if (-not (Test-Path -LiteralPath $configurationPath)) {
    $key = { [Convert]::ToBase64String((New-PCConnectRandomBytes -Count 32)) }
    [ordered]@{
        PostgresPassword = ([BitConverter]::ToString((New-PCConnectRandomBytes -Count 18))).Replace('-', '').ToLowerInvariant()
        TokenHashingKey = & $key
        LegacyCredentialHashingKey = & $key
        ReminderWrappingKey = & $key
        EmailEncryptionKey = & $key
        DeletionTombstoneKey = & $key
        ExportEncryptionKey = & $key
    } | ConvertTo-Json | Set-Content -LiteralPath $configurationPath -Encoding UTF8
}
$configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json

if ($containers -notcontains 'pcconnect-dev-postgres') {
    docker run --name pcconnect-dev-postgres `
        -e POSTGRES_DB=pcconnect `
        -e POSTGRES_USER=pcconnect `
        -e "POSTGRES_PASSWORD=$($configuration.PostgresPassword)" `
        -p 127.0.0.1:5432:5432 `
        -v pcconnect-dev-postgres-data:/var/lib/postgresql `
        -d postgres:18 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the local PostgreSQL container.' }
}
else {
    docker start pcconnect-dev-postgres | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the local PostgreSQL container.' }
}

if ($containers -notcontains 'pcconnect-dev-valkey') {
    docker run --name pcconnect-dev-valkey `
        -p 127.0.0.1:6379:6379 `
        -d valkey/valkey:latest `
        valkey-server --save '' --appendonly no | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the local Valkey container.' }
}
else {
    docker start pcconnect-dev-valkey | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the local Valkey container.' }
}

$postgresReady = $false
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    docker exec pcconnect-dev-postgres pg_isready -U pcconnect -d pcconnect *> $null
    if ($LASTEXITCODE -eq 0) {
        $postgresReady = $true
        break
    }
    Start-Sleep -Seconds 1
}
if (-not $postgresReady) { throw 'PostgreSQL did not become ready within 60 seconds.' }

$env:ConnectionStrings__Postgres = "Host=127.0.0.1;Port=5432;Database=pcconnect;Username=pcconnect;Password=$($configuration.PostgresPassword)"
$env:Realtime__ValkeyConnection = '127.0.0.1:6379,abortConnect=false'
$env:Security__TokenHashingKey = $configuration.TokenHashingKey
$env:Security__LegacyCredentialHashingKey = $configuration.LegacyCredentialHashingKey
$env:Security__ActiveReminderKeyId = 'local_v1'
$env:Security__ReminderWrappingKeys__local_v1 = $configuration.ReminderWrappingKey
$env:Security__ActiveEmailKeyId = 'local_v1'
$env:Security__EmailEncryptionKeys__local_v1 = $configuration.EmailEncryptionKey
$env:Security__DeletionTombstoneKey = $configuration.DeletionTombstoneKey
$env:Security__ExportEncryptionKey = $configuration.ExportEncryptionKey
$env:Security__WebAuthnRpId = 'localhost'
$env:Security__WebAuthnOrigins__0 = 'http://localhost:5080'
$env:Http__AllowedOrigins__0 = 'http://localhost:5080'
$env:Exports__Directory = Join-Path $stateRoot 'exports'
$env:DataProtection__KeyRingPath = Join-Path $stateRoot 'data-protection'
$env:Enrollment__VerificationUri = 'http://localhost:5080/device'
$env:Email__PublicBaseUrl = 'http://localhost:5080'
$env:DOTNET_ENVIRONMENT = 'Development'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://localhost:5080'
$env:OTEL_SDK_DISABLED = 'true'

Push-Location $repositoryRoot
try {
    dotnet run --project tools/PCConnect.DatabaseMigrator --configuration Release --no-build -- --apply
    if ($LASTEXITCODE -ne 0) { throw 'The local database migration failed.' }

    $knownProcesses = @{}
    if (Test-Path -LiteralPath $processPath) {
        $saved = Get-Content -LiteralPath $processPath -Raw | ConvertFrom-Json
        if ($saved.Api) { $knownProcesses.Api = [int]$saved.Api }
        if ($saved.Worker) { $knownProcesses.Worker = [int]$saved.Worker }
    }

    foreach ($knownProcessId in @($knownProcesses.Api, $knownProcesses.Worker)) {
        if ($knownProcessId) {
            Stop-Process -Id $knownProcessId -ErrorAction SilentlyContinue
        }
    }
    Start-Sleep -Milliseconds 500

    $apiProcess = Start-Process dotnet -WorkingDirectory $repositoryRoot -ArgumentList @(
        'run', '--project', 'src/PCConnect.Api', '--configuration', 'Release', '--no-build'
    ) -RedirectStandardOutput (Join-Path $logRoot 'api.stdout.log') `
      -RedirectStandardError (Join-Path $logRoot 'api.stderr.log') -PassThru

    $workerProcess = Start-Process dotnet -WorkingDirectory $repositoryRoot -ArgumentList @(
        'run', '--project', 'src/PCConnect.Worker', '--configuration', 'Release', '--no-build'
    ) -RedirectStandardOutput (Join-Path $logRoot 'worker.stdout.log') `
      -RedirectStandardError (Join-Path $logRoot 'worker.stderr.log') -PassThru

    [ordered]@{ Api = $apiProcess.Id; Worker = $workerProcess.Id } |
        ConvertTo-Json | Set-Content -LiteralPath $processPath -Encoding UTF8

    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $response = Invoke-RestMethod -Uri 'http://localhost:5080/api/v2/health/ready' -TimeoutSec 5
            if ($response.status -eq 'ok') {
                $ready = $true
                break
            }
        }
        catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) {
        throw "The API did not become ready. Inspect $logRoot\api.stderr.log and api.stdout.log."
    }
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host 'PCConnect local backend is ready.' -ForegroundColor Green
Write-Host 'API:       http://localhost:5080/api/v2/'
Write-Host "API logs:  $logRoot\api.stdout.log"
Write-Host "Worker:    PID $($workerProcess.Id)"
Write-Host 'Android:   run adb reverse tcp:5080 tcp:5080, then install the localhost debug APK.'
Write-Host 'Windows:   enroll the loose agent executable before starting agent and companion.'
Write-Host 'Stop:      ./tools/local-dev/Stop-PCConnectLocal.ps1'
