@echo off
setlocal
set "PCCONNECT_CMD_PATH=%~f0"
set "PCCONNECT_CMD_ARGS="
set PCCONNECT_CMD_ARGS=%*
where pwsh.exe >nul 2>nul
if %ERRORLEVEL% equ 0 (
    pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "try { $global:LASTEXITCODE = 0; $lines = [System.IO.File]::ReadAllLines($env:PCCONNECT_CMD_PATH); $i = [Array]::IndexOf($lines, '# --- POWERSHELL ---'); $code = ($lines[($i + 1)..($lines.Length - 1)] -join [Environment]::NewLine); Invoke-Expression ('& {' + [Environment]::NewLine + $code + [Environment]::NewLine + '} ' + $env:PCCONNECT_CMD_ARGS); if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } } catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 1 }"
) else (
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "try { $global:LASTEXITCODE = 0; $lines = [System.IO.File]::ReadAllLines($env:PCCONNECT_CMD_PATH); $i = [Array]::IndexOf($lines, '# --- POWERSHELL ---'); $code = ($lines[($i + 1)..($lines.Length - 1)] -join [Environment]::NewLine); Invoke-Expression ('& {' + [Environment]::NewLine + $code + [Environment]::NewLine + '} ' + $env:PCCONNECT_CMD_ARGS); if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } } catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 1 }"
)
set "EXIT_CODE=%ERRORLEVEL%"
if %EXIT_CODE% equ 0 exit /b 0

echo.
echo Windows launcher exited with code %EXIT_CODE%.
setlocal EnableDelayedExpansion
if /i not "!CMDCMDLINE:%~nx0=!"=="!CMDCMDLINE!" pause
endlocal
exit /b %EXIT_CODE%
# --- POWERSHELL ---
[CmdletBinding()]
param(
    [ValidateRange(1, 65535)]
    [int]$ApiPort = 5080,

    [string]$BaseAddress = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $env:PCCONNECT_CMD_PATH

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

function Stop-ProjectProcesses {
    param(
        [Parameter(Mandatory = $true)][string[]]$ExecutableNames,
        [Parameter(Mandatory = $true)][string]$DotnetCommandPattern
    )

    $pids = [System.Collections.Generic.HashSet[int]]::new()

    foreach ($exe in $ExecutableNames) {
        $procName = [System.IO.Path]::GetFileNameWithoutExtension($exe)
        Get-Process -Name $procName -ErrorAction SilentlyContinue |
            Where-Object { $_.Id -ne $PID } |
            ForEach-Object { [void]$pids.Add($_.Id) }
    }

    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessId -ne $PID -and $_.Name -ieq 'dotnet.exe' -and $_.CommandLine -match $DotnetCommandPattern } |
        ForEach-Object { [void]$pids.Add([int]$_.ProcessId) }

    if ($pids.Count -gt 0) {
        Write-Host "Stopping running Windows client process(es) ($($pids -join ', '))..." -ForegroundColor DarkGray
        foreach ($procId in $pids) {
            try {
                $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
                if ($null -ne $proc) {
                    Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
                    $proc.WaitForExit(5000) | Out-Null
                }
            }
            catch {
                # Process already exited
            }
        }
    }
}

Assert-Command "dotnet"

$effectiveBaseAddress = if ([string]::IsNullOrWhiteSpace($BaseAddress)) {
    "http://localhost:$ApiPort"
}
else {
    $BaseAddress.TrimEnd('/')
}

Push-Location $repoRoot
$agent = $null
try {
    Write-Host "Checking the API at $effectiveBaseAddress..." -ForegroundColor Cyan
    try {
        Invoke-RestMethod -Uri "$effectiveBaseAddress/v2/meta/discovery" -TimeoutSec 5 | Out-Null
    }
    catch {
        throw "The backend is not available at $effectiveBaseAddress. Run .\make-and-run-backend.cmd first."
    }

    # Stop any previously running Agent or Companion (including minimized-to-tray instances) so DLLs are not locked
    Stop-ProjectProcesses -ExecutableNames @("PCConnect.Agent.exe", "PCConnect.Companion.exe") -DotnetCommandPattern 'PCConnect\.(Agent|Companion)'

    Write-Host "Building the Windows agent and companion..." -ForegroundColor Cyan
    Invoke-Checked "dotnet" @("build", "clients\windows\PCConnect.Agent\PCConnect.Agent.csproj", "-c", "Debug", "--nologo") "The Windows agent build failed."
    Invoke-Checked "dotnet" @("build", "clients\windows\PCConnect.Companion\PCConnect.Companion.csproj", "-c", "Debug", "--nologo") "The Windows companion build failed."

    $env:PCCONNECT_AGENT_AGENT__BASEADDRESS = $effectiveBaseAddress
    $env:PCCONNECT_COMPANION_COMPANION__BASEADDRESS = $effectiveBaseAddress

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
    Pop-Location
}
