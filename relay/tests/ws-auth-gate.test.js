// /ws AUTHORIZATION GATE — the security half of the test-mode exemption.
//
// server.js grants a TEST-ONLY bypass (testModeLocalTrust: MUX_TEST_MODE=1 AND a loopback peer) so the
// suite can attach viewers without an hl-auth session. Commit aa74650 exists because ambient loopback
// trust on this exact path was WRONG: containers on this host run with network_mode: host and share
// 127.0.0.1, so "the peer is 127.0.0.1" proves nothing about who is calling.
//
// These tests are the fence around that bypass. They must fail if either half of the condition is ever
// dropped, so a future edit cannot quietly turn a test convenience back into a production hole:
//
//   (a) MUX_TEST_MODE unset  -> a loopback caller with no owner cookie is refused 1008.
//   (b) X-Forwarded-For set  -> refused 1008 even under MUX_TEST_MODE, because that request came
//                               through nginx and is therefore public traffic, not a local caller.
//   (c) positive control     -> under MUX_TEST_MODE, from loopback, WITHOUT that header, attach works.
//
// (c) is not decoration: without it, (a) and (b) would keep passing if /ws broke outright and refused
// everybody, which is precisely how 19 suites came to look broken while the gate looked fine.

const test = require('node:test');
const assert = require('node:assert/strict');
const { once } = require('node:events');
const WebSocket = require('ws');

const { RelayHarness, sleep } = require('./harness');

// A real deploy only ever sees browser upgrades, which always carry an Origin. wsOriginOk() answers
// TEST_MODE for a MISSING Origin, so a no-Origin probe would be destroyed before the handshake and
// never reach the authorization check we are testing. Presenting an allowlisted Origin isolates the
// auth gate from the CSWSH gate: whatever we observe here is the owner check talking, nothing else.
const ORIGIN = 'https://relay-auth-gate.test';

function shellSession(name) {
  return {
    name, alive: true, created: 1000, lastOut: 1000, cols: 100, rows: 30,
    hasCommand: false, shellOnly: true, ready: true, kind: 'shell', sessionId: '', aliases: [],
  };
}

// Resolves with how the relay answered the upgrade: a close code, or 'open' if it stayed up.
async function attachOutcome(port, session, headers = {}) {
  const ws = new WebSocket(
    `ws://127.0.0.1:${port}/ws?session=${session}&cols=80&rows=24`,
    { headers: { origin: ORIGIN, ...headers } },
  );
  const outcome = await new Promise(resolve => {
    let settled = false;
    const done = value => { if (!settled) { settled = true; resolve(value); } };
    // A refusal closes within a round trip; the grace period is for the accept case, where we want
    // to be sure no late close is coming before we call it open.
    ws.on('close', code => done({ code }));
    ws.on('error', err => done({ error: String(err && err.message || err) }));
    ws.on('open', () => setTimeout(() => done({ code: 'open' }), 300));
  });
  try { ws.terminate(); } catch {}
  return outcome;
}

// MUX_TEST_MODE must be absent, not falsy-but-present, or a future `'0'`-style check could disagree
// with `=== '1'`. The harness sets it, so this deletes it from the spawned child's environment.
const NO_TEST_MODE = { MUX_TEST_MODE: undefined, ALLOWED_WS_ORIGINS: ORIGIN };
const TEST_MODE_ON = { ALLOWED_WS_ORIGINS: ORIGIN };

// ---------------------------------------------------------------------------------------------
// (a) THE NEGATIVE. No test mode => loopback buys nothing, even for a session that really exists.
// ---------------------------------------------------------------------------------------------
test('without MUX_TEST_MODE, a loopback viewer with no owner cookie is refused 1008', async t => {
  const h = new RelayHarness(NO_TEST_MODE);
  await h.start();
  t.after(() => h.stop());

  // /host is token-gated, not owner-gated, so the session below exists for real. That matters: it
  // rules out "refused because the session is unknown" and pins the refusal to authorization.
  const host = await h.connectHost([shellSession('gatecase')]);
  t.after(() => host.close());

  const outcome = await attachOutcome(h.port, 'gatecase');
  assert.deepEqual(
    outcome, { code: 1008 },
    'a loopback peer is not a credential: /ws must refuse 1008 unauthorized when MUX_TEST_MODE is unset',
  );

  // And it must refuse without asking muxd for anything — an unauthorized caller may not make the
  // relay do work on its behalf, nor learn whether the session exists from a side effect.
  await host.assertNo(m => m.t === 'sb' && m.s === 'gatecase', 'a refused viewer must not request scrollback');
});

// ---------------------------------------------------------------------------------------------
// (b) The loopback half is load-bearing too: nginx traffic is not local traffic.
// ---------------------------------------------------------------------------------------------
test('even under MUX_TEST_MODE, an X-Forwarded-For upgrade is refused 1008', async t => {
  const h = new RelayHarness(TEST_MODE_ON);
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('xffcase')]);
  t.after(() => host.close());

  // nginx overwrites X-Forwarded-For with $remote_addr on every proxied request, so its presence is
  // the marker of a request that came from the public internet rather than from this box.
  const outcome = await attachOutcome(h.port, 'xffcase', { 'x-forwarded-for': '203.0.113.7' });
  assert.deepEqual(
    outcome, { code: 1008 },
    'a forwarded (public) request must never satisfy the local-trust half of the test exemption',
  );
});

// ---------------------------------------------------------------------------------------------
// (c) POSITIVE CONTROL. The bypass actually works, so (a) and (b) are refusals and not an outage.
// ---------------------------------------------------------------------------------------------
test('under MUX_TEST_MODE, a direct loopback viewer attaches', async t => {
  const h = new RelayHarness(TEST_MODE_ON);
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('okcase')]);
  t.after(() => host.close());

  const outcome = await attachOutcome(h.port, 'okcase');
  assert.deepEqual(
    outcome, { code: 'open' },
    'MUX_TEST_MODE + direct loopback must attach; if this fails the two refusals above prove nothing',
  );
  // Attaching means the viewer reached the hosted path and the relay asked muxd for its screen.
  await host.waitFor(m => m.t === 'sb' && m.s === 'okcase', 'scrollback request for the accepted viewer');
});

// ---------------------------------------------------------------------------------------------
// (d) The exemption is scoped to authorization only — it must not disable the CSWSH origin gate.
// ---------------------------------------------------------------------------------------------
test('the test exemption does not admit a foreign Origin', async t => {
  const h = new RelayHarness(TEST_MODE_ON);
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('origincase')]);
  t.after(() => host.close());

  // Destroyed before the handshake, so the client sees a transport error rather than a close code.
  const outcome = await attachOutcome(h.port, 'origincase', { origin: 'https://evil.example' });
  assert.equal(outcome.code, undefined, `a foreign Origin must never complete the handshake: ${JSON.stringify(outcome)}`);
  assert.ok(outcome.error, 'the upgrade must be destroyed, not politely closed');
  await sleep(50);
});
