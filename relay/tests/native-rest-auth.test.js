// NATIVE CREDENTIAL ON THE OWNER REST SURFACE - the companion fence to native-viewer-auth.test.js.
//
// A1 gave the native client a scoped bearer on the /ws upgrade gate. But /ws is only half the surface
// the client needs: A3 (full parity) has the app drive the SAME owner-gated routes the browser uses -
// /api/sessions (list/create/kill), rename/heal/tail, and principal registration - and the global owner
// middleware admitted ONLY the hl_session cookie (server.js:224). So a native app could attach a
// terminal and then call no /api/* at all. This suite is the fence around the branch that fixes that:
//
//   (a) a VALID native credential opens an owner-gated /api/* route with no cookie - the positive
//       control, asserted against the real handler (the listed hosted session), not merely a 2xx;
//   (b) with no credential the same route is refused - the loopback bypass is OFF here, so nothing but
//       a real credential admits it;
//   (c) a WRONG bearer is refused and never reaches the handler;
//   (d) with no native token configured the branch is closed (a bearer admits nothing);
//   (e) the browser path is UNCHANGED: an owner cookie still opens the route (and still needs no bearer).
//
// All run with MUX_TEST_MODE unset, so testModeLocalTrust() is false and only a real credential works -
// the posture a deploy runs in. /api/health stays reachable because it is a LOCAL_BRIDGE_ROUTE
// (server.js:115), which is what lets connectHost establish the fake host under this posture.

const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const crypto = require('node:crypto');

const { RelayHarness } = require('./harness');

const VIEWER = crypto.randomBytes(32).toString('hex');
const COOKIE = 'fixture-owner';   // the token the fixture hl-auth reports as an owner

// MUX_TEST_MODE: undefined deletes it from the spawned child (Node skips undefined env values), so the
// production posture holds and only a real credential opens an owner route. The harness default is '1'.
const NATIVE_ON = { MUX_TEST_MODE: undefined, MUX_NATIVE_VIEWER_TOKEN: VIEWER };
const NATIVE_OFF = { MUX_TEST_MODE: undefined };

function shellSession(name) {
  return {
    name, alive: true, created: 1000, lastOut: 1000, cols: 100, rows: 30,
    hasCommand: false, shellOnly: true, ready: true, kind: 'shell', sessionId: '', aliases: [],
  };
}

// A fixture hl-auth: reports `isOwner` for the one token /internal/verify is asked about. Owner
// identity is hl-auth's to assert, so the relay must be pointed at a real oracle to test the cookie path.
async function fixtureIdentity() {
  const server = http.createServer((req, res) => {
    if (req.url !== '/internal/verify') { res.writeHead(404); res.end(); return; }
    const token = req.headers['x-session-token'];
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify({ authenticated: true, user: { isOwner: token === COOKIE } }));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  return {
    base: `http://127.0.0.1:${server.address().port}`,
    close: () => new Promise(resolve => { server.closeAllConnections(); server.close(resolve); }),
  };
}

function sessions(h, headers = {}) {
  return h.request('GET', '/api/sessions', undefined, headers);
}

// ---------------------------------------------------------------------------------------------
// (a) POSITIVE CONTROL + (b) refuse-without-credential, on the same route, in one posture.
// ---------------------------------------------------------------------------------------------
test('a valid native credential opens owner-gated /api/* with no cookie; no credential does not', async t => {
  const h = new RelayHarness(NATIVE_ON);
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('restcase')]);
  t.after(() => host.close());

  // (a) A valid bearer reaches the real handler: the response is the session list, and it names the
  // hosted session - not an error body that merely happens to be 2xx.
  const ok = await sessions(h, { authorization: `Bearer ${VIEWER}` });
  assert.equal(ok.status, 200, `a valid native credential must open the owner route: ${ok.status} ${ok.text}`);
  assert.ok(Array.isArray(ok.body), `expected the session list: ${ok.text}`);
  assert.ok(ok.body.some(s => s && s.name === 'restcase'),
    `the native credential must reach the handler, not an error: ${ok.text}`);

  // (b) With no credential the route is refused - the negative control that proves (a) is the
  // credential talking and not a bypass that would have admitted anyone.
  const anon = await sessions(h);
  assert.ok(anon.status >= 400, `an uncredentialed caller must be refused: ${anon.status} ${anon.text}`);
});

// ---------------------------------------------------------------------------------------------
// (c) A WRONG bearer is refused and never reaches the handler.
// ---------------------------------------------------------------------------------------------
test('a wrong bearer is refused on the owner route', async t => {
  const h = new RelayHarness(NATIVE_ON);
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('wrongbearer')]);
  t.after(() => host.close());

  const wrong = crypto.randomBytes(32).toString('hex');
  const res = await sessions(h, { authorization: `Bearer ${wrong}` });
  assert.ok(res.status >= 400, `a wrong bearer must not open the route: ${res.status} ${res.text}`);
  assert.equal(Array.isArray(res.body), false, 'a wrong bearer must not receive the session list');
});

// ---------------------------------------------------------------------------------------------
// (d) CLOSED BY DEFAULT. With no native token configured, a bearer admits nothing.
// ---------------------------------------------------------------------------------------------
test('with no native token configured, a bearer opens nothing', async t => {
  const h = new RelayHarness(NATIVE_OFF);
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('notoken')]);
  t.after(() => host.close());

  const res = await sessions(h, { authorization: `Bearer ${VIEWER}` });
  assert.ok(res.status >= 400, `an unconfigured native branch must stay closed: ${res.status} ${res.text}`);
});

// ---------------------------------------------------------------------------------------------
// (e) PARITY. The browser branch is untouched: an owner cookie still governs the owner route.
// ---------------------------------------------------------------------------------------------
test('the browser path is unchanged: an owner cookie still opens the owner route', async t => {
  const identity = await fixtureIdentity();
  t.after(() => identity.close());

  const h = new RelayHarness({ ...NATIVE_ON, HLAUTH_BASE: identity.base });
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('browsercookie')]);
  t.after(() => host.close());

  // An owner cookie, no bearer -> admitted (the native branch did not disturb the cookie path).
  const mine = await sessions(h, { cookie: `hl_session=${COOKIE}` });
  assert.equal(mine.status, 200, `an owner browser must still be admitted: ${mine.status} ${mine.text}`);
  assert.ok(Array.isArray(mine.body) && mine.body.some(s => s && s.name === 'browsercookie'),
    `the cookie path must reach the handler: ${mine.text}`);

  // A non-owner cookie, no bearer -> refused (the gate is not simply shut, and not simply open).
  const other = await sessions(h, { cookie: 'hl_session=not-owner' });
  assert.ok(other.status >= 400, `a non-owner cookie must be refused: ${other.status} ${other.text}`);
});
