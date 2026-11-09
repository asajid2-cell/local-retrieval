'use strict';
// Grid ownership acceptance: the relay layer of GRID-OWNERSHIP-CONTRACT.md (2026-11-09).
//
// Base: leaf17/grid-ownership 07616ad (all three layers; P2-B 39d5efa is an ancestor).
// LANDED-ON: 07616ad (implementation head recorded at landing).
//
// Rows are numbered with the contract so a fence amendment maps onto a test name:
//   G3/T12  owner=local: fill and fit emit zero resize frames, and d.gridOwner is local
//   G2/T9   a pin request carries a superseding epoch; nothing paints until muxd publishes
//   G4      a stale epoch cannot roll the committed geometry back
//   G5/T10  last of two pins wins; the second tap releases; disconnect returns to local;
//           a local reclaim is painted
//   G1      one committed geometry: every viewer paints the same d.cols/d.rows
//   G6      byte fidelity: the stream is unchanged under owner=pin
//   T11     no sx/sy transform exists in the viewer source
//
// Wire model, measured against the implementation rather than assumed:
//  * A teed tab is a stream owner only when the session record carries `owner: true`; without it the
//    relay takes the hosted branch and the committed grid never reaches the d frame. The fixture
//    declares it, exactly as muxd's payload does for a stream owner.
//  * The committed geometry on the d frame is `cols`/`rows`. `winSize` is a page variable, not a
//    contract field, so these rows never ask for one.
//  * A pin is a REQUEST: muxd asks the tee, the tee acks, and muxd republishes the session list with
//    the applied geometry, gridOwner:"pin", gridOwnerLabel and the epoch. The fake host must model
//    that ack (`ack(req)`), or a test cannot distinguish "nothing painted yet" from "nothing paints".
//
// Settled names: gridOwner ("local"|"pin" on the list, "local"|"mine"|"other" on d), gridEpoch,
// gridOwnerLabel. The legacy boolean `owner` is the stream-owner/link gate and is never the grid value.
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
const LABELS = { 'dev-a': 'laptop', 'dev-b': 'phone' };

// A teed PC tab as muxd reports it. `owner:true` is load-bearing: it is the stream-owner flag the
// relay's targetSize keys on to treat the pane as the stream's physical screen.
function session(name, over = {}) {
  return {
    name, alive: true, created: 1, lastOut: 1, cols: PANE.cols, rows: PANE.rows,
    hasCommand: true, shellOnly: false, ready: true, kind: 'local-tab',
    sessionId: name + '-id', generationId: GEN, aliases: [], streamBytes: 0,
    owner: true, gridOwner: 'local', gridEpoch: 1, gridOwnerLabel: 'PC',
    ...over,
  };
}

