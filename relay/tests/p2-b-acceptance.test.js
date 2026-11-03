'use strict';
// P2-B acceptance: the positional replay cut.
//
// Contract: ISSUES-tab-8648.md, "P2-B fence contract" (2026-11-03).
// Base: leaf17/p2-b-positional-cut off 81b1f60.
// LANDED-ON: 8cf91619264f903376318344c0bc694808b7564b (leaf17/p2-b-positional-cut, parent 81b1f60,
// verified against origin; muxd/muxd.py blob d481fbc605bf43bd0bef9562e8f2b0cf23fe834a,
// relay/server.js blob a49f16969aaa29923153d7d0f30e593e047df14c).
//
// Baseline on 81b1f60 (P2-C only, 2026-11-02): 8 pass / 9 fail.
//   GREEN (the P2-C fallback rows must stay green through the change): 04c, 06, 07, 08, 10, 12, 13, 15.
//   RED (each is a positional behavior that does not exist yet): 01, 01b, 02, 03/04a, 04b, 05, 09, 11, 14.
// The reds are semantic, not harness noise: 01/01b/02/14 lose the post-pos tail with the P2-C queue
// drop, 03/04a paint bytes below pos, 04b/05 never take the banner/re-request bound, 09 repaints an
// already-painted viewer, and 11 publishes a map from the replay.
//
// The hello MUST advertise binaryFrames: the Lever-4 binary `o` path is refused outright without it
// (server.js, host binary handler), so every forwarded byte in these rows would be silently dropped.
//
// Every row is fake-host driven, and the HOST is the source of truth for positions: it counts the
// forwarded `o` bytes it has sent and declares the covered span in its sb reply. No row reads relay
// internals, logs, or counters. A row that cannot be forced from the wire is not written here.
//
// Contract row 15 ("the P2-C 16-row suite, unchanged and green") is a suite-level assertion on
// relay/tests/p2-c-initial-paint.test.js, not a wire row, so it is not reproduced. The final test is
// the collateral-damage smoke that names it.
//
// Numbering follows the contract so a fence amendment maps onto a test name:
//   01 covered-once (incl. amendment (a): a stripped OSC inside the straddler)
//   02 ring-window loss closed
//   03 below-pos arrivals dropped
//   04 ahead: 04a catch-up flush, 04b never-catch-up State L, 04c beyond the 512_000-byte cap
//   05 stale end (banner/re-request, second stale -> permanent State L)
//   06 generation mismatch -> time cut
//   07 epoch bump (host resync) between request and reply -> positional refused
//   08 dirty: pos:null explicitly present -> time cut
//   09 no rewind: pos < painted skips the viewer
//   10 ground start: pre-start bytes never paint
//   11 shape (i): element map live-side only
//   12 resync truth snapshot
//   13 absent pos (legacy muxd) -> time cut
//   14 I-15 fuzz over random ground-state pos and chunk splits
//   15 P2-C collateral-damage smoke
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { once } = require('node:events');
const WebSocket = require('ws');
const { RelayHarness, FakeHost, HOST_CAPS, sleep, waitFor } = require('./harness');

const vectors = JSON.parse(fs.readFileSync(path.join(__dirname, '..', '..', 'docs', 'element-map-vectors.json'), 'utf8'));
const seqFor = gen => Buffer.from(
  vectors.codec.entries.find(e => e.ok && e.frame && e.frame.gen === gen && e.sequence_hex).sequence_hex, 'hex',
).toString('latin1');
const OLD_GEN = 2;
const NEW_GEN = 3;
const THE_GEN = 'gen-p2b-acceptance';

const BANNER = '[scrollback unavailable - showing live output]\r\n';
const CLEAR = Buffer.from(
  '\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l\x1b[?1015l\x1b[?2004l' +
  '\x1b[?1049l\x1b[?25h\x1b[0m\x1b[3J\x1b[2J\x1b[H',
  'latin1',
).toString('latin1');

const count = (hay, needle) => hay.split(needle).length - 1;
const maps = got => got.texts().filter(s => s[0] === 'E').map(s => JSON.parse(s.slice(1)));
const sbReqs = (host, name) => host.messages.filter(m => m.t === 'sb' && m.s === name);

