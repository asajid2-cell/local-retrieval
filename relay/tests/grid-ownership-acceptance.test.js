'use strict';
// Grid ownership acceptance: the relay layer of GRID-OWNERSHIP-CONTRACT.md (2026-11-09).
//
// Base: 39d5efa (leaf17/p2-b-positional-cut, frozen).
// LANDED-ON: (implementation head sha recorded when #17's grid-ownership commits land)
//
// Rows are numbered with the contract so a fence amendment maps onto a test name:
//   G3/T12  owner=local: fill and fit emit zero resize frames, and d.gridOwner is local
//   G2/T9   a pin request carries a superseding epoch; nothing paints until muxd publishes
//   G4      a stale epoch cannot roll the committed geometry back
//   G5/T10  last of two pins wins; the second tap releases; disconnect returns to local;
//           a local reclaim is painted
//   G1      one committed geometry: every viewer paints the same winSize
//   G6      byte fidelity: the stream is unchanged under owner=pin
//   T11     no sx/sy transform exists in the viewer source
//
// Every row drives the real relay over HTTP/ws with a fake host. Nothing reads relay internals:
// the host declares the committed record, the viewer asserts on the `d` frame it receives.
//
// Settled field names (contract §4 ruling, 2026-11-09): the session list and the `d` frame carry
// `gridOwner` ("local"|"pin" on the list, "local"|"mine"|"other" on d), `gridEpoch` and
// `gridOwnerLabel`. The existing boolean `owner` is the stream-owner/link gate and is NOT this value;
// these rows never accept a truthy string on `owner`.
//
// A pin is driven with the existing control frames: 'P' toggles, 'v' reports geometry. The pin
// request is {"t":"resize",s,cols,rows,epoch,owner:"<deviceId>"}. Release is
// {"t":"resize",s,epoch,owner:"local"} with cols/rows OMITTED (the tee knows the pane and acks it).
//
// `winSize` is accepted as {cols,rows} or [cols,rows]; a renderer cannot paint without a pair.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const WebSocket = require('ws');
const { once } = require('node:events');
const { RelayHarness, FakeHost, HOST_CAPS, sleep, waitFor } = require('./harness');

const GEN = 'grid-owner-acceptance';
const PANE = { cols: 209, rows: 51 };
const DEVICE = { cols: 100, rows: 30 };

// A teed PC tab as muxd reports it. `kind:local-tab` is what makes the relay treat the pane as the
// stream's physical screen. `gridOwner`/`gridEpoch`/`gridOwnerLabel` are the contract's new list
// fields; cols/rows are the committed geometry.
function session(name, over = {}) {
  return {
    name, alive: true, created: 1, lastOut: 1, cols: PANE.cols, rows: PANE.rows,
    hasCommand: true, shellOnly: false, ready: true, kind: 'local-tab',
    sessionId: name + '-id', generationId: GEN, aliases: [], streamBytes: 0,
    gridOwner: 'local', gridEpoch: 1, gridOwnerLabel: 'PC',
    ...over,
  };
}

function collect(ws) {
  const frames = [];
  ws.on('message', (data, isBinary) => frames.push({ binary: isBinary, buf: Buffer.from(data), at: Date.now() }));
  const ds = () => frames.filter(f => !f.binary).map(f => f.buf.toString()).filter(s => s[0] === 'd')
    .map(s => { try { return JSON.parse(s.slice(1)); } catch { return null; } }).filter(Boolean);
  return {
    frames,
    ds,
    lastD: () => { const list = ds(); return list[list.length - 1] || null; },
    text: () => Buffer.concat(frames.filter(f => f.binary).map(f => f.buf)).toString('latin1'),
    mark: () => frames.length,
    bytesSince: m => Buffer.concat(frames.slice(m).filter(f => f.binary).map(f => f.buf)),
  };
}

async function boot(t, sessions, env = {}) {
  const h = new RelayHarness({ ...env });
  await h.start();
  t.after(async () => h.stop());
  const host = new FakeHost(h.port);
  await host.connect();
  host.sendHello(sessions, { caps: [...HOST_CAPS, 'binaryFrames'] });
  await waitFor(async () => {
    const health = await h.json('GET', '/api/health');
    return health.host && health.host.connected && health.host.protocolOk ? health : null;
  }, 'host accepted', 5000);
  t.after(() => host.close());
  return { h, host };
}