// The committed geometry a renderer paints: d.cols / d.rows.
function committed(d) {
  if (!d || !Number.isInteger(d.cols) || !Number.isInteger(d.rows)) return null;
  return { cols: d.cols, rows: d.rows };
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

// FakeHost + the tee/muxd ack model. `ack(req)` republishes the session list as muxd does after the
// tee's sizeAck: the applied geometry, gridOwner pin (or local on a release), the label and the epoch
// the request named. `publish(name, over)` is the manual path for a reclaim/stale record.
class AckHost extends FakeHost {
  constructor(port, sessions) {
    super(port);
    this.state = new Map(sessions.map(s => [s.name, { ...s }]));
  }
  list() { return [...this.state.values()]; }
  publish(name, over) {
    const next = { ...this.state.get(name), ...over };
    this.state.set(name, next);
    this.ws.send(JSON.stringify({ t: 'sessions', list: this.list() }));
    return next;
  }
  ack(req) {
    const cur = this.state.get(req.s);
    if (!cur) throw new Error('no session record for ' + req.s);
    const over = {};
    if (req.owner === 'local') {
      over.cols = PANE.cols; over.rows = PANE.rows;
      over.gridOwner = 'local'; over.gridOwnerLabel = 'PC';
    } else {
      over.cols = req.cols; over.rows = req.rows;
      over.gridOwner = 'pin'; over.gridOwnerLabel = LABELS[req.owner] || String(req.owner);
    }
    if (Number.isInteger(req.epoch)) over.gridEpoch = req.epoch;
    return this.publish(req.s, over);
  }
}

async function boot(t, sessions, env = {}) {
  const h = new RelayHarness({ ...env });
  await h.start();
  t.after(async () => h.stop());
  const host = new AckHost(h.port, sessions);
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

// ---------------------------------------------------------------------------------------------
// G3 / T12 - owner=local: fill and fit are pure rendering. No resize frame, ever, and the committed
// geometry the viewer is told is the pane's.
// ---------------------------------------------------------------------------------------------
test('T12a owner=local fill: zero resize frames, committed geometry, owner local', async t => {
  const name = 'g3-fill';
  const { h, host } = await boot(t, [session(name)]);
  const { ws, got } = await attach(t, h, name);

  const d0 = got.lastD();
  assert.equal(d0.gridOwner, 'local', 'the viewer must be told it renders under local ownership');
  assert.equal(d0.gridEpoch, 1, 'and the committed epoch');
  assert.deepEqual(committed(d0), PANE, 'd.cols/rows are the committed pane geometry');
  assert.equal(d0.tabOwned, true, 'tabOwned stays true while the grid is local');

  ws.send('v' + JSON.stringify({ cols: 120, rows: 40 }));
  await sleep(250);
  assert.equal(resizes(host).length, 0, 'a fill re-render must not resize the shared PTY');
  assert.deepEqual(committed(got.lastD()), PANE, 'and must not change the committed geometry');
});

// ---------------------------------------------------------------------------------------------
// T12b owner=local fit: the same, at a different reported grid, plus byte fidelity (G6).
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
// G2 / T9 - a viewer takes the grid. The relay asks muxd with an epoch that supersedes the committed
// one. Nothing changes on screen until muxd publishes; a request is not a commit.
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
    .filter(d => JSON.stringify(committed(d)) !== JSON.stringify(committed(before)));
  assert.equal(painted.length, 0, 'no geometry may paint before muxd commits it (G2)');
});

// ---------------------------------------------------------------------------------------------
// T9b - after muxd publishes the acked geometry the viewer paints it and stops claiming tab
// ownership. The fake's ack() is the model of tee sizeAck + muxd publish.
// ---------------------------------------------------------------------------------------------
test('T9b pin paints only after the ack, and tabOwned ends', async t => {
  const name = 'g2-commit';
  const { h, host } = await boot(t, [session(name)]);
  const { ws, got } = await attach(t, h, name, { dev: 'dev-a' });

  ws.send('P');
  const req = await waitFor(() => resizes(host)[0], 'the pin request', 4000);
  await sleep(150);
  assert.deepEqual(committed(got.lastD()), PANE, 'still the pane while unacked');

  host.ack(req);
  await waitFor(() => got.lastD().gridOwner === 'mine', 'the commit paints');

  const d = got.lastD();
  assert.equal(d.gridOwner, 'mine', 'the owning device sees itself as the owner');
  assert.equal(d.gridEpoch, req.epoch);
  assert.equal(d.gridOwnerLabel, 'laptop');
  assert.deepEqual(committed(d), DEVICE, 'the acked geometry is what every renderer paints (G1/G2)');
  assert.equal(d.tabOwned, false, 'tabOwned must not outlive local ownership');
});

