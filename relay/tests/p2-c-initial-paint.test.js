'use strict';
// P2-C: a viewer's initial paint is never a raw byte log.
//
// A reattaching viewer used to be sent CLEAR plus muxd's byte ring, which its xterm re-parsed. A ring
// is a LOG: its tail can begin mid-escape-sequence and it duplicates whatever the viewer buffered while
// it waited. This suite holds the relay to the three defined states instead:
//
//   S - the model is warm: [CLEAR][snapshot][live], and muxd is not asked for the ring at all.
//   R - the model is cold and the host is reachable: [CLEAR][seed the ring][snapshot][live].
//   L - no trustworthy model (timeout, seed failure, unreachable host, flood relief):
//       [CLEAR][one banner][optional best-effort redraw][live].
//
// Every test names which arm its assertions would flip, so removing that arm turns it red.
const test = require('node:test');
const assert = require('node:assert/strict');
const { once } = require('node:events');
const WebSocket = require('ws');
const { RelayHarness, FakeHost, sleep, waitFor } = require('./harness');

const BANNER = '[scrollback unavailable - showing live output]\r\n';
const CLEAR = Buffer.from(
  '\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l\x1b[?1015l\x1b[?2004l' +
  '\x1b[?1049l\x1b[?25h\x1b[0m\x1b[3J\x1b[2J\x1b[H',
  'latin1',
).toString('latin1');

// A terminal session as muxd reports it. `owner: true` is a teed PC tab: its physical screen owns the
// geometry, so the relay must model the stream at the host size rather than at a viewer's.
function session(name, over = {}) {
  return {
    name, alive: true, created: 1, lastOut: 1, cols: 100, rows: 30,
    hasCommand: true, shellOnly: false, ready: true, kind: 'command',
    sessionId: name + '-id', aliases: [], ...over,
  };
}

function collect(ws) {
  const chunks = [];
  const texts = [];
  ws.on('message', (data, isBinary) => {
    if (isBinary) chunks.push(Buffer.from(data));
    else texts.push(data.toString());
  });
  return {
    bytes: () => Buffer.concat(chunks),
    text: () => Buffer.concat(chunks).toString('latin1'),
    texts: () => texts.slice(),
  };
}

const count = (hay, needle) => hay.split(needle).length - 1;

// The `d` frame is the single size/authority frame the relay sends each viewer.
function sizeFrames(got) {
  return got.texts().filter(s => s[0] === 'd').map(s => JSON.parse(s.slice(1)));
}

function sbRequests(host, name) {
  return host.messages.filter(m => m.t === 'sb' && m.s === name);
}

async function boot(t, sessions, env = {}) {
  const h = new RelayHarness({ MUX_HOST_SB_WAIT_MS: '6000', ...env });
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost(sessions);
  t.after(() => host.close());
  return { h, host };
}

