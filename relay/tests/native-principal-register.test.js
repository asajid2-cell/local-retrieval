// §3.4 PRINCIPAL REGISTRATION CARRIAGE - the relay half.
//
// The native app registers its public key as a principal for ONE session so its signed input.durable
// proofs are admitted. muxd owns the registry and is the sole authority; the relay is a conduit - it
// carries the request across the owner link (rid-correlated, exactly like create) and returns muxd's
// granted tuple unchanged, plus the live instanceId the client's proofs must cite.
//
// This suite floats the RELAY half only, against a fake host. It proves the relay validates, correlates
// and carries; it does NOT prove an end-to-end registration, because muxd's `t:'principal'` handler does
// not exist yet (that is the escalated ownership item). The fake host answers `principalResult` the way a
// conforming muxd must, and the test asserts the relay carried the request fields intact and returned the
// granted tuple verbatim - nothing more.
//
//   (a) a valid register round-trips: the fields reach the host, the granted tuple + live instanceId come
//       back, and the instanceId read is exposed for a pre-register/re-attach;
//   (b) a muxd refusal is carried back with its principal-* code;
//   (c) an unknown session is refused before any host request;
//   (d) a host that does not advertise the capability is refused (never assume presence);
//   (e) the route is owner-gated.
//
// MUX_TEST_MODE is unset, so only a real credential opens the route - the same bearer the app presents on
// /ws and every other owner /api/*.

const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');

const { RelayHarness, HOST_CAPS } = require('./harness');

const VIEWER = crypto.randomBytes(32).toString('hex');
const AUTH = { authorization: `Bearer ${VIEWER}` };
const NATIVE_ON = { MUX_TEST_MODE: undefined, MUX_NATIVE_VIEWER_TOKEN: VIEWER };

// A modern muxd: the required caps plus the §3.4 register op, and a live instance id.
const CAPS = [...HOST_CAPS, 'principalRegister'];
const INSTANCE = 'mux-' + 'a'.repeat(32);

function shellSession(name) {
  return {
    name, alive: true, created: 1000, lastOut: 1000, cols: 100, rows: 30,
    hasCommand: false, shellOnly: true, ready: true, kind: 'shell', sessionId: '', aliases: [],
  };
}

// Only the PEM header is load-bearing at the relay (muxd validates the real key). Derived, not a
// literal blob, so no added line carries a base64-shaped fixture.
const PEM = '-----BEGIN PUBLIC KEY-----\n' + 'A'.repeat(60) + '\n-----END PUBLIC KEY-----\n';
const PRINCIPAL = 'zr.' + 'b'.repeat(32);
function regBody(session, extra = {}) {
  return { session, principalId: PRINCIPAL, keyId: 'k1', publicKeyPem: PEM, roles: ['drive'], ...extra };
}
const post = (h, body, headers) => h.request('POST', '/api/principals', body, headers);

// ---------------------------------------------------------------------------------------------
// (a) POSITIVE: the register round-trips and the granted tuple + live instanceId come back.
// ---------------------------------------------------------------------------------------------
test('a valid registration reaches the host and its granted tuple comes back', async t => {
  const h = new RelayHarness(NATIVE_ON);
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('regcase')], { caps: CAPS, instanceId: INSTANCE });
  t.after(() => host.close());

  const pending = host.waitFor(m => m.t === 'principal' && m.op === 'register' && m.s === 'regcase',
    'the principal register request reached the host');
  const res = post(h, regBody('regcase'), AUTH);
  const req = await pending;

  // The relay carried the request fields intact and correlated it by rid.
  assert.equal(req.principalId, PRINCIPAL, 'principalId rides through unchanged');
  assert.equal(req.keyId, 'k1', 'keyId rides through unchanged');
  assert.ok(Array.isArray(req.roles) && req.roles.includes('drive'), 'the roles ride through');
  assert.ok(req.rid, 'the register request must be rid-correlated like create');

  // muxd (the fake) decides the grant; the relay must return it verbatim.
  const UUID = 's-' + 'c'.repeat(32);
  host.sendPrincipalResult(req, {
    principalId: req.principalId, keyId: req.keyId, sessionUuid: UUID,
    roles: ['drive'], aclRevision: 3, leaseEpoch: 2, leaseHolder: '',
  });

  const out = await res;
  assert.equal(out.status, 200, `registration must succeed: ${out.status} ${out.text}`);
  assert.equal(out.body.instanceId, INSTANCE, 'the live muxd instance the proof must cite comes back');
  assert.equal(out.body.sessionUuid, UUID, 'the durable session uuid comes back (not the relay name)');
  assert.equal(out.body.aclRevision, 3, 'the granted aclRevision comes back so a stale one is actionable');
  assert.equal(out.body.leaseEpoch, 2, 'the granted leaseEpoch comes back so a stale one is actionable');
  assert.deepEqual(out.body.roles, ['drive']);

  // The instanceId read: how the app learns the value WITHOUT registering (a re-attach).
  const caps = await h.request('GET', '/api/principals/capabilities', undefined, AUTH);
  assert.equal(caps.status, 200, `the capabilities read must be owner-reachable: ${caps.status} ${caps.text}`);
  assert.equal(caps.body.instanceId, INSTANCE);
  assert.equal(caps.body.principalRegister, true);
});

