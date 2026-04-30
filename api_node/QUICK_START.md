# Quick Start - Manual Test Execution

## In 3 Steps

### Step 1: Verify Environment (Run Once)
```bash
cd C:\Users\Adam\Documents\Filen\Projects\PCConnect\api_node
node health_check.js
```

**Expected**: All checks pass ✓

---

### Step 2: Start Server (Terminal 1 - Keep Running)
```bash
node server.js
```

**Expected**: 
```
[PCConnect] Unified Gateway + WebSockets running on port 3000
```

---

### Step 3: Run Tests (Terminal 2)
```bash
npm install socket.io-client axios  # (first time only)
node test_reminders.js
```

**Expected**: 
```
✓ Passed: 11
Summary: 11/11 tests passed (100%)
```

---

## Interpreting Results

| Outcome | Meaning | Next Steps |
|---------|---------|-----------|
| 11/11 pass ✓ | Perfect! Everything works | Done - Deployment ready |
| 8-10 pass ⚠ | Minor issues | Review failed test in MANUAL_TEST_REPORT.md |
| <8 pass ✗ | Major issues | Run health_check.js to identify blocker |
| Blocked ⚠ | Can't run tests | Fix blocker (DB, server, config) then retry |

---

## Common Issues

### Issue: "Cannot connect to http://localhost:3000"
→ Ensure `node server.js` is running in Terminal 1

### Issue: "Invalid API key"
→ Database connection issue - run `health_check.js`

### Issue: "reminders_initial timeout"
→ WebSocket not responding - check server terminal for errors

### Issue: "Database connection error"
→ MySQL not running - start MySQL service

---

## Manual Testing with curl

If automated tests fail, test individual endpoints:

```bash
# Get reminders
curl -H 'X-API-Key: 51359fd1b13802000649a6bd2f3f10ba' \
  http://localhost:3000/api_node/v1/reminders

# Create reminder
TODAY=$(date +%Y-%m-%d)
curl -X POST \
  -H 'X-API-Key: 51359fd1b13802000649a6bd2f3f10ba' \
  -H 'Content-Type: application/json' \
  -d "{\"reminder\":\"Test\",\"date\":\"$TODAY\",\"time\":\"14:30\"}" \
  http://localhost:3000/api_node/v1/reminders
```

Save the returned ID and use in further tests.

---

## Test Files

| File | Purpose |
|------|---------|
| `health_check.js` | Pre-flight verification |
| `test_reminders.js` | Automated 9-scenario test suite |
| `README_TESTING.md` | Complete guide (13KB) |
| `MANUAL_TEST_REPORT.md` | Detailed specs (13KB) |
| `TEST_MANIFEST.md` | File reference & matrix |
| `TEST_INSTRUCTIONS.sh` | Command reference |

---

## What Gets Tested

✓ Server connectivity
✓ REST API authentication  
✓ WebSocket authentication
✓ Real-time reminder events
✓ Create/Update/Complete endpoints
✓ End-to-end integration

**Total**: 9 core scenarios, 11 test cases

---

## Database User

All tests use this pre-configured admin user:

```
API Key: 51359fd1b13802000649a6bd2f3f10ba
Username: admin
```

---

## Done ✓

Successful completion = 11/11 tests passing

See `README_TESTING.md` or `MANUAL_TEST_REPORT.md` for details.
