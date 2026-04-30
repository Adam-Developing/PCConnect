# PCConnect Realtime Reminder Backend - Manual Test Guide

## Quick Start

```bash
# Terminal 1: Verify setup
cd C:\Users\Adam\Documents\Filen\Projects\PCConnect\api_node
node health_check.js

# Terminal 1: Install test dependencies if needed
npm install socket.io-client axios

# Terminal 1: Start the server
node server.js

# Terminal 2: Run the test suite
node test_reminders.js
```

---

## What is Being Tested?

The realtime reminder backend (`api_node`) provides:

1. **WebSocket Real-Time Communication**
   - Client connects and authenticates with API key + PC name
   - Server emits `reminders_initial` with all current reminders
   - Server emits `reminder_update` whenever reminder is created/updated/completed

2. **REST API Endpoints**
   - `GET /api_node/v1/reminders` - Get all reminders for user
   - `POST /api_node/v1/reminders` - Create new reminder
   - `PUT /api_node/v1/reminders/:id` - Update existing reminder
   - `POST /api_node/v1/reminders/:id/complete` - Mark as complete/incomplete

3. **Integration Between REST and WebSocket**
   - When REST API modifies a reminder, connected WebSocket clients are notified immediately
   - No polling required - truly real-time

---

## Files Included

| File | Purpose |
|------|---------|
| `test_reminders.js` | **Automated test suite** - Tests all 9 scenarios |
| `health_check.js` | **Pre-flight check** - Verifies DB connectivity and dependencies |
| `MANUAL_TEST_REPORT.md` | **Detailed reference** - Complete test specifications |
| `TEST_INSTRUCTIONS.sh` | **Command reference** - Manual curl/REPL test commands |

---

## Test Scenarios

### Scenario 1: Server Connectivity
```
Verify Node.js gateway responds to HTTP requests
Command: curl http://localhost:3000/ping
Expected: "Node Gateway Active"
```

### Scenario 2: REST API Authentication
```
Verify API key is required for REST endpoints
- Missing API key returns 401
- Valid API key allows access
Endpoint: GET /api_node/v1/reminders
```

### Scenario 3: WebSocket Connect & Initial Reminders
```
1. Client connects to WebSocket
2. Client emits: { apiKey, pcName }
3. Server emits: 'authenticated' event
4. Server emits: 'reminders_initial' event with current reminder list
Expected events in order: authenticated, reminders_initial
```

### Scenario 4: Create Reminder via REST
```
Endpoint: POST /api_node/v1/reminders
Payload: { reminder: "text", date: "YYYY-MM-DD", time: "HH:MM" }
Expected: HTTP 201 with reminder ID
```

### Scenario 5: Real-Time Update on Create
```
1. WebSocket client connected and listening
2. Another client creates reminder via REST API
3. Listening client receives 'reminder_update' event immediately
```

### Scenario 6: Update Reminder via REST
```
Endpoint: PUT /api_node/v1/reminders/:id
Payload: Partial update - only changed fields
Expected: HTTP 200 with success
```

### Scenario 7: Real-Time Update on Update
```
Same as Scenario 5, but triggered by PUT request
```

### Scenario 8: Complete Reminder via REST
```
Endpoint: POST /api_node/v1/reminders/:id/complete
Payload: { completed: 1 }
Expected: HTTP 200 with success
```

### Scenario 9: Real-Time Update on Complete
```
Same as Scenario 5, but triggered by complete endpoint
```

---

## Test Database User

The tests use the admin user from the database dump:

```
ID: 1
Username: admin
API Key: 51359fd1b13802000649a6bd2f3f10ba
Email: 18khattaba@gmail.com
Password: (hashed)
```

This user is pre-configured in `DB/pcconnect_new.sql`.

---

## Running the Test Suite

### Step 1: Pre-Flight Check
```bash
node health_check.js
```

This verifies:
- ✓ Database configuration (config.json)
- ✓ MySQL connectivity
- ✓ Required tables exist (users, reminders)
- ✓ Admin user exists
- ✓ Node dependencies installed

**Output should show**:
```
✓ Database connection successful!
✓ Users table: 1218 total users
✓ Reminders table: 42 total reminders
✓ Admin user found: admin (ID: 1)
✓ All checks passed!
```

### Step 2: Start the Server
```bash
node server.js
```

**Expected server output**:
```
[PCConnect] Unified Gateway + WebSockets running on port 3000
```

The server runs indefinitely. Keep it running in this terminal.