async function attach(t, h, name, { dev = '', cols = DEVICE.cols, rows = DEVICE.rows } = {}) {
  const q = `/ws?session=${encodeURIComponent(name)}&cols=${cols}&rows=${rows}${dev ? `&dev=${encodeURIComponent(dev)}` : ''}`;
  const ws = new WebSocket(`ws://127.0.0.1:${h.port}${q}`);
  const got = collect(ws);
  await once(ws, 'open');
  t.after(() => { try { ws.close(); } catch {} });
  await waitFor(() => got.lastD(), 'first d frame');
  return { ws, got };
}

const resizes = host => host.messages.filter(m => m.t === 'resize');
function winSize(d) {
  if (!d || !d.winSize) return null;
  const w = d.winSize;
  const cols = w.cols != null ? w.cols : (Array.isArray(w) ? w[0] : null);
  const rows = w.rows != null ? w.rows : (Array.isArray(w) ? w[1] : null);
  return Number.isInteger(cols) && Number.isInteger(rows) ? { cols, rows } : null;
}
async function publish(host, list) {
  host.sendSessions(list);
  await sleep(150);   // one pump: the relay normalizes then recomputes changed sessions
}

// ---------------------------------------------------------------------------------------------
// G3 / T12 - owner=local: fill and fit are pure rendering. No resize frame, ever, and the size the
// viewer is told is the committed geometry. Fill and fit differ only in the viewer's grid math, so
// both report a geometry and NEITHER may reach the tee.
// ---------------------------------------------------------------------------------------------
test('T12a owner=local fill: zero resize frames, committed geometry, owner local', async t => {
  const name = 'g3-fill';
  const { h, host } = await boot(t, [session(name)]);
  const { ws, got } = await attach(t, h, name);

  const d0 = got.lastD();
  assert.equal(d0.gridOwner, 'local', 'the viewer must be told it renders under local ownership');
  assert.equal(d0.gridEpoch, 1, 'and the committed epoch');
  assert.deepEqual(winSize(d0), PANE, 'winSize is the committed pane geometry');
  assert.equal(d0.tabOwned, true, 'tabOwned stays true while the grid is local');

  ws.send('v' + JSON.stringify({ cols: 120, rows: 40 }));
  await sleep(250);
  assert.equal(resizes(host).length, 0, 'a fill re-render must not resize the shared PTY');
  assert.deepEqual(winSize(got.lastD()), PANE, 'and must not change the committed geometry');
});

// ---------------------------------------------------------------------------------------------
// T12b owner=local fit: the same, at a different reported grid, plus byte fidelity (G6): the host's
// bytes reach the viewer unchanged while a human is looking at a letterboxed fit.
// ---------------------------------------------------------------------------------------------
test('T12b owner=local fit: zero resize frames and byte-identical paint', async t => {
  const name = 'g3-fit';
  const { h, host } = await boot(t, [session(name)]);
  const { ws, got } = await attach(t, h, name);

  ws.send('v' + JSON.stringify({ cols: 100, rows: 24 }));
  await sleep(250);
  assert.equal(resizes(host).length, 0, 'a fit re-render must not resize the shared PTY');

  const mark = got.mark();
  host.sendOutput(name, 'G3-BYTE-FIDELITY\r\n');
  await waitFor(() => got.text().includes('G3-BYTE-FIDELITY'), 'host bytes reach the viewer');
  assert.equal(got.bytesSince(mark).toString('latin1'), 'G3-BYTE-FIDELITY\r\n',
    'the stream is untouched: no reflow, no in-band metadata (G6)');
  assert.equal(got.lastD().gridOwner, 'local', 'and ownership did not change');
});

