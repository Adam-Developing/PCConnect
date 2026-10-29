[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Device,

    [switch]$Emulator
)

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
    $candidates = @()

    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) {
        $candidates += $env:JAVA_HOME
    }

    # Check ~/.jdks directories
    $jdksDir = Join-Path $env:USERPROFILE ".jdks"
    if (Test-Path $jdksDir) {
        $candidates += (Get-ChildItem -Path $jdksDir -Directory | Select-Object -ExpandProperty FullName)
    }

    # Check common system directories
    $candidates += @(
        (Join-Path $env:ProgramFiles "Eclipse Adoptium\jdk-17*"),
        (Join-Path $env:ProgramFiles "Java\jdk-17*"),
        (Join-Path $env:ProgramFiles "Microsoft\jdk-17*"),
        (Join-Path $env:LOCALAPPDATA "Programs\Android Studio\jbr"),
        (Join-Path $env:ProgramFiles "Android\Android Studio\jbr")
    )

    # Check for compatible Java version (Gradle 8.14 and Kotlin 2.1 support Java 17..23, but fail on Java 25+)
    foreach ($pattern in $candidates) {
        $resolved = Resolve-Path $pattern -ErrorAction SilentlyContinue
        foreach ($r in $resolved) {
            $javaExe = Join-Path $r.Path "bin\java.exe"
            if (Test-Path $javaExe) {
                try {
                    $psi = [System.Diagnostics.ProcessStartInfo]::new($javaExe, "-version")
                    $psi.RedirectStandardError = $true
                    $psi.UseShellExecute = $false
                    $p = [System.Diagnostics.Process]::Start($psi)
                    $verOut = $p.StandardError.ReadToEnd()
                    $p.WaitForExit()

                    if ($verOut -match 'version "(1\.)?(\d+)') {
                        $major = [int]$Matches[2]
                        if ($major -ge 17 -and $major -le 23) {
                            return $r.Path
                        }
                    }
                }
                catch {
                    # Ignore invalid or non-executable Java candidates
                }
            }
        }
    }

    # If no compatible Java 17-23 is installed, auto-download Eclipse Temurin 17 into ~/.jdks/temurin-17
    $targetTemurin = Join-Path $env:USERPROFILE ".jdks\temurin-17"
    if (-not (Test-Path (Join-Path $targetTemurin "bin\java.exe"))) {
        Write-Host "No compatible Java 17-21 installation found (Android Studio bundled JBR is Java 25+, which Gradle 8.14 does not support)." -ForegroundColor Yellow
        Write-Host "Downloading Eclipse Temurin JDK 17 to '$targetTemurin'..." -ForegroundColor Cyan

        $tempZip = Join-Path $env:TEMP "temurin17.zip"
        try {
            if (-not (Test-Path (Join-Path $env:USERPROFILE ".jdks"))) {
                New-Item -ItemType Directory -Path (Join-Path $env:USERPROFILE ".jdks") -Force | Out-Null
            }
            if (-not (Test-Path $targetTemurin)) {
                New-Item -ItemType Directory -Path $targetTemurin -Force | Out-Null
            }

            curl.exe -L "https://api.adoptium.net/v3/binary/latest/17/ga/windows/x64/jdk/hotspot/normal/eclipse" -o $tempZip
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to download Eclipse Temurin JDK 17."
            }

            tar.exe -xf $tempZip -C $targetTemurin --strip-components 1
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to extract Eclipse Temurin JDK 17."
            }
        }
        finally {
            Remove-Item $tempZip -Force -ErrorAction SilentlyContinue
        }
    }

    if (Test-Path (Join-Path $targetTemurin "bin\java.exe")) {
        return $targetTemurin
    }

    throw "Compatible Java 17 installation was not found or could not be installed. Set JAVA_HOME to a Java 17-21 JDK."
}

