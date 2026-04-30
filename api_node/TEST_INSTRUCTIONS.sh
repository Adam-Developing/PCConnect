#!/bin/bash
# Manual Test Execution Guide for PCConnect Realtime Reminder Backend
# Location: C:\Users\Adam\Documents\Filen\Projects\PCConnect\api_node

# ENVIRONMENT SETUP
echo "=== PCConnect Realtime Reminder Backend Manual Test ==="
echo ""
echo "Prerequisites:"
echo "1. MySQL/MariaDB running with pcconnect database"
echo "2. Database config: localhost, root user (no password assumed)"
echo "3. Node.js v14+ installed"
echo "4. Test uses admin user: api_key=51359fd1b13802000649a6bd2f3f10ba"
echo ""

# STEP 1: Install dependencies if needed
echo "Step 1: Installing dependencies..."
cd C:\Users\Adam\Documents\Filen\Projects\PCConnect\api_node
npm install socket.io-client axios

# STEP 2: Start the server
echo ""
echo "Step 2: Starting Node.js server on port 3000..."
echo "Command: node server.js"
echo ""
echo "IMPORTANT: Keep this server running in a separate terminal!"
echo "Then proceed to Step 3 in another terminal."
echo ""

# STEP 3: Run the test suite (in separate terminal)
echo "Step 3: Running the test suite..."
echo "Command: node test_reminders.js"
echo ""
echo "Expected output:"
echo "- Pass/Fail results for each test case"
echo "- Actual reminder IDs created during tests"
echo "- WebSocket event confirmations"
echo "- Final summary showing pass/fail count"
echo ""

# ALTERNATIVE: Manual curl tests for REST endpoints
echo "=== Alternative: Manual REST Endpoint Tests ==="
echo ""
echo "Test 1: Get existing reminders"
echo "curl -H 'X-API-Key: 51359fd1b13802000649a6bd2f3f10ba' \\"
echo "  http://localhost:3000/api_node/v1/reminders"
echo ""

TODAY=$(date +%Y-%m-%d)
echo "Test 2: Create a reminder (requires valid date format)"
echo "curl -X POST -H 'X-API-Key: 51359fd1b13802000649a6bd2f3f10ba' \\"
echo "  -H 'Content-Type: application/json' \\"
echo "  -d '{\"date\":\"$TODAY\",\"time\":\"14:30\",\"reminder\":\"Test reminder\"}' \\"
echo "  http://localhost:3000/api_node/v1/reminders"
echo ""

echo "Test 3: Update a reminder (replace REMINDER_ID)"
echo "curl -X PUT -H 'X-API-Key: 51359fd1b13802000649a6bd2f3f10ba' \\"
echo "  -H 'Content-Type: application/json' \\"
echo "  -d '{\"reminder\":\"Updated reminder text\",\"time\":\"15:45\"}' \\"
echo "  http://localhost:3000/api_node/v1/reminders/REMINDER_ID"
echo ""

echo "Test 4: Mark reminder as complete (replace REMINDER_ID)"
echo "curl -X POST -H 'X-API-Key: 51359fd1b13802000649a6bd2f3f10ba' \\"
echo "  -H 'Content-Type: application/json' \\"
echo "  -d '{\"completed\":1}' \\"
echo "  http://localhost:3000/api_node/v1/reminders/REMINDER_ID/complete"
echo ""

echo "=== WebSocket Manual Test (using Node.js REPL) ==="
echo ""
echo "In a separate terminal, run: node"
echo "Then paste the following code:"
echo ""
echo "const io = require('socket.io-client');"
echo "const socket = io('http://localhost:3000');"
echo ""
echo "socket.on('connect', () => {"
echo "  console.log('Connected!');"
echo "  socket.emit('authenticate', {"
echo "    apiKey: '51359fd1b13802000649a6bd2f3f10ba',"
echo "    pcName: 'TEST_PC_MANUAL'"
echo "  });"
echo "});"
echo ""
echo "socket.on('authenticated', (data) => {"
echo "  console.log('Authenticated:', data);"
echo "});"
echo ""
echo "socket.on('reminders_initial', (data) => {"
echo "  console.log('Initial reminders:', data.reminders);"
echo "});"
echo ""
echo "socket.on('reminder_update', (data) => {"
echo "  console.log('Reminder update received:', data);"
echo "});"
echo ""
echo "socket.on('auth_error', (data) => {"
echo "  console.log('Auth error:', data);"
echo "});"
echo ""
