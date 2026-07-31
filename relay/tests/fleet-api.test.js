// GET /api/fleet — the fleet glance. One request must answer "what is every session doing right now"
// with a hard-bounded snippet per row, and it must NEVER be the slow path: tails fan out in parallel,
// a short cache absorbs refresh bursts, and a silent or offline host degrades rows instead of failing.
const test = require('node:test');
const assert = require('node:assert/strict');
const { RelayHarness, waitFor } = require('./harness');

function hostedSession(name, sessionId = `${name}-id`) {
  return {
    name, alive: true, created: 2000, lastOut: 2000, cols: 100, rows: 30,
    hasCommand: true, shellOnly: false, ready: true, kind: 'command',
    sessionId, aliases: [], tail: 'cached tail',
  };
}

// Correlate replies to the exact in-flight tail request so a later fleet call never reuses an
// already-answered rid (FakeHost.waitFor also matches messages it has already seen).
function tailAnswerer(host) {
  const answered = new Set();
  return async function answer(name, text, sig = '') {
    const req = await host.waitFor(
      m => m.t === 'tail' && m.s === name && !answered.has(m.rid),
      `tail request for ${name}`,
      8000,
    );
    answered.add(req.rid);
    host.sendTail(name, text, { sig, request: req });
    return req;
  };
}

test('one /api/fleet request covers the whole fleet, each row with its own snippet', async () => {
  const h = new RelayHarness();
  await h.start();
  try {
    const names = ['fleet-a', 'fleet-b', 'fleet-c'];
    const host = await h.connectHost(names.map(n => hostedSession(n)));
    const answer = tailAnswerer(host);

    const pending = h.request('GET', '/api/fleet');
    for (const n of names) await answer(n, `working on ${n}`);
    const res = await pending;

    assert.equal(res.status, 200);
    assert.equal(res.body.sessions.length, 3);
    assert.deepEqual(res.body.sessions.map(s => s.name).sort(), [...names].sort());
    const snippets = res.body.sessions.map(s => s.snippet);
    assert.equal(new Set(snippets).size, 3, `snippets should be distinct: ${JSON.stringify(snippets)}`);
    for (const row of res.body.sessions) {
      assert.equal(row.snippet, `working on ${row.name}`);
      assert.equal(row.snippetDegraded, false);
      assert.equal(row.hosted, true);
    }
  } finally {
    await h.stop();
  }
});

test('snippets are capped to the advertised line and byte bounds', async () => {
  const h = new RelayHarness();
  await h.start();
  try {
    const host = await h.connectHost([hostedSession('capcase')]);
    const answer = tailAnswerer(host);

    const lines = [];
    for (let i = 0; i < 8; i++) lines.push(`line ${i}`);
    lines.push('X'.repeat(5000));   // second-to-last: survives the line slice, must be byte-trimmed
    lines.push('final line');

    const pending = h.request('GET', '/api/fleet');
    await answer('capcase', lines.join('\n'));
    const res = await pending;

    assert.equal(res.status, 200);
    assert.equal(res.body.snippetLines, 2);
    assert.equal(res.body.snippetBytes, 2048);
    const snippet = res.body.sessions[0].snippet;
    assert.ok(snippet.length <= res.body.snippetBytes, `snippet was ${snippet.length} chars`);
    assert.ok(
      snippet.split('\n').length <= res.body.snippetLines,
      `snippet had ${snippet.split('\n').length} lines`,
    );
    assert.ok(snippet.endsWith('final line'), 'the newest line must survive the trim');
  } finally {
    await h.stop();
  }
});

test('muxd tail signatures pass through untouched and are never validated', async () => {
  const h = new RelayHarness();
  await h.start();
  try {
    const names = ['sig-yes', 'sig-no', 'sig-bogus'];
    const host = await h.connectHost(names.map(n => hostedSession(n)));
    const answer = tailAnswerer(host);

    const pending = h.request('GET', '/api/fleet');
    await answer('sig-yes', 'signed output', 'sig-abc');
    await answer('sig-no', 'unsigned output');
    await answer('sig-bogus', 'bogus output', 'not-a-real-signature!!');
    const res = await pending;

    assert.equal(res.status, 200);
    const by = Object.fromEntries(res.body.sessions.map(s => [s.name, s]));
    assert.equal(by['sig-yes'].snippetSig, 'sig-abc');
    assert.equal(by['sig-no'].snippetSig, '');
    // The relay holds no key: a bogus signature is relayed verbatim and the row is still served.
    assert.equal(by['sig-bogus'].snippetSig, 'not-a-real-signature!!');
    assert.equal(by['sig-bogus'].snippet, 'bogus output');
  } finally {
    await h.stop();
  }
});

test('tail fan-out is parallel: a silent host costs one timeout, not N', { timeout: 60000 }, async () => {
  const h = new RelayHarness();
  await h.start();
  try {
    // Host connects and reports 3 sessions but answers no tail at all.
    await h.connectHost(['slow-a', 'slow-b', 'slow-c'].map(n => hostedSession(n)));

    const started = Date.now();
    const res = await h.request('GET', '/api/fleet');
    const elapsed = Date.now() - started;

    assert.equal(res.status, 200);
    assert.equal(res.body.sessions.length, 3);
    // Serial fetches would cost 3 x 2500ms; Promise.all costs about one 2500ms timeout.
    assert.ok(elapsed < 6000, `fleet took ${elapsed}ms — fan-out looks serial`);
    for (const row of res.body.sessions) {
      assert.equal(row.snippetDegraded, true);
      assert.equal(row.snippetSig, '');
    }
  } finally {
    await h.stop();
  }
});

test('an offline host still yields a fast 200 with a valid degraded shape', async () => {
  const h = new RelayHarness();
  await h.start();
  try {
    const host = await h.connectHost([hostedSession('offlinecase')]);
    host.close();
    await waitFor(async () => {
      const health = await h.json('GET', '/api/health');
      return health.host && !health.host.connected ? health : null;
    }, 'host drop observed');

    const started = Date.now();
    const res = await h.request('GET', '/api/fleet');
    const elapsed = Date.now() - started;

    assert.equal(res.status, 200);
    assert.equal(res.body.hostUp, false);
    assert.equal(res.body.hostProtocolOk, false);
    // hostSessions is cleared on drop, so the list legitimately degrades to legacy-only (usually empty).
    assert.ok(Array.isArray(res.body.sessions));
    assert.ok(elapsed < 2000, `offline fleet took ${elapsed}ms — it must not wait on a dead host`);
  } finally {
    await h.stop();
  }
});

test('the fleet cache absorbs a refresh burst instead of re-tailing the host', async () => {
  const h = new RelayHarness();
  await h.start();
  try {
    const host = await h.connectHost([hostedSession('burstcase')]);
    const answer = tailAnswerer(host);

    const first = h.request('GET', '/api/fleet');
    await answer('burstcase', 'burst output');
    const firstRes = await first;
    assert.equal(firstRes.status, 200);
    assert.equal(firstRes.body.sessions[0].snippet, 'burst output');

    const secondRes = await h.request('GET', '/api/fleet');
    assert.equal(secondRes.status, 200);
    assert.equal(secondRes.body.sessions[0].snippet, 'burst output');

    const tails = host.messages.filter(m => m.t === 'tail');
    assert.ok(tails.length < 2, `expected the 5s cache to serve the second call; saw ${tails.length} tail requests`);
  } finally {
    await h.stop();
  }
});