// A terminal session as muxd reports it. `generationId` is what the sb reply must echo; `streamBytes`
// is the record-level anchor (absent = positional mode disabled for that session).
function session(name, over = {}) {
  return {
    name, alive: true, created: 1, lastOut: 1, cols: 100, rows: 30,
    hasCommand: true, shellOnly: false, ready: true, kind: 'command',
    sessionId: name + '-id', generationId: THE_GEN, streamBytes: 0, aliases: [], ...over,
  };
}

// Ordered viewer frames. binary frames are terminal bytes; text frames are `d`/`E` control frames.
function collect(ws) {
  const frames = [];
  ws.on('message', (data, isBinary) => frames.push({ binary: isBinary, buf: Buffer.from(data) }));
  return {
    frames,
    mark: () => frames.length,
    bytes: () => Buffer.concat(frames.filter(f => f.binary).map(f => f.buf)),
    bytesSince: m => Buffer.concat(frames.slice(m).filter(f => f.binary).map(f => f.buf)),
    text: () => Buffer.concat(frames.filter(f => f.binary).map(f => f.buf)).toString('latin1'),
    texts: () => frames.filter(f => !f.binary).map(f => f.buf.toString()),
  };
}

// FakeHost plus host-side byte accounting. Every forwarded `o` byte this host sends advances its own
// counter, and the sb reply declares spans in that same coordinate. One session per test, so the
// binary slot is always 0.
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

  forwardedBytes(name) { return this.forwarded.get(name) || 0; }
  sbReqs(name) { return sbReqs(this, name); }

  // The positional reply. `rid` selects an older request when a boundary superseded the latest one.
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
    this.lastReply = msg;   // the exact reply object, so a row can assert its wire shape
    return req.rid;
  }
}

