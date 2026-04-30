/**
 * Manual Test Suite for Realtime Reminder Backend
 * Tests WebSocket authentication, reminder events, and REST endpoints
 */

const io = require('socket.io-client');
const axios = require('axios');

// Configuration
const TEST_CONFIG = {
    // Test server details
    serverUrl: 'http://localhost:3000',
    wsUrl: 'http://localhost:3000',
    
    // Known test user from database (admin user)
    testUser: {
        apiKey: '51359fd1b13802000649a6bd2f3f10ba', // admin user from pcconnect_new.sql
        username: 'admin',
        pcName: 'TEST_PC_' + Date.now()
    },
    
    // Test reminder payloads
    testReminders: {
        create: {
            reminder: 'Test Reminder Created',
            date: new Date().toISOString().split('T')[0], // Today
            time: '14:30' // 2:30 PM
        },
        update: {
            reminder: 'Test Reminder Updated',
            time: '15:45' // 3:45 PM
        },
        complete: {
            completed: 1
        }
    }
};

// Test Results Tracker
const results = {
    passed: [],
    failed: [],
    blockers: []
};

function log(message, type = 'info') {
    const prefix = {
        'pass': '✓ PASS',
        'fail': '✗ FAIL',
        'info': 'ℹ INFO',
        'test': '━━ TEST',
        'error': '✗ ERROR'
    }[type] || 'ℹ';
    console.log(`[${prefix}] ${message}`);
}

function recordResult(testName, passed, details = '') {
    if (passed) {
        results.passed.push(testName);
        log(testName, 'pass');
    } else {
        results.failed.push(testName);
        log(testName + (details ? ` - ${details}` : ''), 'fail');
    }
}

function recordBlocker(testName, blocker) {
    results.blockers.push({ test: testName, blocker });
    log(`${testName} BLOCKED: ${blocker}`, 'error');
}

/**
 * TEST 1: Server connectivity
 */
async function testServerConnectivity() {
    log('Testing server connectivity...', 'test');
    try {
        const response = await axios.get(`${TEST_CONFIG.serverUrl}/ping`);
        recordResult('Server connectivity', response.status === 200 && response.data === 'Node Gateway Active');
    } catch (e) {
        recordBlocker('Server connectivity', `Cannot connect to ${TEST_CONFIG.serverUrl}: ${e.message}`);
        throw e;
    }
}

/**
 * TEST 2: REST login and API key validation
 */
async function testRestApiKey() {
    log('Testing REST API key validation...', 'test');
    try {
        // Try to get reminders without API key (should fail)
        try {
            await axios.get(`${TEST_CONFIG.serverUrl}/api_node/v1/reminders`);
            recordResult('REST missing API key rejection', false, 'Should have rejected request without API key');
        } catch (e) {
            if (e.response && e.response.status === 401) {
                recordResult('REST missing API key rejection', true);
            } else {
                recordResult('REST missing API key rejection', false, `Got ${e.response?.status || 'unknown'} status`);
            }
        }

        // Try with valid API key
        const response = await axios.get(`${TEST_CONFIG.serverUrl}/api_node/v1/reminders`, {
            headers: { 'X-API-Key': TEST_CONFIG.testUser.apiKey }
        });
        recordResult('REST API key validation', response.status === 200, `Got reminders: ${response.data.length || 0} existing`);
    } catch (e) {
        recordBlocker('REST API key validation', `Cannot access REST API: ${e.message}`);
        throw e;
    }
}

/**
 * TEST 3: WebSocket authenticate emits reminders_initial
 */
