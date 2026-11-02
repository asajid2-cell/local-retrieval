'use strict';
// The element map through the real relay: strip in deliverHostOutput, a separate viewer frame,
// gen-only dedupe, and a freshly attached viewer receiving the current map.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { once } = require('node:events');
const WebSocket = require('ws');
const { RelayHarness, FakeHost, sleep, waitFor, HOST_CAPS } = require('./harness');

const repo = path.join(__dirname, '..', '..');
const vectors = JSON.parse(fs.readFileSync(path.join(repo, 'docs', 'element-map-vectors.json'), 'utf8'));
const SEQ = Buffer.from(vectors.codec.entries[0].sequence_hex, 'hex');   // minimal empty frame, gen 1
const FRAME = vectors.codec.entries[0].frame;
const C1 = Buffer.concat([
  Buffer.from('9d32313333383b', 'hex'),
  SEQ.subarray(8, SEQ.length - 2),
  Buffer.from([0x9c]),
]);

const session = {
  name: 'emap', alive: true, created: 1, lastOut: 1, cols: 80, rows: 24,
  hasCommand: false, shellOnly: true, ready: true, kind: 'shell',
  sessionId: '', sessionUuid: 's-emap', aliases: [],
};

function collect(viewer) {
  const bytes = [];
  const texts = [];
  viewer.on('message', (data, isBinary) => {
    if (isBinary) bytes.push(Buffer.from(data));
    else texts.push(data.toString());
  });
  return { bytes: () => Buffer.concat(bytes), texts: () => texts.slice() };
}

async function boot(t) {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = new FakeHost(h.port);
  await host.connect();
  t.after(() => host.close());
  host.sendHello([session], { caps: [...HOST_CAPS, 'binaryFrames'] });
  return { h, host };
}

// The newest scrollback request, not the first one already in the log: a second viewer asks again,
// and answering with the stale rid would be ignored by the relay's rid guard.
async function newestSb(host, name, before) {
  await waitFor(() => host.messages.filter(m => m.t === 'sb' && m.s === name).length > before, name + ' scrollback');
  return host.messages.filter(m => m.t === 'sb' && m.s === name).at(-1);
}

async function attach(t, h, host, reply = '') {
  const before = host.messages.filter(m => m.t === 'sb' && m.s === 'emap').length;
  const viewer = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=emap`);
  const got = collect(viewer);
  await once(viewer, 'open');
  t.after(() => viewer.close());
  host.sendScrollback('emap', reply, await newestSb(host, 'emap', before));
  await sleep(80);
  return { viewer, got };
}

const maps = got => got.texts().filter(s => s[0] === 'E').map(s => JSON.parse(s.slice(1)));

test('a stream with no element-map frame reaches the viewer byte-for-byte', async t => {
  const { h, host } = await boot(t);
  const { got } = await attach(t, h, host);
  const payload = Buffer.from('plain \u001b[1mbold\u001b[0m text\r\nno frame here', 'utf8');

  host.sendOutputBinary(0, payload.toString('utf8'));
  await sleep(150);
  assert.deepEqual(got.bytes().subarray(-payload.length), payload, 'the payload must arrive unmodified');
  assert.deepEqual(maps(got), [], 'no map frame without a sequence');
});

test('the sequence is stripped and the decoded map arrives as its own frame', async t => {
  const { h, host } = await boot(t);
  const { got } = await attach(t, h, host);
  const before = Buffer.from('before', 'utf8');
  const after = Buffer.from('after', 'utf8');

  host.sendOutputBinary(0, before.toString() + SEQ.toString('latin1') + after.toString());
  await sleep(150);

  assert.deepEqual(got.bytes().subarray(-(before.length + after.length)), Buffer.concat([before, after]),
    'the OSC must not reach the text stream');
  assert.deepEqual(maps(got), [FRAME], 'one decoded map');
});

test('the 8-bit introducer and terminator are stripped the same way', async t => {
  const { h, host } = await boot(t);
  const { got } = await attach(t, h, host);
  const before = Buffer.from('A', 'utf8');
  const after = Buffer.from('B', 'utf8');

  host.ws.send(Buffer.concat([
    Buffer.from([1, 2, 0, 0]),
    before, C1, after,
  ]));
  await sleep(150);

  assert.deepEqual(got.bytes().subarray(-2), Buffer.concat([before, after]));
  assert.deepEqual(maps(got), [FRAME]);
});

test('the same gen is deduped; a new gen replaces it', async t => {
  const { h, host } = await boot(t);
  const { got } = await attach(t, h, host);

  host.sendOutputBinary(0, SEQ.toString('latin1'));
  await sleep(120);
  host.sendOutputBinary(0, SEQ.toString('latin1'));      // heartbeat: byte-identical, same gen
  await sleep(120);
  assert.deepEqual(maps(got), [FRAME], 'a repeated gen must not be forwarded twice');

  const next = { ...FRAME, gen: 2, cols: 100, rows: 30 };
  host.sendOutputBinary(0, makeSequence(next).toString('latin1'));
  await sleep(120);
  assert.deepEqual(maps(got), [FRAME, next], 'a changed gen replaces the map');
});

test('a viewer that attaches later receives the current map after its size frame', async t => {
  const { h, host } = await boot(t);
  await attach(t, h, host);
  host.sendOutputBinary(0, SEQ.toString('latin1'));
  await sleep(120);

  const before = host.messages.filter(m => m.t === 'sb' && m.s === 'emap').length;
  const second = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=emap`);
  const got = collect(second);
  await once(second, 'open');
  t.after(() => second.close());
  host.sendScrollback('emap', '', await newestSb(host, 'emap', before));
  await sleep(150);

  const texts = got.texts();
  const sizeAt = texts.findIndex(s => s[0] === 'd');
  const mapAt = texts.findIndex(s => s[0] === 'E');
  assert.ok(sizeAt >= 0, 'the size frame arrives');
  assert.ok(mapAt > sizeAt, 'the map follows the size frame');
  assert.deepEqual(JSON.parse(texts[mapAt].slice(1)), FRAME);
});

test('a frame inside the scrollback replay is stripped and delivered once', async t => {
  const { h, host } = await boot(t);
  const replay = Buffer.concat([Buffer.from('replayed ', 'utf8'), SEQ]);
  const { got } = await attach(t, h, host, replay.toString('latin1'));
  await sleep(150);

  assert.ok(got.bytes().toString('utf8').includes('replayed '), 'the replay text arrives');
  assert.equal(got.bytes().toString('utf8').includes('\u001b]21338'), false, 'the replay OSC is stripped');
  assert.deepEqual(maps(got), [FRAME]);
});

// Re-encode a frame with the same codec the fixture pins, so a test frame is never hand-written.
function makeSequence(frame) {
  const body = Buffer.from(JSON.stringify(frame), 'utf8').toString('base64url');
  return Buffer.concat([Buffer.from('1b5d32313333383b', 'hex'), Buffer.from(body, 'latin1'), Buffer.from('1b5c', 'hex')]);
}
