import http from 'k6/http';
import { check } from 'k6';
import exec from 'k6/execution';

const base = (__ENV.PCCONNECT_LOAD_BASE_URL || '').replace(/\/$/, '');
const accessTokens = JSON.parse(__ENV.PCCONNECT_LOAD_ACCESS_TOKENS || '[]');
const deviceIds = JSON.parse(__ENV.PCCONNECT_LOAD_DEVICE_IDS || '[]');

if (__ENV.PCCONNECT_ENVIRONMENT !== 'staging' || __ENV.PCCONNECT_LOAD_APPROVED !== 'STAGING_ONLY') {
  throw new Error('Load tests require PCCONNECT_ENVIRONMENT=staging and PCCONNECT_LOAD_APPROVED=STAGING_ONLY');
}
if (!base.startsWith('https://') || accessTokens.length === 0 || accessTokens.length !== deviceIds.length) {
  throw new Error('Provide an HTTPS staging URL and equal non-empty synthetic access-token/device-id JSON arrays');
}

export const options = {
  scenarios: {
    api: { executor: 'constant-arrival-rate', exec: 'apiTraffic', rate: 50, timeUnit: '1s', duration: __ENV.PCCONNECT_LOAD_DURATION || '10m', preAllocatedVUs: 50, maxVUs: 250 },
    commands: { executor: 'constant-arrival-rate', exec: 'commandTraffic', rate: 10, timeUnit: '1s', duration: __ENV.PCCONNECT_LOAD_DURATION || '10m', preAllocatedVUs: 20, maxVUs: 100 },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    'http_req_duration{scenario:api}': ['p(95)<500'],
    'http_req_duration{scenario:commands}': ['p(95)<500'],
    dropped_iterations: ['count==0'],
  },
};

function identity() {
  const index = exec.scenario.iterationInTest % accessTokens.length;
  return { token: accessTokens[index], deviceId: deviceIds[index] };
}

function headers(token) {
  return { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };
}

function uuid() {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
    const value = Math.floor(Math.random() * 16);
    return (c === 'x' ? value : (value & 0x3) | 0x8).toString(16);
  });
}

export function apiTraffic() {
  const current = identity();
  const response = http.get(`${base}/api/v2/devices?limit=50`, { headers: headers(current.token), tags: { endpoint: 'devices' } });
  check(response, { 'device listing succeeds': r => r.status === 200 });
}

export function commandTraffic() {
  const current = identity();
  const response = http.post(
    `${base}/api/v2/devices/${current.deviceId}/commands`,
    JSON.stringify({ type: 'lock', expiresInSeconds: 120, explicitlyConfirmed: true }),
    { headers: { ...headers(current.token), 'Idempotency-Key': uuid() }, tags: { endpoint: 'commands' } },
  );
  check(response, { 'lock command accepted': r => r.status === 202 });
}