// ---------------------------------------------------------------------------------------------
// G2 / T9 - a viewer takes the grid. The relay asks muxd to resize to THIS device's viewport with an
// epoch that supersedes the one it believes is current. Nothing changes on screen until muxd
// publishes the committed geometry; a request is not a commit.
// ---------------------------------------------------------------------------------------------
test('T9a pin request carries a superseding epoch and the device viewport', async t => {
  const name = 'g2-request';
  const { h, host } = await boot(t, [session(name)]);
  const { ws, got } = await attach(t, h, name, { dev: 'dev-a' });
  const before = got.lastD();
  const mark = got.mark();

  ws.send('P');
  const req = await waitFor(() => resizes(host)[0], 'a resize request for the pin', 4000);
  assert.equal(req.s, name);
  assert.equal(req.cols, DEVICE.cols, "the pin asks for this device's viewport, not a scaled grid");
  assert.equal(req.rows, DEVICE.rows);
  assert.ok(Number.isInteger(req.epoch) && req.epoch > before.gridEpoch,
    'the request names the epoch it supersedes: ' + JSON.stringify(req));
  assert.equal(req.owner, 'dev-a',
    'and the requesting device id, so muxd can enforce last-pin-wins: ' + JSON.stringify(req.owner));

  await sleep(250);
  const painted = got.frames.slice(mark).filter(f => !f.binary).map(f => f.buf.toString())
    .filter(s => s[0] === 'd').map(s => JSON.parse(s.slice(1)))
    .filter(d => JSON.stringify(winSize(d)) !== JSON.stringify(winSize(before)));
  assert.equal(painted.length, 0, 'no geometry may paint before muxd commits it (G2)');
});

// ---------------------------------------------------------------------------------------------
// T9b - after muxd publishes owner=pin with the acked geometry, the viewer paints it 1:1 and stops
// claiming tab ownership.
// ---------------------------------------------------------------------------------------------
test('T9b pin paints only after the commit, and tabOwned ends', async t => {
  const name = 'g2-commit';
  const { h, host } = await boot(t, [session(name)]);
  const { ws, got } = await attach(t, h, name, { dev: 'dev-a' });

  ws.send('P');
  await waitFor(() => resizes(host)[0], 'the pin request', 4000);
  await sleep(150);
  assert.deepEqual(winSize(got.lastD()), PANE, 'still the pane while unacked');

  await publish(host, [session(name, {
    gridOwner: 'pin', gridEpoch: 2, gridOwnerLabel: 'dev-a', cols: DEVICE.cols, rows: DEVICE.rows,
  })]);

  const d = got.lastD();
  assert.equal(d.gridOwner, 'mine', 'the owning device sees itself as the owner');
  assert.equal(d.gridEpoch, 2);
  assert.deepEqual(winSize(d), DEVICE, 'the acked geometry is what every renderer paints (G1/G2)');
  assert.equal(d.tabOwned, false, 'tabOwned must not outlive local ownership');
});

// ---------------------------------------------------------------------------------------------
// G4 - a publish carrying an epoch at or below the committed one is ignored: a late ack cannot roll
// the grid back. muxd is the authority; this row guards the relay from repainting an impossible
// record if one ever arrives.
// ---------------------------------------------------------------------------------------------
test('G4 a stale epoch cannot roll the committed geometry back', async t => {
  const name = 'g4-stale';
  const pinned = session(name, {
    gridOwner: 'pin', gridEpoch: 7, gridOwnerLabel: 'dev-a', cols: 100, rows: 30,
  });
  const { h, host } = await boot(t, [pinned]);
  const { got } = await attach(t, h, name, { dev: 'dev-a' });
  assert.deepEqual(winSize(got.lastD()), { cols: 100, rows: 30 }, 'starts at the committed pin');

  await publish(host, [session(name, { gridOwner: 'pin', gridEpoch: 5, gridOwnerLabel: 'dev-b', cols: 209, rows: 51 })]);
  await sleep(200);
  const d = got.lastD();
  assert.deepEqual(winSize(d), { cols: 100, rows: 30 }, 'a lower epoch must not move the geometry');
  assert.equal(d.gridEpoch, 7, 'nor regress the epoch');
});