// Attach a viewer and wait for the `d` frame. Whether muxd is asked for the ring afterwards is the
// difference between State S and State R, so each test asserts the request count itself.
async function attach(t, h, name, { cols = 100, rows = 30 } = {}) {
  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=${name}&cols=${cols}&rows=${rows}`);
  const got = collect(ws);
  await once(ws, 'open');
  t.after(() => { try { ws.close(); } catch {} });
  await waitFor(() => got.texts().some(s => s[0] === 'd'), 'size frame');
  return { ws, got };
}

// A ring whose first line must fall outside the model's bounded 500-line snapshot, so "the top line is
// absent while the last line is present" distinguishes a snapshot from a forwarded log.
function longRing() {
  let ring = '';
  for (let i = 0; i < 3000; i++) ring += `\x1b[36mring line ${String(i).padStart(5, '0')}\x1b[0m\r\n`;
  return ring + 'THE_ONLY_VISIBLE_LINE\r\n';
}

test('a cold attach seeds the ring and paints a bounded screen, never the log', async t => {
  const name = 'cold-attach';
  const { h, host } = await boot(t, [session(name)]);
  const before = sbRequests(host, name).length;
  const { got } = await attach(t, h, name);
  await waitFor(() => sbRequests(host, name).length > before, 'scrollback request for a cold model');

  assert.ok(sizeFrames(got).length >= 1, 'the size frame must precede the screen');
  const ring = longRing();
  host.sendScrollback(name, ring);

  await waitFor(() => got.text().includes('THE_ONLY_VISIBLE_LINE'), 'the ring screen is painted');
  const text = got.text();
  assert.ok(text.startsWith(CLEAR), 'a first paint is defined by CLEAR, not by a bare suffix');
  assert.ok(!text.includes('ring line 00000'),
    'the pre-scrollback head of the ring must not be forwarded: a log is not a screen');
  // The snapshot carries a bounded 500-line slice of history, not the 3000-line log.
  assert.ok(text.length < Buffer.byteLength(ring) / 5,
    `the paint must be a compact screen, not the log (paint=${text.length}, log=${Buffer.byteLength(ring)})`);
});

test('bytes buffered while waiting are not painted a second time after the snapshot', async t => {
  const name = 'cq-discard';
  const { h, host } = await boot(t, [session(name)]);
  const before = sbRequests(host, name).length;
  const { got } = await attach(t, h, name);
  await waitFor(() => sbRequests(host, name).length > before, 'scrollback request');

  // Live output arrives while the viewer still waits. The ring (as muxd serializes it at reply time)
  // already contains that line; the buffered copy must be discarded, not replayed after the snapshot.
  host.sendOutput(name, 'BUFFERED_WHILE_WAITING\r\n');
  await sleep(120);
  host.sendScrollback(name, 'START_OF_SCREEN\r\nBUFFERED_WHILE_WAITING\r\n');

  await waitFor(() => got.text().includes('BUFFERED_WHILE_WAITING'), 'the line is on the screen');
  const text = got.text();
  assert.ok(text.includes('START_OF_SCREEN'), 'the ring screen arrives');
  assert.equal(count(text, 'BUFFERED_WHILE_WAITING'), 1,
    'the buffered copy is discarded after the snapshot; the old code replayed it and doubled the line');
});

test('output that arrives while the ring parses is in the snapshot, not lost', async t => {
  const name = 'seed-race';
  const { h, host } = await boot(t, [session(name)]);
  const before = sbRequests(host, name).length;
  const { got } = await attach(t, h, name);
  await waitFor(() => sbRequests(host, name).length > before, 'scrollback request');

  // The reply and the next live line leave the host in one tick. The relay starts seeding on the reply,
  // the line is fed to the model while the large ring is still parsing, and the ring's own write callback
  // fires BEFORE that line parses. A snapshot taken there would miss it and the per-viewer buffer is cut
  // at the reply, so it could not be replayed either - the line would simply be gone. seed()'s drain
  // marker is enqueued behind every pending write, so the snapshot waits for it.
  host.sendScrollback(name, longRing());
  host.sendOutput(name, 'RACED_WITH_THE_SEED\r\n');

  await waitFor(() => got.text().includes('THE_ONLY_VISIBLE_LINE'), 'the seed completed');
  await waitFor(() => got.text().includes('RACED_WITH_THE_SEED'), 'the raced line is painted');
  assert.equal(count(got.text(), 'RACED_WITH_THE_SEED'), 1, 'the raced line is painted exactly once');
});

test('a warm attach is answered by the model with no host request and no duplication', async t => {
  const name = 'warm-attach';
  const { h, host } = await boot(t, [session(name)]);
  const first = await attach(t, h, name);
  await waitFor(() => sbRequests(host, name).length > 0, 'first scrollback request');
  host.sendScrollback(name, 'ALPHA\r\nBETA\r\n');
  await waitFor(() => first.got.text().includes('ALPHA'), 'first viewer painted');
  await sleep(80);                                  // let xterm-headless finish parsing

  const sbBefore = sbRequests(host, name).length;
  const second = await attach(t, h, name, {});
  await waitFor(() => second.got.text().includes('ALPHA'), 'State S paints the second viewer');
  await sleep(80);
  assert.equal(sbRequests(host, name).length, sbBefore, 'a warm model must not ask muxd for the ring');
  assert.equal(count(second.got.text(), 'ALPHA'), 1, 'the screen is painted once');
  assert.equal(count(second.got.text(), 'BETA'), 1);
  assert.ok(second.got.text().startsWith(CLEAR), 'State S is CLEAR + snapshot');
});

test('a tab-owned session is modelled at the host geometry, not the viewer geometry', async t => {
  const name = 'tab-owned';
  const { h, host } = await boot(t, [session(name, { owner: true, cols: 120, rows: 40 })]);
  const before = sbRequests(host, name).length;
  const { got } = await attach(t, h, name, { cols: 80, rows: 24 });
  await waitFor(() => sbRequests(host, name).length > before, 'scrollback request');

  await waitFor(() => sizeFrames(got).length > 0, 'size frame');
  const d = sizeFrames(got).at(-1);
  assert.deepEqual({ cols: d.cols, rows: d.rows }, { cols: 120, rows: 40 },
    'the tab physical screen owns the geometry');
  assert.equal(d.tabOwned, true);
  assert.equal(host.messages.filter(m => m.t === 'resize' && m.s === name).length, 0,
    'the relay must not resize a tab-owned stream to a viewer');

  host.sendScrollback(name, 'HOST_GEOM_SCREEN\r\n');
  await waitFor(() => got.text().includes('HOST_GEOM_SCREEN'), 'the screen is painted at the host grid');
  await sleep(80);

  // The model must really be at 120x40: a viewer that asks for exactly that grid is answered from it
  // with no host request. If the model had been built at this viewer's 80x24, that attach would be
  // refused and muxd would be asked for the ring again.
  const sbBefore = sbRequests(host, name).length;
  const atHost = await attach(t, h, name, { cols: 120, rows: 40 });
  await waitFor(() => atHost.got.text().includes('HOST_GEOM_SCREEN'), 'the host-grid viewer is served');
  assert.equal(sbRequests(host, name).length, sbBefore,
    'the model is at the host grid, so a host-grid attach needs no ring');
});

test('a scrollback timeout is CLEAR then exactly one banner, and live bytes follow', async t => {
  const name = 'state-l-timeout';
  // The harness default (40ms) is the point of this test: no reply is sent at all.
  const { h, host } = await boot(t, [session(name)], { MUX_HOST_SB_WAIT_MS: '40' });
  const { got } = await attach(t, h, name);
  await waitFor(() => got.text().includes(BANNER), 'the watchdog defines the state');

  const text = got.text();
  assert.ok(text.startsWith(CLEAR), 'State L starts with CLEAR, so a half-read sequence is grounded');
  assert.equal(count(text, BANNER), 1, 'the banner is one write, sent once');

  host.sendOutput(name, 'LIVE_AFTER_TIMEOUT\r\n');
  await waitFor(() => got.text().includes('LIVE_AFTER_TIMEOUT'), 'live output follows the banner');
  assert.ok(got.text().indexOf('LIVE_AFTER_TIMEOUT') > got.text().indexOf(BANNER),
    'the banner precedes the first live byte');
  assert.equal(count(got.text(), BANNER), 1, 'the banner is not re-sent');
});

test('a ring that lands after the banner replaces the banner with the real screen', async t => {
  const name = 'state-l-repair';
  const { h, host } = await boot(t, [session(name)], { MUX_HOST_SB_WAIT_MS: '40' });
  const before = sbRequests(host, name).length;
  const { got } = await attach(t, h, name);
  await waitFor(() => sbRequests(host, name).length > before, 'scrollback request');
  await waitFor(() => got.text().includes(BANNER), 'the watchdog defines the state');
  const bannerAt = got.text().length;

  // The ring arrives late. It gives the model a trustworthy screen, so the banner is superseded rather
  // than left as the viewer's only content (the black-screen regression, in its new form).
  host.sendScrollback(name, 'LATE_RING_SCREEN\r\n');
  await waitFor(() => got.text().includes('LATE_RING_SCREEN'), 'the late ring repairs the viewer');
  const after = got.text().slice(bannerAt);
  assert.ok(after.startsWith(CLEAR), 'the repair is a fresh CLEAR + snapshot');
  assert.ok(got.text().includes(BANNER), 'the banner was shown before the repair');
  assert.equal(count(got.text(), BANNER), 1, 'a repair does not re-send the banner');
});

test('a resync with no ring from the host still defines the screen', async t => {
  const name = 'resync-no-answer';
  const { h, host } = await boot(t, [session(name)], { MUX_HOST_SB_WAIT_MS: '1000' });
  const first = await attach(t, h, name);
  await waitFor(() => sbRequests(host, name).length > 0, 'initial scrollback request');
  host.sendScrollback(name, 'PRE_GAP_SCREEN\r\n');
  await waitFor(() => first.got.text().includes('PRE_GAP_SCREEN'), 'initial screen');

  const before = sbRequests(host, name).length;
  const mark = first.got.text().length;
  host.ws.send(JSON.stringify({ t: 'resync', s: name }));
  await waitFor(() => sbRequests(host, name).length > before, 'post-resync scrollback request');
  // Deliberately never answer: the post-gap stream has no trustworthy model, so the watchdog must
  // define the state instead of leaving the pre-gap screen with live bytes appended.
  await waitFor(() => first.got.text().slice(mark).includes(BANNER), 'post-resync banner', 4000);

  host.sendOutput(name, 'AFTER_GAP_LINE\r\n');
  await waitFor(() => first.got.text().slice(mark).includes('AFTER_GAP_LINE'), 'post-gap live bytes');
  const after = first.got.text().slice(mark);
  assert.ok(after.includes(CLEAR), 'the post-gap paint is CLEAR-bounded');
  assert.ok(after.indexOf(BANNER) < after.indexOf('AFTER_GAP_LINE'), 'the banner precedes the post-gap bytes');
  assert.ok(!after.includes('PRE_GAP_SCREEN'), 'the pre-gap screen is not left under the new stream');
});

test('a sessions-frame geometry change makes the model cold and the next attach reseeds', async t => {
  const name = 'geom-change';
  const base = session(name, { owner: true, cols: 120, rows: 40 });
  const { h, host } = await boot(t, [base]);
  const first = await attach(t, h, name);
  await waitFor(() => sbRequests(host, name).length > 0, 'initial scrollback request');
  host.sendScrollback(name, 'OLD_GEOM_SCREEN\r\n');
  await waitFor(() => first.got.text().includes('OLD_GEOM_SCREEN'), 'initial screen');

  host.sendSessions([{ ...base, cols: 100, rows: 30 }]);
  await sleep(120);

  const before = sbRequests(host, name).length;
  const second = await attach(t, h, name, { cols: 100, rows: 30 });
  await waitFor(() => sbRequests(host, name).length > before,
    'a model at the old geometry must not answer the new one');

  host.sendScrollback(name, 'NEW_GEOM_SCREEN\r\n');
  await waitFor(() => second.got.text().includes('NEW_GEOM_SCREEN'), 'the reseeded screen');
  const d = sizeFrames(second.got).at(-1);
  assert.deepEqual({ cols: d.cols, rows: d.rows }, { cols: 100, rows: 30 });
});

test('a same-identity hello during a seed does not cancel it (heartbeat-mid-seed)', async t => {
  const name = 'heartbeat-seed';
  const { h, host } = await boot(t, [session(name)]);
  const before = sbRequests(host, name).length;
  const { got } = await attach(t, h, name);
  await waitFor(() => sbRequests(host, name).length > before, 'scrollback request');

  // A slow seed (3000 lines) and a heartbeat hello while it parses. The hello is the same link and the
  // same identity, so it must not reset the model under the running seed.
  host.sendScrollback(name, longRing());
  host.sendHello([session(name)]);
  await waitFor(() => got.text().includes('THE_ONLY_VISIBLE_LINE'), 'the seed still completes and paints');

  const sbAfter = sbRequests(host, name).length;
  const second = await attach(t, h, name, {});
  await waitFor(() => second.got.text().includes('THE_ONLY_VISIBLE_LINE'), 'the model is warm after the seed');
  assert.equal(sbRequests(host, name).length, sbAfter,
    'the heartbeat left the seeded model in place: a warm attach asks for nothing');
});

test('a disposed model is cold, not a suffix of the post-boundary stream (cold-after-dispose)', async t => {
  const name = 'cold-after-dispose';
  const { h, host } = await boot(t, [session(name)]);
  const first = await attach(t, h, name);
  await waitFor(() => sbRequests(host, name).length > 0, 'initial scrollback request');
  host.sendScrollback(name, 'ORIGINAL_SCREEN\r\n');
  await waitFor(() => first.got.text().includes('ORIGINAL_SCREEN'), 'initial screen');

  // A changed session identity is a stream boundary: the model is dropped, and the bytes that follow
  // begin mid-stream. They may be fed (the model accepts them while cold) but must never be served.
  host.sendHello([{ ...session(name), sessionId: name + '-two' }]);
  await sleep(120);
  host.sendOutput(name, 'POST_BOUNDARY_SUFFIX\r\n');
  await sleep(120);

  const before = sbRequests(host, name).length;
  const second = await attach(t, h, name, {});
  await waitFor(() => sbRequests(host, name).length > before,
    'a disposed model must be treated as cold: the ring is the only trustworthy screen');

  host.sendScrollback(name, 'REAL_STREAM_START\r\n');
  await waitFor(() => second.got.text().includes('REAL_STREAM_START'), 'the reseeded screen');
  assert.ok(!second.got.text().includes('POST_BOUNDARY_SUFFIX'),
    'the cold suffix is replaced by the ring, not appended to');
});

test('a host that is gone still refuses the attach with 1013, warm model or not', async t => {
  const name = 'host-gone';
  const { h, host } = await boot(t, [session(name)]);
  const first = await attach(t, h, name);
  await waitFor(() => sbRequests(host, name).length > 0, 'initial scrollback request');
  host.sendScrollback(name, 'WARM_BEFORE_DROP\r\n');
  await waitFor(() => first.got.text().includes('WARM_BEFORE_DROP'), 'the model is warm');

  host.close();
  await waitFor(async () => {
    const health = await h.json('GET', '/api/health');
    return !(health.host && health.host.connected);
  }, 'the relay noticed the host link is gone');

  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=${name}&cols=100&rows=30`);
  await once(ws, 'open');
  const [code] = await once(ws, 'close');
  assert.equal(code, 1013, 'State S must still verify the host is reachable before painting');
});
