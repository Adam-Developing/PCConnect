[CmdletBinding()]
param(
    [ValidateRange(1, 65535)]
    [int]$PostgresPort = 15432,
    [switch]$DependenciesOnly
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot

function Assert-Command {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "'$Name' was not found. Install it and make sure it is available in PATH."
    }
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Ensure-Container {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$CreateArguments
    )

    & docker container inspect $Name *> $null
    if ($LASTEXITCODE -eq 0) {
        $isRunning = (& docker inspect --format "{{.State.Running}}" $Name 2>$null).Trim()
        if ($isRunning -ne "true") {
            Write-Host "Starting existing container $Name..." -ForegroundColor Cyan
            Invoke-Checked "docker" @("start", $Name) "Could not start $Name."
        }
        else {
            Write-Host "$Name is already running." -ForegroundColor DarkGray
        }
        return
    }

    Write-Host "Creating container $Name..." -ForegroundColor Cyan
    Invoke-Checked "docker" $CreateArguments "Could not create $Name."
}

function Assert-AvailablePort {
    param([int]$Port)

    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    try {
        $listener.Start()
    }
    catch {
        throw "Local port $Port is in use or reserved by Windows. Run this script with -PostgresPort <available-port>. To see Windows reservations: netsh interface ipv4 show excludedportrange protocol=tcp"
    }
    finally {
        $listener.Stop()
    }
}

function Ensure-PostgresContainer {
    $name = "pcconnect-dev-pg"
    $existingJson = & docker container inspect $name 2>$null
    if ($LASTEXITCODE -ne 0) {
        Assert-AvailablePort $PostgresPort
        Ensure-Container $name @(
            "run", "-d", "--name", $name,
            "-e", "POSTGRES_PASSWORD=postgres",
            "-p", "127.0.0.1:${PostgresPort}:5432",
            "postgres:18-alpine"
        )
        return
    }

    $existing = @($existingJson | ConvertFrom-Json)[0]
    $bindings = @($existing.HostConfig.PortBindings.'5432/tcp')
    if ($bindings.Count -eq 1 -and $bindings[0].HostPort -eq "$PostgresPort") {
        if (-not $existing.State.Running) {
            Assert-AvailablePort $PostgresPort
        }
        Ensure-Container $name @("start", $name)
        return
    }

    if ($existing.State.Running) {
        throw "$name is running on a different port. Use -PostgresPort $($bindings[0].HostPort) to keep using it, or stop that development container before changing its port."
    }

    # Docker cannot edit published ports. Keep the stopped original and attach
    # its data volumes to the replacement; never delete or initialise its data.
    $dataSetting = @($existing.Config.Env | Where-Object { $_ -like 'PGDATA=*' })
    if ($dataSetting.Count -ne 1) {
        throw "Cannot safely locate $name's PGDATA directory. The original container has been left intact."
    }
    $dataPath = $dataSetting[0].Substring(7).TrimEnd('/')
    $dataMount = @($existing.Mounts | Where-Object {
        $_.RW -and $_.Type -in @('volume', 'bind') -and
        ($dataPath -eq $_.Destination.TrimEnd('/') -or
         $dataPath.StartsWith($_.Destination.TrimEnd('/') + '/'))
    })
    if ($dataMount.Count -eq 0) {
        throw "$name's database is not in a reusable data mount. The original container has been left intact."
    }

    Assert-AvailablePort $PostgresPort
    $backupName = "$name-before-port-change-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    Write-Host "Moving PostgreSQL to localhost:$PostgresPort; keeping $backupName and its data volume..." -ForegroundColor Cyan
    Invoke-Checked "docker" @("rename", $name, $backupName) "Could not preserve the original PostgreSQL container."

    try {
        $createArguments = @(
            "create", "--name", $name,
            "--volumes-from", $backupName,
            "-p", "127.0.0.1:${PostgresPort}:5432"
        )
        foreach ($setting in $existing.Config.Env) {
            $createArguments += @("--env", $setting)
        }
        if ($existing.Config.User) {
            $createArguments += @("--user", $existing.Config.User)
        }
        if ($existing.Config.WorkingDir) {
            $createArguments += @("--workdir", $existing.Config.WorkingDir)
        }
        $entrypoint = @($existing.Config.Entrypoint)
        if ($entrypoint.Count -gt 0) {
            $createArguments += @("--entrypoint", $entrypoint[0])
        }
        # Use the exact installed image, including any custom entrypoint/command.
        $createArguments += $existing.Image
        if ($entrypoint.Count -gt 1) {
            $createArguments += $entrypoint[1..($entrypoint.Count - 1)]
        }
        $createArguments += @($existing.Config.Cmd)
        Invoke-Checked "docker" $createArguments "Could not recreate PostgreSQL on port $PostgresPort."
        Invoke-Checked "docker" @("start", $name) "Could not start PostgreSQL on port $PostgresPort."
    }
    catch {
        $failure = $_
        & docker container inspect $name *> $null
        if ($LASTEXITCODE -eq 0) {
            Invoke-Checked "docker" @("rename", $name, "$backupName-failed") "The original is preserved as $backupName; restoring its name failed."
        }
        Invoke-Checked "docker" @("rename", $backupName, $name) "The original is preserved as $backupName; restoring its name failed."
        throw $failure
    }
}

