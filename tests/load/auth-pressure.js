import http from 'k6/http';
import { check } from 'k6';
import exec from 'k6/execution';

const base = (__ENV.PCCONNECT_LOAD_BASE_URL || '').replace(/\/$/, '');
const identities = JSON.parse(__ENV.PCCONNECT_LOAD_IDENTITIES || '[]');

if (__ENV.PCCONNECT_ENVIRONMENT !== 'staging' || __ENV.PCCONNECT_LOAD_APPROVED !== 'STAGING_ONLY') {
  throw new Error('Load tests require PCCONNECT_ENVIRONMENT=staging and PCCONNECT_LOAD_APPROVED=STAGING_ONLY');
}
if (!base.startsWith('https://') || identities.length < 20) {
  throw new Error('Provide an HTTPS staging URL and at least 20 synthetic password identities');
}

export const options = {
  scenarios: {
    argon2: { executor: 'constant-arrival-rate', rate: Number(__ENV.PCCONNECT_AUTH_RATE || '5'), timeUnit: '1s', duration: __ENV.PCCONNECT_LOAD_DURATION || '5m', preAllocatedVUs: 20, maxVUs: 100 },
  },
  thresholds: {
    dropped_iterations: ['count==0'],
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<3000'],
  },
};

export default function () {
  const identity = identities[exec.scenario.iterationInTest % identities.length];
  const response = http.post(`${base}/api/v2/auth/password/login`, JSON.stringify({
    login: identity.login,
    password: identity.password,
    client: { platform: 'web', name: 'staging-load', version: '2' },
  }), { headers: { 'Content-Type': 'application/json' } });
  check(response, { 'synthetic Argon2 login succeeds': r => r.status === 200 });
}
