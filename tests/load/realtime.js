import http from 'k6/http';
import ws from 'k6/ws';
import { check } from 'k6';
import exec from 'k6/execution';

const base = (__ENV.PCCONNECT_LOAD_BASE_URL || '').replace(/\/$/, '');
const tokens = JSON.parse(__ENV.PCCONNECT_HUB_TOKENS || '[]');
const connectionSeconds = Number(__ENV.PCCONNECT_HUB_SECONDS || '300');

if (__ENV.PCCONNECT_ENVIRONMENT !== 'staging' || __ENV.PCCONNECT_LOAD_APPROVED !== 'STAGING_ONLY') {
  throw new Error('Load tests require PCCONNECT_ENVIRONMENT=staging and PCCONNECT_LOAD_APPROVED=STAGING_ONLY');
}
if (!base.startsWith('https://') || tokens.length < 1000) {
  throw new Error('Provide an HTTPS staging URL and at least 1,000 synthetic controller access tokens');
}

export const options = {
  scenarios: {
    hubs: { executor: 'constant-vus', vus: 1000, duration: `${connectionSeconds + 30}s`, gracefulStop: '10s' },
  },
  thresholds: {
    checks: ['rate>0.99'],
    ws_connecting: ['p(95)<2000'],
  },
};

export default function () {
  const token = tokens[exec.vu.idInTest - 1];
  const authorization = { Authorization: `Bearer ${token}` };
  const negotiate = http.post(`${base}/api/v2/hubs/controller/negotiate?negotiateVersion=1`, null, { headers: authorization });
  check(negotiate, { 'SignalR negotiate succeeds': r => r.status === 200 });
  if (negotiate.status !== 200) return;
  const connectionToken = negotiate.json('connectionToken');
  const socketUrl = `${base.replace(/^https:/, 'wss:')}/api/v2/hubs/controller?id=${encodeURIComponent(connectionToken)}`;
  const response = ws.connect(socketUrl, { headers: authorization }, socket => {
    socket.on('open', () => socket.send('{"protocol":"json","version":1}\u001e'));
    socket.on('message', message => {
      if (message === '{}\u001e') socket.setTimeout(() => socket.close(), connectionSeconds * 1000);
    });
    socket.setTimeout(() => socket.close(), (connectionSeconds + 5) * 1000);
  });
  check(response, { 'SignalR websocket upgraded': r => r && r.status === 101 });

  // The durable recovery path remains authoritative after any missed hint.
  const recovery = http.get(`${base}/api/v2/commands?limit=10`, { headers: authorization });
  check(recovery, { 'REST recovery succeeds after disconnect': r => r.status === 200 });
}
