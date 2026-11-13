'use strict';
// An EMPTY truth snapshot must not become an infinite redraw loop.
//
// The live finding (2026-11-11): a teed tab whose ring was momentarily empty answered the resync with
// `resync:true, pos:0, d:""` - a truth snapshot carrying no screen. The relay painted it (nothing), the
// State L watchdog bannered, and paintStateL asked muxd for a redraw. The redraw made the tee push its
// ring again, which was still empty, so the banner and the redraw repeated at ~20-80 ms for as long as
// the viewer stayed attached. A real tab whose app does not repaint on demand would sit in that loop.
//
// The rule: a no-progress repair must not re-request without bound. After a redraw has been asked for
// and the ring still yields no screen, the viewer keeps the banner and the relay STOPS asking.
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
function redraws(host) {
  return host.messages.filter(m => m.t === 'redraw');
}
function sbRequests(host, name) {
  return host.messages.filter(m => m.t === 'sb' && m.s === name);
}

test('an empty truth snapshot does not start an unbounded redraw loop', async t => {
  const name = 'empty-ring-loop';
  const h = new RelayHarness({ MUX_HOST_SB_WAIT_MS: '250' });
  await h.start();
  t.after(async () => { await h.stop(); });
  // helloExtra REPLACES nothing but is spread AFTER the default caps, so pass the whole list.
  const host = await h.connectHost([teed(name)], { caps: [...HOST_CAPS, 'watch', 'watchAck'] });
  t.after(() => host.close());

  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=${name}&cols=100&rows=30`);
  const texts = [];
  // The relay writes its page frames as Buffers, so a string-only collector never sees the size frame.
  ws.on('message', (d, isBinary) => texts.push(Buffer.from(d).toString('latin1')));
  await once(ws, 'open');
  t.after(() => { try { ws.close(); } catch {} });
  await waitFor(() => texts.some(s => s[0] === 'd'), 'size frame');

  // The flip, the ack, and a resync - then answer EVERY scrollback request with an empty truth snapshot,
  // exactly as an empty ring does, for a few seconds.
  await waitFor(() => host.messages.some(m => m.t === 'watch'), 'watch frame');
  host.ws.send(JSON.stringify({ t: 'watchAck', s: name, snapshot: true }));
  host.ws.send(JSON.stringify({ t: 'resync', s: name }));
  const answered = new Set();
  const stop = Date.now() + 4000;
  while (Date.now() < stop) {
    for (const req of sbRequests(host, name)) {
      if (!req.rid || answered.has(req.rid)) continue;
      answered.add(req.rid);
      host.ws.send(JSON.stringify({
        t: 'sb', s: name, rid: req.rid, d: '', pos: 0, start: 0, resync: true,
      }));
    }
    await sleep(50);
  }

  assert.ok(texts.join('').includes(BANNER), 'the viewer is told there is no screen');
  // The point of the fix: a repair that cannot make progress must taper off, not hammer the PC forever.
  const n = redraws(host).length;
  assert.ok(n <= 3, `redraw requests must not repeat without bound (saw ${n})`);
  console.error(`[measured] redraws=${n} sbRequests=${sbRequests(host, name).length}`);
});
