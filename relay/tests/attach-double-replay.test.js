'use strict';
// The attach path must not pay for the same history twice.
//
// A teed PC tab only forwards bytes while the relay is watching it, and the tee answers the FIRST watch
// by pushing its whole ring as a resync. The relay also used to ask muxd for scrollback on that same
// attach, so a new viewer's history crossed the wire twice: once as the scrollback reply (taken from a
// ring the resync was about to replace) and once as the resync snapshot itself. Both had to be parsed by
// the page before the viewer's first keystroke echo could paint, which is the measured 0.5-1.8 s first-key
// stall in GRID-OWNERSHIP-CONTRACT.md 10a; with a holder already watching, the same key echoes in 57 ms.
//
// The rule this suite pins: when THIS attach is the one that starts the feed, the relay waits for the
// resync instead of requesting scrollback. Every other attach is unchanged, and a tee that never sends
// the resync still reaches a defined state (banner) rather than hanging.
const test = require('node:test');
const assert = require('node:assert/strict');
const { once } = require('node:events');
const WebSocket = require('ws');
const { RelayHarness, HOST_CAPS, sleep, waitFor } = require('./harness');

const BANNER = '[scrollback unavailable - showing live output]';

function teed(name, over = {}) {
  return {
    name, alive: true, created: 1, lastOut: 1, cols: 100, rows: 30,
    hasCommand: false, shellOnly: true, ready: true, kind: 'local-tab', owner: true,
    sessionId: name + '-id', generationId: 'gen-' + name, aliases: [], streamBytes: 0,
    // Per-session, from the tee's hello: THIS tab answers `watch` with a `watchAck`. The relay arms its
    // bounded wait only where this is true, because an old tee never sends the ack and would otherwise
    // pay the full timeout on every attach.
    watchAck: true,
    ...over,
  };
}

function sbRequests(host, name) {
  return host.messages.filter(m => m.t === 'sb' && m.s === name);
}

function watchFrames(host) {
  return host.messages.filter(m => m.t === 'watch' || m.t === 'unwatch');
}

function collect(ws) {
  const chunks = [];
  const texts = [];
  ws.on('message', (data, isBinary) => {
    if (isBinary) chunks.push(Buffer.from(data));
    else texts.push(data.toString());
  });
  return { bytes: () => Buffer.concat(chunks), text: () => Buffer.concat(chunks).toString('latin1'), texts: () => texts.slice() };
}

async function boot(t, sessions, env = {}) {
  // A long State L fallback: the point is that the resync arrives first, not that the banner races it.
  const h = new RelayHarness({ MUX_HOST_SB_WAIT_MS: '6000', ...env });
  await h.start();
  t.after(async () => { await h.stop(); });
  // `watch` is the capability that makes the relay tell the tee to start feeding; `watchAck` is what lets
  // the relay WAIT for the tee's snapshot instead of guessing one is coming. Both are advertised here.
  const host = await h.connectHost(sessions, { caps: [...HOST_CAPS, 'watch', 'watchAck'] });
  t.after(() => host.close());
  return { h, host };
}

