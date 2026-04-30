async function test(name, method, p, headers={}, body=null) {
    console.log(`\n=== Testing ${name} ===`);
    try {
        const url = `http://localhost:3000${p}`;
        const ops = { method, headers: {'Content-Type': 'application/json', ...headers} };
        if (body) ops.body = JSON.stringify(body);
        const res = await fetch(url, ops);
        console.log(`Status: ${res.status}`);
        const t = await res.text();
        console.log(t);
    } catch(e){ console.error(e.message); }
}

async function run(){
    await test('Login - Valid', 'POST', '/api_node/auth/login', {}, {loginUsername: 'admin', loginPassword: 'password'});
    await test('Login - SQLi', 'POST', '/api_node/auth/login', {}, {loginUsername: "' OR 1=1--", loginPassword: 'password'});
    
    await test('Devices - Missing headers', 'GET', '/api_node/v1/devices');
    await test('Devices - Valid', 'GET', '/api_node/v1/devices', {'X-API-Key': 'key123'});
    
    await test('Devices - Add', 'POST', '/api_node/v1/devices', {'X-API-Key': 'key123', 'PCName': 'PC1'});
    
    await test('Get Internet', 'GET', '/api_node/v1/system/checkinternet');

    await test('Requests GET', 'GET', '/api_node/v1/devices/requests', {'X-API-Key': 'key', 'PCName': 'p'});
    await test('Requests CLEAR', 'POST', '/api_node/v1/devices/requests/clear', {'X-API-Key': 'key', 'PCName': 'p'});

    await test('Exchange Req - SQLi/XSS', 'POST', '/api_node/v1/devices/requests/exchange', {'X-API-Key': 'key', 'PCName': 'p'}, {Request: '<script>alert()</script>Shut_Down'});
    await test('Exchange Req - Valid', 'POST', '/api_node/v1/devices/requests/exchange', {'X-API-Key': 'key', 'PCName': 'p'}, {Request: 'Shut_Down'});
    await test('Exchange Req - Missing', 'POST', '/api_node/v1/devices/requests/exchange', {'X-API-Key': 'key', 'PCName': 'p'}, {});
    
    await test('Reminders GET', 'GET', '/api_node/v1/reminders', {'X-API-Key': 'key'});
    await test('Reminders POST valid', 'POST', '/api_node/v1/reminders', {'X-API-Key': 'key'}, {date: '2023-10-10', time: '14:00', reminder: 'Test'});
    await test('Reminders POST missing', 'POST', '/api_node/v1/reminders', {'X-API-Key': 'key'}, {date: '2023-10-10'});
}
run();