### Step 3: Run Tests (in new terminal)
```bash
node test_reminders.js
```

**Expected output** (9 tests, all passing):
```
✓ PASS Server connectivity
✓ PASS REST missing API key rejection
✓ PASS REST API key validation
✓ PASS WebSocket authenticated event
✓ PASS WebSocket reminders_initial event
✓ PASS REST create reminder - Created reminder ID: 123
✓ PASS WebSocket reminder_update on create - Type: created
✓ PASS REST update reminder - Updated reminder ID: 123
✓ PASS WebSocket reminder_update on update - Type: updated
✓ PASS REST complete reminder - Completed reminder ID: 123
✓ PASS WebSocket reminder_update on complete - Type: updated

Summary: 11/11 tests passed (100%)
```

---

## Manual Testing with curl

If you prefer to test individual endpoints manually:

### Get Existing Reminders
```bash
curl -H 'X-API-Key: 51359fd1b13802000649a6bd2f3f10ba' \
  http://localhost:3000/api_node/v1/reminders
```

### Create a Reminder
```bash
CURRENT_DATE=$(date +%Y-%m-%d)
curl -X POST \
  -H 'X-API-Key: 51359fd1b13802000649a6bd2f3f10ba' \
  -H 'Content-Type: application/json' \
  -d "{
    \"reminder\": \"Manual test reminder\",
    \"date\": \"$CURRENT_DATE\",
    \"time\": \"14:30\"
  }" \
  http://localhost:3000/api_node/v1/reminders
```

Response will include the reminder ID. Save it for next steps.

### Update a Reminder
```bash
curl -X PUT \
  -H 'X-API-Key: 51359fd1b13802000649a6bd2f3f10ba' \
  -H 'Content-Type: application/json' \
  -d "{
    \"reminder\": \"Updated manual test reminder\",
    \"time\": \"15:45\"
  }" \
  http://localhost:3000/api_node/v1/reminders/REMINDER_ID
```

Replace `REMINDER_ID` with the ID from create response.

### Mark as Complete
```bash
curl -X POST \
  -H 'X-API-Key: 51359fd1b13802000649a6bd2f3f10ba' \
  -H 'Content-Type: application/json' \
  -d '{"completed": 1}' \
  http://localhost:3000/api_node/v1/reminders/REMINDER_ID/complete
```

---

## Manual Testing with WebSocket (Node REPL)

To manually test WebSocket functionality:

```bash
# Start Node REPL
node

# Inside Node REPL, paste this code:
```

```javascript
const io = require('socket.io-client');
const socket = io('http://localhost:3000');

// Connect event
socket.on('connect', () => {
  console.log('✓ Connected to server');
  socket.emit('authenticate', {
    apiKey: '51359fd1b13802000649a6bd2f3f10ba',
    pcName: 'MANUAL_TEST_PC'
  });
});

// Authentication successful
socket.on('authenticated', (data) => {
  console.log('✓ Authenticated:', data.message);
});

// Initial reminders list
socket.on('reminders_initial', (data) => {
  console.log(`✓ Received ${data.reminders.length} reminders:`);
  data.reminders.forEach(r => {
    console.log(`  [${r.ID}] ${r.Reminder} on ${r.Date} at ${r.Time}`);
  });
});

// Real-time reminder updates
socket.on('reminder_update', (data) => {
  console.log(`✓ Reminder update: type=${data.type}`);
  if (data.reminder) {
    console.log(`  Reminder: ${data.reminder.Reminder}`);
  }
  console.log(`  Total reminders: ${data.reminders.length}`);
});

// Authentication error
socket.on('auth_error', (data) => {
  console.error('✗ Auth failed:', data.message);
});

// Leave connected and test REST endpoints from another terminal
console.log('Connected. Now test REST endpoints from another terminal.');
```

---

## Understanding Test Output

### Success Messages
```
✓ PASS Test Name - Additional details
```

### Failure Messages
```
✗ FAIL Test Name - Error details
```

### Blocker Messages
```
✗ ERROR Test Name BLOCKED: Reason why test couldn't run
```

Blockers indicate environmental issues (database not accessible, server not running, etc.)

---

## Common Issues & Solutions

### Issue: "Cannot connect to http://localhost:3000"
**Cause**: Server not running
**Solution**: 
1. Check that `node server.js` is running in another terminal
2. Verify port 3000 is not in use: `lsof -i :3000` (Mac/Linux)
3. Try accessing manually: `curl http://localhost:3000/ping`

