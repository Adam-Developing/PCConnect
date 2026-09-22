[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$androidRoot = Join-Path $repoRoot "clients\android"

function Resolve-AndroidSdk {
    $candidates = @(
        $env:ANDROID_SDK_ROOT,
        $env:ANDROID_HOME,
        (Join-Path $env:LOCALAPPDATA "Android\Sdk")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (Test-Path (Join-Path $candidate "platform-tools\adb.exe")) {
            return $candidate
        }
    }

    throw "The Android SDK was not found. Install it through Android Studio, or set ANDROID_SDK_ROOT."
}

function Resolve-JavaHome {
    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME) -and
        (Test-Path (Join-Path $env:JAVA_HOME "bin\java.exe"))) {
        return $env:JAVA_HOME
    }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Android Studio\jbr"),
        (Join-Path $env:ProgramFiles "Android\Android Studio\jbr")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path (Join-Path $candidate "bin\java.exe")) {
            return $candidate
        }
    }

    throw "Java 17 was not found. Install Android Studio, or set JAVA_HOME to its bundled jbr directory."
}

function Get-ReadyDevices {
    param([Parameter(Mandatory = $true)][string]$Adb)

    $lines = & $Adb devices
    if ($LASTEXITCODE -ne 0) {
        throw "adb could not list Android devices."
    }

    return @($lines | ForEach-Object {
        if ($_ -match "^([^\s]+)\s+device$") {
            $Matches[1]
        }
    })
}

$androidSdk = Resolve-AndroidSdk
$env:JAVA_HOME = Resolve-JavaHome
$env:ANDROID_SDK_ROOT = $androidSdk
$env:Path = "$(Join-Path $env:JAVA_HOME 'bin');$(Join-Path $androidSdk 'platform-tools');$env:Path"

$adb = Join-Path $androidSdk "platform-tools\adb.exe"
$emulator = Join-Path $androidSdk "emulator\emulator.exe"

& $adb start-server | Out-Null
$devices = Get-ReadyDevices $adb

if ($devices.Count -eq 0) {
    if (-not (Test-Path $emulator)) {
        throw "No Android device is connected and the Android emulator was not found. Start a device in Android Studio and retry."
    }

    $avds = @(& $emulator -list-avds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($avds.Count -eq 0) {
        throw "No Android device or emulator is available. Create an emulator in Android Studio's Device Manager and retry."
    }

    $avd = $avds[0].Trim()
    Write-Host "Starting Android emulator '$avd'..." -ForegroundColor Cyan
    Start-Process -FilePath $emulator -ArgumentList @("-avd", $avd) | Out-Null

    & $adb wait-for-device
    Write-Host "Waiting for Android to finish booting..." -ForegroundColor Cyan
    $booted = $false
    for ($attempt = 0; $attempt -lt 90; $attempt++) {
        $devices = Get-ReadyDevices $adb
        if ($devices.Count -gt 0) {
            $bootState = (& $adb -s $devices[0] shell getprop sys.boot_completed 2>$null).Trim()
            if ($bootState -eq "1") {
                $booted = $true
                break
            }
        }
        Start-Sleep -Seconds 2
    }
    if (-not $booted) {
        throw "The Android emulator did not finish booting within three minutes."
    }
}

$devices = Get-ReadyDevices $adb
$device = $devices[0]
Write-Host "Using Android device $device." -ForegroundColor DarkGray

Write-Host "Forwarding the device's localhost:5080 to the local API..." -ForegroundColor Cyan
& $adb -s $device reverse tcp:5080 tcp:5080
if ($LASTEXITCODE -ne 0) {
    throw "adb could not expose the local API to $device."
}

Set-Location $androidRoot
Write-Host "Building and installing the Android debug app..." -ForegroundColor Cyan
& ".\gradlew.bat" installDebug
if ($LASTEXITCODE -ne 0) {
    throw "The Android build or installation failed."
}

Write-Host "Launching PCConnect..." -ForegroundColor Green
& $adb -s $device shell am start -n "uk.co.adamkhattab.pcconnect.debug/uk.co.adamkhattab.pcconnect.MainActivity"
if ($LASTEXITCODE -ne 0) {
    throw "The app installed, but Android could not launch it."
}
