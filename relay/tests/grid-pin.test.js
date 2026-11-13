'use strict';
// Grid ownership at the relay: a teed tab's grid is committed by muxd, and a device that takes it is
// asking, not painting. Rows T9-T12 of GRID-OWNERSHIP-CONTRACT.md.
//
// The relay may only paint a geometry muxd has published as committed; a pin request that the tee has
// not acked must change nothing on any viewer. The old non-uniform scale is gone.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { once } = require('node:events');
const WebSocket = require('ws');
const { RelayHarness, FakeHost, HOST_CAPS, sleep, waitFor } = require('./harness');

const GEN = 'gen-grid-ownership';

function tabSession(name, over = {}) {
  return {
    name, alive: true, created: 1, lastOut: 1, cols: 120, rows: 30,
    hasCommand: true, shellOnly: false, ready: true, kind: 'local-tab', owner: true,
    localFirst: true, localViewers: 1, sessionId: name + '-id', generationId: GEN,
    streamBytes: 0, aliases: [], gridOwner: 'local', gridOwnerLabel: '', gridEpoch: 10, ...over,
  };
}

function control(ws) {
  const frames = [];
  ws.on('message', (data, isBinary) => {
    if (isBinary) return;
    const s = Buffer.from(data).toString();
    if (s[0] === 'd') { try { frames.push(JSON.parse(s.slice(1))); } catch {} }
  });
  return { all: () => frames, last: () => frames[frames.length - 1], mark: () => frames.length, since: m => frames.slice(m) };
}

async function boot(t, sessions) {
  const h = new RelayHarness({});
  await h.start();
  t.after(async () => h.stop());
  const host = new FakeHost(h.port);
  await host.connect();
  host.sendHello(sessions, { caps: [...HOST_CAPS] });
  await waitFor(async () => {
    const health = await h.json('GET', '/api/health');
    return health.host && health.host.connected && health.host.protocolOk ? health : null;
  }, 'host accepted', 5000);
  t.after(() => host.close());
  return { h, host };
}