function Get-AdbDevices {
    param([Parameter(Mandatory = $true)][string]$Adb)

    $lines = & $Adb devices -l
    if ($LASTEXITCODE -ne 0) {
        throw "adb could not list Android devices."
    }

    $devices = @()
    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line -match "^List of devices attached") {
            continue
        }

        if ($line -match '^\s*([^\s]+)\s+([^\s]+)(.*)$') {
            $serial = $Matches[1]
            $state = $Matches[2]
            $extra = $Matches[3]

            $model = if ($extra -match '\bmodel:([^\s]+)') { $Matches[1] } else { "" }
            $product = if ($extra -match '\bproduct:([^\s]+)') { $Matches[1] } else { "" }
            $isUsb = $extra -match '\busb:'

            $isEmulator = $false
            if ($serial -match '^emulator-\d+$') {
                $isEmulator = $true
            }
            elseif ($model -match '^(sdk_|google_sdk|Android_SDK|emulator)' -or $product -match '^(sdk_|google_sdk|emulator)') {
                $isEmulator = $true
            }
            elseif ($isUsb) {
                # Physically connected via USB controller
                $isEmulator = $false
            }
            elseif ($state -eq "device") {
                # For network/wireless debugging devices, query system properties
                $qemu = (& $Adb -s $serial shell getprop ro.kernel.qemu 2>$null).Trim()
                $bootQemu = (& $Adb -s $serial shell getprop ro.boot.qemu 2>$null).Trim()
                $hardware = (& $Adb -s $serial shell getprop ro.hardware 2>$null).Trim()
                $characteristics = (& $Adb -s $serial shell getprop ro.build.characteristics 2>$null).Trim()

                if ($qemu -eq "1" -or $bootQemu -eq "1" -or $hardware -match '^(goldfish|ranchu|vbox86|cuttlefish)' -or $characteristics -match 'emulator') {
                    $isEmulator = $true
                }
            }

            $displayName = if (-not [string]::IsNullOrWhiteSpace($model)) {
                "$model ($serial)"
            }
            else {
                $serial
            }

            $devices += [PSCustomObject]@{
                Serial      = $serial
                State       = $state
                Model       = $model
                Product     = $product
                IsUsb       = $isUsb
                IsEmulator  = $isEmulator
                IsPhysical  = (-not $isEmulator)
                DisplayName = $displayName
            }
        }
    }

    return ,$devices
}

