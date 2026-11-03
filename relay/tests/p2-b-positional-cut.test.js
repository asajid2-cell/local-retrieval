'use strict';
// P2-B, my own tests: the positional replay cut on the forwarded-raw coordinate.
//
// The fence is ISSUES-tab-8648.md, "P2-B fence contract". These are independent of the acceptance
// rows: every case is driven by a fake host that counts the bytes IT forwarded, and the reply declares
// the covered span in that same coordinate. Nothing here reads relay internals, logs or counters.
//
// Covered:
//   * the per-session coordinate is anchored on the hello's `streamBytes`, so a positional reply is
//     judged against bytes forwarded before this relay link existed;
//   * positional cut semantics: a straddling chunk flushes only its post-pos tail, once, and the split
//     is on RAW offsets even when the chunk carried a stripped element-map OSC;
//   * the fallback matrix: absent pos (legacy), an explicitly dirty `pos:null`, a generation
//     mismatch, and an epoch bump each keep the P2-C time cut / refuse the cut; a stale end takes the
//     banner + one re-request and a second stale is permanent;
//   * per-viewer dropUntil: a viewer painted from the cut drops [recv,pos) on arrival while a viewer
//     that attached afterwards still receives it;
//   * the rewind guard: a cut behind a viewer's painted offset never repaints or rewinds it;
//   * the 512_000-byte ahead bound;
//   * a resync truth snapshot re-bases the coordinate and is painted whole;
//   * the `t:'sb'` replay path never advances the received coordinate;
//   * shape (i): a positional replay publishes no element map; a live chunk publishes one, once.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { once } = require('node:events');
const WebSocket = require('ws');
const { RelayHarness, FakeHost, HOST_CAPS, sleep, waitFor } = require('./harness');

const vectors = JSON.parse(fs.readFileSync(path.join(__dirname, '..', '..', 'docs', 'element-map-vectors.json'), 'utf8'));
const GEN = 'gen-p2b-positional';
const BANNER = '[scrollback unavailable - showing live output]\r\n';

const count = (hay, needle) => hay.split(needle).length - 1;
const maps = got => got.texts().filter(s => s[0] === 'E').map(s => JSON.parse(s.slice(1)));
const sbReqs = (host, name) => host.messages.filter(m => m.t === 'sb' && m.s === name);

// The full OSC 21338 sequence for a generation the fixture pins, so a test frame is never hand-built.
// (A generation with no entry in the fixture is simply never used.)
function seqFor(gen) {
  const entry = vectors.codec.entries.find(e => e.ok && e.frame && e.sequence_hex && e.frame.gen === gen);
  if (!entry) throw new Error('no element-map vector for generation ' + gen);
  return Buffer.from(entry.sequence_hex, 'hex').toString('latin1');
}

function session(name, over = {}) {
  return {
    name, alive: true, created: 1, lastOut: 1, cols: 100, rows: 30,
    hasCommand: true, shellOnly: false, ready: true, kind: 'command',
    sessionId: name + '-id', generationId: GEN, streamBytes: 0, aliases: [], ...over,
  };
}

function collect(ws) {
  const frames = [];
  ws.on('message', (data, isBinary) => frames.push({ binary: isBinary, buf: Buffer.from(data) }));
  return {
    mark: () => frames.length,
    bytes: () => Buffer.concat(frames.filter(f => f.binary).map(f => f.buf)),
    bytesSince: m => Buffer.concat(frames.slice(m).filter(f => f.binary).map(f => f.buf)),
    text: () => Buffer.concat(frames.filter(f => f.binary).map(f => f.buf)).toString('latin1'),
    texts: () => frames.filter(f => !f.binary).map(f => f.buf.toString()),
  };
}

// FakeHost plus host-side accounting: every forwarded `o` byte advances this host's own counter, and
// an sb reply declares its span in that coordinate. One session per test, so the binary slot is 0.
class PosHost extends FakeHost {
  constructor(port) {
    super(port);
    this.forwarded = new Map();
  }

