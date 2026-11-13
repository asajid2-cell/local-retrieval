'use strict';
// P2-A (I-2): the relay's live element-map strip state must start fresh at every stream boundary -
// a resync replay, and a hello that replaces the muxd link or changes a session's identity. A partial
// body carried across a boundary can never be completed, and it swallows the head of the next stream
// until a stray BEL. A hello on the SAME link with the SAME identity is a heartbeat, not a boundary:
// muxd re-sends the full session list periodically, and a body split across one must still complete.
//
// The measured rows are the ones in local-fixtures-20261102/probe_relay_strip.cjs, reproduced with
// the same 40-element map (body 5,739 B): fresh-state replay 34 B, stale-carry replay 0 B with a
// 342-byte MODULE-LEVEL carry, follow-up terminator 5 B out / dropped 1, replay-head-mid-body 5,439 B
// painted (the last is I-5's residual, not fixed by this change). The module-level carry is the
// stripper's own bytes (8-byte introducer + 300 body + 34 replay); the WIRE carry after a boundary is
// 322 B (308 + the 14 B head) - do not re-quote 342 or 356 as a measured wire value.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { once } = require('node:events');
const WebSocket = require('ws');
const { RelayHarness, FakeHost, sleep, waitFor, HOST_CAPS } = require('./harness');
const { createStripState, stripElementMap, clipFrame } = require('../element-map');

const repo = path.join(__dirname, '..', '..');
const vectors = JSON.parse(fs.readFileSync(path.join(repo, 'docs', 'element-map-vectors.json'), 'utf8'));
const SEQ = Buffer.from(vectors.codec.entries[0].sequence_hex, 'hex');
const FRAME = vectors.codec.entries[0].frame;

const NAME = 'emap';
const session = {
  name: NAME, alive: true, created: 1, lastOut: 1, cols: 80, rows: 24,
  hasCommand: false, shellOnly: true, ready: true, kind: 'shell',
  sessionId: '', sessionUuid: 's-emap', aliases: [],
};

const MAP = {
  v: 1, gen: 7, cols: 120, rows: 30,
  els: Array.from({ length: 40 }, (_, i) => ({
    id: 'btn-' + i, role: 'button', r: [i, i, 10, 2], label: 'Element ' + i,
    act: { t: 'ptr', x: i, y: i, b: 0 },
  })),
};
const BODY = Buffer.from(JSON.stringify(MAP), 'utf8').toString('base64url');
const BODYB = Buffer.from(BODY, 'ascii');
const INTRO = Buffer.from('\x1b]21338;');
const REPLAY = Buffer.from('PROMPT> ls\r\nfile1  file2\r\nPROMPT> ');
const AFTER_RESYNC = Buffer.from('AFTER-RESYNC\r\n');
const AFTER_HELLO = Buffer.from('AFTER-HELLO\r\n');

test('measured rows: a boundary reset is what keeps the next stream whole', () => {
  // The live chunk that ends mid-body. The viewer gets the prompt text and 308 body bytes are held.
  const live = createStripState();
  const midBody = stripElementMap(Buffer.concat([Buffer.from('prompt$ '), INTRO, BODYB.subarray(0, 300)]), live);
  assert.equal(midBody.text.toString('latin1'), 'prompt$ ');
  assert.equal(live.carry.length, 308, 'the partial body is carried');

  // The ordinary replay text is 34 bytes and is clean under a fresh state (this is what the `sb` path
  // already does, and the reason the replay itself was never the broken half).
  assert.equal(REPLAY.length, 34);
  const fresh = createStripState();
  assert.equal(stripElementMap(REPLAY, fresh).text.length, 34);

  // The same bytes against the stale live state: the whole head is swallowed, the module-level carry
  // grows to 342 (308 held + the 34-byte replay). This is the module's own byte count; on the wire the
  // pre-fix carry after a resync is 322 B (308 + the 14-byte head), because the viewer receives the
  // replay text only after the stripper has applied them.
  const stale = stripElementMap(REPLAY, live);
  assert.equal(stale.text.length, 0, 'pre-fix: the replay head is eaten');
  assert.equal(live.carry.length, 342, 'pre-fix module-level carry holds both fragments');
  // The first post-boundary live chunk is eaten too, and the module-level carry grows by its 14 bytes.
  assert.equal(AFTER_RESYNC.length, 14);
  assert.equal(stripElementMap(AFTER_RESYNC, live).text.length, 0, 'pre-fix: the head is eaten');
  assert.equal(live.carry.length, 356, 'module-level carry, not a wire figure');

  // Only a stray terminator releases what is left, and it is dropped as a body.
  const followup = stripElementMap(Buffer.from('X\x07 tail'), live);
  assert.equal(followup.text.toString('latin1'), ' tail');
  assert.equal(live.dropped, 1);
  assert.equal(live.carry.length, 0);

  // What the relay now does at the boundary: a fresh state. The replay is whole, the next chunk after
  // it is delivered untouched, and a BEL is ordinary text when no body is open.
  const reset = createStripState();
  assert.equal(stripElementMap(REPLAY, reset).text.length, 34);
  assert.equal(reset.carry.length, 0);
  assert.equal(stripElementMap(AFTER_RESYNC, reset).text.length, 14);
  assert.equal(stripElementMap(Buffer.from('X\x07 tail'), reset).text.length, 7);
  assert.equal(reset.carry.length, 0);
  assert.equal(reset.dropped, 0);
});

