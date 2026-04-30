# PCConnect Realtime Reminder Backend - Manual Test Report

**Date**: Run on demand
**Tester**: Manual execution
**Environment**: Windows, local Node.js server on port 3000
**Database**: MySQL/MariaDB (pcconnect database)

---

## Executive Summary

This document provides a complete manual test suite for the realtime reminder backend implementation in `api_node`. The tests verify end-to-end functionality for:

1. ✓ WebSocket authentication emitting `reminders_initial` with current reminders
2. ✓ REST endpoints for creating, updating, and completing reminders
3. ✓ Real-time event emission to connected WebSocket clients

---

## Test Prerequisites

### Database Configuration
- **Location**: `C:\Users\Adam\Documents\Filen\Projects\PCConnect\DB\pcconnect_new.sql`
- **Host**: localhost
- **User**: root
- **Database**: pcconnect
- **Config file**: `api_node/config.json`

### Test User (from database)
```
Username: admin
API Key: 51359fd1b13802000649a6bd2f3f10ba
User ID: 1
```

### Required Packages
```bash
npm install socket.io-client axios  # For test_reminders.js
```

---

## Test Suite Overview

### Test Script Location
`C:\Users\Adam\Documents\Filen\Projects\PCConnect\api_node\test_reminders.js`

### Test Cases

#### 1. Server Connectivity Test
- **Purpose**: Verify Node.js server is running
- **Endpoint**: `GET /ping`
- **Expected**: HTTP 200, response: "Node Gateway Active"
- **Pass Criteria**: Successful response

#### 2. REST API Key Validation
- **Purpose**: Verify API key authentication on REST endpoints
- **Tests**:
  - Missing API key returns HTTP 401
  - Valid API key allows access
- **Endpoint**: `GET /api_node/v1/reminders`
- **Pass Criteria**: Both sub-tests pass

#### 3. WebSocket Authenticate & reminders_initial Event
- **Purpose**: Verify WebSocket authentication and initial reminder list emission
- **Flow**:
  1. Connect to WebSocket
  2. Emit `authenticate` event with apiKey and pcName
  3. Receive `authenticated` event
  4. Receive `reminders_initial` event with current reminders array
- **Pass Criteria**: Both events received within 5 seconds
- **Payload sent**:
  ```json
  {
    "apiKey": "51359fd1b13802000649a6bd2f3f10ba",
    "pcName": "TEST_PC_<timestamp>"
  }
  ```

#### 4. REST Create Reminder
- **Purpose**: Create a new reminder via REST API
- **Endpoint**: `POST /api_node/v1/reminders`
- **Payload**:
  ```json
  {
    "reminder": "Test Reminder Created",
    "date": "YYYY-MM-DD",
    "time": "14:30"
  }
  ```
- **Expected Response**: HTTP 201
  ```json
  {
    "success": true,
    "data": {
      "message": "Reminder created",
      "id": <reminder_id>
    }
  }
  ```
- **Pass Criteria**: Returns 201 with reminder ID

#### 5. WebSocket reminder_update on Create
- **Purpose**: Verify real-time notification when reminder is created
- **Flow**:
  1. Connect WebSocket and authenticate
  2. Create reminder via REST API
  3. Receive `reminder_update` event with type="created"
- **Expected Event**:
  ```json
  {
    "type": "created",
    "reminder": { <reminder_object> },
    "reminders": [ <all_user_reminders> ]
  }
  ```
- **Pass Criteria**: Event received within 5 seconds of creation

#### 6. REST Update Reminder
- **Purpose**: Update an existing reminder
- **Endpoint**: `PUT /api_node/v1/reminders/:id`
- **Payload**:
  ```json
  {
    "reminder": "Test Reminder Updated",
    "time": "15:45"
  }
  ```
- **Expected Response**: HTTP 200
  ```json
  {
    "success": true,
    "data": {
      "message": "Reminder updated",
      "id": <reminder_id>
    }
  }
  ```
- **Pass Criteria**: Returns 200 with updated reminder ID

#### 7. WebSocket reminder_update on Update
- **Purpose**: Verify real-time notification when reminder is updated
- **Flow**:
  1. Connect WebSocket and authenticate
  2. Update reminder via REST API
  3. Receive `reminder_update` event with type="updated"
- **Pass Criteria**: Event received within 5 seconds of update

#### 8. REST Complete Reminder
- **Purpose**: Mark reminder as complete
- **Endpoint**: `POST /api_node/v1/reminders/:id/complete`
- **Payload**:
  ```json
  {
    "completed": 1
  }
  ```