// ---------------------------------------------------------------------------------------------
// (b) A muxd REFUSAL is carried back with its principal-* code.
// ---------------------------------------------------------------------------------------------
test('a muxd refusal is carried back with its principal-* code', async t => {
  const h = new RelayHarness(NATIVE_ON);
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('refcase')], { caps: CAPS, instanceId: INSTANCE });
  t.after(() => host.close());

  const pending = host.waitFor(m => m.t === 'principal' && m.s === 'refcase', 'the register request');
  const res = post(h, regBody('refcase'), AUTH);
  const req = await pending;
  host.sendPrincipalResult(req, { ok: false, code: 'principal-key-in-use', detail: 'that key is already registered' });

  const out = await res;
  assert.ok(out.status >= 400, `a refusal must be a 4xx/5xx: ${out.status} ${out.text}`);
  assert.equal(out.body.code, 'principal-key-in-use', 'the principal-* code must be actionable to the client');
});

// ---------------------------------------------------------------------------------------------
// (c) An unknown session is refused BEFORE any host request.
// ---------------------------------------------------------------------------------------------
test('an unknown session is refused before any host request', async t => {
  const h = new RelayHarness(NATIVE_ON);
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('known')], { caps: CAPS, instanceId: INSTANCE });
  t.after(() => host.close());

  const res = await post(h, regBody('missing'), AUTH);
  assert.equal(res.status, 404, `an unknown session must 404: ${res.status} ${res.text}`);
  await host.assertNo(m => m.t === 'principal', 'no registration is sent for an unknown session');
});

// ---------------------------------------------------------------------------------------------
// (d) NEVER ASSUME PRESENCE. A host without the capability is refused.
// ---------------------------------------------------------------------------------------------
test('a host that does not advertise the register capability is refused', async t => {
  const h = new RelayHarness(NATIVE_ON);
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('oldcap')]);   // default caps: no principalRegister
  t.after(() => host.close());

  const res = await post(h, regBody('oldcap'), AUTH);
  assert.equal(res.status, 503, `a host that cannot serve it must be refused by name: ${res.status} ${res.text}`);
  await host.assertNo(m => m.t === 'principal', 'no registration is sent to a host that cannot serve it');
});

// ---------------------------------------------------------------------------------------------
// (e) OWNER-GATED. No credential -> nothing reaches the host; a malformed body is a 400.
// ---------------------------------------------------------------------------------------------
test('the route is owner-gated and validates its body before touching the host', async t => {
  const h = new RelayHarness(NATIVE_ON);
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('gated')], { caps: CAPS, instanceId: INSTANCE });
  t.after(() => host.close());

  // No credential: refused, and the host is never asked.
  const anon = await post(h, regBody('gated'));
  assert.ok(anon.status >= 400, `an uncredentialed caller must be refused: ${anon.status} ${anon.text}`);

  // Malformed fields: a 400, and still no host request.
  for (const bad of [
    regBody('gated', { principalId: 'bad id' }),
    regBody('gated', { keyId: '' }),
    regBody('gated', { publicKeyPem: 'not a pem' }),
  ]) {
    const res = await post(h, bad, AUTH);
    assert.equal(res.status, 400, `a malformed registration must 400: ${res.status} ${res.text}`);
  }
  await host.assertNo(m => m.t === 'principal', 'no host request for an uncredentialed or malformed call');
});