async function attach(t, h, name, cols = 100, rows = 30) {
  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=${name}&cols=${cols}&rows=${rows}`);
  const got = control(ws);
  await once(ws, 'open');
  t.after(() => { try { ws.close(); } catch {} });
  await waitFor(() => got.all().length > 0, 'first control frame');
  return { ws, got };
}

const resizeFrames = host => host.messages.filter(m => m.t === 'resize');

test('T9 a viewer paints only the committed geometry, never a pending request', async t => {
  const name = 'grid-tab';
  const { h, host } = await boot(t, [tabSession(name)]);
  const { ws, got } = await attach(t, h, name, 100, 30);

  assert.equal(got.last().cols, 120, 'the tab\'s committed width is painted');
  assert.equal(got.last().gridOwner, 'local');
  assert.equal(got.last().tabOwned, true, 'local ownership is the "you cannot pin this" lock');

  const mark = got.mark();
  ws.send('P');                                   // take the grid at this device's viewport
  await waitFor(() => resizeFrames(host).length > 0, 'the relay asks muxd');
  const req = resizeFrames(host)[0];
  assert.equal(req.epoch, 11, 'the request carries the committed epoch + 1');
  assert.ok(req.owner && req.owner !== 'local', 'and names the requesting device');
  assert.equal(req.cols, 100);
  await sleep(150);
  assert.equal(got.since(mark).some(f => f.cols === 100), false,
    'a request that has not been acked must not resize any viewer');

  // muxd publishes the committed grid after the tee's ack.
  host.sendSessions([tabSession(name, { cols: 100, rows: 30, gridOwner: 'pin', gridEpoch: 11, gridOwnerLabel: 'this device' })]);
  await waitFor(() => got.all().some(f => f.cols === 100), 'the acked grid is painted');
  const painted = got.all().filter(f => f.cols === 100).pop();
  assert.equal(painted.gridOwner, 'mine');
  assert.equal(painted.gridEpoch, 11);
  assert.equal(painted.tabOwned, false, 'tabOwned must not outlive local ownership');
});

test('T10 a second tap releases the grid with no geometry', async t => {
  const name = 'grid-release';
  const { h, host } = await boot(t, [tabSession(name, { gridOwner: 'pin', gridEpoch: 11, gridOwnerLabel: 'a phone' })]);
  const { ws, got } = await attach(t, h, name, 100, 30);
  await waitFor(() => got.all().length > 0, 'first control frame');
  assert.equal(got.last().gridOwner, 'other', 'this viewer is not the owner');

  ws.send('P0');                                   // release
  await waitFor(() => resizeFrames(host).length > 0, 'the release reaches muxd');
  const req = resizeFrames(host)[0];
  assert.equal(req.owner, 'local');
  assert.equal(req.epoch, 12);
  assert.equal('cols' in req, false, 'a release must not dictate the pane size');
  assert.equal('rows' in req, false);

  host.sendSessions([tabSession(name, { gridOwner: 'local', gridEpoch: 12 })]);
  await waitFor(() => got.all().some(f => f.gridOwner === 'local' && f.gridEpoch === 12), 'the pane grid returns');
});

test('a refused epoch retries exactly once at muxd\'s current epoch + 1', async t => {
  const name = 'grid-retry';
  const { h, host } = await boot(t, [tabSession(name, { gridEpoch: 10 })]);
  const { ws } = await attach(t, h, name, 100, 30);
  ws.send('P');
  await waitFor(() => resizeFrames(host).length === 1, 'first request');
  assert.equal(resizeFrames(host)[0].epoch, 11);

  // muxd declines a stale epoch and tells us where it actually is; the relay's own record may predate
  // a muxd restart, so guessing from the session list would retry the same stale epoch forever.
  host.ws.send(JSON.stringify({ t: 'gridAck', s: name, epoch: 11, ok: false, reason: 'stale epoch', gridEpoch: 40 }));
  await waitFor(() => resizeFrames(host).length === 2, 'one retry');
  assert.equal(resizeFrames(host)[1].epoch, 41);
  assert.equal(resizeFrames(host)[1].owner, resizeFrames(host)[0].owner);

  host.ws.send(JSON.stringify({ t: 'gridAck', s: name, epoch: 41, ok: false, reason: 'stale epoch', gridEpoch: 40 }));
  await sleep(200);
  assert.equal(resizeFrames(host).length, 2, 'a second refusal is final, not a retry loop');
});

test('a request the local terminal superseded is answered and never retried', async t => {
  const name = 'grid-superseded';
  const { h, host } = await boot(t, [tabSession(name, { gridEpoch: 10 })]);
  const { ws } = await attach(t, h, name, 100, 30);
  ws.send('P');
  await waitFor(() => resizeFrames(host).length === 1, 'take the grid');
  assert.equal(resizeFrames(host)[0].epoch, 11);

  // The user typed at the PC while the take was in flight. The local terminal wins; the superseded
  // epoch is still answered so the relay's pending record resolves - and it must NOT be retried,
  // because a retry would take the grid straight back from the person who just typed.
  host.ws.send(JSON.stringify({ t: 'gridAck', s: name, epoch: 11, ok: false, reason: 'superseded by local reclaim', gridEpoch: 11 }));
  await sleep(250);
  assert.equal(resizeFrames(host).length, 1, 'a superseded request is not retried');

  // The pin is gone with it: the next tap is a fresh take, not a release of a grid this device never held.
  ws.send('P');
  await waitFor(() => resizeFrames(host).length === 2, 'a fresh take goes out');
  const fresh = resizeFrames(host)[1];
  assert.ok(fresh.owner && fresh.owner !== 'local', 'the chip is not stuck believing it owns the grid');
  // The relay only knows the committed epoch from its session list (10), so the fresh take is 11. If
  // muxd has already reserved 11 for the reclaim it refuses with its current epoch and the one-retry
  // path moves it to 12 - that path is covered by the stale-epoch test.
  assert.ok(Number(fresh.epoch) >= 11, 'the next request moves past the committed epoch');
});

test('T13 the pinning device disconnecting releases the grid to local', async t => {
  const name = 'grid-disconnect';
  const { h, host } = await boot(t, [tabSession(name, { gridEpoch: 10 })]);
  const { ws } = await attach(t, h, name, 100, 30);

  ws.send('P');                                    // this connection becomes the pin record's device
  await waitFor(() => resizeFrames(host).length === 1, 'take the grid');
  const take = resizeFrames(host)[0];

  // Closing the last viewer of that device is the relay's immediate release; muxd's TTL is only the
  // backstop for a relay that is gone.
  ws.close();
  await waitFor(() => resizeFrames(host).length === 2, 'a release goes out on disconnect', 4000);
  const release = resizeFrames(host)[1];
  assert.equal(release.owner, 'local');
  assert.equal('cols' in release, false, 'a release must not dictate the pane size');
  assert.equal('rows' in release, false);
  assert.ok(Number(release.epoch) >= Number(take.epoch), 'the release never goes back in time');
});

test('T13b a persisted pin for a device that is gone is released when the last viewer leaves', async t => {
  // PINS ARE PERSISTED, so the pin record can outlive the viewer list: a relay restart, or a viewer that
  // closes while its take is still in flight, leaves a pin whose device matches no client. The id path then
  // returns early and the pane stays committed to a device that is gone. The empty-viewer condition is the
  // backstop, and this row seeds exactly that state on disk before the relay starts.
  const name = 'grid-ghost-pin';
  const h = new RelayHarness({});
  fs.writeFileSync(path.join(h.tmp, 'pins.json'),
    JSON.stringify([[name, { deviceId: 'ghost-device', label: 'gone', cols: 100, rows: 30, at: Date.now() }]]));
  await h.start();
  t.after(async () => h.stop());
  const host = new FakeHost(h.port);
  await host.connect();
  host.sendHello([tabSession(name, { gridOwner: 'pin', gridOwnerLabel: 'gone', cols: 100, rows: 30 })], { caps: [...HOST_CAPS] });
  await waitFor(async () => {
    const health = await h.json('GET', '/api/health');
    return health.host && health.host.connected && health.host.protocolOk ? health : null;
  }, 'host accepted', 5000);
  t.after(() => host.close());

  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=${name}&cols=80&rows=24&dev=someone-else`);
  t.after(() => { try { ws.close(); } catch {} });
  await once(ws, 'open');
  await sleep(250);
  host.messages.length = 0;
  ws.close();
  await waitFor(() => resizeFrames(host).length === 1, 'the pane gets the grid back', 4000);
  const release = resizeFrames(host)[0];
  assert.equal(release.owner, 'local', 'a pinned tab with no viewers left must be released to the pane');
  assert.equal('cols' in release, false, 'a release must not dictate the pane size');
});