- **Expected Response**: HTTP 200
  ```json
  {
    "success": true,
    "data": {
      "message": "Reminder completion updated",
      "id": <reminder_id>,
      "completed": 1
    }
  }
  ```
- **Pass Criteria**: Returns 200

#### 9. WebSocket reminder_update on Complete
- **Purpose**: Verify real-time notification when reminder is marked complete
- **Flow**:
  1. Connect WebSocket and authenticate
  2. Complete reminder via REST API
  3. Receive `reminder_update` event with type="updated"
- **Pass Criteria**: Event received within 5 seconds

---

## Running the Tests

### Method 1: Automated Test Suite

```bash
# Terminal 1: Start the server
cd C:\Users\Adam\Documents\Filen\Projects\PCConnect\api_node
node server.js

# Terminal 2: Run the test suite
cd C:\Users\Adam\Documents\Filen\Projects\PCConnect\api_node
npm install socket.io-client axios  # (if not already installed)
node test_reminders.js
```

**Expected Output**:
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

### Method 2: Manual REST Tests (curl)

```bash
# Test 1: Get existing reminders
curl -H 'X-API-Key: 51359fd1b13802000649a6bd2f3f10ba' \
  http://localhost:3000/api_node/v1/reminders

# Test 2: Create a reminder
CURRENT_DATE=$(date +%Y-%m-%d)
curl -X POST -H 'X-API-Key: 51359fd1b13802000649a6bd2f3f10ba' \
  -H 'Content-Type: application/json' \
  -d "{\"date\":\"$CURRENT_DATE\",\"time\":\"14:30\",\"reminder\":\"Test reminder\"}" \
  http://localhost:3000/api_node/v1/reminders

# Test 3: Update a reminder (replace 1 with actual ID from create)
curl -X PUT -H 'X-API-Key: 51359fd1b13802000649a6bd2f3f10ba' \
  -H 'Content-Type: application/json' \
  -d '{"reminder":"Updated reminder text","time":"15:45"}' \
  http://localhost:3000/api_node/v1/reminders/1

# Test 4: Mark as complete (replace 1 with actual ID)
curl -X POST -H 'X-API-Key: 51359fd1b13802000649a6bd2f3f10ba' \
  -H 'Content-Type: application/json' \
  -d '{"completed":1}' \
  http://localhost:3000/api_node/v1/reminders/1/complete
```

### Method 3: Manual WebSocket Test

```bash
# Terminal 1: Start server
cd C:\Users\Adam\Documents\Filen\Projects\PCConnect\api_node
node server.js

# Terminal 2: Open Node REPL and paste this code
node

# Inside Node REPL:
const io = require('socket.io-client');
const socket = io('http://localhost:3000');

socket.on('connect', () => {
  console.log('Connected!');
  socket.emit('authenticate', {
    apiKey: '51359fd1b13802000649a6bd2f3f10ba',
    pcName: 'TEST_PC_MANUAL'
  });
});

socket.on('authenticated', (data) => {
  console.log('✓ Authenticated:', data.message);
});

socket.on('reminders_initial', (data) => {
  console.log('✓ Reminders initial:', data.reminders.length, 'reminders');
  data.reminders.forEach(r => {
    console.log(`  - [${r.ID}] ${r.Reminder} on ${r.Date} at ${r.Time} (Completed: ${r.Completed})`);
  });
});

socket.on('reminder_update', (data) => {
  console.log('✓ Reminder update:', data.type, data.reminder?.ID || 'all');
});

socket.on('auth_error', (data) => {
  console.log('✗ Auth error:', data.message);
  process.exit(1);
});

// Leave connected to test real-time updates
```

---

## Payload Examples

### Reminder Object (from API)
```json
{
  "ID": 1,
  "Username": 1,
  "Date": "16/04/25",
  "Time": "14:30:00",
  "Reminder": "Go to meeting",
  "Completed": 0
}
```

### CREATE Payload
```json
{
  "reminder": "Test Reminder Created",
  "date": "2025-04-16",
  "time": "14:30"
}
```

Supported time formats:
- `14:30` (24-hour)
- `14:30:45` (24-hour with seconds)
- `2:30 PM` (12-hour)
- `2:30 AM` (12-hour)

### UPDATE Payload
```json
{
  "reminder": "Updated text",
  "time": "15:45",
  "date": "2025-04-16",
  "completed": 0
}
```
(All fields optional - only provided fields are updated)

### COMPLETE Payload
```json
{
  "completed": 1
}
```
(0 = incomplete, 1 = complete)

---

## Key Code Paths

### WebSocket Authentication Flow
**File**: `server.js` (lines 45-101)
- Socket connects and receives `authenticate` event
- Validates API key against database
- Resolves PC name to PCID (creates if new)
- Joins rooms: `user_{userId}` and `user_{userId}_pc_{pcId}`
- Emits `authenticated` event
- Fetches and emits `reminders_initial` event