test('replay head that starts mid-body still paints (I-5 residual, not this fix)', () => {
  const st = createStripState();
  const r = stripElementMap(BODYB.subarray(300), st);
  assert.equal(r.text.length, 5439, 'all 5,439 body bytes pass as text; P2-C owns this');
});

function collect(viewer) {
  const bytes = [];
  const texts = [];
  viewer.on('message', (data, isBinary) => {
    if (isBinary) bytes.push(Buffer.from(data));
    else texts.push(data.toString());
  });
  return { bytes: () => Buffer.concat(bytes), texts: () => texts.slice() };
}
const maps = got => got.texts().filter(s => s[0] === 'E').map(s => JSON.parse(s.slice(1)));

// A scrollback reply must beat the State-L watchdog for these to exercise State R. The harness default
// is 40ms so suites do not wait on it; these tests script a reply and need it to win the race.
async function boot(t, sessions = [session]) {
  const h = new RelayHarness({ MUX_HOST_SB_WAIT_MS: '6000' });
  await h.start();
  t.after(async () => h.stop());
  const host = new FakeHost(h.port);
  await host.connect();
  t.after(() => host.close());
  host.sendHello(sessions, { caps: [...HOST_CAPS] });
  return { h, host };
}

function sbRequests(host) { return host.messages.filter(m => m.t === 'sb' && m.s === NAME); }

