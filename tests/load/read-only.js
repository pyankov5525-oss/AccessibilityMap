import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';

const target = (__ENV.TARGET_URL || '').replace(/\/$/, '');
if (!target.startsWith('https://')) throw new Error('TARGET_URL must use https://');

const stage = __ENV.LOAD_STAGE || '1';
const profiles = {
  '1': { vus: 1, duration: '30s' },
  '10': { vus: 10, duration: '45s' },
  '100': { vus: 100, duration: '60s' },
  '1000': { vus: 1000, duration: '30s' },
};
const profile = profiles[stage];
if (!profile) throw new Error(`Unknown LOAD_STAGE: ${stage}`);

const applicationErrors = new Rate('application_errors');
const placemarkLatency = new Trend('placemark_latency', true);

export const options = {
  vus: profile.vus,
  duration: profile.duration,
  gracefulStop: '15s',
  discardResponseBodies: true,
  userAgent: `AccessibilityMap-authorized-load-test/${stage}`,
  thresholds: {
    http_req_failed: ['rate<0.10'],
    application_errors: ['rate<0.10'],
    http_req_duration: ['p(95)<10000'],
    placemark_latency: ['p(95)<10000'],
  },
};

export function setup() {
  const health = http.get(`${target}/health`, { responseType: 'text', timeout: '60s' });
  if (health.status !== 200) throw new Error(`Health check failed: HTTP ${health.status}`);
  return { target };
}

export default function (data) {
  const choice = (__ITER + __VU) % 5;
  let response;
  if (choice === 0) {
    response = http.get(`${data.target}/`, { timeout: '30s', tags: { endpoint: 'home' } });
  } else if (choice === 1) {
    response = http.get(`${data.target}/health`, { timeout: '30s', tags: { endpoint: 'health' } });
  } else {
    response = http.get(
      `${data.target}/api/placemarks?minLat=51&minLon=38&maxLat=53&maxLon=41`,
      { timeout: '30s', tags: { endpoint: 'placemarks' } },
    );
    placemarkLatency.add(response.timings.duration);
  }
  const ok = check(response, { 'HTTP 200': (r) => r.status === 200 });
  applicationErrors.add(!ok);
  sleep(1 + Math.random() * 2);
}