// ---------------------------------------------------------------------------------------------
// G4 - a publish carrying an epoch at or below the committed one is ignored: a late ack cannot roll
// the grid back. muxd is the authority; this row guards the relay from repainting an impossible
// record if one ever arrives.
// ---------------------------------------------------------------------------------------------
test('G4 a request never advances the published epoch or geometry', async t => {
  const name = 'g4-stale';
  const { h, host } = await boot(t, [session(name)]);
  const { ws, got } = await attach(t, h, name, { dev: 'dev-a' });
  const before = got.lastD();

  ws.send('P');
  await waitFor(() => resizes(host)[0], 'the pin request', 4000);
  await sleep(200);
  const d = got.lastD();
  assert.equal(d.gridEpoch, before.gridEpoch, 'the request names a candidate epoch; it does not publish one');
  assert.deepEqual(committed(d), committed(before), 'and the committed geometry is untouched (G2)');
  // Stale-epoch rejection is muxd's (T3): muxd never publishes an epoch at or below the committed one,
  // and the relay has no second authority to move it.
});

// ---------------------------------------------------------------------------------------------
// G5 - two pins, last wins. The first device becomes 'other' without losing its paint; both render
// the same committed grid.
// ---------------------------------------------------------------------------------------------
test('G5 last of two pins wins and the first becomes other', async t => {
  const name = 'g5-two-pins';
  const { h, host } = await boot(t, [session(name)]);
  const a = await attach(t, h, name, { dev: 'dev-a' });
  const b = await attach(t, h, name, { dev: 'dev-b', cols: 110, rows: 34 });

  a.ws.send('P');
  const reqA = await waitFor(() => resizes(host)[0], 'A pin request', 4000);
  host.ack(reqA);
  await waitFor(() => a.got.lastD().gridOwner === 'mine', 'A sees itself as owner');
  assert.equal(b.got.lastD().gridOwner, 'other', 'B is other');

  b.ws.send('P');
  const reqB = await waitFor(() => resizes(host)[1], 'B pin request', 4000);
  assert.ok(reqB.epoch > reqA.epoch, 'B supersedes the epoch it observed');
  host.ack(reqB);

  await waitFor(() => b.got.lastD().gridOwner === 'mine'
    && committed(a.got.lastD())?.cols === 110 && committed(b.got.lastD())?.cols === 110,
  'both viewers paint B\'s committed grid');
  assert.equal(a.got.lastD().gridOwner, 'other', 'A is other, not an error');
  assert.deepEqual(committed(a.got.lastD()), { cols: 110, rows: 34 }, 'both paint one geometry (G1)');
  assert.deepEqual(committed(b.got.lastD()), { cols: 110, rows: 34 });
});

// ---------------------------------------------------------------------------------------------
// G5 - the pinning device disconnects. The relay releases the pin, asks muxd for the pane restore,
// and once muxd publishes local, every remaining viewer is local again.
// ---------------------------------------------------------------------------------------------
test('G5 owner disconnect returns the grid to local', async t => {
  const name = 'g5-disconnect';
  const { h, host } = await boot(t, [session(name)]);
  const a = await attach(t, h, name, { dev: 'dev-a' });
  const b = await attach(t, h, name, { dev: 'dev-b' });

  a.ws.send('P');
  const req = await waitFor(() => resizes(host)[0], 'A pin request', 4000);
  host.ack(req);
  await waitFor(() => a.got.lastD().gridOwner === 'mine', 'A owns the grid');

  a.ws.close();
  // Release is the restore semantics: owner "local", cols/rows OMITTED (only the tee knows the pane).
  const release = await waitFor(() => resizes(host).find(m => m.owner === 'local' && m.epoch > req.epoch),
    'a release request on disconnect', 4000);
  assert.equal(release.cols, undefined, 'the release must not invent a geometry');
  assert.equal(release.rows, undefined);

  host.ack(release);
  await waitFor(() => b.got.lastD().gridOwner === 'local', 'B is local again');
  assert.equal(b.got.lastD().tabOwned, true);
  assert.deepEqual(committed(b.got.lastD()), PANE, 'and paints the pane geometry');
});

