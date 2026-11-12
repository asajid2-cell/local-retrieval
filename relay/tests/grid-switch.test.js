'use strict';
// Taking and releasing the grid must not throw the viewer onto the scrollback banner.
//
// Live, 2026-11-12 (tab-26f4, take_probe.cjs): the take committed at ~7449 ms and the banner was painted at
// 7774 ms - 325 ms AFTER a successful ack. Release behaved the same way, ~1 s to settle. The mechanism is a
// combination of two implicit state machines that nobody wrote down together:
//
//   the tee (TeeEngine.TryResize / RestorePaneLocal) clears the ring and sends an EMPTY replace_history on
//   every resize - a remote take AND a local restore - because "the ring's bytes are at the old width and
//   cannot be honestly replayed"; muxd turns that into `{"t":"resync"}`;
//
//   the relay (resync branch) treats a resync as a NEW STREAM: it resets the boundary, sets sbWait on every
//   viewer and asks for the ring again. The app is expected to repaint itself at the new grid.
//
// So a take is: commit -> ring emptied -> resync -> sbWait -> re-request. Any viewer whose re-request does
// not land inside HOST_SB_WAIT_MS is shown the banner - and an app that does not repaint on demand (or a
// ring still emptying) makes that the common path, not the exception. That is the reported glitch.
//
// These tests pin the invariants the transition must satisfy, whatever the mechanism:
//   1. a resync on an ALREADY-PAINTED viewer does not blank it while the new ring is being fetched;
//   2. a viewer whose ring reply lands in time is never shown the banner;
//   3. the banner is still reachable when nothing arrives (the defined state must not be lost).
const test = require('node:test');
const assert = require('node:assert/strict');
const { once } = require('node:events');
const WebSocket = require('ws');
const { RelayHarness, HOST_CAPS, sleep, waitFor } = require('./harness');

const BANNER = '[scrollback unavailable - showing live output]';

function teed(name) {
  return {
    name, alive: true, created: 1, lastOut: 1, cols: 100, rows: 30,
    hasCommand: false, shellOnly: true, ready: true, kind: 'local-tab', owner: true,
    sessionId: name + '-id', generationId: 'gen-' + name, aliases: [], streamBytes: 0, watchAck: true,
  };
}
function collect(ws) {
  const chunks = [];
  const texts = [];
  ws.on('message', (d, isBinary) => {
    if (isBinary) chunks.push(Buffer.from(d));
    else texts.push(d.toString());
  });
  return { text: () => Buffer.concat(chunks).toString('latin1'), texts: () => texts.slice() };
}
function sbRequests(host, name) {
  return host.messages.filter(m => m.t === 'sb' && m.s === name);
}
function replyResync(host, name, screen) {
  const req = sbRequests(host, name).at(-1);
  if (!req || !req.rid) throw new Error('no scrollback request to answer');
  host.ws.send(JSON.stringify({
    t: 'sb', s: name, rid: req.rid, d: Buffer.from(screen, 'utf8').toString('base64'),
    pos: 0, start: 0, resync: true,
  }));
}

