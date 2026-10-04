// Lever 4: the binary host-link frames.
//
// The `o` and unsigned `i` directions were JSON dicts with a base64 payload, so the relay decoded bytes
// muxd had just encoded -- per frame, per viewer. `binaryFrames` moves those two onto one binary websocket
// frame with a four-byte header. The tests below pin the three things that make that safe:
//   1. the relay forwards a binary `o` to the viewer as the raw bytes, with no JSON and no base64;
//   2. it upgrades its own `i` to binary ONLY when the host advertised the capability, and stays JSON
//      otherwise -- the compatibility guarantee, proven in both directions;
//   3. a frame with no slot, a bad version, or an unknown kind is refused whole, never guessed at.
//
// No npm deps beyond `ws`, and the harness is the same one the rest of the relay suite uses.
const test = require('node:test');
const assert = require('node:assert/strict');
const { once } = require('node:events');
const WebSocket = require('ws');
const { RelayHarness, FakeHost, sleep, HOST_CAPS } = require('./harness');

const session = {
  name: 'bin', alive: true, created: 1, lastOut: 1, cols: 80, rows: 24,
  hasCommand: false, shellOnly: true, ready: true, kind: 'shell',
  sessionId: '', sessionUuid: 's-1', aliases: [],
};

async function attachedViewer(h) {
  const viewer = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=bin`);
  await once(viewer, 'open');
  return viewer;
}

// The viewer's own bytes, as the browser would receive them. Output is binary on relay->viewer, so a
// TextDecoder over the frame is the honest read.
function collectViewerBytes(viewer) {
  const chunks = [];
  viewer.on('message', (data, isBinary) => { if (isBinary) chunks.push(Buffer.from(data)); });
  return { text: () => Buffer.concat(chunks).toString('utf8') };
}

test('a binary o frame reaches the viewer as raw bytes, with no JSON and no base64', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = new FakeHost(h.port);
  await host.connect();
  t.after(() => host.close());
  host.sendHello([session], { caps: [...HOST_CAPS, 'binaryFrames'] });
  const viewer = await attachedViewer(h);
  t.after(() => viewer.close());
  const sb = await host.waitFor(m => m.t === 'sb', 'scrollback');
  host.sendScrollback('bin', '', sb);
  const got = collectViewerBytes(viewer);

  // Slot 0 is `bin` (the only session), exactly as muxd would index its own hello.sessions.
  host.sendOutputBinary(0, '\u001b[31mHELLO\u001b[0m\r\n');
  await sleep(120);
  // The first live byte after a scrollback wait clears the screen once (CLEAR_SCREEN); the payload is
  // what follows it, and it must be present byte-for-byte with no JSON wrapper and no base64.
  assert.ok(got.text().endsWith('\u001b[31mHELLO\u001b[0m\r\n'),
    `the payload was not forwarded byte-for-byte: ${JSON.stringify(got.text())}`);
});

test('the relay upgrades its own i frame to binary only when the host advertises binaryFrames', async t => {
  // Capable host: the relay must send a BINARY `i` with a slot header, not a JSON dict.
  const h1 = new RelayHarness();
  await h1.start();
  t.after(async () => h1.stop());
  const capable = new FakeHost(h1.port);
  await capable.connect();
  t.after(() => capable.close());
  capable.sendHello([session], { caps: [...HOST_CAPS, 'binaryFrames'] });
  const v1 = await attachedViewer(h1);
  t.after(() => v1.close());
  const sb1 = await capable.waitFor(m => m.t === 'sb', 'scrollback');
  capable.sendScrollback('bin', '', sb1);
  v1.send('ikeys');
  const bin = await capable.waitFor(m => m.__binary && m.kind === 'i', 'binary input');
  assert.equal(bin.version, 1);
  assert.equal(bin.index, 0);
  assert.equal(bin.payload.toString('utf8'), 'keys');

  // Plain host: same viewer input, but the frame must stay the JSON dict it has always been.
  const h2 = new RelayHarness();
  await h2.start();
  t.after(async () => h2.stop());
  const plain = new FakeHost(h2.port);
  await plain.connect();
  t.after(() => plain.close());
  plain.sendHello([session]);                       // no binaryFrames
  const v2 = await attachedViewer(h2);
  t.after(() => v2.close());
  const sb2 = await plain.waitFor(m => m.t === 'sb', 'scrollback');
  plain.sendScrollback('bin', '', sb2);
  v2.send('ikeys');
  const json = await plain.waitFor(m => m.t === 'i', 'json input');
  assert.equal(json.__binary, undefined, 'a host without binaryFrames was sent a binary frame');
  assert.equal(Buffer.from(json.d, 'base64').toString('utf8'), 'keys');
});

test('a binary frame with no slot, a bad version or an unknown kind is refused whole', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = new FakeHost(h.port);
  await host.connect();
  t.after(() => host.close());
  host.sendHello([session], { caps: [...HOST_CAPS, 'binaryFrames'] });
  const viewer = await attachedViewer(h);
  t.after(() => viewer.close());
  const sb = await host.waitFor(m => m.t === 'sb', 'scrollback');
  host.sendScrollback('bin', '', sb);
  const got = collectViewerBytes(viewer);

  const mk = (ver, kind, index, text) => Buffer.concat([Buffer.from([ver, kind, (index >> 8) & 0xff, index & 0xff]), Buffer.from(text)]);

  host.ws.send(mk(1, 2, 9, 'NOSLOT'));             // slot 9 does not exist
  host.ws.send(mk(9, 2, 0, 'BADVER'));             // wrong version
  host.ws.send(mk(1, 7, 0, 'BADKIND'));            // unknown kind
  host.ws.send(Buffer.from([1, 2, 0]));            // truncated header
  await sleep(120);
  // Only the go-live CLEAR_SCREEN may reach the viewer; none of the four malformed payloads may.
  for (const junk of ['NOSLOT', 'BADVER', 'BADKIND'])
    assert.equal(got.text().includes(junk), false, `a malformed binary frame leaked ${junk}`);
});