Assert-Command "dotnet"
Assert-Command "docker"

& docker info *> $null
if ($LASTEXITCODE -ne 0) {
    throw "Docker is not running. Open Docker Desktop, wait for it to finish starting, then run this file again."
}

Set-Location $repoRoot

Ensure-PostgresContainer

Ensure-Container "pcconnect-dev-valkey" @(
    "run", "-d", "--name", "pcconnect-dev-valkey",
    "-p", "56379:6379",
    "valkey/valkey:8-alpine"
)

Write-Host "Waiting for PostgreSQL..." -ForegroundColor Cyan
$postgresReady = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    & docker exec pcconnect-dev-pg pg_isready -U postgres -d pcconnect *> $null
    if ($LASTEXITCODE -eq 0) {
        $postgresReady = $true
        break
    }
    Start-Sleep -Seconds 2
}
if (-not $postgresReady) {
    throw "PostgreSQL did not become ready within 60 seconds. Check: docker logs pcconnect-dev-pg"
}

if ($DependenciesOnly) {
    Write-Host "Dependencies are ready. PostgreSQL: localhost:$PostgresPort; Valkey: localhost:56379" -ForegroundColor Green
    return
}

Write-Host "Building backend projects..." -ForegroundColor Cyan
Invoke-Checked "dotnet" @("build", "src\PCConnect.Api\PCConnect.Api.csproj", "--nologo") "The API build failed."
Invoke-Checked "dotnet" @("build", "src\PCConnect.Worker\PCConnect.Worker.csproj", "--nologo") "The worker build failed."

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:DOTNET_ENVIRONMENT = "Development"
$env:PCCONNECT_DATABASE__CONNECTIONSTRING = "Host=localhost;Port=$PostgresPort;Database=pcconnect;Username=postgres;Password=postgres"
$env:PCCONNECT_CACHE__CONNECTIONSTRING = "localhost:56379"

$worker = $null
try {
    Write-Host "Starting the background worker..." -ForegroundColor Cyan
    $worker = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList @("bin\Debug\net10.0\PCConnect.Worker.dll") `
        -WorkingDirectory (Join-Path $repoRoot "src\PCConnect.Worker") `
        -WindowStyle Hidden `
        -PassThru

    Write-Host ""
    Write-Host "Backend is starting at http://localhost:5080" -ForegroundColor Green
    Write-Host "Discovery: http://localhost:5080/v2/meta/discovery"
    Write-Host "Press Ctrl+C to stop the API and worker. PostgreSQL and Valkey will remain running."
    Write-Host ""

    Push-Location (Join-Path $repoRoot "src\PCConnect.Api")
    try {
        & dotnet "bin\Debug\net10.0\PCConnect.Api.dll" --urls "http://localhost:5080"
    }
    finally {
        Pop-Location
    }
}
finally {
    if ($null -ne $worker -and -not $worker.HasExited) {
        Write-Host "Stopping the background worker..." -ForegroundColor DarkGray
        Stop-Process -Id $worker.Id -Force -ErrorAction SilentlyContinue
        $worker.WaitForExit(5000) | Out-Null
    }
}
