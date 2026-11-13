'use strict';
// A session whose screen is genuinely BLANK is not a broken session.
//
// Live (2026-11-13, tab-7a38): every attach painted `[scrollback unavailable - showing live output]` and
// nothing else. Its ring was 151 KB and contained zero printable cells - only OSC 0 window-title escapes -
// so the session's screen really is empty. The relay was describing a working transport as a failure:
// "scrollback unavailable" when scrollback had answered perfectly and simply had no cells to paint.
//
// Two different causes need two different answers, and this suite pins the distinction:
//   - no trustworthy screen (cold model, host silent, parser failed) -> the banner, as before;
//   - a SETTLED model whose screen is empty                            -> a blank screen, live, no banner.
const test = require('node:test');
const assert = require('node:assert/strict');
const { once } = require('node:events');
const WebSocket = require('ws');
const { RelayHarness, HOST_CAPS, sleep, waitFor } = require('./harness');

const BANNER = '[scrollback unavailable - showing live output]';
const NAME = 'blank-screen';

function session(over = {}) {
  return {
    name: NAME, alive: true, created: 1, lastOut: 1, cols: 80, rows: 24,
    hasCommand: false, shellOnly: true, ready: true, kind: 'local-tab', owner: true,
    sessionId: NAME + '-id', generationId: 'gen-1', aliases: [], streamBytes: 0, watchAck: true,
    ...over,
  };
}

function collect(ws) {
  const chunks = [], texts = [];
  ws.on('message', (data, isBinary) => { if (isBinary) chunks.push(Buffer.from(data)); else texts.push(data.toString()); });
  return { text: () => Buffer.concat(chunks).toString('latin1'), texts: () => texts.slice() };
}

async function boot(t) {
  const h = new RelayHarness({ MUX_HOST_SB_WAIT_MS: '6000' });
  await h.start();
  t.after(async () => { await h.stop(); });
  const host = await h.connectHost([session()], { caps: [...HOST_CAPS, 'watch', 'watchAck'] });
  t.after(() => host.close());
  return { h, host };
}

async function attach(t, h) {
  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=${NAME}&cols=80&rows=24`);
  const got = collect(ws);
  await once(ws, 'open');
  t.after(() => { try { ws.close(); } catch {} });
  await waitFor(() => got.texts().some(s => s[0] === 'd'), 'size frame');
  return { ws, got };
}

// A ring that carries real terminal traffic but produces NO cells: only window-title escapes, which is
// exactly what tab-7a38's ring held.
const TITLES_ONLY = Array.from({ length: 200 }, (_, i) => `\x1b]0;\u2810 harmonizer-suite \u00b7 8ae9cab7\x07`).join('');

test('a session whose screen is genuinely blank paints blank, not the transport banner', async t => {
  const { h, host } = await boot(t);
  const first = await attach(t, h);
  await waitFor(() => host.messages.some(m => m.t === 'sb' && m.s === NAME), 'ring request');
  host.sendScrollback(NAME, TITLES_ONLY);
  await sleep(200);                                  // let the model settle on that ring
  assert.equal(first.got.text().includes(BANNER), false,
    'an empty SCREEN is not an unavailable scrollback: the transport answered');

  // A second attach is answered by the now-warm model. Its screen is empty by construction, so the paint is
  // a CLEAR and nothing else - and still no banner.
  const second = await attach(t, h);
  await sleep(300);
  const wire = second.got.text();
  assert.equal(wire.includes(BANNER), false, 'a warm, empty screen must not be described as unavailable');
  assert.ok(!wire.includes('harmonizer-suite'), 'the title escapes are never painted as text');

  // And it is LIVE: the next byte the app writes lands on screen, because the viewer was never parked.
  host.sendOutput(NAME, 'FIRST REAL LINE\r\n');
  await waitFor(() => second.got.text().includes('FIRST REAL LINE'),
    'the viewer is live, so the first real output paints', 4000);
});

test('a screen that is genuinely unavailable still gets the banner', async t => {
  // The distinction has to cut both ways or it is just a different lie. A cold model with a host that never
  // answers the ring is the real "no trustworthy screen" case and must keep its banner.
  const h = new RelayHarness({ MUX_HOST_SB_WAIT_MS: '200' });
  await h.start();
  t.after(async () => { await h.stop(); });
  const host = await h.connectHost([session({ watchAck: false })], { caps: [...HOST_CAPS, 'watch'] });
  t.after(() => host.close());
  const { got } = await attach(t, h);
  await waitFor(() => got.text().includes(BANNER), 'a host that never answers still banners', 4000);
});