async function boot(t, sessions, env = {}) {
  const h = new RelayHarness({ MUX_HOST_SB_WAIT_MS: '6000', ...env });
  await h.start();
  t.after(async () => h.stop());
  const host = new PosHost(h.port);
  await host.connect();
  // The Lever-4 binary `o` path is refused unless this hello advertises binaryFrames (server.js, binary
  // handler). Production muxd does advertise it, so the acceptance rows drive bytes exactly as it does.
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

// ---------------------------------------------------------------------------------------------
// 01 - a chunk straddling pos: the snapshot covers [start,pos), the tail flushes exactly once.
// ---------------------------------------------------------------------------------------------
test('01 covered-once: a straddling chunk flushes its post-pos tail exactly once', async t => {
  const name = 'covered-once';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  const covered = 'COVER-A!\r\n';                  // in the ring AND forwarded before the reply
  const tail = 'TAIL-ONLY\r\n';                    // forwarded, outside the covered span
  host.sendOut(name, covered + tail);              // one chunk, straddling the cut
  host.replySb(name, { d: covered, pos: Buffer.byteLength(covered), start: 0, generationId: THE_GEN });

  await waitFor(() => got.text().includes('TAIL-ONLY'), 'post-pos tail flushed');
  await sleep(120);
  const text = got.text();
  assert.ok(text.startsWith(CLEAR), 'a first paint is CLEAR + snapshot, never a bare suffix');
  assert.equal(count(text, 'TAIL-ONLY'), 1,
    'the tail must flush exactly once; a lost tail is the P2-C ring-window defect');
  assert.equal(count(text, 'COVER-A!'), 1,
    'the covered prefix is represented by the snapshot only; the buffered copy must be dropped');
});

// 01 (amendment a): the straddler contains an OSC 21338 that the replay path strips, so the covered
// span's TEXT is shorter than its RAW length. The flush must re-strip raw[pos:] - slicing the already
// stripped text would land past the end and lose the tail.
test('01b covered-once: a stripped OSC inside the straddler does not mis-slice the tail', async t => {
  const name = 'osc-straddler';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  const osc = seqFor(OLD_GEN);
  const coveredRaw = 'OSC-HEAD!' + osc + 'OSC-EDGE';
  const tail = 'AFTER-OSC-TAIL';
  host.sendOut(name, coveredRaw + tail);
  host.replySb(name, { d: coveredRaw, pos: Buffer.byteLength(coveredRaw), start: 0, generationId: THE_GEN });

  await waitFor(() => got.text().includes(tail), 'tail flushed past a stripped OSC');
  await sleep(120);
  const text = got.text();
  assert.equal(count(text, tail), 1,
    'the tail must survive the off-by-raw-length trap; text.slice(pos - rawStart) would emit nothing');
  assert.equal(count(text, 'OSC-HEAD!'), 1, 'the covered text appears once, from the snapshot');
  assert.equal(count(text, osc), 0, 'the raw OSC must never reach a viewer');
});

// ---------------------------------------------------------------------------------------------
// 02 - the ring-window loss: bytes forwarded after muxd serialized the ring, before the reply.
// ---------------------------------------------------------------------------------------------
test('02 ring-window loss closed: a byte forwarded before the reply is not dropped at the cut', async t => {
  const name = 'ring-window';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  const inRing = 'RING-WINDOW\r\n';
  const windowed = 'LOST-IN-P2C\r\n';              // serialized after the ring, arrives before the reply
  host.sendOut(name, inRing);
  await sleep(40);
  host.sendOut(name, windowed);
  host.replySb(name, { d: inRing, pos: Buffer.byteLength(inRing), start: 0, generationId: THE_GEN });

  await waitFor(() => got.text().includes('LOST-IN-P2C'), 'windowed byte painted');
  await sleep(120);
  const text = got.text();
  assert.equal(count(text, 'LOST-IN-P2C'), 1,
    'a byte forwarded after ring serialization but before the reply must paint exactly once');
  assert.equal(count(text, 'RING-WINDOW'), 1, 'the ring content appears once, from the snapshot');
});

// ---------------------------------------------------------------------------------------------
// 03/04a - pos > recv: arrivals below pos are DROPPED on arrival (never held and flushed), and the
// first chunk that reaches pos flushes once. One scenario, two rows.
// ---------------------------------------------------------------------------------------------
test('03/04a ahead: below-pos arrivals are dropped, the chunk reaching pos flushes once', async t => {
  const name = 'ahead';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  const covered = 'COVERED-16-BYTES';              // exactly 16 bytes, the covered span
  assert.equal(Buffer.byteLength(covered), 16);
  host.replySb(name, { d: covered, pos: 16, start: 0, generationId: THE_GEN });
  await waitFor(() => got.text().includes('COVERED-16-BYTES'), 'snapshot painted');
  const mark = got.mark();

  host.sendOut(name, 'DROP00!!');                  // [0,8)
  await sleep(120);
  host.sendOut(name, 'DROP01!!');                  // [8,16)
  await sleep(120);

  // Row 3: nothing below pos may paint, and it must not be buffered for a later flush.
  assert.equal(got.bytesSince(mark).length, 0,
    'bytes below pos are covered by the snapshot; they must be dropped on arrival');

  host.sendOut(name, 'FLUSH02!');                  // [16,24): recv reaches pos
  await waitFor(() => got.bytesSince(mark).length > 0, 'the chunk at pos flushes');
  await sleep(120);

  // Row 4a: exactly the chunk at pos, once, in order; the covered window never surfaces.
  assert.equal(got.bytesSince(mark).toString('latin1'), 'FLUSH02!',
    'the first chunk at or after pos flushes exactly once, and only it');
  const text = got.text();
  assert.equal(count(text, 'DROP00!!'), 0);
  assert.equal(count(text, 'DROP01!!'), 0);
});

// ---------------------------------------------------------------------------------------------
// 04b - never catch up: a dropUntil timer of HOST_SB_WAIT_MS bounds the window.
// The request timeout is set far out so the ONLY thing that can produce a re-request is the drop-window
// expiry. That makes "exactly one re-request" deterministic instead of racing the retry timer.
// ---------------------------------------------------------------------------------------------
test('04b ahead-never-catch-up: the drop window expires into State L and one re-request', async t => {
  const name = 'ahead-stall';
  const { h, host } = await boot(t, [session(name)], {
    MUX_HOST_SB_WAIT_MS: '200', MUX_HOST_SB_REQUEST_TIMEOUT_MS: '60000',
  });
  const { ws, got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  // A legal but unreachable cut: pos is ahead of recv and the host sends nothing more.
  host.replySb(name, { d: '', pos: 4096, start: 0, generationId: THE_GEN });

  await waitFor(() => got.text().includes(BANNER), 'State L after the bounded drop window', 3000);
  await waitFor(() => host.sbReqs(name).length > 1, 're-request after the bounded window', 3000);
  await sleep(400);                                  // longer than the wait, well inside the 60 s retry
  assert.equal(count(got.text(), BANNER), 1, 'exactly one banner');
  assert.equal(host.sbReqs(name).length, 2, 'exactly one re-request: a stall costs one request, not a loop');
  assert.equal(ws.readyState, 1, 'the viewer is still attached, not silently dropped');
});

// 04c - beyond the 512_000-byte pending cap the reply degrades to the time cut: no drop, no banner.
test('04c ahead-beyond-bound: pos - recv > 512000 degrades to the time cut', async t => {
  const name = 'ahead-bound';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  // The positional bound is muxd's pending cap: 512_000 bytes, not 512*1024. 512_000 ahead is the
  // largest legal gap; 512_001 is the smallest impossible one, so this row sits exactly on the edge.
  host.replySb(name, { d: 'BOUND-RING', pos: 512001, start: 0, generationId: THE_GEN });
  await waitFor(() => got.text().includes('BOUND-RING'), 'snapshot painted');
  host.sendOut(name, 'BOUND-GUARD');
  await waitFor(() => got.text().includes('BOUND-GUARD'), 'positional mode was refused, so bytes flush');
  assert.equal(count(got.text(), BANNER), 0, 'an over-bound reply is a fallback, not State L');
});

// ---------------------------------------------------------------------------------------------
// 05 - a stale end: pos < reqPos cannot be positional; banner/re-request, then State L.
// ---------------------------------------------------------------------------------------------
test('05 stale end: banner and re-request, and a second stale lands State L', async t => {
  const name = 'stale';
  const { h, host } = await boot(t, [session(name)], { MUX_HOST_SB_WAIT_MS: '400' });
  host.sendOut(name, 'PRE-ATTACH');                 // recv=10, so reqPos > pos below
  await sleep(80);
  const { ws, got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  host.replySb(name, { d: 'STALE-RING', pos: 4, start: 0, generationId: THE_GEN });
  await waitFor(() => got.text().includes(BANNER), 'a stale reply lands the banner');
  await waitFor(() => host.sbReqs(name).length > 1, 'a stale reply re-requests', 3000);
  assert.equal(count(got.text(), 'STALE-RING'), 0,
    'nothing from a stale reply is positional - it must not be painted as a screen');

  host.replySb(name, { d: 'STALE-RING-2', pos: 4, start: 0, generationId: THE_GEN });
  await sleep(400);
  assert.equal(count(got.text(), BANNER), 1, 'the banner stays, exactly one copy');
  assert.equal(host.sbReqs(name).length, 2, 'a second stale in a row stops: State L is permanent');
  assert.equal(ws.readyState, 1, 'the viewer remains attached');
});

// ---------------------------------------------------------------------------------------------
// 06 - generation mismatch: the reply is a fallback, not a positional cut.
// ---------------------------------------------------------------------------------------------
test('06 generation mismatch: the reply degrades to the time cut', async t => {
  const name = 'gen-mismatch';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  // Distinct from the ring text: an assertion that the ring "contains" the marker would pass vacuously.
  // The marker is buffered before the reply. A positional cut with pos=0 would flush it; a refused cut
  // drops the whole buffer (its bytes are not anchored to anything the relay can trust).
  host.sendOut(name, 'MISMATCH-BYTES');
  host.replySb(name, { d: 'RING-GEN-MISMATCH', pos: 0, start: 0, generationId: 'some-other-generation' });
  await waitFor(() => got.text().includes('RING-GEN-MISMATCH'), 'the ring is painted');
  host.sendOut(name, 'HELD-AFTER-GEN');            // held across the reply; a time cut flushes it
  await waitFor(() => got.text().includes('HELD-AFTER-GEN'), 'the time cut flushes the held bytes', 3000);
  assert.equal(count(got.text(), 'MISMATCH-BYTES'), 0,
    'a refused positional cut drops the pre-reply buffer; flushing it would be a positional cut');
});

// ---------------------------------------------------------------------------------------------
// 07 - a host resync between request and reply bumps the boundary epoch: no positional cut.
// ---------------------------------------------------------------------------------------------
test('07 epoch bump: a resync between request and reply refuses the positional cut', async t => {
  const name = 'epoch';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'first scrollback request');
  const firstRid = host.sbReqs(name)[0].rid;

  host.ws.send(JSON.stringify({ t: 'resync', s: name }));
  await waitFor(() => host.sbReqs(name).length > 1, 'the resync re-requests', 3000);

  // The answer to the superseded request must paint nothing at all.
  host.replySb(name, { rid: firstRid, d: 'PRE-EPOCH-RING', pos: 0, start: 0, generationId: THE_GEN });
  await sleep(150);
  assert.equal(count(got.text(), 'PRE-EPOCH-RING'), 0, 'the superseded reply must be ignored');

  // The new request is answered without a position: the P2-C time cut applies.
  host.replySb(name, { d: 'AFTER-EPOCH-RING', generationId: THE_GEN });
  await waitFor(() => got.text().includes('AFTER-EPOCH-RING'), 'the new ring is painted');
  host.sendOut(name, 'AFTER-EPOCH-LIVE');
  await waitFor(() => got.text().includes('AFTER-EPOCH-LIVE'), 'live bytes still flow');
});

// ---------------------------------------------------------------------------------------------
// 08 - dirty: pos is EXPLICITLY null; the reply falls back to the time cut.
// ---------------------------------------------------------------------------------------------
test('08 dirty: an explicit pos:null reply is a time cut, never positional against zero', async t => {
  const name = 'dirty';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  host.replySb(name, { d: 'DIRTY-RING', pos: null, start: null, generationId: THE_GEN });
  // The wire shape is the point of this row: `pos` is PRESENT and null, not absent. Row 13 sends the
  // absent-key form. Both route to the time cut, but they must stay distinguishable - an implementation
  // that only tests `pos === undefined` would treat this reply as positional against zero.
  assert.ok(Object.prototype.hasOwnProperty.call(host.lastReply, 'pos'), 'pos must be present');
  assert.equal(host.lastReply.pos, null, 'and explicitly null');
  await waitFor(() => got.text().includes('DIRTY-RING'), 'the ring is painted by the time cut');
  host.sendOut(name, 'DIRTY-LIVE');
  await waitFor(() => got.text().includes('DIRTY-LIVE'), 'live bytes flow');
  assert.equal(count(got.text(), BANNER), 0, 'a dirty reply is a fallback, not State L');
});

// ---------------------------------------------------------------------------------------------
// 09 - a viewer already painted live is skipped: no rewind, banner stays, one strike.
// ---------------------------------------------------------------------------------------------
test('09 no rewind: pos below what a viewer painted skips the viewer', async t => {
  const name = 'rewind';
  const { h, host } = await boot(t, [session(name)], {
    MUX_HOST_SB_WAIT_MS: '200', MUX_HOST_SB_REQUEST_TIMEOUT_MS: '350',
  });
  const { ws, got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  // Let the request time out so the viewer is on the banner and live, then the relay re-requests.
  await waitFor(() => got.text().includes(BANNER), 'State L after the wait', 3000);
  await waitFor(() => host.sbReqs(name).length > 1, 'the relay re-requests', 3000);

  // The viewer is live now, so its painted offset advances past the cut we are about to declare.
  host.sendOut(name, 'LIVE-PAINTED-1');
  host.sendOut(name, 'LIVE-PAINTED-2');
  await waitFor(() => got.text().includes('LIVE-PAINTED-2'), 'live bytes painted');
  const mark = got.mark();

  // reqPos was captured at the re-request (before the live bytes), so 8 is not stale for the
  // session - but it is below this viewer's painted offset, which makes it a rewind hazard.
  const reqPos = 8;
  host.replySb(name, { d: 'COVERED-8', pos: reqPos, start: 0, generationId: THE_GEN });
  await sleep(300);

  assert.equal(got.bytesSince(mark).length, 0,
    'an already-painted viewer must not be repainted or rewound by a cut behind it');
  assert.equal(count(got.text(), BANNER), 1, 'the banner stays, exactly one copy');
  await waitFor(() => host.sbReqs(name).length > 2, 'one strike -> one more re-request', 3000);
  assert.equal(ws.readyState, 1, 'the viewer remains attached');
});

// ---------------------------------------------------------------------------------------------
// 10 - ground start: bytes below start are unavailable and are never flushed.
// ---------------------------------------------------------------------------------------------
test('10 ground start: pre-start bytes never paint and the tail still flows', async t => {
  const name = 'ground-start';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  // Forward all 13 bytes BEFORE the reply: [0,9) is the unavailable prefix, [9,13) is the span the ring
  // covers. Because recv == pos == 13 at the reply, this is a ground start, not the ahead case (with
  // only 9 bytes forwarded, pos - recv would be 4 and the next chunk would flush partially, which is
  // row 4/04a's contract and not this row's).
  host.sendOut(name, 'PRE-START');
  host.sendOut(name, 'RING');                       // the covered span, represented by 'UNAV'
  host.replySb(name, { d: 'UNAV', pos: 13, start: 9, generationId: THE_GEN });
  await waitFor(() => got.text().includes('UNAV'), 'the covered ring is painted');
  host.sendOut(name, 'FLUSH10!');
  await waitFor(() => got.text().includes('FLUSH10!'), 'bytes at pos flush whole');
  await sleep(120);
  assert.equal(count(got.text(), 'PRE-START'), 0,
    'a byte below start is unavailable and must never be flushed');
  assert.equal(count(got.text(), 'RING'), 0,
    'the covered span is represented by the snapshot; its raw bytes must not be painted again');
});

// ---------------------------------------------------------------------------------------------
// 11 - shape (i): the element map is published live-side only, exactly once.
// ---------------------------------------------------------------------------------------------
test('11 shape (i): no replay-side map, exactly one live map frame', async t => {
  const name = 'map-live-only';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  const ring = 'RING-BEFORE\r\n' + seqFor(OLD_GEN) + 'RING-AFTER\r\n';
  host.replySb(name, { d: ring, pos: Buffer.byteLength(ring), start: 0, generationId: THE_GEN });
  await waitFor(() => got.text().includes('RING-AFTER'), 'the ring is painted');
  await sleep(120);
  assert.equal(maps(got).filter(m => m.gen === OLD_GEN).length, 0,
    'the replay must not publish a map: the ring is history, not a live stream');

  const live = 'LIVE-BEFORE\r\n' + seqFor(NEW_GEN) + 'LIVE-AFTER\r\n';
  host.sendOut(name, live);
  await waitFor(() => maps(got).some(m => m.gen === NEW_GEN), 'the live map is published');
  await sleep(120);
  const published = maps(got);
  assert.equal(published.length, 1, 'exactly one map frame on the whole session');
  assert.equal(published[0].gen, NEW_GEN, 'and it is the live generation');
});

// ---------------------------------------------------------------------------------------------
// 12 - resync truth snapshot: pos === start === base, painted whole, later bytes from the new base.
// ---------------------------------------------------------------------------------------------
test('12 resync truth snapshot: the body is painted whole and the buffer re-bases', async t => {
  const name = 'resync-truth';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'first scrollback request');

  host.ws.send(JSON.stringify({ t: 'resync', s: name }));
  await waitFor(() => host.sbReqs(name).length > 1, 'the resync re-requests', 3000);

  host.replySb(name, { d: 'RESYNC-TRUTH\r\n', pos: 0, start: 0, generationId: THE_GEN, resync: true });
  await waitFor(() => got.text().includes('RESYNC-TRUTH'), 'the truth snapshot is painted');
  await sleep(100);
  assert.equal(count(got.text(), 'RESYNC-TRUTH'), 1, 'the truth body is painted exactly once');

  host.sendOut(name, 'AFTER-RESYNC');
  await waitFor(() => got.text().includes('AFTER-RESYNC'), 'post-resync bytes flush from the new base');
  assert.equal(count(got.text(), 'AFTER-RESYNC'), 1);
});

// ---------------------------------------------------------------------------------------------
// 13 - legacy muxd: no pos key at all -> the unchanged P2-C time cut.
// ---------------------------------------------------------------------------------------------
test('13 absent pos: a legacy reply keeps the P2-C time cut', async t => {
  const name = 'legacy';
  const { h, host } = await boot(t, [session(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request');

  host.replySb(name, { d: 'LEGACY-RING\r\n' });     // no pos, no start, no generationId
  // The complement of row 8: the key is ABSENT, which is the old-muxd form. `pos: null` here would be
  // a different wire shape and the two tests would stop proving the two distinct fallbacks.
  assert.equal(Object.prototype.hasOwnProperty.call(host.lastReply, 'pos'), false, 'pos must be absent');
  await waitFor(() => got.text().includes('LEGACY-RING'), 'the ring is painted');
  host.sendOut(name, 'LEGACY-LIVE');
  await waitFor(() => got.text().includes('LEGACY-LIVE'), 'live bytes flow');
  assert.equal(count(got.text(), BANNER), 0, 'legacy is a time cut, not State L');
});

// ---------------------------------------------------------------------------------------------
// 14 - I-15 partition invariant over random ground-state pos and random chunk splits.
// ---------------------------------------------------------------------------------------------
function mulberry32(seed) {
  return function next() {
    seed |= 0; seed = (seed + 0x6D2B79F5) | 0;
    let v = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    v = (v + Math.imul(v ^ (v >>> 7), 61 | v)) ^ v;
    return ((v ^ (v >>> 14)) >>> 0) / 4294967296;
  };
}

test('14 I-15 fuzz: covered once, flushed once, no gap across random cuts', async t => {
  const seed = 0x5EEDBEEF;
  const rnd = mulberry32(seed);
  for (let i = 0; i < 6; i++) {
    const name = 'fuzz-' + i;
    const { h, host } = await boot(t, [session(name)]);
    const { got } = await attach(t, h, name);
    await waitFor(() => host.sbReqs(name).length > 0, 'scrollback request for ' + name);

    const pos = 8 + Math.floor(rnd() * 25);                    // 8..32
    const coverMarker = `FZ${i}-COVER`;
    const tailMarker = `FZ${i}-TAIL`;
    let covered = coverMarker;
    while (Buffer.byteLength(covered) < pos) covered += '.';
    covered = covered.slice(0, pos);                           // exactly pos bytes
    const tail = tailMarker + '\r\n';

    // Random chunk splits across the straddler: 1..3 pieces, sent before the reply.
    const whole = covered + tail;
    const pieces = 1 + Math.floor(rnd() * 3);
    let at = 0;
    for (let p = 0; p < pieces; p++) {
      const remaining = whole.length - at;
      const size = p === pieces - 1 ? remaining : 1 + Math.floor(rnd() * Math.max(1, remaining - 1));
      host.sendOut(name, whole.slice(at, at + size));
      at += size;
    }

    host.replySb(name, { d: covered, pos: Buffer.byteLength(covered), start: 0, generationId: THE_GEN });
    await waitFor(() => got.text().includes(tailMarker), `iteration ${i}: tail flushed`, 4000);
    await sleep(80);

    const text = got.text();
    const fail = (why) => {
      throw new Error(`I-15 violated at seed=0x${seed.toString(16)} iteration=${i} pos=${pos} pieces=${pieces}: ${why}`);
    };
    if (count(text, tailMarker) !== 1) fail('tail not exactly once');
    if (count(text, coverMarker) !== 1) fail('covered text not exactly once');
    if (text.includes(BANNER)) fail('unexpected State L banner');
    await h.stop();
  }
});

// ---------------------------------------------------------------------------------------------
// 15 - P2-C collateral damage: a cold attach is still a snapshot, a warm attach asks for nothing.
// ---------------------------------------------------------------------------------------------
test('15 P2-C smoke: cold attach is a snapshot, warm attach requests nothing', async t => {
  const name = 'p2c-smoke';
  const { h, host } = await boot(t, [session(name)]);
  const first = await attach(t, h, name);
  await waitFor(() => host.sbReqs(name).length > 0, 'cold attach requests the ring');

  const ring = 'SMOKE-RING\r\n' + seqFor(OLD_GEN) + 'SMOKE-TAIL\r\n';
  host.replySb(name, { d: ring, generationId: THE_GEN });   // no pos: the pure P2-C path
  await waitFor(() => first.got.text().includes('SMOKE-TAIL'), 'cold attach painted');
  await sleep(120);
  assert.ok(first.got.text().startsWith(CLEAR), 'cold attach is CLEAR + snapshot');
  assert.equal(count(first.got.text(), 'SMOKE-TAIL'), 1, 'the screen appears once');

  const before = host.sbReqs(name).length;
  const second = await attach(t, h, name);
  await sleep(250);
  assert.equal(host.sbReqs(name).length, before, 'a warm attach must not request scrollback');
  assert.ok(second.got.text().includes('SMOKE-TAIL'), 'the warm attach is painted from the model');
  const all = maps(second.got).filter(m => m.gen === OLD_GEN);
  assert.equal(all.length, 1, 'the map reaches the warm attach exactly once');
});