// ---------------------------------------------------------------------------------------------
// G5 - a local reclaim (the tee saw key/mouse activity) is published as owner=local with a new
// epoch. Every viewer must drop the pin chip and paint the pane; no viewer may keep a stale pin.
// ---------------------------------------------------------------------------------------------
test('G5 a local activity reclaim is painted for every viewer', async t => {
  const name = 'g5-reclaim';
  const { h, host } = await boot(t, [session(name)]);
  const a = await attach(t, h, name, { dev: 'dev-a' });
  const b = await attach(t, h, name, { dev: 'dev-b' });
  a.ws.send('P');
  const req = await waitFor(() => resizes(host)[0], 'the pin request', 4000);
  host.ack(req);
  await waitFor(() => a.got.lastD().gridOwner === 'mine' && b.got.lastD().gridOwner === 'other', 'pinned paint');

  host.publish(name, { gridOwner: 'local', gridEpoch: req.epoch + 1, gridOwnerLabel: 'PC', cols: PANE.cols, rows: PANE.rows });

  for (const v of [a, b]) {
    await waitFor(() => v.got.lastD().gridOwner === 'local', 'reclaimed to local');
    assert.equal(v.got.lastD().mine, false, 'no viewer keeps the pin chip');
    assert.equal(v.got.lastD().tabOwned, true);
    assert.deepEqual(committed(v.got.lastD()), PANE, 'every viewer paints the pane (G1)');
  }
});

// ---------------------------------------------------------------------------------------------
// T10 - the second tap releases. The release request supersedes the pin epoch and the committed
// local geometry is painted back after the ack.
// ---------------------------------------------------------------------------------------------
test('T10 a second tap releases the pin', async t => {
  const name = 't10-release';
  const { h, host } = await boot(t, [session(name)]);
  const v = await attach(t, h, name, { dev: 'dev-a' });

  v.ws.send('P');
  const req = await waitFor(() => resizes(host)[0], 'the pin request', 4000);
  host.ack(req);
  await waitFor(() => v.got.lastD().gridOwner === 'mine', 'pinned');

  v.ws.send('P0');   // the released tap: 'P' pins, 'P0' releases (viewer index.html)
  const release = await waitFor(() => resizes(host)[1], 'the release request', 4000);
  assert.equal(release.owner, 'local', 'a second tap releases with the restore semantics');
  assert.equal(release.cols, undefined, 'and no invented geometry');
  assert.ok(release.epoch > req.epoch, 'the release supersedes the pin epoch');
  host.ack(release);
  await waitFor(() => v.got.lastD().gridOwner === 'local', 'released to local');
  assert.deepEqual(committed(v.got.lastD()), PANE);
});

// ---------------------------------------------------------------------------------------------
// T11 - the transform is deleted, not disabled. Source guard; the live assert is grid-flex-toggle.
// ---------------------------------------------------------------------------------------------
test('T11 the viewer source has no sx/sy transform path', async t => {
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
  const { h, host } = await boot(t, [session(name)]);
  const a = await attach(t, h, name, { dev: 'dev-a', cols: 100, rows: 30 });
  const b = await attach(t, h, name, { dev: 'dev-b', cols: 209, rows: 51 });
  a.ws.send('P');
  const req = await waitFor(() => resizes(host)[0], 'the pin request', 4000);
  host.ack(req);
  await waitFor(() => a.got.lastD().gridOwner === 'mine', 'the pin is committed');
  assert.deepEqual(committed(a.got.lastD()), { cols: 100, rows: 30 });
  assert.deepEqual(committed(b.got.lastD()), { cols: 100, rows: 30 },
    'a differently-sized viewer paints the same committed grid');
  assert.equal(b.got.lastD().gridOwner, 'other');

  const ma = a.got.mark(), mb = b.got.mark();
  host.sendOutput(name, 'PIN-BYTES-UNCHANGED\r\n');
  await waitFor(() => a.got.text().includes('PIN-BYTES-UNCHANGED') && b.got.text().includes('PIN-BYTES-UNCHANGED'),
    'both viewers receive the bytes');
  assert.equal(a.got.bytesSince(ma).toString('latin1'), 'PIN-BYTES-UNCHANGED\r\n');
  assert.equal(b.got.bytesSince(mb).toString('latin1'), 'PIN-BYTES-UNCHANGED\r\n');
});