async function attach(t, h, name) {
  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=${name}&cols=100&rows=30`);
  const got = collect(ws);
  await once(ws, 'open');
  t.after(() => { try { ws.close(); } catch {} });
  await waitFor(() => got.texts().some(s => s[0] === 'd'), 'size frame');
  return { ws, got };
}

// The resync reply shape muxd sends after replace_history: pos === start === 0 marks it a truth snapshot.
function replyResync(host, name, screen) {
  const req = sbRequests(host, name).at(-1);
  if (!req || !req.rid) throw new Error('no scrollback request to answer');
  host.ws.send(JSON.stringify({
    t: 'sb', s: name, rid: req.rid, d: Buffer.from(screen, 'utf8').toString('base64'),
    pos: 0, start: 0, resync: true,
  }));
}

test('the attach that starts the feed waits for the resync instead of asking for scrollback', async t => {
  const name = 'watch-flip-attach';
  const { h, host } = await boot(t, [teed(name)]);
  const { got } = await attach(t, h, name);

  // The relay told the tee to start watching...
  await waitFor(() => watchFrames(host).some(m => m.t === 'watch'), 'watch frame for the new viewer');
  await sleep(200);
  assert.equal(sbRequests(host, name).length, 0,
    "the relay waits for the tee's watchAck instead of spending its own scrollback request");

  // The tee says its watch produced a snapshot; the resync that follows paints this viewer from the new ring.
  host.ws.send(JSON.stringify({ t: 'watchAck', s: name, snapshot: true }));
  host.ws.send(JSON.stringify({ t: 'resync', s: name }));
  await waitFor(() => sbRequests(host, name).length === 1, 'the resync requests exactly one reply');
  replyResync(host, name, 'SCREEN-FROM-THE-TEE\r\n');
  await waitFor(() => got.text().includes('SCREEN-FROM-THE-TEE'), 'the resync snapshot is painted');

  // Live bytes after the boundary flow normally, and the whole history was sent once.
  host.sendOutput(name, 'LIVE-AFTER-RESYNC\r\n');
  await waitFor(() => got.text().includes('LIVE-AFTER-RESYNC'), 'live output after the resync');
  assert.equal(sbRequests(host, name).length, 1, 'exactly one scrollback exchange for the whole attach');
});

test('an attach that does NOT start the feed keeps the ordinary paint path', async t => {
  const name = 'watch-already-held';
  const { h, host } = await boot(t, [teed(name)], { MUX_HOST_SB_WAIT_MS: '400' });
  const first = await attach(t, h, name);
  await waitFor(() => watchFrames(host).some(m => m.t === 'watch'), 'the first viewer starts the feed');
  // The tee's resync bumps the boundary, which DISPOSES the model: a resync means the old screen is gone.
  // Leave the reply unanswered so the model stays cold, which is the only state where the two attach
  // paths differ (a warm model paints without asking anyone, on both paths).
  host.ws.send(JSON.stringify({ t: 'watchAck', s: name, snapshot: true }));
  host.ws.send(JSON.stringify({ t: 'resync', s: name }));
  await waitFor(() => sbRequests(host, name).length === 1, 'the resync asks for the ring');
  assert.equal(first.got.text().includes(BANNER), false, 'the first viewer must not have fallen to the banner yet');

  // No watch flip this time - the feed is already running - so this attach must take the scrollback path
  // rather than wait for a resync that was never requested.
  const second = await attach(t, h, name);
  await waitFor(() => sbRequests(host, name).length === 2, 'a plain attach still requests the ring');
  host.sendScrollback(name, 'SECOND-VIEWER-SCREEN\r\n');
  await waitFor(() => second.got.text().includes('SECOND-VIEWER-SCREEN'), 'second viewer painted');
  assert.equal(watchFrames(host).filter(m => m.t === 'watch').length, 1,
    'the feed is started once; a second attach must not re-fire it');
});

test('a watch that produced NO snapshot falls back to asking, instead of idling into the banner', async t => {
  // The live regression this pins: the tee only snapshots on a REAL watch flip, but muxd re-states its
  // interest whenever ITS state was reset (a relay restart) while the tee never stopped watching. The relay
  // used to assume a snapshot was always coming, so that attach sat in the waiting state and the viewer
  // ended on the banner with a blank pane.
  const name = 'watch-no-snapshot';
  const { h, host } = await boot(t, [teed(name)], { MUX_HOST_SB_WAIT_MS: '6000' });
  const { got } = await attach(t, h, name);
  await waitFor(() => watchFrames(host).some(m => m.t === 'watch'), 'watch frame for the new viewer');
  await sleep(150);
  assert.equal(sbRequests(host, name).length, 0, 'nothing is asked while the answer is outstanding');

  host.ws.send(JSON.stringify({ t: 'watchAck', s: name, snapshot: false }));
  await waitFor(() => sbRequests(host, name).length === 1,
    'a watch with no snapshot must fall back to the ordinary scrollback request', 3000);
  host.sendScrollback(name, 'SCREEN-WITHOUT-A-SNAPSHOT\r\n');
  await waitFor(() => got.text().includes('SCREEN-WITHOUT-A-SNAPSHOT'), 'the viewer is painted from scrollback');
  assert.equal(got.text().includes(BANNER), false, 'and it never needed the banner');
});

test('a tee that never sends the resync still reaches the banner, not a hang', async t => {
  const name = 'watch-flip-silent';
  const { h, host } = await boot(t, [teed(name)], { MUX_HOST_SB_WAIT_MS: '120' });
  const { got } = await attach(t, h, name);
  await waitFor(() => watchFrames(host).some(m => m.t === 'watch'), 'watch frame');
  // The tee says nothing - not even the watchAck. The bounded wait must define the state rather than leave
  // the viewer waiting forever.
  await waitFor(() => got.text().includes(BANNER), 'State L banner from the bounded wait', 4000);
});

test('a non-owner session is untouched: it never flips a watch, so it always asks', async t => {
  const name = 'plain-shell';
  const { h, host } = await boot(t, [{
    name, alive: true, created: 1, lastOut: 1, cols: 100, rows: 30,
    hasCommand: true, shellOnly: false, ready: true, kind: 'command',
    sessionId: name + '-id', aliases: [],
  }]);
  const { got } = await attach(t, h, name);
  await waitFor(() => sbRequests(host, name).length === 1, 'a plain session attaches by scrollback as before');
  host.sendScrollback(name, 'PLAIN-SCREEN\r\n');
  await waitFor(() => got.text().includes('PLAIN-SCREEN'), 'painted');
  assert.equal(watchFrames(host).length, 0, 'no watch frames for a session with no stream owner');
});

test('State V does not spend its banner budget on the ack wait - a healthy flip never banners', async t => {
  // The live defect this pins (2026-11-11): the State L watchdog was armed at ATTACH, so the ack wait was
  // charged against the window the scrollback reply needed. A healthy teed tab bannered at 908 ms with its
  // resync in flight. The watchdog must start when the request is ISSUED, so each question gets its full
  // window. Sizes are tight (300 ms) to make the difference decisive: the reply lands 200 ms after the
  // resync's request - comfortable on its own budget, fatal on a budget already spent waiting for an ack.
  const name = 'watch-flip-budget';
  const { h, host } = await boot(t, [teed(name)],
    { MUX_HOST_SB_WAIT_MS: '300', MUX_WATCH_ACK_WAIT_MS: '250' });
  const { got } = await attach(t, h, name);
  await waitFor(() => watchFrames(host).some(m => m.t === 'watch'), 'watch frame');
  // The tee answers promptly, exactly as a real one does; the snapshot itself follows a moment later.
  await sleep(50);
  host.ws.send(JSON.stringify({ t: 'watchAck', s: name, snapshot: true }));
  await sleep(150);
  host.ws.send(JSON.stringify({ t: 'resync', s: name }));
  await waitFor(() => sbRequests(host, name).length === 1, 'the resync asks for the ring');
  await sleep(200);                       // 200 ms into a 300 ms window: this must paint, not banner
  replyResync(host, name, 'HEALTHY-FLIP-SCREEN\r\n');
  await waitFor(() => got.text().includes('HEALTHY-FLIP-SCREEN'), 'the screen is painted');
  assert.equal(got.text().includes(BANNER), false,
    'a healthy flip must never reach the banner: the reply had 200 ms left of its own 300 ms window');
});

test('an old tee that never acks is never waited on - its attach asks immediately', async t => {
  // The per-session gate, and why it is per-session: the muxd host is the SAME here (it advertises the
  // watch cap either way), but this tab's tee predates the ack. muxd's set_watched is idempotent, so once
  // its flag is set it forwards no further `watch` and no ack can ever arrive. A relay that armed the
  // bounded wait anyway would make every attach on every pre-upgrade tab pay the full timeout for nothing.
  const name = 'old-tee-tab';
  // A LONG ack window makes the distinction measurable: with the per-session gate the request goes out at
  // once; without it the attach waits the full 1500 ms before falling back. Both eventually ask, so the
  // assertion is on LATENCY, not on the request merely existing - otherwise the test passes either way.
  const { h, host } = await boot(t, [teed(name, { watchAck: false })],
    { MUX_HOST_SB_WAIT_MS: '6000', MUX_WATCH_ACK_WAIT_MS: '1500' });
  const t0 = Date.now();
  const { got } = await attach(t, h, name);
  await waitFor(() => sbRequests(host, name).length === 1,
    'a tab whose tee cannot ack asks for scrollback', 4000);
  const waited = Date.now() - t0;
  host.sendScrollback(name, 'OLD-TEE-SCREEN\r\n');
  await waitFor(() => got.text().includes('OLD-TEE-SCREEN'), 'painted without an ack ever arriving');
  assert.equal(got.text().includes(BANNER), false, 'and it never idled into the banner');
  assert.ok(waited < 1200, `the request must not wait for an ack that cannot come (waited ${waited} ms)`);
});

test('a resync reply paints even when it arrives BEFORE the announcement frame', async t => {
  // The live ordering (2026-11-11): muxd sends the `sb` reply with a direct ws.send while the
  // {"t":"resync"} announcement travels its async out-queue, so the REPLY routinely lands first. The old
  // check required st.resyncPending, which is set by the announcement - so a perfectly good truth snapshot
  // was judged stale, the viewer bannered, and the relay asked for a redraw in a loop. The reply carries
  // resync:true and pos===start===0, which is self-describing; the announcement must not be a precondition.
  const name = 'resync-before-announce';
  const { h, host } = await boot(t, [teed(name)]);
  const { got } = await attach(t, h, name);
  await waitFor(() => watchFrames(host).some(m => m.t === 'watch'), 'watch frame');
  // The exact live ordering, byte for byte: the feed starts firing, so the relay has ALREADY received
  // bytes (reqPos > 0) when it asks for scrollback. muxd answers that request from its freshly-replaced
  // ring with pos=0 (a truth snapshot) but its direct ws.send overtakes the {"t":"resync"} announcement
  // on the async out-queue, so the reply arrives with NO announcement in front of it. Under the old
  // check (resync && st.resyncPending) a pos=0 < reqPos reply is STALE: the viewer bannered and the relay
  // re-asked in a loop. A reply that says resync:true at pos===start===0 is self-describing and must paint.
  host.sendOutput(name, 'LIVE-BYTES-BEFORE-THE-ASK');
  await sleep(120);
  await waitFor(() => sbRequests(host, name).length >= 1, 'a scrollback request exists to answer');
  replyResync(host, name, 'REPLY-FIRST-SCREEN' + String.fromCharCode(13) + String.fromCharCode(10));
  await waitFor(() => got.text().includes('REPLY-FIRST-SCREEN'),
    'the truth snapshot paints without waiting for the announcement', 4000);
  assert.equal(got.text().includes(BANNER), false,
    'a correctly-shaped truth snapshot is never treated as stale');
});
