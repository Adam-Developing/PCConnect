[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$processPath = Join-Path $repositoryRoot 'artifacts\local-dev\processes.json'

if (Test-Path -LiteralPath $processPath) {
    $saved = Get-Content -LiteralPath $processPath -Raw | ConvertFrom-Json
    foreach ($processId in @($saved.Api, $saved.Worker)) {
        if ($processId) {
            Stop-Process -Id ([int]$processId) -ErrorAction SilentlyContinue
        }
    }
    Remove-Item -LiteralPath $processPath -Force
}

foreach ($container in @('pcconnect-dev-postgres', 'pcconnect-dev-valkey')) {
    $exists = @(docker ps -a --format '{{.Names}}') -contains $container
    if ($exists) { docker stop $container | Out-Null }
}

Write-Host 'PCConnect API, worker, PostgreSQL, and Valkey are stopped. Local database data was preserved.' -ForegroundColor Green
