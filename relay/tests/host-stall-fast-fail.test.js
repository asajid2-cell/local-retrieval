// A wedged muxd keeps its WebSocket open and stops turning its event loop: `host.connected`, `protocolOk`
// and `caps` all stay green while nothing is answered. host-frame-liveness.test.js already proves the AGE of
// the last `sessions` frame is measured and published on /api/health -- but a measurement only the dashboard
// reads is not a fix. The create path still sent its frame to a host that could not reply and then sat on the
// full 15s deadline, reporting "muxd did not acknowledge the create request": no cause, and indistinguishable
// from a slow success. That is the user-visible hang. These tests pin the REQUEST paths to the same
// measurement, so the refusal is immediate and names what is wrong.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const { RelayHarness, sleep } = require('./harness');

const STALE_MS = 1000;
const shellSession = name => ({
  name, alive: true, created: 1000, lastOut: 1000, cols: 100, rows: 30,
  hasCommand: false, shellOnly: true, ready: true, kind: 'shell', sessionId: '', aliases: [],
});

test('a stalled host is refused at once, by name, instead of holding the click for the whole deadline', async t => {
  const h = new RelayHarness({ MUX_HOST_FRAME_STALE_MS: String(STALE_MS) });
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([]);
  t.after(() => host.close());

  await sleep(STALE_MS + 600);
  const health = await h.json('GET', '/api/health');
  assert.equal(health.host.connected, true, 'precondition: the link is still up');
  assert.equal(health.host.frameStale, true, 'precondition: and the host is stalled');

  const started = Date.now();
  const res = await h.request('POST', '/api/sessions', { name: 'stalled-create' });
  const elapsed = Date.now() - started;

  assert.equal(res.status, 503, `a stalled host must be refused, got ${res.status} ${res.text}`);
  assert.equal(res.body.error, 'PC mux host not responding');
  assert.match(res.body.detail, /not responding \(no status frame for \d+s\)/, `the detail must name the stall: ${res.body.detail}`);
  assert.match(res.body.detail, /MuxdSessionHost/, 'and name what to restart on the PC');
  // The production deadline for this request is 15s, and the old code spent every millisecond of it.
  assert.ok(elapsed < 2000, `refusal must not wait out the create deadline (took ${elapsed}ms)`);
  await host.assertNo(
    m => m.t === 'create' && m.s === 'stalled-create',
    'a stalled host must not be sent a create it provably cannot answer',
  );
});

test('one status frame clears the stall and the same create then succeeds', async t => {
  const h = new RelayHarness({ MUX_HOST_FRAME_STALE_MS: String(STALE_MS) });
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([]);
  t.after(() => host.close());

  await sleep(STALE_MS + 600);
  const refused = await h.request('POST', '/api/sessions', { name: 'recovering-create' });
  assert.equal(refused.status, 503, 'a stalled host is refused first');

  // Without this half the refusal above would be a latch rather than a measurement.
  host.sendSessions([]);
  const post = h.request('POST', '/api/sessions', { name: 'recovering-create' });
  const create = await host.waitFor(
    m => m.t === 'create' && m.s === 'recovering-create',
    'create after the heartbeat resumed',
  );
  host.sendCreateResult(create, { session: shellSession('recovering-create') });
  const res = await post;
  assert.equal(res.status, 200, `a resumed heartbeat must clear the refusal: ${res.text}`);
  assert.equal(res.body.created, true);
});

// Structural, because a timing assertion can only prove the gate exists SOMEWHERE. This pins that each of
// the three request paths consults it: the API gate that the web Create click hits first, the create
// request itself, and the post-create visibility wait. Any one of them left out re-opens the hang for that
// path alone, which is exactly the kind of gap a single end-to-end test would miss.
test('every host request path consults the stall, not only the health endpoint', () => {
  const source = fs.readFileSync(path.join(__dirname, '..', 'server.js'), 'utf8');
  for (const fn of ['function requireHostProtocol', 'function requestHostCreate', 'function waitForHostState']) {
    const start = source.indexOf(fn);
    assert.ok(start > 0, `${fn} must exist`);
    assert.match(source.slice(start, start + 1600), /hostStallReason\(\)/, `${fn} must refuse a stalled host`);
  }
  assert.match(source, /function hostFrameAge\(\)/, 'the age is measured in one place');
  assert.match(source, /function hostStallReason\(\)/, 'and named in one place');
});