### REST Reminder Endpoints
**File**: `routes.js`

**Create** (lines 177-206):
- `POST /v1/reminders`
- Validates date/time format
- Encrypts reminder text with user's API key
- Inserts into database
- Calls `emitReminderUpdate()` to notify connected WebSockets

**Update** (lines 208-265):
- `PUT /v1/reminders/:id`
- Validates and encrypts updated fields
- Updates database
- Calls `emitReminderUpdate()` to notify

**Complete** (lines 267-289):
- `POST /v1/reminders/:id/complete`
- Sets Completed field
- Calls `emitReminderUpdate()` to notify

### Real-Time Push Manager
**File**: `server.js` (lines 21-31)
- `PushManager.pushCommand()` - sends execute_command events
- `PushManager.pushReminderUpdate()` - sends reminder_update events to room `user_{userId}`

---

## Expected Test Results

### Success Criteria
- [ ] 11 tests pass (100%)
- [ ] WebSocket connects without auth errors
- [ ] All REST endpoints return expected status codes
- [ ] Real-time events arrive within 5 seconds
- [ ] Created reminders have proper ID and structure

### Common Issues & Troubleshooting

#### Database Connection Error
**Symptom**: "Database error" or "Connection timeout"
**Cause**: MySQL not running or config.json incorrect
**Solution**: 
1. Verify MySQL is running
2. Check `config.json` host/user/database settings
3. Confirm database `pcconnect` exists

#### WebSocket Connection Timeout
**Symptom**: Test hangs, no authenticated/auth_error events
**Cause**: Server not running or CORS issue
**Solution**:
1. Verify server is running on port 3000
2. Check firewall allows port 3000
3. Look for errors in server terminal

#### API Key Validation Fails
**Symptom**: "Invalid API key" error
**Cause**: User not found or API key incorrect
**Solution**:
1. Verify user ID 1 exists in database
2. Confirm API key is `51359fd1b13802000649a6bd2f3f10ba`
3. Check user.api_key field is not NULL

#### Missing Reminders in reminders_initial
**Symptom**: reminders_initial received but empty array
**Cause**: No reminders exist for user or decryption issue
**Solution**:
1. This is normal if no reminders exist
2. Run create test first to generate sample data
3. Check reminder Completed field (should fetch all states)

---

## Files Created for Testing

- `test_reminders.js` - Automated test suite (18KB)
- `TEST_INSTRUCTIONS.sh` - Command reference guide
- `MANUAL_TEST_REPORT.md` - This file

---

## Cleanup

After testing, created reminders will remain in the database. To clean up:

```sql
-- Delete test reminders created during tests
DELETE FROM reminders WHERE UserID = 1 AND Reminder LIKE 'Test Reminder%';
```

Or delete reminders through the REST API (implement a DELETE endpoint if needed).

---

## Verification Checklist

After running tests, verify:
- [ ] Server console shows: `[PCConnect] Unified Gateway + WebSockets running on port 3000`
- [ ] Socket connections logged: `Socket Connected: <socket.id>`
- [ ] Authentication logged: `PC [<pcname>] joined room [user_1_pc_<pcid>]`
- [ ] Reminder updates logged: `[PUSH] Emitting reminder update to room [user_1]`
- [ ] No database errors in console
- [ ] All test assertions passed

---

## Code Quality Notes

- ✓ Reminder text encrypted with AES-256-CBC (matches PHP implementation)
- ✓ Date format validation (YYYY-MM-DD)
- ✓ Time format validation (24-hour and 12-hour)
- ✓ WebSocket rooms isolated per user and PC
- ✓ API key authentication on all endpoints
- ✓ Proper HTTP status codes (201 for create, 200 for update, 404 for not found)
- ✓ Graceful disconnection handling

---

## Notes for Developer

1. **Time Zone Handling**: Times stored in DB as HH:MM:SS local time (no timezone conversion)
2. **Date Format**: Dates stored as YYYY-MM-DD, displayed to client as DD/MM/YY
3. **Encryption Key**: Uses full API key (32 bytes for AES-256) directly as encryption key
4. **Room Isolation**: Each user has own `user_{id}` room, each PC has own `user_{id}_pc_{pc_id}` room
5. **Event Payload**: `reminder_update` includes both changed reminder and full reminder list
6. **WebSocket Join**: Client only joins room on successful authentication
7. **PC Auto-creation**: New PC names automatically created in pcnames table on first connection

---

**Last Updated**: Test suite created for manual execution
**Status**: Ready for testing
