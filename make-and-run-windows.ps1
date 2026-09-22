[CmdletBinding()]
param()

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

Assert-Command "dotnet"
Set-Location $repoRoot

Write-Host "Checking the local API..." -ForegroundColor Cyan
try {
    Invoke-RestMethod -Uri "http://localhost:5080/v2/meta/discovery" -TimeoutSec 5 | Out-Null
}
catch {
    throw "The backend is not available at http://localhost:5080. Run .\make-and-run-backend.ps1 first."
}

Write-Host "Building the Windows agent and companion..." -ForegroundColor Cyan
Invoke-Checked "dotnet" @("build", "clients\windows\PCConnect.Agent\PCConnect.Agent.csproj", "--nologo") "The Windows agent build failed."
Invoke-Checked "dotnet" @("build", "clients\windows\PCConnect.Companion\PCConnect.Companion.csproj", "--nologo") "The Windows companion build failed."

$env:PCCONNECT_AGENT_AGENT__BASEADDRESS = "http://localhost:5080"
$env:PCCONNECT_COMPANION_COMPANION__BASEADDRESS = "http://localhost:5080"

$agent = $null
try {
    Write-Host "Starting the Windows agent..." -ForegroundColor Cyan
    $agent = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList @("bin\Debug\net10.0-windows\PCConnect.Agent.dll") `
        -WorkingDirectory (Join-Path $repoRoot "clients\windows\PCConnect.Agent") `
        -NoNewWindow `
        -PassThru

    Write-Host "Starting the PCConnect companion..." -ForegroundColor Green
    Write-Warning "PC commands are real. Shutdown, restart, sleep, hibernate, lock, and sign-out affect this computer."
    Write-Host "Closing the companion will also stop the development agent."

    Push-Location (Join-Path $repoRoot "clients\windows\PCConnect.Companion")
    try {
        & dotnet "bin\Debug\net10.0-windows\PCConnect.Companion.dll"
    }
    finally {
        Pop-Location
    }
}
finally {
    if ($null -ne $agent -and -not $agent.HasExited) {
        Write-Host "Stopping the Windows agent..." -ForegroundColor DarkGray
        Stop-Process -Id $agent.Id -Force -ErrorAction SilentlyContinue
        $agent.WaitForExit(5000) | Out-Null
    }
}