// ---------------------------------------------------------------------------------------------
// G5 - two pins, last wins. The first device becomes 'other' without losing its paint; both render
// the same committed grid.
// ---------------------------------------------------------------------------------------------
test('G5 last of two pins wins and the first becomes other', async t => {
  const name = 'g5-two-pins';
  const { h, host } = await boot(t, [session(name)]);
  const a = await attach(t, h, name, { dev: 'dev-a' });
  const b = await attach(t, h, name, { dev: 'dev-b' });

  a.ws.send('P');
  await waitFor(() => resizes(host)[0], 'A pin request', 4000);
  await publish(host, [session(name, { gridOwner: 'pin', gridEpoch: 2, gridOwnerLabel: 'dev-a', cols: 100, rows: 30 })]);
  await waitFor(() => a.got.lastD().gridOwner === 'mine', 'A sees itself as owner');
  assert.equal(b.got.lastD().gridOwner, 'other', 'B is other');

  b.ws.send('P');
  await waitFor(() => resizes(host).length > 1, 'B pin request', 4000);
  const reqB = resizes(host)[1];
  assert.ok(reqB.epoch > 2, 'B supersedes the epoch it observed');
  await publish(host, [session(name, { gridOwner: 'pin', gridEpoch: 3, gridOwnerLabel: 'dev-b', cols: 110, rows: 34 })]);

  await waitFor(() => b.got.lastD().gridOwner === 'mine', 'B now owns the grid');
  assert.equal(a.got.lastD().gridOwner, 'other', 'A is other, not an error');
  assert.deepEqual(winSize(a.got.lastD()), { cols: 110, rows: 34 }, 'both paint one committed geometry (G1)');
  assert.deepEqual(winSize(b.got.lastD()), { cols: 110, rows: 34 });
});

// ---------------------------------------------------------------------------------------------
// G5 - the pinning device disconnects. The relay releases the pin and asks for the pane geometry
// back; once muxd publishes local, every remaining viewer is local again.
// ---------------------------------------------------------------------------------------------
test('G5 owner disconnect returns the grid to local', async t => {
  const name = 'g5-disconnect';
  const { h, host } = await boot(t, [session(name)]);
  const a = await attach(t, h, name, { dev: 'dev-a' });
  const b = await attach(t, h, name, { dev: 'dev-b' });

  a.ws.send('P');
  await waitFor(() => resizes(host)[0], 'A pin request', 4000);
  await publish(host, [session(name, { gridOwner: 'pin', gridEpoch: 2, gridOwnerLabel: 'dev-a', cols: 100, rows: 30 })]);
  await waitFor(() => a.got.lastD().gridOwner === 'mine', 'A owns the grid');

  a.ws.close();
  // Release is the restore semantics: owner "local", cols/rows OMITTED (only the tee knows the pane).
  const release = await waitFor(() => resizes(host).find(m => m.owner === 'local' && m.epoch > 2),
    'a release request', 4000);
  assert.equal(release.cols, undefined, 'the release must not invent a geometry (ruling: cols omitted)');
  assert.equal(release.rows, undefined);

  await publish(host, [session(name, { gridOwner: 'local', gridEpoch: 3, gridOwnerLabel: 'PC' })]);
  await waitFor(() => b.got.lastD().gridOwner === 'local', 'B is local again');
  assert.equal(b.got.lastD().tabOwned, true);
  assert.deepEqual(winSize(b.got.lastD()), PANE, 'and paints the pane geometry');
});

// ---------------------------------------------------------------------------------------------
// G5 - a local reclaim (the tee saw key/mouse activity) is published as owner=local with a new
// epoch. Every viewer must drop the pin chip and paint the pane; no viewer may keep a stale pin.
// ---------------------------------------------------------------------------------------------
test('G5 a local activity reclaim is painted for every viewer', async t => {
  const name = 'g5-reclaim';
  const pinned = session(name, { gridOwner: 'pin', gridEpoch: 4, gridOwnerLabel: 'dev-a', cols: 100, rows: 30 });
  const { h, host } = await boot(t, [pinned]);
  const a = await attach(t, h, name, { dev: 'dev-a' });
  const b = await attach(t, h, name, { dev: 'dev-b' });
  await waitFor(() => a.got.lastD().gridOwner === 'mine' && b.got.lastD().gridOwner === 'other', 'pinned paint');

  await publish(host, [session(name, { gridOwner: 'local', gridEpoch: 5, gridOwnerLabel: 'PC' })]);

  for (const v of [a, b]) {
    await waitFor(() => v.got.lastD().gridOwner === 'local', 'reclaimed to local');
    assert.equal(v.got.lastD().mine, false, 'no viewer keeps the pin chip');
    assert.equal(v.got.lastD().tabOwned, true);
    assert.deepEqual(winSize(v.got.lastD()), PANE, 'every viewer paints the pane (G1)');
  }
});