async function testWebSocketAuthAndInitialReminders() {
    log('Testing WebSocket authenticate and reminders_initial event...', 'test');
    
    return new Promise((resolve) => {
        const socket = io(TEST_CONFIG.wsUrl);
        let remindersInitialReceived = false;
        let authenticatedReceived = false;
        let receivedReminders = [];

        const timeout = setTimeout(() => {
            socket.disconnect();
            recordResult('WebSocket reminders_initial event', false, 'Timeout waiting for reminders_initial');
            resolve();
        }, 5000);

        socket.on('connect', () => {
            log('  → WebSocket connected', 'info');
            socket.emit('authenticate', {
                apiKey: TEST_CONFIG.testUser.apiKey,
                pcName: TEST_CONFIG.testUser.pcName
            });
        });

        socket.on('authenticated', (data) => {
            authenticatedReceived = true;
            log(`  → authenticated event received: ${data.message}`, 'info');
        });

        socket.on('reminders_initial', (data) => {
            remindersInitialReceived = true;
            receivedReminders = data.reminders || [];
            log(`  → reminders_initial event received with ${receivedReminders.length} reminders`, 'info');
            
            if (receivedReminders.length > 0) {
                log(`  → Sample reminder: ${JSON.stringify(receivedReminders[0])}`, 'info');
            }
            
            clearTimeout(timeout);
            socket.disconnect();
            
            recordResult('WebSocket authenticated event', authenticatedReceived);
            recordResult('WebSocket reminders_initial event', remindersInitialReceived);
            resolve(receivedReminders);
        });

        socket.on('auth_error', (data) => {
            clearTimeout(timeout);
            socket.disconnect();
            recordBlocker('WebSocket authentication', `auth_error: ${data.message}`);
            resolve([]);
        });

        socket.on('error', (error) => {
            clearTimeout(timeout);
            socket.disconnect();
            recordBlocker('WebSocket connection', `Socket error: ${error}`);
            resolve([]);
        });
    });
}

/**
 * TEST 4: REST create reminder
 */
async function testRestCreateReminder() {
    log('Testing REST create reminder endpoint...', 'test');
    try {
        const payload = TEST_CONFIG.testReminders.create;
        const response = await axios.post(
            `${TEST_CONFIG.serverUrl}/api_node/v1/reminders`,
            payload,
            { headers: { 'X-API-Key': TEST_CONFIG.testUser.apiKey } }
        );
        
        if (response.status === 201 && response.data.data && response.data.data.id) {
            recordResult('REST create reminder', true, `Created reminder ID: ${response.data.data.id}`);
            return response.data.data.id;
        } else {
            recordResult('REST create reminder', false, `Unexpected response: ${JSON.stringify(response.data)}`);
            return null;
        }
    } catch (e) {
        recordResult('REST create reminder', false, `${e.response?.status || 'Error'}: ${e.response?.data?.message || e.message}`);
        return null;
    }
}

/**
 * TEST 5: WebSocket receives reminder_update on create
 */
async function testWebSocketReminderUpdateOnCreate(reminderId) {
    if (!reminderId) {
        recordResult('WebSocket reminder_update on create', false, 'No reminder ID from create test');
        return;
    }

    log('Testing WebSocket reminder_update event on create...', 'test');
    
    return new Promise((resolve) => {
        const socket = io(TEST_CONFIG.wsUrl);
        let reminderUpdateReceived = false;

        const timeout = setTimeout(() => {
            socket.disconnect();
            recordResult('WebSocket reminder_update on create', false, 'Timeout waiting for reminder_update');
            resolve();
        }, 5000);

        socket.on('connect', () => {
            socket.emit('authenticate', {
                apiKey: TEST_CONFIG.testUser.apiKey,
                pcName: TEST_CONFIG.testUser.pcName
            });
        });

        socket.on('authenticated', () => {
            // Now create a reminder - should trigger reminder_update
            setTimeout(() => {
                axios.post(
                    `${TEST_CONFIG.serverUrl}/api_node/v1/reminders`,
                    TEST_CONFIG.testReminders.create,
                    { headers: { 'X-API-Key': TEST_CONFIG.testUser.apiKey } }
                ).catch(() => {});
            }, 200);
        });

        socket.on('reminder_update', (data) => {
            reminderUpdateReceived = true;
            log(`  → reminder_update event received: type=${data.type}, has_reminder=${!!data.reminder}`, 'info');
            clearTimeout(timeout);
            socket.disconnect();
            recordResult('WebSocket reminder_update on create', true, `Type: ${data.type}`);
            resolve();
        });

        socket.on('auth_error', (data) => {
            clearTimeout(timeout);
            socket.disconnect();
            recordResult('WebSocket reminder_update on create', false, `Auth failed: ${data.message}`);
            resolve();
        });
    });
}

/**
 * TEST 6: REST update reminder
 */