// Attach a viewer and answer its scrollback request. `reply` is ordinary screen text, so the client
// goes live and later host output is delivered directly. A warm model answers outright (State S), so
// `expectSb: false` is for the attach-after-a-boundary case where no request must be sent at all.
async function attach(t, h, host, reply = '', { expectSb = true } = {}) {
  const before = sbRequests(host).length;
  const viewer = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=${NAME}`);
  const got = collect(viewer);
  await once(viewer, 'open');
  t.after(() => viewer.close());
  if (expectSb) {
    await waitFor(() => sbRequests(host).length > before, 'scrollback request');
    host.sendScrollback(NAME, reply, sbRequests(host).at(-1));
  }
  await sleep(80);
  return { viewer, got };
}

// Prime a viewer with one complete map and then a live chunk that stops 300 bytes into the next
// body. The prompt is painted and 308 bytes (the 8-byte introducer plus 300 body bytes) are held.
async function primePartialBody(t, h, host) {
  const { got } = await attach(t, h, host);
  host.sendOutput(NAME, SEQ.toString('latin1'));
  await sleep(120);
  assert.deepEqual(maps(got), [FRAME], 'the map is cached before the boundary');
  host.sendOutput(NAME, 'prompt$ ' + INTRO.toString('latin1') + BODY.slice(0, 300));
  await sleep(120);
  assert.ok(got.bytes().toString('latin1').endsWith('prompt$ '), 'the prompt text arrived');
  return got;
}

test('a resync resets the live strip and drops the cached map', async t => {
  const { h, host } = await boot(t);
  const { got } = await attach(t, h, host);

  host.sendOutput(NAME, SEQ.toString('latin1'));
  await sleep(120);
  assert.deepEqual(maps(got), [FRAME], 'the map is cached before the boundary');

  host.sendOutput(NAME, 'prompt$ ' + INTRO.toString('latin1') + BODY.slice(0, 300));
  await sleep(120);
  assert.ok(got.bytes().toString('latin1').endsWith('prompt$ '), 'the prompt text arrived');

  const beforeResync = got.bytes().length;
  const beforeSb = sbRequests(host).length;
  host.ws.send(JSON.stringify({ t: 'resync', s: NAME }));
  await waitFor(() => sbRequests(host).length > beforeSb, 'post-resync scrollback request');
  host.sendScrollback(NAME, REPLAY.toString('latin1'), sbRequests(host).at(-1));
  await sleep(120);

  host.sendOutput(NAME, AFTER_RESYNC.toString('latin1'));
  await sleep(120);

  // The repaint is the post-boundary ring, serialized from a model that was dropped at the boundary.
  // It must show the replay and must NOT stack the pre-gap screen underneath it - the old noteScrollback
  // appended the replay to a model that still held the old screen, which is the state P2-C removes.
  const after = got.bytes().subarray(beforeResync).toString('latin1');
  assert.ok(after.includes('PROMPT> ls'), 'the post-gap ring is on the screen');
  assert.ok(!after.includes('prompt$ '), 'the pre-gap screen is gone, not stacked under the repaint');
  assert.ok(after.endsWith(AFTER_RESYNC.toString('latin1')),
    'the next live chunk arrives whole; before P2-A the stale carry swallowed it');

  // A viewer that attaches after the boundary must not receive the pre-boundary map. The reseeded model
  // is warm, so it answers outright and muxd is not asked for the ring again.
  const sbBeforeSecond = sbRequests(host).length;
  const second = await attach(t, h, host, '', { expectSb: false });
  assert.equal(sbRequests(host).length, sbBeforeSecond, 'a warm model serves the boundary repaint');
  assert.deepEqual(maps(second.got), [], 'no cached map after the boundary');
  assert.ok(second.got.texts().some(s => s[0] === 'd'), 'the size frame still arrives');
});

test('a same-identity hello is a heartbeat: the open body survives and completes', async t => {
  const { h, host } = await boot(t);
  const got = await primePartialBody(t, h, host);
  const mapsBefore = maps(got).length;
  const beforeHello = got.bytes().length;

  host.sendHello([session], { caps: [...HOST_CAPS] });
  await sleep(150);
  // The rest of the body plus its BEL. Only what follows the terminator may be painted: if the hello
  // had reset the stripper, the base64 remainder and the BEL would both paint as text, so this
  // equality is the discriminator.
  host.sendOutput(NAME, BODY.slice(300) + '\x07' + 'AFTER-BODY\r\n');
  await sleep(150);

  const after = got.bytes().subarray(beforeHello).toString('latin1');
  assert.equal(after, 'AFTER-BODY\r\n', 'the held body completes; no body byte is painted as text');
  // Completion decodes the 40-element map (gen 7) - a new generation over the gen-1 vector map, so it
  // is published exactly once. The point is that the strip state still held the body across the
  // heartbeat rather than treating it as text.
  assert.equal(maps(got).length, mapsBefore + 1, 'the completed body decodes to exactly one map');
  assert.deepEqual(maps(got).at(-1), clipFrame(MAP));
});

test('a hello that changes a session identity resets the live strip and drops the cached map', async t => {
  const cases = [
    { label: 'changed generationId', change: { generationId: 'gen-2' } },
    { label: 'changed sessionId', change: { sessionId: 's-emap-2' } },
  ];
  for (const { label, change } of cases) {
    await t.test(label, async t2 => {
      const { h, host } = await boot(t2);
      const got = await primePartialBody(t2, h, host);
      const beforeHello = got.bytes().length;

      host.sendHello([{ ...session, ...change }], { caps: [...HOST_CAPS] });
      await sleep(150);
      // A trailing BEL is ordinary text only when no body is open, so receiving it proves the stale
      // carry was emptied at the boundary rather than merely hidden.
      host.sendOutput(NAME, AFTER_HELLO.toString('latin1') + '\x07');
      await sleep(150);

      const after = got.bytes().subarray(beforeHello).toString('latin1');
      assert.equal(after, AFTER_HELLO.toString('latin1') + '\x07',
        'the first chunk after the boundary arrives whole, BEL included (the carry is empty)');

      // `expectSb: false` because the boundary already re-requested the ring on behalf of the FIRST
      // viewer (it is on the banner - its attach was answered with an empty ring - and a boundary is
      // exactly what repairs a banner). That request is still in flight, so this attach joins it rather
      // than sending a second one. What this test is about is the cached map and the size frame, and
      // both are asserted below either way.
      const second = await attach(t2, h, host, '', { expectSb: false });
      assert.deepEqual(maps(second.got), [], 'no cached map after the identity change');
      assert.ok(second.got.texts().some(s => s[0] === 'd'), 'the size frame still arrives');
    });
  }
});

test('a replacing link resets the live strip even when the session identity is unchanged', async t => {
  const { h, host } = await boot(t);
  const got = await primePartialBody(t, h, host);
  const beforeHello = got.bytes().length;

  const host2 = new FakeHost(h.port);
  await host2.connect();
  t.after(() => host2.close());
  host2.sendHello([session], { caps: [...HOST_CAPS] });
  await sleep(200);

  host2.sendOutput(NAME, AFTER_HELLO.toString('latin1') + '\x07');
  await sleep(150);

  const after = got.bytes().subarray(beforeHello).toString('latin1');
  assert.equal(after, AFTER_HELLO.toString('latin1') + '\x07',
    'the new link starts fresh; a partial body from the old link is not carried across');

  // `expectSb: false` for the same reason as the identity-change case above: the boundary already asked
  // for the ring on behalf of the bannered first viewer, so a second attach joins that request.
  const second = await attach(t, h, host2, '', { expectSb: false });
  assert.deepEqual(maps(second.got), [], 'no cached map after the link replacement');
});