async function painted(t, h, host, name) {
  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=${name}&cols=100&rows=30`);
  const got = collect(ws);
  await once(ws, 'open');
  t.after(() => { try { ws.close(); } catch {} });
  await waitFor(() => got.texts().some(s => s[0] === 'd'), 'size frame');
  await waitFor(() => host.messages.some(m => m.t === 'watch'), 'watch frame');
  host.ws.send(JSON.stringify({ t: 'watchAck', s: name, snapshot: true }));
  host.ws.send(JSON.stringify({ t: 'resync', s: name }));
  await waitFor(() => sbRequests(host, name).length === 1, 'the resync asks for the ring');
  replyResync(host, name, 'ORIGINAL SCREEN\r\n');
  await waitFor(() => got.text().includes('ORIGINAL SCREEN'), 'an ordinary attach paints first');
  return { ws, got };
}

test('a grid switch does not blank an already-painted viewer while the new ring is fetched', async t => {
  const name = 'take-mid-resync';
  const h = new RelayHarness({ MUX_HOST_SB_WAIT_MS: '600' });
  await h.start();
  t.after(async () => { await h.stop(); });
  const host = await h.connectHost([teed(name)], { caps: [...HOST_CAPS, 'watch', 'watchAck'] });
  t.after(() => host.close());

  const { got } = await painted(t, h, host, name);
  assert.equal(got.text().includes(BANNER), false, 'painted before the take');

  // THE TAKE: the tee clears the ring and the relay is told so, exactly as a committed resize does.
  host.ws.send(JSON.stringify({ t: 'resync', s: name }));
  await waitFor(() => sbRequests(host, name).length === 2, 'the take asks for the new ring');
  // The app is repainting at the new width and the reply is a moment away (250 ms, inside the 600 ms window).
  await sleep(250);
  assert.equal(got.text().includes(BANNER), false,
    'an already-painted viewer must keep its screen while the new ring is in flight - not flash the banner');
  replyResync(host, name, 'SCREEN AFTER THE TAKE\r\n');
  await waitFor(() => got.text().includes('SCREEN AFTER THE TAKE'), 'the new screen lands');
  assert.equal(got.text().includes(BANNER), false, 'and the banner never appeared at all');
});

test('a resync reply that arrives in time never shows the banner', async t => {
  const name = 'take-fast-reply';
  const h = new RelayHarness({ MUX_HOST_SB_WAIT_MS: '600' });
  await h.start();
  t.after(async () => { await h.stop(); });
  const host = await h.connectHost([teed(name)], { caps: [...HOST_CAPS, 'watch', 'watchAck'] });
  t.after(() => host.close());
  const { got } = await painted(t, h, host, name);
  host.ws.send(JSON.stringify({ t: 'resync', s: name }));
  await waitFor(() => sbRequests(host, name).length === 2, 'the take asks');
  await sleep(100);
  replyResync(host, name, 'FAST TAKE\r\n');
  await waitFor(() => got.text().includes('FAST TAKE'), 'painted');
  assert.equal(got.text().includes(BANNER), false, 'no banner on the fast path');
});

test('the banner is still the defined state when the ring never arrives', async t => {
  // The safety property must survive: a transition that cannot be completed still ends on the banner
  // rather than buffering forever. This is the row that keeps the fix honest.
  const name = 'take-no-reply';
  const h = new RelayHarness({ MUX_HOST_SB_WAIT_MS: '500' });
  await h.start();
  t.after(async () => { await h.stop(); });
  const host = await h.connectHost([teed(name)], { caps: [...HOST_CAPS, 'watch', 'watchAck'] });
  t.after(() => host.close());
  const { got } = await painted(t, h, host, name);
  host.ws.send(JSON.stringify({ t: 'resync', s: name }));
  await waitFor(() => sbRequests(host, name).length === 2, 'the take asks');
  await waitFor(() => got.text().includes(BANNER), 'a ring that never arrives still reaches the banner', 5000);
});

test('an EMPTY truth snapshot is a cleared ring, not an answer - wait for the repaint', async t => {
  // The live sequence (tab-26f4, 2026-11-12), from the relay's own log:
  //     [g-sb] resync=true pos=0 dlen=0        <- the tee cleared the ring on the resize
  //     [g-sb] resync=true pos=0 dlen=65428    <- the app's repaint, ~300 ms later
  // The tee clears its ring on every resize because the bytes are at the old width (TeeEngine.TryResize /
  // RestorePaneLocal both send an empty replace_history). An empty body is therefore not "here is the
  // screen", it is "the ring is being refilled". Treating it as a completed seed painted nothing and let
  // the watchdog banner before the real repaint arrived - which is the glitch under investigation.
  const name = 'empty-then-real';
  const h = new RelayHarness({ MUX_HOST_SB_WAIT_MS: '1200' });
  await h.start();
  t.after(async () => { await h.stop(); });
  const host = await h.connectHost([teed(name)], { caps: [...HOST_CAPS, 'watch', 'watchAck'] });
  t.after(() => host.close());
  const { got } = await painted(t, h, host, name);

  // The take: resync, and the ring is EMPTY.
  host.ws.send(JSON.stringify({ t: 'resync', s: name }));
  await waitFor(() => sbRequests(host, name).length === 2, 'the take asks');
  replyResync(host, name, '');
  await sleep(400);
  assert.equal(got.text().includes(BANNER), false,
    'an empty ring must not be painted as an answer, and must not blank the viewer');

  // The app repaints. That reply is the real screen, and it must paint.
  host.ws.send(JSON.stringify({ t: 'resync', s: name }));
  await waitFor(() => sbRequests(host, name).length === 3, 'the repaint is fetched');
  replyResync(host, name, 'REPAINTED AT THE NEW GRID\r\n');
  await waitFor(() => got.text().includes('REPAINTED AT THE NEW GRID'), 'the repaint paints');
  assert.equal(got.text().includes(BANNER), false, 'and the banner never appeared');
});

test('an empty ring that is never refilled still ends on the banner', async t => {
  // The other half: "always wait for a repaint" must not become "wait for ever". A tee that clears its
  // ring and then goes silent (a dead app) has to reach the defined state.
  const name = 'empty-forever';
  const h = new RelayHarness({ MUX_HOST_SB_WAIT_MS: '700' });
  await h.start();
  t.after(async () => { await h.stop(); });
  const host = await h.connectHost([teed(name)], { caps: [...HOST_CAPS, 'watch', 'watchAck'] });
  t.after(() => host.close());
  const { got } = await painted(t, h, host, name);
  host.ws.send(JSON.stringify({ t: 'resync', s: name }));
  await waitFor(() => sbRequests(host, name).length === 2, 'the take asks');
  replyResync(host, name, '');
  await waitFor(() => got.text().includes(BANNER), 'no repaint -> the banner is still reached', 6000);
});
