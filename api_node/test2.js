const axios = require('axios');
const SERVER_URL = 'http://localhost:3000/api_node';
const MOCK_API_KEY = '51359fd1b13802000649a6bd2f3f10ba'; 
const MOCK_PC_NAME = 'TestNodeDesktop';

let pd = 0;
let fd = 0;

async function r(name, reqFn, expectStatus) {
    try {
        const res = await reqFn();
        if (res.status === expectStatus) {
            console.log('[PASS] ' + name);
            pd++;
        } else {
            console.log('[FAIL] ' + name + ' Expected ' + expectStatus + ' Got ' + res.status);
            fd++;
        }
    } catch (e) {
        if (e.response && e.response.status === expectStatus) {
            console.log('[PASS] ' + name);
            pd++;
        } else {
            console.log('[FAIL] ' + name + ' Expected ' + expectStatus + ' Got ' + (e.response ? e.response.status : e.message) + ' ' + (e.response ? JSON.stringify(e.response.data) : ''));
            fd++;
        }
    }
}

async function t() {
    console.log('Testing Edge Cases & Resilience...');

    await r('Login: Max User length', () => axios.post(`${SERVER_URL}/auth/login`, { loginUsername: 'a'.repeat(300), loginPassword: 'pwd' }), 400);
    await r('Login: Empty Payload', () => axios.post(`${SERVER_URL}/auth/login`, {}), 400);

    await r('Devices: Bypass Auth', () => axios.get(`${SERVER_URL}/v1/devices`), 401);
    await r('Devices: Invalid Auth', () => axios.get(`${SERVER_URL}/v1/devices`, { headers: { 'X-API-Key': 'inv', 'PCName': MOCK_PC_NAME } }), 401);

    const h = { headers: { 'X-API-Key': MOCK_API_KEY, 'PCName': MOCK_PC_NAME } };
    const aH = { headers: { 'X-API-Key': MOCK_API_KEY } };

    await r('Devices: Valid Auth', () => axios.get(`${SERVER_URL}/v1/devices`, h), 200);

    const sqli = "' OR 1=1; --";
    const xss = "<script>alert(1)</script> Hello";
    
    await r('Reminders: SQLi in params', () => axios.post(`${SERVER_URL}/v1/reminders`, { date: "2026-05-01", time: "12:00", reminder: sqli }, aH), 201); 
    await r('Reminders: Max Length limits', () => axios.post(`${SERVER_URL}/v1/reminders`, { date: "2026-05-01", time: "12:00", reminder: 'r'.repeat(3000) }, aH), 400);
    await r('Reminders PUT: Invalid type ID', () => axios.put(`${SERVER_URL}/v1/reminders/admin`, { completed: 1 }, aH), 400);
    await r('Reminders Complete: Invalid ID', () => axios.post(`${SERVER_URL}/v1/reminders/abc/complete`, { completed: 1 }, aH), 400);

    await r('Requests: Max length exchange', () => axios.post(`${SERVER_URL}/v1/devices/requests/exchange`, { Request: 'r'.repeat(2000) }, h), 400);
    await r('Requests: XSS stripping', () => axios.post(`${SERVER_URL}/v1/devices/requests/exchange`, { Request: xss }, h), 200); 
    await r('Requests: Missing PC Name', () => axios.post(`${SERVER_URL}/v1/devices/requests/exchange`, { Request: "test" }, aH), 401);

    await r('Global CheckInternet', () => axios.get(`${SERVER_URL}/v1/system/checkinternet`), 200);

    console.log(`=== Result: ${pd} Passed | ${fd} Failed ===`);
    process.exit(fd > 0 ? 1 : 0);
}

t();