// ---------------------------------------------------------------------------------------------
// T10 - the second tap (or flex toggle) releases. The release request supersedes the pin epoch and
// the committed local geometry is painted back.
// ---------------------------------------------------------------------------------------------
test('T10 a second tap releases the pin', async t => {
  const name = 't10-release';
  const { h, host } = await boot(t, [session(name)]);
  const v = await attach(t, h, name, { dev: 'dev-a' });

  v.ws.send('P');
  await waitFor(() => resizes(host)[0], 'the pin request', 4000);
  await publish(host, [session(name, { gridOwner: 'pin', gridEpoch: 2, gridOwnerLabel: 'dev-a', cols: 100, rows: 30 })]);
  await waitFor(() => v.got.lastD().gridOwner === 'mine', 'pinned');

  v.ws.send('P');
  const release = await waitFor(() => resizes(host)[1], 'the release request', 4000);
  assert.equal(release.owner, 'local', 'a second tap releases with the restore semantics');
  assert.equal(release.cols, undefined, 'and no invented geometry');
  assert.ok(release.epoch > 2, 'the release supersedes the pin epoch');
  await publish(host, [session(name, { gridOwner: 'local', gridEpoch: 3, gridOwnerLabel: 'PC' })]);
  await waitFor(() => v.got.lastD().gridOwner === 'local', 'released to local');
  assert.deepEqual(winSize(v.got.lastD()), PANE);
});

// ---------------------------------------------------------------------------------------------
// T11 - the transform is deleted, not disabled. A source guard: no scale(sx,sy) path may exist in
// the viewer after the change. (The live assert is grid-flex-toggle.cjs.)
// ---------------------------------------------------------------------------------------------
test('T11 the viewer source has no sx/sy transform path', async () => {
  const src = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');
  for (const needle of ['applyFlexTransform', 'FLEX_SCALE_FLOOR', 'FLEX_STRETCH_MAX']) {
    assert.equal(src.includes(needle), false, `${needle} must be deleted, not merely unused (T11)`);
  }
  assert.equal(/\.xterm-screen[^\n]*scale\(/.test(src), false,
    'no rule may scale .xterm-screen: that is the pointer-desync path (I-1/T11)');
});

// ---------------------------------------------------------------------------------------------
// G1 / G6 - under owner=pin every renderer paints the same committed geometry, and the byte stream
// is still exactly the host's bytes. A pin buys a grid, never a rewrite.
// ---------------------------------------------------------------------------------------------
test('G1 one committed geometry and G6 byte fidelity under a pin', async t => {
  const name = 'g1-one-grid';
  const { h, host } = await boot(t, [session(name, {
    gridOwner: 'pin', gridEpoch: 9, gridOwnerLabel: 'dev-a', cols: 100, rows: 30,
  })]);
  const a = await attach(t, h, name, { dev: 'dev-a', cols: 100, rows: 30 });
  const b = await attach(t, h, name, { dev: 'dev-b', cols: 209, rows: 51 });
  assert.deepEqual(winSize(a.got.lastD()), { cols: 100, rows: 30 });
  assert.deepEqual(winSize(b.got.lastD()), { cols: 100, rows: 30 },
    'a differently-sized viewer paints the same committed grid');
  assert.equal(a.got.lastD().gridOwner, 'mine');
  assert.equal(b.got.lastD().gridOwner, 'other');

  const ma = a.got.mark(), mb = b.got.mark();
  host.sendOutput(name, 'PIN-BYTES-UNCHANGED\r\n');
  await waitFor(() => a.got.text().includes('PIN-BYTES-UNCHANGED') && b.got.text().includes('PIN-BYTES-UNCHANGED'),
    'both viewers receive the bytes');
  assert.equal(a.got.bytesSince(ma).toString('latin1'), 'PIN-BYTES-UNCHANGED\r\n');
  assert.equal(b.got.bytesSince(mb).toString('latin1'), 'PIN-BYTES-UNCHANGED\r\n');
});
