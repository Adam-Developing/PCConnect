const http = require('http');

async function test(name, method, p, headers={}, body=null) {
    console.log(\n=== Testing  ===);
    try {
        const res = await fetch(http://localhost:3000, { method, headers: {'Content-Type': 'application/json', ...headers}, body: body ? JSON.stringify(body) : undefined });
        console.log(Status: );
        const t = await res.text();
        console.log(t);
    } catch(e){ console.error(e.message); }
}

async function run(){
    await test('Login', 'POST', '/api_node/auth/login', {}, {loginUsername: 'admin', loginPassword: 'password'});
    await test('Login - SQLi', 'POST', '/api_node/auth/login', {}, {loginUsername: '\\' OR 1=1--', loginPassword: ''});
    

    await test('Devices - Missing', 'GET', '/api_node/v1/devices');
    await test('Devices - Valid', 'GET', '/api_node/v1/devices', {'X-API-Key': 'key123'});
    
    await test('Devices - POST', 'POST', '/api_node/v1/devices', {'X-API-Key': 'key123', 'PCName': 'PC1'});
    
    await test('Exchange Req', 'POST', '/api_node/v1/devices/requests/exchange', {'X-API-Key': 'k', 'PCName': 'p'}, {Request: 'Shut_Down'});
    await test('Exchange Req XSS', 'POST', '/api_node/v1/devices/requests/exchange', {'X-API-Key': 'k', 'PCName': 'p'}, {Request: '<script>alert()</script>Shut_Down'});
}
run();
