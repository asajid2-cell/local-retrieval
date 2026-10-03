// NATIVE VIEWER CREDENTIAL (A1) — the scoped bearer the iOS app uses on /ws.
//
// The relay's /ws is browser-only in production: it demands an allowlisted Origin (WS upgrades are
// not covered by CORS) AND the owner hl_session cookie. A native client has neither, so A1 adds a
// scoped bearer to the EXISTING upgrade gate as an alternative, leaving the browser branch
// bit-identical. These tests are the fence around that alternative:
//
//   (a) a VALID viewer credential attaches (and reaches muxd for its screen) - the positive control;
//   (b) an INVALID one is refused BEFORE the handshake (no 101) and never reaches muxd;
//   (c) the browser path is UNCHANGED: an allowlisted Origin with no owner cookie is still refused
//       1008, an owner cookie still attaches, and a missing Origin is still destroyed when no
//       native credential is presented;
//   (d) the credential is SCOPED, not interchangeable: the viewer token is not a /host token and the
//       host token is not a viewer token;
//   (e) with no native token configured, nothing attaches via ?viewer= (no accidental open).
//
// All run with MUX_TEST_MODE unset, so the loopback browser bypass is OFF and only a real credential
// works — the same posture a deploy runs in (testModeLocalTrust() returns false without MUX_TEST_MODE).

const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const crypto = require('node:crypto');
const WebSocket = require('ws');

const { RelayHarness, sleep } = require('./harness');

const VIEWER = crypto.randomBytes(32).toString('hex');
const HOST_TOKEN = 'test-token';                 // the harness fixes MUX_HOST_TOKEN to this
const ORIGIN = 'https://relay-auth-gate.test';   // present an allowlisted Origin to isolate the auth gate

// MUX_TEST_MODE: undefined deletes it from the spawned child (Node skips undefined env values), so the
// production posture holds. The harness default is '1'; overriding it here is the whole point.
const NATIVE_ON = { MUX_TEST_MODE: undefined, MUX_NATIVE_VIEWER_TOKEN: VIEWER, ALLOWED_WS_ORIGINS: ORIGIN };
const NATIVE_OFF = { MUX_TEST_MODE: undefined, ALLOWED_WS_ORIGINS: ORIGIN };

function shellSession(name) {
  return {
    name, alive: true, created: 1000, lastOut: 1000, cols: 100, rows: 30,
    hasCommand: false, shellOnly: true, ready: true, kind: 'shell', sessionId: '', aliases: [],
  };
}

// Resolve with how the relay answered a /ws upgrade: a close code, 'open', or an {error} (socket
// destroyed before the handshake - the ONLY shape a bad credential may produce).
async function attachOutcome(port, session, { viewer, headers = {} } = {}) {
  const q = `session=${encodeURIComponent(session)}&cols=80&rows=24`
    + (viewer !== undefined ? `&viewer=${encodeURIComponent(viewer)}` : '');
  const ws = new WebSocket(`ws://127.0.0.1:${port}/ws?${q}`, { headers });
  const outcome = await new Promise(resolve => {
    let settled = false;
    const done = value => { if (!settled) { settled = true; resolve(value); } };
    ws.on('close', code => done({ code }));
    ws.on('error', err => done({ error: String(err && err.message || err) }));
    ws.on('open', () => setTimeout(() => done({ code: 'open' }), 300));
  });
  try { ws.terminate(); } catch {}
  return outcome;
}

async function hostOutcome(port, token) {
  const ws = new WebSocket(`ws://127.0.0.1:${port}/host?token=${encodeURIComponent(token)}`);
  const outcome = await new Promise(resolve => {
    let settled = false;
    const done = value => { if (!settled) { settled = true; resolve(value); } };
    ws.on('close', code => done({ code }));
    ws.on('error', err => done({ error: String(err && err.message || err) }));
    ws.on('open', () => setTimeout(() => done({ code: 'open' }), 300));
  });
  try { ws.terminate(); } catch {}
  return outcome;
}

// ---------------------------------------------------------------------------------------------
// (a) POSITIVE CONTROL. A valid native credential attaches with NO Origin and NO cookie.
// ---------------------------------------------------------------------------------------------
test('a valid native viewer credential attaches with no Origin and no owner cookie', async t => {
  const h = new RelayHarness(NATIVE_ON);
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('nativecase')]);
  t.after(() => host.close());

  const outcome = await attachOutcome(h.port, 'nativecase', { viewer: VIEWER });
  assert.deepEqual(outcome, { code: 'open' },
    'a valid viewer credential must attach: it is the whole reason the native path exists');
  // Attaching means the viewer reached the hosted path and the relay asked muxd for its screen.
  await host.waitFor(m => m.t === 'sb' && m.s === 'nativecase', 'scrollback request for the native viewer');
});