async function testRestUpdateReminder(reminderId) {
    if (!reminderId) {
        recordResult('REST update reminder', false, 'No reminder ID from create test');
        return;
    }

    log('Testing REST update reminder endpoint...', 'test');
    try {
        const payload = TEST_CONFIG.testReminders.update;
        const response = await axios.put(
            `${TEST_CONFIG.serverUrl}/api_node/v1/reminders/${reminderId}`,
            payload,
            { headers: { 'X-API-Key': TEST_CONFIG.testUser.apiKey } }
        );
        
        if (response.status === 200 && response.data.success) {
            recordResult('REST update reminder', true, `Updated reminder ID: ${reminderId}`);
            return true;
        } else {
            recordResult('REST update reminder', false, `Unexpected response: ${JSON.stringify(response.data)}`);
            return false;
        }
    } catch (e) {
        recordResult('REST update reminder', false, `${e.response?.status || 'Error'}: ${e.response?.data?.message || e.message}`);
        return false;
    }
}

/**
 * TEST 7: WebSocket receives reminder_update on update
 */
async function testWebSocketReminderUpdateOnUpdate(reminderId) {
    if (!reminderId) {
        recordResult('WebSocket reminder_update on update', false, 'No reminder ID from create test');
        return;
    }

    log('Testing WebSocket reminder_update event on update...', 'test');
    
    return new Promise((resolve) => {
        const socket = io(TEST_CONFIG.wsUrl);
        let reminderUpdateReceived = false;

        const timeout = setTimeout(() => {
            socket.disconnect();
            recordResult('WebSocket reminder_update on update', false, 'Timeout waiting for reminder_update');
            resolve();
        }, 5000);

        socket.on('connect', () => {
            socket.emit('authenticate', {
                apiKey: TEST_CONFIG.testUser.apiKey,
                pcName: TEST_CONFIG.testUser.pcName
            });
        });

        socket.on('authenticated', () => {
            // Now update the reminder - should trigger reminder_update
            setTimeout(() => {
                axios.put(
                    `${TEST_CONFIG.serverUrl}/api_node/v1/reminders/${reminderId}`,
                    TEST_CONFIG.testReminders.update,
                    { headers: { 'X-API-Key': TEST_CONFIG.testUser.apiKey } }
                ).catch(() => {});
            }, 200);
        });

        socket.on('reminder_update', (data) => {
            reminderUpdateReceived = true;
            log(`  → reminder_update event received: type=${data.type}, has_reminder=${!!data.reminder}`, 'info');
            clearTimeout(timeout);
            socket.disconnect();
            recordResult('WebSocket reminder_update on update', true, `Type: ${data.type}`);
            resolve();
        });

        socket.on('auth_error', (data) => {
            clearTimeout(timeout);
            socket.disconnect();
            recordResult('WebSocket reminder_update on update', false, `Auth failed: ${data.message}`);
            resolve();
        });
    });
}

/**
 * TEST 8: REST complete reminder
 */
async function testRestCompleteReminder(reminderId) {
    if (!reminderId) {
        recordResult('REST complete reminder', false, 'No reminder ID from create test');
        return;
    }

    log('Testing REST complete reminder endpoint...', 'test');
    try {
        const payload = TEST_CONFIG.testReminders.complete;
        const response = await axios.post(
            `${TEST_CONFIG.serverUrl}/api_node/v1/reminders/${reminderId}/complete`,
            payload,
            { headers: { 'X-API-Key': TEST_CONFIG.testUser.apiKey } }
        );
        
        if (response.status === 200 && response.data.success) {
            recordResult('REST complete reminder', true, `Completed reminder ID: ${reminderId}`);
            return true;
        } else {
            recordResult('REST complete reminder', false, `Unexpected response: ${JSON.stringify(response.data)}`);
            return false;
        }
    } catch (e) {
        recordResult('REST complete reminder', false, `${e.response?.status || 'Error'}: ${e.response?.data?.message || e.message}`);
        return false;
    }
}

/**
 * TEST 9: WebSocket receives reminder_update on complete
 */