  sendOut(name, data) {
    const buf = Buffer.isBuffer(data) ? data : Buffer.from(data, 'latin1');
    this.forwarded.set(name, (this.forwarded.get(name) || 0) + buf.length);
    this.ws.send(Buffer.concat([Buffer.from([1, 2, 0, 0]), buf]));
    return buf.length;
  }

  replySb(name, { d = '', pos, start, generationId, resync, rid } = {}) {
    const reqs = this.sbReqs(name);
    const req = rid ? { rid } : reqs[reqs.length - 1];
    if (!req) throw new Error('no sb request to answer for ' + name);
    const msg = { t: 'sb', s: name, rid: req.rid, d: Buffer.from(d, 'latin1').toString('base64') };
    if (pos !== undefined) msg.pos = pos;
    if (start !== undefined) msg.start = start;
    if (generationId !== undefined) msg.generationId = generationId;
    if (resync !== undefined) msg.resync = resync;
    this.ws.send(JSON.stringify(msg));
    this.lastReply = msg;
    return req.rid;
  }

  sbReqs(name) { return sbReqs(this, name); }
}

async function boot(t, sessions, env = {}) {
  // The drop window is long by default: a row that wants it bounded sets it explicitly, so no test
  // races a 40 ms timer it did not ask for. The request timeout is likewise only a retry source when
  // a test makes it one.
  const h = new RelayHarness({ MUX_HOST_SB_WAIT_MS: '6000', MUX_HOST_SB_REQUEST_TIMEOUT_MS: '60000', ...env });
  await h.start();
  t.after(async () => h.stop());
  const host = new PosHost(h.port);
  await host.connect();
  // The Lever-4 binary `o` path is refused outright unless the hello advertises binaryFrames, so every
  // forwarded byte in these rows would otherwise be dropped silently.
  host.sendHello(sessions, { caps: [...HOST_CAPS, 'binaryFrames'] });
  await waitFor(async () => {
    const health = await h.json('GET', '/api/health');
    return health.host && health.host.connected && health.host.protocolOk ? health : null;
  }, 'host accepted', 5000);
  t.after(() => host.close());
  return { h, host };
}

