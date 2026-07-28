const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const REPO = path.join(__dirname, '..');

function read(rel) {
  return fs.readFileSync(path.join(REPO, ...rel.split('/')), 'utf8');
}

// Static scanner: for every `fetch(` occurrence, take the following ~400 chars as
// the call text and flag it MUTATING when that text carries an explicit
// method:'POST'|'PUT'|'PATCH'|'DELETE'. A mutating raw fetch is EXEMPT when any of
// the 3 source lines preceding the fetch carries a `raw-fetch-allowlist:` marker.
// Returns the surviving (line, snippet) list — every entry is a policy violation.
function rawMutatingFetches(source) {
  const lines = source.split('\n');
  const survivors = [];
  const NEEDLE = 'fetch(';
  const MUTATING = /method\s*:\s*['"](POST|PUT|PATCH|DELETE)['"]/;
  for (let idx = source.indexOf(NEEDLE); idx !== -1; idx = source.indexOf(NEEDLE, idx + 1)) {
    const callText = source.slice(idx, idx + 400);
    if (!MUTATING.test(callText)) continue;
    // 1-based line of this fetch(
    const lineNo = source.slice(0, idx).split('\n').length;
    // 3 source lines preceding the fetch (lines lineNo-1 .. lineNo-3, 1-based)
    let exempt = false;
    for (let back = 1; back <= 3; back++) {
      const prev = lines[lineNo - 1 - back];
      if (prev !== undefined && prev.includes('raw-fetch-allowlist:')) { exempt = true; break; }
    }
    if (exempt) continue;
    survivors.push({ line: lineNo, snippet: callText.slice(0, 80) });
  }
  return survivors;
}

// ===========================================================================
// r.1.6.2 — static no-raw-mutating-fetch allowlist scan
// (later siblings append integration blocks below this delimited section)
// ===========================================================================

test('r.1.6.2 static: no un-allowlisted raw mutating fetch in browser sources', () => {
  for (const rel of ['public/index.html', 'public/projects.html']) {
    const survivors = rawMutatingFetches(read(rel));
    assert.deepEqual(survivors, [], `${rel} has un-allowlisted raw mutating fetch: ${JSON.stringify(survivors)}`);
  }
});

test('r.1.6.2 static: NEGATIVE CONTROL — scanner is not vacuously passing', () => {
  const raw = "await fetch(base+'/api/sessions/x',{method:'DELETE'});";
  assert.equal(rawMutatingFetches(raw).length, 1, 'bare raw mutating fetch must be flagged');
  const allowlisted = "// raw-fetch-allowlist: test\n" + raw;
  assert.equal(rawMutatingFetches(allowlisted).length, 0, 'allowlist marker within 3 lines must exempt');
});

test('r.1.6.2 static: index.html allowlist markers guard the raw-byte upload POSTs', () => {
  const index = read('public/index.html');
  const idxLines = index.split('\n');
  const markers = [];
  idxLines.forEach((l, i) => { if (l.includes('raw-fetch-allowlist:')) markers.push(i + 1); });
  assert.equal(markers.length, 2, `index.html must hold exactly 2 raw-fetch-allowlist markers, got ${markers}`);
  for (const m of markers) {
    let near = false;
    for (let d = -3; d <= 3; d++) {
      const l = idxLines[m - 1 + d];
      if (l && l.includes("fetch(base+'/api/upload?")) { near = true; break; }
    }
    assert.ok(near, `allowlist marker at line ${m} must sit within 3 lines of a raw-byte upload POST`);
  }
  const projMarkers = read('public/projects.html').split('\n').filter(l => l.includes('raw-fetch-allowlist:'));
  assert.equal(projMarkers.length, 0, 'projects.html must hold zero allowlist markers');
});

test('r.1.6.2 static: no XMLHttpRequest / sendBeacon escape hatches', () => {
  for (const rel of ['public/index.html', 'public/projects.html']) {
    const src = read(rel);
    assert.ok(!src.includes('XMLHttpRequest'), `${rel} must not use XMLHttpRequest`);
    assert.ok(!src.includes('sendBeacon'), `${rel} must not use sendBeacon`);
  }
});

test('r.1.6.2 static: both sources load intent-journal.js', () => {
  for (const rel of ['public/index.html', 'public/projects.html']) {
    assert.ok(read(rel).includes('<script src="intent-journal.js"></script>'), `${rel} must load intent-journal.js`);
  }
});

test('r.1.6.2 static: index.html routes residue mutations through sendIntent with the right verbs', () => {
  const index = read('public/index.html');
  const has = (re) => assert.ok(re.test(index), `index.html missing ${re}`);
  const count = (re) => (index.match(re) || []).length;
  has(/sendIntent\('POST',[^\n]*\/autoheal/);
  assert.equal(count(/sendIntent\('DELETE',[^\n]*\/api\/sessions\//g), 2, 'two DELETE /api/sessions/ sendIntent routes');
  has(/sendIntent\('PATCH',[^\n]*\/api\/sessions\//);
  has(/sendIntent\('PATCH',[^\n]*\/api\/uploads\//);
  has(/sendIntent\('DELETE',[^\n]*\/api\/uploads\//);
});

// ===========================================================================
// r.1.6.3 — intent-journal exactly-once integration (session.kill + autoheal)
// Drives a real relay child through the extracted harness. Proves that a
// byte-identical replay of a mutating request with the same intentId returns
// the STORED {code, body} while the side effect fires exactly once (frames are
// COUNTED, not merely awaited), and that reusing an intentId for a different
// operation is rejected 409. Depends on relay/server.js withMutationIntent
// wiring: DELETE /api/sessions/:name ('session.kill'), POST .../autoheal
// ('session.autoheal').
// ===========================================================================

const { RelayHarness, FakeHost } = require('./helpers/intent-harness');

function sleep(ms) { return new Promise(resolve => setTimeout(resolve, ms)); }

// Mirror of relay.test.js's shellSession shape — enough for the relay to treat
// the name as a live hosted session (hostedHas === true).
function shellSession(name) {
  return { name, alive: true, created: 1000, lastOut: 1000, cols: 100, rows: 30, hasCommand: false, shellOnly: true, ready: true, kind: 'shell', sessionId: '', aliases: [] };
}

test('r.1.6.3 integration: session.kill intent replays exactly-once and rejects fingerprint reuse', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([shellSession('kill-intent')]);
  t.after(() => host.close());

  // (a) First kill: no COMPLETED record yet, so the raw handler runs, ships the
  // kill frame to muxd, and blocks until the hosted row is confirmed gone.
  const first = h.request('DELETE', '/api/sessions/kill-intent', { intentId: 't-kill-1' });
  await host.waitFor(m => m.t === 'kill' && m.s === 'kill-intent', 'kill frame');
  host.sendKilled('kill-intent');
  const firstRes = await first;
  assert.equal(firstRes.status, 200);
  assert.deepEqual(firstRes.body, { ok: true });

  // Byte-identical replay: intentId 't-kill-1' is now COMPLETED, so the stored
  // {code, body} is replayed and the handler is skipped entirely.
  const replayRes = await h.request('DELETE', '/api/sessions/kill-intent', { intentId: 't-kill-1' });
  assert.equal(replayRes.status, firstRes.status);
  assert.deepEqual(replayRes.body, firstRes.body);

  // Exactly-once side effect: COUNT kill frames (a re-run would emit a second).
  await sleep(150);
  const killFrames = host.messages.filter(m => m.t === 'kill' && m.s === 'kill-intent');
  assert.equal(killFrames.length, 1, `expected exactly one kill frame, got ${killFrames.length}`);

  // (c) Same intentId, different session name => different fingerprint => 409.
  const conflict = await h.request('DELETE', '/api/sessions/kill-other', { intentId: 't-kill-1' });
  assert.equal(conflict.status, 409);
  assert.deepEqual(conflict.body, { error: 'intent id already used for a different operation' });
});

test('r.1.6.3 integration: session.autoheal intent replays exactly-once', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([shellSession('heal-intent')]);
  t.after(() => host.close());

  // (b) First autoheal on: raw handler pushes the boolean heal policy to muxd
  // and waits for the host to echo the new heal state back.
  const first = h.request('POST', '/api/sessions/heal-intent/autoheal', { on: true, intentId: 't-heal-1' });
  await host.waitFor(m => m.t === 'heal' && m.s === 'heal-intent', 'heal frame');
  host.sendSessions([{ ...shellSession('heal-intent'), heal: true }]);
  const firstRes = await first;
  assert.equal(firstRes.status, 200);
  assert.deepEqual(firstRes.body, { ok: true, name: 'heal-intent', autoheal: true });

  // Byte-identical replay: COMPLETED record replays the stored response, handler skipped.
  const replayRes = await h.request('POST', '/api/sessions/heal-intent/autoheal', { on: true, intentId: 't-heal-1' });
  assert.equal(replayRes.status, firstRes.status);
  assert.deepEqual(replayRes.body, firstRes.body);

  // The autoheal policy changed exactly once — COUNT heal frames pushed to muxd.
  await sleep(150);
  const healFrames = host.messages.filter(m => m.t === 'heal' && m.s === 'heal-intent');
  assert.equal(healFrames.length, 1, `expected exactly one heal frame, got ${healFrames.length}`);
});

// ===========================================================================
// r.1.6.4 - session.rename PATCH exactly-once replay + legacy no-intentId path
// ===========================================================================

test('r.1.6.4 integration: session.rename intent replays exactly-once and preserves legacy path', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([shellSession('rename-from')]);
  t.after(() => host.close());

  // (a) First rename with intentId
  const first = h.request('PATCH', '/api/sessions/rename-from', { name: 'rename-to', intentId: 't-rename-1' });
  await host.waitFor(m => m.t === 'rename' && m.s === 'rename-from' && m.to === 'rename-to', 'rename frame');
  host.sendSessions([shellSession('rename-to')]);
  const firstRes = await first;
  assert.equal(firstRes.status, 200);
  assert.deepEqual(firstRes.body, { ok: true, name: 'rename-to', hosted: true });

  // (b) Restart + byte-identical replay
  await h.restart();
  const host2 = new FakeHost(h.port);
  await host2.connect();
  host2.sendHello([shellSession('rename-to')]);
  t.after(() => host2.close());

  const replayRes = await h.request('PATCH', '/api/sessions/rename-from', { name: 'rename-to', intentId: 't-rename-1' });
  assert.equal(replayRes.status, firstRes.status);
  assert.deepEqual(replayRes.body, firstRes.body);

  // Exactly-once side effect: replay must NOT emit a rename frame to the NEW host
  await host2.assertNo(m => m.t === 'rename' && m.s === 'rename-from', 'no rename frame on host2 during replay');

  // Original (disconnected) host received exactly ONE rename frame (before restart)
  await sleep(150);
  const renameFrames = host.messages.filter(m => m.t === 'rename' && m.s === 'rename-from');
  assert.equal(renameFrames.length, 1, `expected exactly one rename frame, got ${renameFrames.length}`);

  // (c) Same intentId, different fingerprint -> 409
  const conflict = await h.request('PATCH', '/api/sessions/rename-from', { name: 'rename-other', intentId: 't-rename-1' });
  assert.equal(conflict.status, 409);
  assert.deepEqual(conflict.body, { error: 'intent id already used for a different operation' });

  // (d) Legacy no-intentId path still works
  host2.sendSessions([shellSession('rename-to')]);
  const legacy = h.request('PATCH', '/api/sessions/rename-to', { name: 'rename-back' });
  await host2.waitFor(m => m.t === 'rename' && m.s === 'rename-to' && m.to === 'rename-back', 'legacy rename frame');
  host2.sendSessions([shellSession('rename-back')]);
  const legacyRes = await legacy;
  assert.equal(legacyRes.status, 200);
  assert.deepEqual(legacyRes.body, { ok: true, name: 'rename-back', hosted: true });
});