async function testWebSocketReminderUpdateOnComplete(reminderId) {
    if (!reminderId) {
        recordResult('WebSocket reminder_update on complete', false, 'No reminder ID from create test');
        return;
    }

    log('Testing WebSocket reminder_update event on complete...', 'test');
    
    return new Promise((resolve) => {
        const socket = io(TEST_CONFIG.wsUrl);
        let reminderUpdateReceived = false;

        const timeout = setTimeout(() => {
            socket.disconnect();
            recordResult('WebSocket reminder_update on complete', false, 'Timeout waiting for reminder_update');
            resolve();
        }, 5000);

        socket.on('connect', () => {
            socket.emit('authenticate', {
                apiKey: TEST_CONFIG.testUser.apiKey,
                pcName: TEST_CONFIG.testUser.pcName
            });
        });

        socket.on('authenticated', () => {
            // Now complete the reminder - should trigger reminder_update
            setTimeout(() => {
                axios.post(
                    `${TEST_CONFIG.serverUrl}/api_node/v1/reminders/${reminderId}/complete`,
                    TEST_CONFIG.testReminders.complete,
                    { headers: { 'X-API-Key': TEST_CONFIG.testUser.apiKey } }
                ).catch(() => {});
            }, 200);
        });

        socket.on('reminder_update', (data) => {
            reminderUpdateReceived = true;
            log(`  → reminder_update event received: type=${data.type}, completed=${data.reminder?.Completed}`, 'info');
            clearTimeout(timeout);
            socket.disconnect();
            recordResult('WebSocket reminder_update on complete', true, `Type: ${data.type}`);
            resolve();
        });

        socket.on('auth_error', (data) => {
            clearTimeout(timeout);
            socket.disconnect();
            recordResult('WebSocket reminder_update on complete', false, `Auth failed: ${data.message}`);
            resolve();
        });
    });
}

/**
 * Run all tests in sequence
 */
async function runTests() {
    console.log('\n╔═══════════════════════════════════════════════════════════════╗');
    console.log('║  PCConnect Realtime Reminder Backend - Manual Test Suite      ║');
    console.log('╚═══════════════════════════════════════════════════════════════╝\n');

    try {
        // Test connectivity first
        await testServerConnectivity();
        
        // Test REST API
        await testRestApiKey();
        
        // Test WebSocket basic flow
        await testWebSocketAuthAndInitialReminders();
        
        // Test create reminder
        const reminderId = await testRestCreateReminder();
        
        // Test WebSocket update events
        await testWebSocketReminderUpdateOnCreate(reminderId);
        
        // Test update reminder
        await testRestUpdateReminder(reminderId);
        
        // Test WebSocket update event
        await testWebSocketReminderUpdateOnUpdate(reminderId);
        
        // Test complete reminder
        await testRestCompleteReminder(reminderId);
        
        // Test WebSocket update event on complete
        await testWebSocketReminderUpdateOnComplete(reminderId);

    } catch (e) {
        if (e.message && !e.message.includes('blocked')) {
            log(`Fatal error: ${e.message}`, 'error');
        }
    }

    // Print summary
    console.log('\n╔═══════════════════════════════════════════════════════════════╗');
    console.log('║  Test Summary                                                 ║');
    console.log('╚═══════════════════════════════════════════════════════════════╝\n');

    if (results.blockers.length > 0) {
        console.log(`❌ BLOCKERS (${results.blockers.length}):`);
        results.blockers.forEach(b => {
            console.log(`  • [${b.test}] ${b.blocker}`);
        });
        console.log();
    }

    console.log(`✓ Passed: ${results.passed.length}`);
    results.passed.forEach(t => console.log(`  • ${t}`));

    if (results.failed.length > 0) {
        console.log(`\n✗ Failed: ${results.failed.length}`);
        results.failed.forEach(t => console.log(`  • ${t}`));
    }

    console.log();
    const total = results.passed.length + results.failed.length + (results.blockers.length ? 0 : 0);
    const successRate = total > 0 ? ((results.passed.length / total) * 100).toFixed(0) : 0;
    console.log(`Summary: ${results.passed.length}/${total} tests passed (${successRate}%)\n`);

    process.exit(results.failed.length > 0 || results.blockers.length > 0 ? 1 : 0);
}

// Run tests
runTests().catch(e => {
    console.error('Test suite crashed:', e);
    process.exit(1);
});
