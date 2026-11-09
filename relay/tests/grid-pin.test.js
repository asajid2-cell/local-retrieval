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

test('T11 no non-uniform screen transform remains in the viewer', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');
  assert.equal(/FLEX_SCALE_FLOOR|FLEX_STRETCH_MAX|applyFlexTransform|flexScaleTarget|flexStretchTarget/.test(html), false,
    'the flex constants and helpers must be deleted');
  assert.equal(/\.style\.transform\s*=\s*['"]scale\(/.test(html), false,
    'nothing may scale the screen element');
  assert.equal(/tabFlexSx|tabFlexSy/.test(html), false);
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