test('T11 no non-uniform screen transform remains in the viewer', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');
  assert.equal(/FLEX_SCALE_FLOOR|FLEX_STRETCH_MAX|applyFlexTransform|flexScaleTarget|flexStretchTarget/.test(html), false,
    'the flex constants and helpers must be deleted');
  assert.equal(/\.style\.transform\s*=\s*['"]scale\(/.test(html), false,
    'nothing may scale the screen element');
  assert.equal(/tabFlexSx|tabFlexSy/.test(html), false);
});

test('T11b a re-request needs a whole cell of overflow and a post-ack settle window', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');
  const at = html.indexOf('function checkOwnedCellDrift(){');
  assert.notEqual(at, -1, 'the owned-cell drift check is gone');
  const body = html.slice(at, html.indexOf('function requestNativeRemeasure(){', at));
  // A re-request re-pins, resizes the child pty and clears the ring, so the app repaints its whole screen.
  // The 2px/1.5% rule re-committed 247x50 -> 248x50 after a take; a whole cell is the floor now.
  assert.match(body, /over>colW\+1/,
    'the drift check is back to a sub-column threshold — a rounding difference would re-commit the grid');
  assert.match(body, /__muxLastAckAt/,
    'the drift check no longer waits for the renderer to settle after the ack');
  // And the page must not report a viewport identical to the grid it already owns.
  const vp = html.indexOf('function sendViewport(dim){');
  assert.notEqual(vp, -1, 'sendViewport is gone');
  assert.match(html.slice(vp, vp + 600), /dim\.cols===term\.cols && dim\.rows===term\.rows/,
    'an owner now re-reports the grid it already holds, which re-pins and clears the ring for nothing');
  // The ack timestamp has to be written somewhere, or the settle window never opens.
  assert.match(html, /window\.__muxLastAckAt = Date\.now\(\)/,
    'nothing stamps the ack time the settle window reads');
});

test('T12 local ownership emits zero resize frames and the bytes are untouched', async t => {
  const name = 'grid-local';
  const { h, host } = await boot(t, [tabSession(name)]);
  const { ws, got } = await attach(t, h, name, 100, 30);
  ws.send('v' + JSON.stringify({ cols: 80, rows: 24 }));   // a viewer reporting a different viewport
  await sleep(200);
  assert.equal(resizeFrames(host).length, 0, 'the pane owns the grid; nobody resizes the tab');

  // Bytes travel on the terminal channel, not the control channel. Local ownership must not rewrite or
  // interleave anything: the viewer gets exactly what the host sent.
  const bytes = [];
  ws.on('message', (data, isBinary) => { if (isBinary) bytes.push(Buffer.from(data)); });
  host.sendOutput(name, 'BYTE-IDENTICAL\r\n');
  await waitFor(() => Buffer.concat(bytes).toString('latin1').includes('BYTE-IDENTICAL'), 'bytes delivered');
  await sleep(80);
  assert.equal(resizeFrames(host).length, 0, 'no resize frame on a local-owned tab');
  assert.ok(Buffer.concat(bytes).toString('latin1').endsWith('BYTE-IDENTICAL\r\n'),
    'the delivered bytes are the host\'s, unmodified');
});