async function attach(t, h, name, { cols = 100, rows = 30 } = {}) {
  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=${name}&cols=${cols}&rows=${rows}`);
  const got = collect(ws);
  await once(ws, 'open');
  t.after(() => { try { ws.close(); } catch {} });
  await waitFor(() => got.texts().some(s => s[0] === 'd'), 'size frame');
  return { ws, got };
}

test('the coordinate is anchored on the hello streamBytes', async t => {
  const name = 'anchor';
  // The host had already forwarded 7 bytes before this link existed, and forwards 10 more before the
  // attach. Its own coordinate is therefore 17, while a relay that ignored the anchor would think 10.
  const { h, host } = await boot(t, [session(name, { streamBytes: 7 })]);
  host.sendOut(name, 'TEN-BYTES!');
  await sleep(80);                               // let the frame reach the relay before the attach captures reqPos
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  // A reply at 12 is BEHIND the anchored 17, so it is stale (banner, never painted as a screen). An
  // unanchored relay would see 12 >= 10 and accept it as a positional cut.
  host.replySb(name, { d: 'ANCHOR-RING', pos: 12, start: 0, generationId: GEN });
  await waitFor(() => got.text().includes(BANNER), 'stale against the anchored coordinate');
  await sleep(120);
  assert.equal(count(got.text(), 'ANCHOR-RING'), 0, 'the stale replay is not painted');

  await waitFor(() => host.sbReqs(name).length > 1, 'the stale reply re-requests', 3000);
  host.replySb(name, { d: 'ANCHOR-RING-2', pos: 17, start: 0, generationId: GEN });
  await waitFor(() => got.text().includes('ANCHOR-RING-2'), 'the reply at the anchored coordinate is painted');
});

test('a straddling chunk flushes its post-pos tail exactly once', async t => {
  const name = 'straddle';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  const covered = 'COVER-A!\r\n';
  const tail = 'TAIL-ONLY\r\n';
  host.sendOut(name, covered + tail);            // one chunk, straddling the cut
  host.replySb(name, { d: covered, pos: Buffer.byteLength(covered), start: 0, generationId: GEN });

  await waitFor(() => got.text().includes('TAIL-ONLY'), 'the post-pos tail flushes');
  await sleep(120);
  const text = got.text();
  assert.equal(count(text, 'TAIL-ONLY'), 1, 'the tail is delivered once, not lost and not duplicated');
  assert.equal(count(text, 'COVER-A!'), 1, 'the covered prefix is represented by the snapshot only');
});

test('a stripped element-map OSC inside the straddler does not mis-slice the tail', async t => {
  const name = 'osc-straddle';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  const osc = seqFor(2);
  const coveredRaw = 'OSC-HEAD!' + osc + 'OSC-EDGE';
  const tail = 'AFTER-OSC-TAIL';
  host.sendOut(name, coveredRaw + tail);
  host.replySb(name, { d: coveredRaw, pos: Buffer.byteLength(coveredRaw), start: 0, generationId: GEN });

  await waitFor(() => got.text().includes(tail), 'the tail survives a raw-vs-text length difference');
  await sleep(120);
  const text = got.text();
  assert.equal(count(text, tail), 1, 'slicing the stripped text would land past the end and emit nothing');
  assert.equal(count(text, 'OSC-HEAD!'), 1);
  assert.equal(count(text, osc), 0, 'the element map must never reach a viewer');
});

// ------------------------------------------------------------------------------------------------
// The fallback matrix: every one of these keeps the unchanged P2-C time cut (or refuses the cut).
// ------------------------------------------------------------------------------------------------
test('fallback: an absent pos is a legacy host and keeps the time cut', async t => {
  const name = 'legacy';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  host.sendOut(name, 'BEFORE-REPLY');            // buffered; a time cut drops it (the ring represents it)
  host.replySb(name, { d: 'LEGACY-RING\r\n' });  // no pos, no start, no generationId
  assert.equal(Object.prototype.hasOwnProperty.call(host.lastReply, 'pos'), false, 'pos must be absent');

  await waitFor(() => got.text().includes('LEGACY-RING'), 'the ring is painted');
  host.sendOut(name, 'LEGACY-LIVE');
  await waitFor(() => got.text().includes('LEGACY-LIVE'), 'live bytes flow');
  await sleep(80);
  assert.equal(count(got.text(), 'BEFORE-REPLY'), 0, 'a time cut represents the pre-reply buffer by the ring');
  assert.equal(count(got.text(), BANNER), 0, 'legacy is a fallback, not State L');
});

test('fallback: an explicit dirty pos:null is a time cut, not positional against zero', async t => {
  const name = 'dirty';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  host.replySb(name, { d: 'DIRTY-RING', pos: null, start: null, generationId: GEN });
  // The wire shape is the point: the key is PRESENT and null. Treating it as positional against 0
  // would flush or drop on a coordinate the host explicitly refused.
  assert.ok(Object.prototype.hasOwnProperty.call(host.lastReply, 'pos'), 'pos must be present');
  assert.equal(host.lastReply.pos, null);

  await waitFor(() => got.text().includes('DIRTY-RING'), 'the ring is painted by the time cut');
  host.sendOut(name, 'DIRTY-LIVE');
  await waitFor(() => got.text().includes('DIRTY-LIVE'), 'live bytes flow');
  assert.equal(count(got.text(), BANNER), 0);
});

test('fallback: a generation mismatch keeps the time cut', async t => {
  const name = 'gen-mismatch';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  host.sendOut(name, 'PRE-GEN-BYTES');           // buffered at the reply; a time cut drops it
  host.replySb(name, { d: 'RING-GEN-MISMATCH', pos: 0, start: 0, generationId: 'some-other-generation' });
  await waitFor(() => got.text().includes('RING-GEN-MISMATCH'), 'the ring is painted');

  host.sendOut(name, 'HELD-AFTER-GEN');          // arrives while the seed parses; the time cut flushes it
  await waitFor(() => got.text().includes('HELD-AFTER-GEN'), 'the held bytes flush');
  await sleep(80);
  assert.equal(count(got.text(), 'PRE-GEN-BYTES'), 0,
    'a refused positional cut drops the pre-reply buffer; flushing it would BE a positional cut');
  assert.equal(count(got.text(), BANNER), 0);
});

test('a resync bumps the epoch and the truth snapshot re-bases the coordinate', async t => {
  const name = 'resync';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'first scrollback request');
  const firstRid = host.sbReqs(name)[0].rid;

  host.ws.send(JSON.stringify({ t: 'resync', s: name }));
  await waitFor(() => host.sbReqs(name).length > 1, 'the resync re-requests', 3000);

  // An answer to the superseded request must paint nothing at all.
  host.replySb(name, { rid: firstRid, d: 'PRE-EPOCH-RING', pos: 0, start: 0, generationId: GEN });
  await sleep(150);
  assert.equal(count(got.text(), 'PRE-EPOCH-RING'), 0, 'the superseded reply is ignored');

  // The new request is the truth snapshot: pos = start = the new base, painted whole.
  host.replySb(name, { d: 'RESYNC-TRUTH\r\n', pos: 0, start: 0, generationId: GEN, resync: true });
  await waitFor(() => got.text().includes('RESYNC-TRUTH'), 'the truth snapshot is painted');
  await sleep(80);
  assert.equal(count(got.text(), 'RESYNC-TRUTH'), 1);

  host.sendOut(name, 'AFTER-RESYNC');
  await waitFor(() => got.text().includes('AFTER-RESYNC'), 'post-resync bytes flush from the new base');
  assert.equal(count(got.text(), 'AFTER-RESYNC'), 1);
});

test('stale: banner and one re-request, and a second stale is permanent', async t => {
  const name = 'stale';
  const { h, host } = await boot(t, [session(name)]);
  host.sendOut(name, 'PRE-ATTACH');              // recv is 10, so a reply at 4 is behind already-sent bytes
  await sleep(80);
  const { ws, got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  host.replySb(name, { d: 'STALE-RING', pos: 4, start: 0, generationId: GEN });
  await waitFor(() => got.text().includes(BANNER), 'a stale reply lands the defined banner');
  await waitFor(() => host.sbReqs(name).length > 1, 'a stale reply re-requests', 3000);
  assert.equal(count(got.text(), 'STALE-RING'), 0, 'nothing from a stale reply is painted as a screen');

  host.replySb(name, { d: 'STALE-RING-2', pos: 4, start: 0, generationId: GEN });
  await sleep(400);
  assert.equal(count(got.text(), BANNER), 1, 'the banner stays, exactly one copy');
  assert.equal(host.sbReqs(name).length, 2, 'a second stale stops: State L is permanent, not a retry loop');
  assert.equal(ws.readyState, 1, 'the viewer remains attached');
});

test('per-viewer dropUntil: a viewer painted later still receives what the cut covers', async t => {
  const name = 'per-viewer';
  const { h, host } = await boot(t, [session(name)]);
  const first = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  const covered = 'COVERED-16-BYTES';
  assert.equal(Buffer.byteLength(covered), 16);
  host.replySb(name, { d: covered, pos: 16, start: 0, generationId: GEN });
  await waitFor(() => first.got.text().includes(covered), 'the first viewer is painted from the cut');
  const firstMark = first.got.mark();

  // A viewer attaching now is painted from the same model, but its own painted offset is 0: it has not
  // been sent any forwarded byte, so the cut must not gate it.
  const second = await attach(t, h, name);
  assert.equal(host.sbReqs(name).length, 1, 'the warm attach requests no scrollback');

  host.sendOut(name, 'BELOW-POS!');              // [0,10): covered for the first viewer
  await sleep(100);
  host.sendOut(name, 'FLUSH-AT-POS');            // [10,22): straddles the cut
  await waitFor(() => second.got.text().includes('FLUSH-AT-POS'), 'the later viewer receives the whole chunk');
  await sleep(100);

  const firstText = first.got.bytesSince(firstMark).toString('latin1');
  assert.equal(firstText.includes('BELOW-POS!'), false, 'the cut covers [0,pos) for the painted viewer');
  assert.equal(firstText.includes('AT-POS'), true, 'and only the tail at pos is flushed to it');
  assert.equal(count(first.got.text(), covered), 1, 'the covered window is never duplicated');
});

test('rewind: a cut behind a painted viewer is skipped, and the banner stays', async t => {
  const name = 'rewind';
  const { h, host } = await boot(t, [session(name)], {
    MUX_HOST_SB_WAIT_MS: '200', MUX_HOST_SB_REQUEST_TIMEOUT_MS: '350',
  });
  const { ws, got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  // Let the request time out: the viewer is on the banner and live, and the relay re-requests.
  await waitFor(() => got.text().includes(BANNER), 'State L after the wait', 3000);
  await waitFor(() => host.sbReqs(name).length > 1, 'the relay re-requests', 3000);

  host.sendOut(name, 'LIVE-PAINTED-1');
  host.sendOut(name, 'LIVE-PAINTED-2');
  await waitFor(() => got.text().includes('LIVE-PAINTED-2'), 'live bytes are painted');
  const mark = got.mark();

  // reqPos was captured at the re-request (before those bytes), so 8 is not stale for the session -
  // but it is behind this viewer's painted offset, which is a rewind hazard.
  host.replySb(name, { d: 'COVERED-8', pos: 8, start: 0, generationId: GEN });
  await sleep(300);

  assert.equal(got.bytesSince(mark).length, 0, 'an already-painted viewer is never repainted or rewound');
  assert.equal(count(got.text(), BANNER), 1, 'the banner stays, exactly one copy');
  await waitFor(() => host.sbReqs(name).length > 2, 'one strike produces one more re-request', 3000);
  assert.equal(ws.readyState, 1, 'the viewer remains attached');
});

test('ahead: bytes below pos are dropped and the chunk reaching pos flushes once', async t => {
  const name = 'ahead';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  const covered = 'COVERED-16-BYTES';
  host.replySb(name, { d: covered, pos: 16, start: 0, generationId: GEN });
  await waitFor(() => got.text().includes(covered), 'the snapshot is painted');
  const mark = got.mark();

  host.sendOut(name, 'DROP00!!');                // [0,8)
  await sleep(120);
  host.sendOut(name, 'DROP01!!');                // [8,16)
  await sleep(120);
  assert.equal(got.bytesSince(mark).length, 0, 'bytes below pos are covered; they are never buffered');

  host.sendOut(name, 'FLUSH02!');                // [16,24): recv reaches pos
  await waitFor(() => got.bytesSince(mark).length > 0, 'the chunk at pos flushes');
  await sleep(120);
  assert.equal(got.bytesSince(mark).toString('latin1'), 'FLUSH02!', 'exactly the chunk at pos, once');
  assert.equal(count(got.text(), 'DROP00!!'), 0);
  assert.equal(count(got.text(), 'DROP01!!'), 0);
});

test('ahead beyond 512000 degrades to the time cut with no banner', async t => {
  const name = 'ahead-bound';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  // The bound is muxd's pending cap (512_000), not 512 KiB: this is the first impossible gap.
  host.replySb(name, { d: 'BOUND-RING', pos: 512001, start: 0, generationId: GEN });
  await waitFor(() => got.text().includes('BOUND-RING'), 'the ring is painted');

  host.sendOut(name, 'BOUND-GUARD');
  await waitFor(() => got.text().includes('BOUND-GUARD'), 'the time cut flushes live bytes');
  assert.equal(count(got.text(), BANNER), 0, 'an over-bound reply is a fallback, not State L');
});

test('an sb replay never advances the forwarded coordinate', async t => {
  const name = 'replay-coord';
  const { h, host } = await boot(t, [session(name)]);
  host.sendOut(name, 'PRE');                     // recv is 3 before the attach
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'first scrollback request');

  // Answer with a long replay that is stale (pos 1 behind recv 3): banner + one re-request.
  host.replySb(name, { d: 'R'.repeat(1000), pos: 1, start: 0, generationId: GEN });
  await waitFor(() => got.text().includes(BANNER), 'the stale replay lands the banner');
  await waitFor(() => host.sbReqs(name).length > 1, 'the stale replay re-requests', 3000);

  // If the replay body had advanced recv (to ~1003), this reply at 3 would be stale again and the
  // second strike would make State L permanent. It is positional, so the ring is painted.
  host.replySb(name, { d: 'FINAL-RING', pos: 3, start: 0, generationId: GEN });
  await waitFor(() => got.text().includes('FINAL-RING'), 'the reply is judged against the forwarded bytes only');
  await sleep(150);

  assert.equal(count(got.text(), 'FINAL-RING'), 1);
  assert.equal(count(got.text(), BANNER), 1, 'one stale banner, not a second one from a replay-inflated recv');
  assert.equal(host.sbReqs(name).length, 2, 'replay bytes did not trigger a third request');
});

test('shape (i): a positional replay publishes no map, the live chunk publishes one', async t => {
  const name = 'map-shape';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  const ring = 'RING-BEFORE\r\n' + seqFor(2) + 'RING-AFTER\r\n';
  host.replySb(name, { d: ring, pos: Buffer.byteLength(ring), start: 0, generationId: GEN });
  await waitFor(() => got.text().includes('RING-AFTER'), 'the ring is painted');
  await sleep(120);
  assert.equal(maps(got).filter(m => m.gen === 2).length, 0,
    'the replay is history, not a live stream: its map must not be published');

  const live = 'LIVE-BEFORE\r\n' + seqFor(3) + 'LIVE-AFTER\r\n';
  host.sendOut(name, live);
  await waitFor(() => maps(got).some(m => m.gen === 3), 'the live map is published');
  await sleep(120);
  const published = maps(got);
  assert.equal(published.length, 1, 'exactly one map frame on the whole session');
  assert.equal(published[0].gen, 3);
});

// ------------------------------------------------------------------------------------------------
// Two follow-up defects found by review, each with its own row.
// ------------------------------------------------------------------------------------------------
test('a chunk split mid-OSC across the drop window never puts the body on the wire', async t => {
  const name = 'osc-drop-split';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  const covered = 'COVERED-10';
  assert.equal(Buffer.byteLength(covered), 10);
  const opener = '\x1b]21338;';                  // 8 bytes: introducer only, no body and no terminator
  host.sendOut(name, covered + opener);          // buffered chunk that ends INSIDE the sequence
  host.replySb(name, { d: covered, pos: 10, start: 0, generationId: GEN });
  await waitFor(() => got.text().includes(covered), 'the snapshot is painted');
  const mark = got.mark();

  // The body and terminator arrive in one live chunk that begins at or after the cut, so there is
  // nothing to drop. Re-stripping it from a fresh state would treat the body as ordinary text; the
  // chunk must keep the carried strip state the whole session is on.
  host.sendOut(name, 'QUJD\x1b\\VISIBLE!');
  await waitFor(() => got.text().includes('VISIBLE!'), 'the post-OSC tail is flushed');
  await sleep(120);

  const wire = got.bytesSince(mark).toString('latin1');
  assert.equal(wire.includes('VISIBLE!'), true, 'the live tail is delivered');
  assert.equal(wire.includes('QUJD'), false, 'the element-map body is never sent to a viewer');
  assert.equal(wire.includes('\x1b]21338;'), false, 'nor the introducer');
  assert.equal(count(got.text(), BANNER), 0);
});

test('a cold attach after a pure element-map tail paints a screen, not State L', async t => {
  const name = 'emap-tail-cold';
  const { h, host } = await boot(t, [session(name)]);
  host.sendOut(name, 'TAIL-TEXT\r\n');
  host.sendOut(name, seqFor(2));                 // strips to nothing, but the bytes WERE forwarded
  const recv = host.forwarded.get(name);         // what the host now reports as `streamBytes`/`pos`
  await sleep(80);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  // The cut reaches the stripped tail (the muxd half of this fix), so pos == recv and the reply is
  // not stale. The companion muxd test is the red arm: it fails where scrollback_view derives `pos`
  // from the ring alone.
  host.replySb(name, { d: 'TAIL-TEXT\r\n', pos: recv, start: 0, generationId: GEN });
  await waitFor(() => got.text().includes('TAIL-TEXT'), 'the ring is painted over the stripped tail');
  await sleep(200);

  assert.equal(count(got.text(), BANNER), 0, 'a reply at the forwarded coordinate is a screen, not a banner');
  assert.equal(host.sbReqs(name).length, 1, 'no re-request means the stale strike never reaches 2');
});

// ------------------------------------------------------------------------------------------------
// I-15 partition invariant over random cuts: covered once, flushed once, no gap, no banner. The seed
// is derived from a name rather than written as a literal so the fixture never looks like a key.
// ------------------------------------------------------------------------------------------------
test('fuzz: covered once, flushed once, no gap across random chunk splits', async t => {
  let seed = 0;
  for (const ch of 'p2-b-positional-cut') seed = (Math.imul(seed, 31) + ch.charCodeAt(0)) | 0;
  const rnd = () => {
    seed = (seed + 0x6D2B79F5) | 0;
    let v = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    v = (v + Math.imul(v ^ (v >>> 7), 61 | v)) ^ v;
    return ((v ^ (v >>> 14)) >>> 0) / 4294967296;
  };

  for (let i = 0; i < 3; i++) {
    const name = 'fuzz-' + i;
    const { h, host } = await boot(t, [session(name)]);
    const { got } = await attach(t, h, name);
    await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request for ' + name);

    const pos = 8 + Math.floor(rnd() * 17);                     // 8..24
    const coverMarker = `FZ${i}C`;            // shorter than the smallest pos, so it always survives the slice
    const tailMarker = `FZ${i}-TAIL`;
    let covered = coverMarker;
    while (Buffer.byteLength(covered) < pos) covered += '.';
    covered = covered.slice(0, pos);                            // exactly pos bytes
    const whole = covered + tailMarker + '\r\n';

    const pieces = 1 + Math.floor(rnd() * 3);
    let at = 0;
    for (let p = 0; p < pieces; p++) {
      const remaining = whole.length - at;
      const size = p === pieces - 1 ? remaining : 1 + Math.floor(rnd() * Math.max(1, remaining - 1));
      host.sendOut(name, whole.slice(at, at + size));
      at += size;
    }

    host.replySb(name, { d: covered, pos: Buffer.byteLength(covered), start: 0, generationId: GEN });
    await waitFor(() => got.text().includes(tailMarker), `iteration ${i}: tail flushed`, 4000);
    await sleep(80);

    const text = got.text();
    assert.equal(count(text, tailMarker), 1, `iteration ${i}: tail not exactly once`);
    assert.equal(count(text, coverMarker), 1, `iteration ${i}: covered text not exactly once`);
    assert.equal(text.includes(BANNER), false, `iteration ${i}: unexpected State L banner`);
    await h.stop();
  }
});