// ---------------------------------------------------------------------------------------------
// (b) NEGATIVE. A wrong viewer credential is destroyed BEFORE the handshake and does no work.
// ---------------------------------------------------------------------------------------------
test('an invalid native viewer credential is refused with no 101 and never reaches muxd', async t => {
  const h = new RelayHarness(NATIVE_ON);
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('badnative')]);
  t.after(() => host.close());

  const outcome = await attachOutcome(h.port, 'badnative', { viewer: crypto.randomBytes(32).toString('hex') });
  assert.equal(outcome.code, undefined, `a bad credential must never complete the handshake: ${JSON.stringify(outcome)}`);
  assert.ok(outcome.error, 'the upgrade must be destroyed, not politely closed with a code');
  await host.assertNo(m => m.t === 'sb' && m.s === 'badnative', 'a refused viewer must not make the relay query muxd');
});

// ---------------------------------------------------------------------------------------------
// (c) PARITY. The browser branch is untouched: Origin+cookie still governs a normal viewer.
// ---------------------------------------------------------------------------------------------
test('the browser path is unchanged by the native credential', async t => {
  // A fixture hl-auth so the owner-cookie positive can actually reach the real middleware check.
  const identity = http.createServer((req, res) => {
    if (req.url !== '/internal/verify') { res.writeHead(404); res.end(); return; }
    const token = req.headers['x-session-token'];
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify({ authenticated: token === 'fixture-owner', user: { isOwner: token === 'fixture-owner' } }));
  });
  await new Promise(resolve => identity.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => { identity.closeAllConnections(); identity.close(resolve); }));

  const h = new RelayHarness({ ...NATIVE_ON, HLAUTH_BASE: `http://127.0.0.1:${identity.address().port}` });
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('browsercase')]);
  t.after(() => host.close());

  // (1) allowlisted Origin, no cookie -> still refused 1008 (the native change opened nothing here).
  const anon = await attachOutcome(h.port, 'browsercase', { headers: { origin: ORIGIN, 'x-forwarded-for': '203.0.113.7' } });
  assert.deepEqual(anon, { code: 1008 }, 'a browser with no owner cookie must still be refused');

  // (2) no Origin and no viewer param -> destroyed before the handshake (browser-only in prod).
  const noOrigin = await attachOutcome(h.port, 'browsercase', {});
  assert.equal(noOrigin.code, undefined, `a no-Origin, no-credential probe must be destroyed: ${JSON.stringify(noOrigin)}`);
  assert.ok(noOrigin.error, 'destroyed, not closed');

  // (3) allowlisted Origin + owner cookie -> attaches (positive control: the gate is not simply shut).
  const owner = await attachOutcome(h.port, 'browsercase', {
    headers: { origin: ORIGIN, 'x-forwarded-for': '203.0.113.7', cookie: 'hl_session=fixture-owner' },
  });
  assert.deepEqual(owner, { code: 'open' }, 'an owner browser must still attach');
  await host.waitFor(m => m.t === 'sb' && m.s === 'browsercase', 'owner browser reached muxd');
});

// ---------------------------------------------------------------------------------------------
// (d) SCOPING. Interchangeable credentials are not scoped credentials.
// ---------------------------------------------------------------------------------------------
test('the viewer credential is not a host token and the host token is not a viewer credential', async t => {
  const h = new RelayHarness(NATIVE_ON);
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('scopecase')]);
  t.after(() => host.close());

  // The viewer token must not open the host link.
  const asHost = await hostOutcome(h.port, VIEWER);
  assert.equal(asHost.code, undefined, `the viewer token must not authenticate /host: ${JSON.stringify(asHost)}`);
  assert.ok(asHost.error, 'the /host upgrade must be destroyed');

  // The host token must not open the viewer socket.
  const asViewer = await attachOutcome(h.port, 'scopecase', { viewer: HOST_TOKEN });
  assert.equal(asViewer.code, undefined, `the host token must not authenticate /ws: ${JSON.stringify(asViewer)}`);
  assert.ok(asViewer.error, 'the /ws upgrade must be destroyed');

  // Both distinct credentials together still do not let an UNRELATED token through (sanity).
  await host.assertNo(m => m.t === 'sb' && m.s === 'scopecase', 'no scrollback from a cross-scoped token');
});

// ---------------------------------------------------------------------------------------------
// (e) CLOSED BY DEFAULT. With no native token configured, ?viewer= opens nothing.
// ---------------------------------------------------------------------------------------------
test('with no native token configured, a ?viewer= credential is refused', async t => {
  const h = new RelayHarness(NATIVE_OFF);
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('noviewercase')]);
  t.after(() => host.close());

  // Even a well-formed 64-hex credential must fail when the relay holds no native token to compare.
  const outcome = await attachOutcome(h.port, 'noviewercase', { viewer: crypto.randomBytes(32).toString('hex') });
  assert.equal(outcome.code, undefined, `an unconfigured native path must stay closed: ${JSON.stringify(outcome)}`);
  assert.ok(outcome.error, 'destroyed before the handshake');
  await sleep(50);
});
