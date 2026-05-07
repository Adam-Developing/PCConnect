 # PCClientWails

Go + Wails v3 desktop implementation of PCClient using the project blueprint.

## What is implemented

- Wails v3 app scaffold (`main.go`, `wails.json`, Go backend bindings)
- React + TypeScript frontend with screens:
  - Login
  - Dashboard
  - Reminders
  - Devices
  - Settings (scaffold)
- WebSocket-first realtime behavior:
  - listens for `execute_command`, `reminders_initial`, `reminder_update`
  - no constant polling when socket is healthy
  - fallback polling only when disconnected with backoff (`5s -> 10s -> 20s -> 30s`)
- Offline queue for reminder create/complete operations
- Command execution allowlist in Go backend
- Realtime policy unit tests in `internal/realtime/policy_test.go`

## Project structure

- `app/` - Go Wails bindings, session storage, command execution
- `internal/realtime/` - polling policy logic + tests
- `frontend/` - React UI and realtime/fallback client logic

## Prerequisites

- Go `1.22+`
- Node.js `18+`
- Wails CLI v2 (`go install github.com/wailsapp/wails/v3/cmd/wails3@latest`)

## Quick start

### One-command full launch (DB check + API + desktop app)

```powershell
Set-Location "c:\Users\Adam\Documents\Filen\Projects\PCConnect\PCClientWails"
.\run_full_app.bat
```

This script will:
- run `api_node\health_check.js` (DB + config checks)
- start `api_node\server.js` in a new terminal window
- wait for `http://localhost:3000/ping`
- launch `wails3 dev`

### 1) Install frontend dependencies

```powershell
Set-Location "c:\Users\Adam\Documents\Filen\Projects\PCConnect\PCClientWails\frontend"
npm install
```

### 2) Run frontend dev server only (UI development)

```powershell
Set-Location "c:\Users\Adam\Documents\Filen\Projects\PCConnect\PCClientWails\frontend"
npm run dev
```

### 3) Run Wails desktop app (full app)

```powershell
Set-Location "c:\Users\Adam\Documents\Filen\Projects\PCConnect\PCClientWails"
wails3 dev
```

### 4) Build desktop executable

```powershell
Set-Location "c:\Users\Adam\Documents\Filen\Projects\PCConnect\PCClientWails"
wails3 build
```

## Configure API base URL

Use your Node API URL in login screen, for example:
- `http://localhost:3000/api_node`

## Validation checks used in this repo

```powershell
Set-Location "c:\Users\Adam\Documents\Filen\Projects\PCConnect\PCClientWails"
go test .\internal\realtime

Set-Location "c:\Users\Adam\Documents\Filen\Projects\PCConnect\PCClientWails\frontend"
npm run build
```

## Notes

- Current session store is file-based (`%AppData%/PCClient/wails/session.json`) to keep the scaffold straightforward.
- You can upgrade to Windows Credential Manager storage in the next hardening pass.
- If `wails3 dev` fails due to Application Control policy, run the script from an elevated shell or allow required binaries in your endpoint policy.
