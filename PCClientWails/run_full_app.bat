@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "DRY_RUN=0"
if /I "%~1"=="--dry-run" set "DRY_RUN=1"

for %%I in ("%~dp0.") do set "APP_DIR=%%~fI"
for %%I in ("%APP_DIR%\..") do set "REPO_DIR=%%~fI"
set "API_DIR=%REPO_DIR%\api_node"

echo.
echo ================================================
echo   PCClient Full Launcher (DB + API + Wails)
echo ================================================
echo.
echo [INFO] App directory : "%APP_DIR%"
echo [INFO] API directory : "%API_DIR%"
echo.

where node >nul 2>nul
if errorlevel 1 (
  echo [ERROR] Node.js not found in PATH.
  exit /b 1
)

where wails >nul 2>nul
if errorlevel 1 (
  echo [ERROR] Wails CLI not found in PATH.
  echo Install with:
  echo   go install github.com/wailsapp/wails/v2/cmd/wails@latest
  exit /b 1
)

if not exist "%API_DIR%\server.js" (
  echo [ERROR] API server not found at "%API_DIR%\server.js".
  exit /b 1
)

echo [1/4] Checking DB/API health...
pushd "%API_DIR%"
node health_check.js
if errorlevel 1 (
  popd
  echo.
  echo [ERROR] Health check failed.
  echo Make sure MySQL is running and DB config in api_node\config.json is correct.
  exit /b 1
)
popd

if "%DRY_RUN%"=="1" (
  echo.
  echo [DRY-RUN] Health check passed and prerequisites are available.
  echo [DRY-RUN] Skipping API start and Wails launch.
  exit /b 0
)

echo.
echo [2/4] Starting backend API in a new terminal...
start "PCConnect API" /D "%API_DIR%" cmd /k "node server.js"

echo.
echo [3/4] Waiting for API readiness on http://localhost:3000/ping ...
set "READY=0"
for /L %%N in (1,1,30) do (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $r = Invoke-WebRequest -UseBasicParsing -Uri 'http://localhost:3000/ping' -TimeoutSec 2; if($r.StatusCode -eq 200){ exit 0 } else { exit 1 } } catch { exit 1 }"
  if !errorlevel! EQU 0 (
    set "READY=1"
    goto :ready
  )
  timeout /t 1 /nobreak >nul
)

:ready
if "%READY%"=="1" (
  echo [OK] API is ready.
) else (
  echo [WARN] API readiness check timed out.
  echo Continuing anyway; verify server output in the "PCConnect API" terminal.
)

echo.
echo [4/4] Launching Wails desktop app...
pushd "%APP_DIR%"
wails dev
set "WAILS_EXIT=%ERRORLEVEL%"
popd

echo.
echo Wails exited with code %WAILS_EXIT%.
echo The API terminal remains open for logs.
exit /b %WAILS_EXIT%
