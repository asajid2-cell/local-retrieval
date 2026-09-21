// muxd's pump_status() pushes a `t:"sessions"` frame every 5s for as long as its event loop runs, so the
// relay gets a heartbeat for free — and it is the only one it has.
//
// The failure this covers is TCP-alive-but-stalled: muxd's loop stops turning, the WebSocket stays open,
// so `host.connected`, `protocolOk` and `caps` all stay green while no byte moves. That is how the stall
// ran for three days with a healthy-looking /api/health. A readiness check answers "is the TCP session
// up", never "is the process running", so the missing signal is the AGE of the last frame.
//
// These tests drive the real relay and assert a real state change: the frame age crosses the threshold
// and comes back when a frame arrives. The threshold is an env knob so the crossing can be observed in
// under two seconds instead of waiting out the production 15s.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const { RelayHarness, sleep } = require('./harness');

const STALE_MS = 1000;

test('a connected host that stops pushing frames is reported stalled, and recovers when frames resume', async t => {
  const h = new RelayHarness({ MUX_HOST_FRAME_STALE_MS: String(STALE_MS) });
  await h.start();
  t.after(() => h.stop());

  const host = await h.connectHost([]);

  // The hello itself is a heartbeat: it carries a full session list, and without stamping it the age
  // would be measured from process start and a host that just connected would read as stale.
  const fresh = (await h.json('GET', '/api/health')).host;
  assert.equal(fresh.frameStale, false, 'a host that just sent its hello is not stale');
  assert.equal(typeof fresh.frameAgeMs, 'number', 'a stamped host publishes a measurable age');
  assert.ok(fresh.frameAgeMs < STALE_MS, `a fresh age is under the threshold (${fresh.frameAgeMs}ms)`);
  assert.equal(fresh.frameStaleMs, STALE_MS, 'the threshold is published so no consumer re-derives it');

  // The stall: the link stays up, nothing arrives. `connected` cannot see this, which is the point.
  await sleep(STALE_MS + 600);
  const stalledHealth = await h.json('GET', '/api/health');
  assert.equal(stalledHealth.host.connected, true, 'the link is still up — this is the whole problem');
  assert.equal(stalledHealth.host.protocolOk, true, 'and the protocol still agrees');
  assert.equal(stalledHealth.host.frameStale, true, 'the silent host is reported stalled');
  assert.equal(stalledHealth.degraded, true, 'a stalled host degrades the relay');
  assert.ok(
    stalledHealth.degradedReasons.some(r => /host stalled \(no frame for \d+s\)/.test(r)),
    `degradedReasons must name the stall, got ${JSON.stringify(stalledHealth.degradedReasons)}`,
  );

  // A frame arrives: the age resets and the relay goes green again. Without this the signal would be a
  // latch rather than a measurement.
  host.sendSessions([]);
  const resumed = await (async () => {
    for (let i = 0; i < 40; i++) {
      const health = await h.json('GET', '/api/health');
      if (health.host.frameStale === false) return health;
      await sleep(50);
    }
    return null;
  })();
  assert.ok(resumed, 'one status frame must clear the stall');
  assert.equal(resumed.degraded, false, 'and clear the aggregate with it');
  assert.deepEqual(resumed.degradedReasons, [], 'no reasons remain once the heartbeat resumes');
});

test('a rejected session frame does not count as a heartbeat', async t => {
  // The stamp goes on the accepted branch only. A malformed frame closes the link, so treating it as
  // liveness would let a broken host keep the age fresh while the relay refuses every update it sends.
  const h = new RelayHarness({ MUX_HOST_FRAME_STALE_MS: String(STALE_MS) });
  await h.start();
  t.after(() => h.stop());

  const host = await h.connectHost([]);
  host.ws.send(JSON.stringify({ t: 'sessions', list: 'not-an-array' }));
  await sleep(STALE_MS + 600);

  const health = await h.json('GET', '/api/health');
  // The malformed frame closed the link, so there is no longer a frame to measure — which is itself
  // evidence it was not accepted as liveness.
  assert.equal(health.host.frameAgeMs, null, 'a rejected frame leaves nothing stamped');
  assert.equal(health.host.frameStale, false, 'a down link is not reported as a stall');
  assert.ok(
    !health.degradedReasons.some(r => /host stalled/.test(r)),
    'the relay names the down link, not a stall it cannot measure',
  );
});

test('a host with no frame to measure never reports a stall', async t => {
  // A down link has no frames to be late, and a fresh connection has not stamped one yet. Both publish
  // frameAgeMs=null and frameStale=false; collapsing null into a falsy-but-present age is how this field
  // would start describing sockets that do not exist.
  const h = new RelayHarness({ MUX_HOST_FRAME_STALE_MS: String(STALE_MS) });
  await h.start();
  t.after(() => h.stop());

  const noHost = await h.json('GET', '/api/health');
  assert.equal(noHost.host.connected, false);
  assert.equal(noHost.host.frameAgeMs, null, 'nothing to measure before any host connects');
  assert.equal(noHost.host.frameStale, false);

  await sleep(STALE_MS + 600);
  const stillDown = await h.json('GET', '/api/health');
  assert.equal(stillDown.host.frameStale, false, 'a host that never connected is not a stalled host');
  assert.ok(
    !stillDown.degradedReasons.some(r => /host stalled/.test(r)),
    'and the reason reported is the link, not a stall',
  );
});

// Structural, because the timing above can only prove the stamp happens somewhere. This pins WHERE: the
// accepted `sessions` branch, and the disconnect reset. A stamp added to the wrong branch (or an age that
// survives the socket it described) would still pass the timing assertions in some orderings.
test('the frame age is stamped on the accepted sessions branch and cleared on disconnect', () => {
  const source = fs.readFileSync(path.join(__dirname, '..', 'server.js'), 'utf8');

  const hello = source.slice(source.indexOf("if (m.t === 'hello')"), source.indexOf("if (m.t === 'sessions')"));
  assert.match(hello, /hostFrame = \{ helloAt: Date\.now\(\), sessionsAt: Date\.now\(\) \}/,
    'the hello is the first heartbeat');

  const sessions = source.slice(source.indexOf("if (m.t === 'sessions')"), source.indexOf("} else if (m.t === 'createResult')"));
  const stampAt = sessions.indexOf('hostFrame.sessionsAt = Date.now()');
  const rejectAt = sessions.indexOf('rejected malformed session list');
  assert.ok(stampAt > 0, 'the accepted frame stamps the age');
  assert.ok(stampAt > rejectAt, 'and only after validation — a rejected frame closes the link instead');

  const disconnect = source.slice(source.indexOf("console.log('[host] PC session host disconnected')") - 900);
  assert.match(disconnect, /hostFrame = \{ helloAt: 0, sessionsAt: 0 \}/,
    'a disconnect clears the age rather than freezing the last host\'s number');
});

// The dashboard dot is the other consumer of this field, and the one a human reads. Its tooltip used to
// describe a connected host as healthy by construction ("host NAME proto 4"), which stays true through
// exactly the stall this field reports — so an amber dot would have explained itself away.
test('the dashboard health tooltip names a stalled host instead of describing its link', () => {
  const page = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');
  const start = page.indexOf('async function pollHealth');
  assert.ok(start > 0, 'the health poller must exist');
  const body = page.slice(start, page.indexOf('pollHealth(); setInterval(pollHealth'));
  assert.match(body, /h\.host\.frameStale/, 'the tooltip reads the stall signal');
  assert.match(body, /STALLED/, 'and says so in the words an operator scans for');
});
