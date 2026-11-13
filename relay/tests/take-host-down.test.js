'use strict';
// A take that crossed a host reconnect must not disable every LATER take on that session.
//
// Live (2026-11-12/13): "taking the grid A sometimes doesn't work", on the same host flap that was
// bannering attaches - the relay log shows `PC session host disconnected`, then `hello from CRACKERBARREL`
// minutes later. `requestTabGrid` records `st.gridReq` as "a request is in flight". That record is
// cleared only by a matching COMMIT or a matching `gridAck`, and it GATES the viewport re-pin path
// (`handleClientMsg` 'v' requires `!st.gridReq`). A request minted against the old link's `gridEpoch` can
// never be matched by anything the new link sends, so the record survived the reconnect forever and
// every later viewport re-pin was silently skipped.
//
// Two rules are pinned here, both about a boundary (a reconnect hello, or a replaced link):
//   1. a request that could not be SENT is not in flight, so it is never recorded; and
//   2. a request recorded against the old link is dropped at the boundary.
const test = require('node:test');
const assert = require('node:assert/strict');
const { once } = require('node:events');
const WebSocket = require('ws');
const { RelayHarness, HOST_CAPS, sleep, waitFor } = require('./harness');

const NAME = 'take-while-host-down';

function teed(over = {}) {
  return {
    name: NAME, alive: true, created: 1, lastOut: 1, cols: 200, rows: 50,
    hasCommand: false, shellOnly: true, ready: true, kind: 'local-tab', owner: true,
    sessionId: NAME + '-id', generationId: 'gen-1', aliases: [], streamBytes: 0,
    gridOwner: 'pin', gridEpoch: 4, watchAck: true,
    ...over,
  };
}

function collect(ws) {
  const chunks = [];
  const texts = [];
  ws.on('message', (data, isBinary) => {
    if (isBinary) chunks.push(Buffer.from(data));
    else texts.push(data.toString());
  });
  return { text: () => Buffer.concat(chunks).toString('latin1'), texts: () => texts.slice() };
}

async function boot(t, rows) {
  // A long banner window: this suite is not about the first paint.
  const h = new RelayHarness({ MUX_HOST_SB_WAIT_MS: '6000' });
  await h.start();
  t.after(async () => { await h.stop(); });
  const host = await h.connectHost(rows, { caps: [...HOST_CAPS, 'watch', 'watchAck'] });
  t.after(() => host.close());
  return { h, host };
}

// A viewer reported as the pinning device, so its viewport is the one the re-pin path follows.
async function attach(t, h, { cols = 80, rows = 24 } = {}) {
  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=${NAME}&cols=${cols}&rows=${rows}&dev=devA`);
  const got = collect(ws);
  await once(ws, 'open');
  t.after(() => { try { ws.close(); } catch {} });
  await waitFor(() => got.texts().some(s => s[0] === 'd'), 'size frame');
  return { ws, got };
}

const resizes = host => host.messages.filter(m => m.t === 'resize');

test('a re-pin after the host returns is not blocked by a take that crossed the reconnect', async t => {
  const { h, host } = await boot(t, [teed({ gridOwner: 'local' })]);
  const { ws } = await attach(t, h, { cols: 80, rows: 24 });
  await sleep(150);

  // ---- A take really goes out on the OLD link and is never acked (the link drops first), which is the
  // ordinary shape of a flap: the request is in flight when the socket dies under it.
  ws.send('P');
  await waitFor(() => resizes(host).some(m => m.owner === 'devA'), 'the take reaches the old host', 4000);
  host.close();
  await sleep(250);

  // ---- The PC returns having applied the take it was asked for: it reports the pin at its own epoch,
  // which is not the epoch the relay minted. That mismatch is the point - nothing on the new link can
  // ever acknowledge the old record.
  const back = await h.connectHost([teed({ gridOwner: 'pin', gridEpoch: 41, cols: 80, rows: 24 })],
    { caps: [...HOST_CAPS, 'watch', 'watchAck'] });
  t.after(() => back.close());
  await sleep(250);
  const before = resizes(back).length;

  // A viewport change is the re-pin path, and it is gated on there being no request in flight. With the
  // stale record left behind, NOTHING is sent here - the take is simply lost, forever.
  ws.send('v' + JSON.stringify({ cols: 96, rows: 28, vis: true, act: 1 }));
  await waitFor(() => resizes(back).length > before,
    'the viewport re-pin reaches the host after the reconnect', 4000);
  const frame = resizes(back).at(-1);
  assert.equal(frame.owner, 'devA', 'and it is this device taking the grid');
  assert.ok((frame.cols | 0) === 96 && (frame.rows | 0) === 28, 'at the grid the viewer reports');
});

test('a re-pin still works when the take was only ever attempted (never sent)', async t => {
  // The send-side half. The pin record is written BEFORE the request goes out, so a take issued with no
  // socket still leaves this device pinned - and the re-pin path is exactly what must then work. Without
  // the "not sent, not in flight" rule the record is armed with an epoch no link will ever answer.
  const { h, host } = await boot(t, [teed()]);
  const { ws } = await attach(t, h, { cols: 80, rows: 24 });
  await sleep(150);
  host.close();
  await sleep(200);
  ws.send('P');
  await sleep(250);
  const back = await h.connectHost([teed()], { caps: [...HOST_CAPS, 'watch', 'watchAck'] });
  t.after(() => back.close());
  await sleep(250);
  const before = resizes(back).length;
  ws.send('v' + JSON.stringify({ cols: 91, rows: 27, vis: true, act: 1 }));
  await waitFor(() => resizes(back).length > before, 'a later request still goes out', 4000);
});