### Issue: "Invalid API key"
**Cause**: API key doesn't match database
**Solution**:
1. Verify you're using: `51359fd1b13802000649a6bd2f3f10ba`
2. Check database: `SELECT api_key FROM users WHERE id = 1;`
3. User must be enabled: `SELECT Enabled FROM users WHERE id = 1;`

### Issue: "Database connection error"
**Cause**: MySQL not running or config incorrect
**Solution**:
1. Start MySQL: `sudo systemctl start mysql` (Linux) or Services app (Windows)
2. Check config.json: host, user, database values
3. Verify database exists: `mysql -u root -e "SHOW DATABASES;"`

### Issue: "reminders table not found"
**Cause**: Database schema not imported
**Solution**:
```bash
mysql -u root pcconnect < ../DB/pcconnect_new.sql
```

### Issue: WebSocket times out
**Cause**: Server running but WebSocket not accepting connections
**Solution**:
1. Check server console for errors
2. Verify server shows: "running on port 3000"
3. Look for socket connection logs

---

## Interpreting Results

### 11/11 Tests Passing (100%)
✓ **PERFECT** - All functionality working correctly
- WebSocket authentication works
- Real-time events deliver immediately
- REST endpoints functional
- Database encryption/decryption working

### 8-10 Tests Passing (80-90%)
⚠ **GOOD** - Minor issues
- Likely WebSocket timing issue
- Real-time events might be slightly delayed
- Or single REST endpoint problem

### < 8 Tests Passing (< 80%)
✗ **ISSUES** - Major problems
- Check blockers section for root cause
- Database or server connectivity problem
- Configuration issue

---

## Next Steps After Testing

### If All Tests Pass
1. Deployment ready - code works as designed
2. Can proceed with client integration
3. Monitor server logs in production for errors

### If Some Tests Fail
1. Review MANUAL_TEST_REPORT.md for detailed test specs
2. Check server console for error messages
3. Run individual tests using curl for isolation
4. Review code in routes.js for reminder endpoints

### If Tests Can't Run (Blockers)
1. Run health_check.js to identify issue
2. Fix environmental issues first
3. Then retry tests

---

## Performance Notes

- WebSocket events emit within 100-500ms typically
- Tests use 5-second timeout to be generous
- Database queries should complete in <100ms for typical dataset
- Encryption/decryption adds ~10-20ms per reminder

---

## Cleanup

Test reminders created during testing remain in the database. To clean up:

```bash
mysql -u root pcconnect -e "DELETE FROM reminders WHERE UserID = 1 AND Reminder LIKE 'Test Reminder%';"
```

Or keep them for manual verification with:
```bash
curl -H 'X-API-Key: 51359fd1b13802000649a6bd2f3f10ba' \
  http://localhost:3000/api_node/v1/reminders
```

---

## Architecture Overview

```
                    Client 1
                       |
                       | WebSocket
                       |
┌──────────────────────┴──────────────────────────┐
│         Node.js API Server (Port 3000)          │
├───────────────────────────────────────────────────┤
│  Express.js                  Socket.io            │
│  - REST Endpoints           - Real-time Events   │
│  - API Authentication       - Room Management    │
│  - Data Validation          - Event Broadcasting │
└───────────────────────┬───────────────────────────┘
                        |
                        | MySQL Protocol
                        |
                   ┌────▼─────┐
                   │  MySQL   │
                   │ Database │
                   │pcconnect │
                   └──────────┘
```

### Data Flow

**Create Reminder**:
```
Client 1 → REST API → Validate & Encrypt → Database
                                           ↓
                                    Room: user_1
                                           ↓
                         Client 2 (WebSocket) ← Broadcast
```

---

## For Developers

### Key Files to Review

- `server.js` - WebSocket setup and authentication (lines 45-101)
- `routes.js` - REST endpoints and real-time emission (lines 177-289)
- `helpers.js` - Encryption/decryption and database queries
- `db.js` - Database pool configuration

### Important Code Paths

1. **WebSocket Authentication** → server.js line 49
2. **Reminder Creation** → routes.js line 177
3. **Real-Time Broadcast** → routes.js line 201 (emitReminderUpdate)
4. **Encryption** → helpers.js line 90 (encryptString)
5. **Decryption** → helpers.js line 71 (decryptString)

---

**Status**: ✓ Ready for manual testing
**Created**: April 2025
**Test Coverage**: 9 core scenarios covering WebSocket, REST, and real-time integration