function Start-And-Wait-Emulator {
    param(
        [Parameter(Mandatory = $true)][string]$Adb,
        [Parameter(Mandatory = $true)][string]$EmulatorExe
    )

    if (-not (Test-Path $EmulatorExe)) {
        throw "No Android device is connected and the Android emulator was not found. Connect a physical device or start an emulator in Android Studio."
    }

    $avds = @(& $EmulatorExe -list-avds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($avds.Count -eq 0) {
        throw "No Android device or emulator is available. Connect a physical device or create an emulator in Android Studio's Device Manager and retry."
    }

    $avd = $avds[0].Trim()
    Write-Host "Starting Android emulator '$avd'..." -ForegroundColor Cyan
    Start-Process -FilePath $EmulatorExe -ArgumentList @("-avd", $avd) | Out-Null

    & $Adb wait-for-device
    Write-Host "Waiting for Android emulator to finish booting..." -ForegroundColor Cyan
    $booted = $false
    for ($attempt = 0; $attempt -lt 90; $attempt++) {
        $devices = Get-AdbDevices $Adb
        $booting = @($devices | Where-Object { $_.IsEmulator -and $_.State -eq "device" })
        if ($booting.Count -gt 0) {
            $bootState = (& $Adb -s $booting[0].Serial shell getprop sys.boot_completed 2>$null).Trim()
            if ($bootState -eq "1") {
                $booted = $true
                return $booting[0]
            }
        }
        Start-Sleep -Seconds 2
    }

    throw "The Android emulator did not finish booting within three minutes."
}

function Select-TargetDevice {
    param(
        [Parameter(Mandatory = $true)][string]$Adb,
        [Parameter(Mandatory = $true)][string]$EmulatorExe,
        [string]$RequestedDevice,
        [switch]$ForceEmulator
    )

    $devices = Get-AdbDevices $Adb

    # Case 1: Explicit device requested
    if (-not [string]::IsNullOrWhiteSpace($RequestedDevice)) {
        $matching = @($devices | Where-Object {
            $_.Serial -eq $RequestedDevice -or
            $_.Model -eq $RequestedDevice -or
            $_.Serial -like "*$RequestedDevice*" -or
            $_.Model -like "*$RequestedDevice*"
        })

        if ($matching.Count -eq 0) {
            $known = if ($devices.Count -gt 0) { ($devices.DisplayName -join ", ") } else { "none" }
            throw "Requested device '$RequestedDevice' was not found. Connected devices: $known"
        }

        $chosen = $matching[0]
        if ($chosen.State -eq "unauthorized") {
            throw "Device '$($chosen.DisplayName)' is unauthorized. Accept the debugging prompt on the device screen and retry."
        }
        if ($chosen.State -ne "device") {
            throw "Device '$($chosen.DisplayName)' is in state '$($chosen.State)' and cannot accept deployments."
        }

        Write-Host "Using requested device: $($chosen.DisplayName)" -ForegroundColor Cyan
        return $chosen
    }

    # Case 2: Forced emulator requested
    if ($ForceEmulator) {
        $runningEmus = @($devices | Where-Object { $_.IsEmulator -and $_.State -eq "device" })
        if ($runningEmus.Count -gt 0) {
            Write-Host "Using running Android emulator $($runningEmus[0].DisplayName)." -ForegroundColor DarkGray
            return $runningEmus[0]
        }

        return Start-And-Wait-Emulator -Adb $Adb -EmulatorExe $EmulatorExe
    }

    # Case 3: Default behavior - prefer physical device where available
    $readyPhysical = @($devices | Where-Object { $_.IsPhysical -and $_.State -eq "device" })
    if ($readyPhysical.Count -gt 0) {
        $chosen = $readyPhysical[0]
        $runningEmus = @($devices | Where-Object { $_.IsEmulator -and $_.State -eq "device" })
        if ($runningEmus.Count -gt 0) {
            Write-Host "Physical device is available; prioritizing over running emulator ($($runningEmus[0].DisplayName))." -ForegroundColor DarkGray
        }
        if ($readyPhysical.Count -gt 1) {
            Write-Host "Multiple physical devices connected. Defaulting to $($chosen.DisplayName). (Use -Device to select another)" -ForegroundColor DarkGray
        }
        Write-Host "Using physical Android device $($chosen.DisplayName)." -ForegroundColor DarkGray
        return $chosen
    }

    # Check if a physical device is connected but unauthorized
    $unauthPhysical = @($devices | Where-Object { $_.IsPhysical -and $_.State -eq "unauthorized" })
    if ($unauthPhysical.Count -gt 0) {
        $unauth = $unauthPhysical[0]
        Write-Host "Physical device $($unauth.DisplayName) is connected but unauthorized." -ForegroundColor Yellow
        Write-Host "Please unlock the device and accept the USB/wireless debugging prompt..." -ForegroundColor Yellow

        for ($i = 0; $i -lt 15; $i++) {
            Start-Sleep -Seconds 1
            $currentDevices = Get-AdbDevices $Adb
            $authorized = @($currentDevices | Where-Object { $_.Serial -eq $unauth.Serial -and $_.State -eq "device" })
            if ($authorized.Count -gt 0) {
                Write-Host "Device $($authorized[0].DisplayName) authorized!" -ForegroundColor Green
                return $authorized[0]
            }
        }

        Write-Host "Physical device $($unauth.DisplayName) was not authorized in time. Falling back to emulator..." -ForegroundColor Yellow
    }

    # No physical device available (or authorization timed out); check running emulators
    $runningEmus = @($devices | Where-Object { $_.IsEmulator -and $_.State -eq "device" })
    if ($runningEmus.Count -gt 0) {
        Write-Host "No ready physical device found. Using running Android emulator $($runningEmus[0].DisplayName)." -ForegroundColor DarkGray
        return $runningEmus[0]
    }

    # No physical device and no running emulator -> start emulator
    Write-Host "No ready physical device or running emulator found." -ForegroundColor DarkGray
    return Start-And-Wait-Emulator -Adb $Adb -EmulatorExe $EmulatorExe
}

$androidSdk = Resolve-AndroidSdk
$env:JAVA_HOME = Resolve-JavaHome
$env:ANDROID_SDK_ROOT = $androidSdk
$env:Path = "$(Join-Path $env:JAVA_HOME 'bin');$(Join-Path $androidSdk 'platform-tools');$env:Path"

$adbExe = Join-Path $androidSdk "platform-tools\adb.exe"
$emulatorExe = Join-Path $androidSdk "emulator\emulator.exe"

& $adbExe start-server | Out-Null

$targetDevice = Select-TargetDevice -Adb $adbExe -EmulatorExe $emulatorExe -RequestedDevice $Device -ForceEmulator:$Emulator
$selectedSerial = $targetDevice.Serial
$env:ANDROID_SERIAL = $selectedSerial

Write-Host "Forwarding the device's localhost:5080 to the local API..." -ForegroundColor Cyan
& $adbExe -s $selectedSerial reverse tcp:5080 tcp:5080
if ($LASTEXITCODE -ne 0) {
    throw "adb could not expose the local API to $selectedSerial."
}

Set-Location $androidRoot
Write-Host "Building the Android debug app..." -ForegroundColor Cyan
& ".\gradlew.bat" assembleDebug
if ($LASTEXITCODE -ne 0) {
    throw "The Android build failed."
}

$apkPath = Join-Path $androidRoot "app\build\outputs\apk\debug\app-debug.apk"
if (-not (Test-Path $apkPath)) {
    throw "Build succeeded but APK was not found at '$apkPath'."
}

Write-Host "Installing the debug app on $($targetDevice.DisplayName)..." -ForegroundColor Cyan
& $adbExe -s $selectedSerial install -r -d $apkPath
if ($LASTEXITCODE -ne 0) {
    throw "The APK installation on $($targetDevice.DisplayName) failed."
}

Write-Host "Launching PCConnect on $($targetDevice.DisplayName)..." -ForegroundColor Green
& $adbExe -s $selectedSerial shell am start -n "uk.co.adamkhattab.pcconnect.debug/uk.co.adamkhattab.pcconnect.MainActivity"
if ($LASTEXITCODE -ne 0) {
    throw "The app installed, but Android could not launch it."
}
