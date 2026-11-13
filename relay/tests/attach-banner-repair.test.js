'use strict';
// A viewer that fell to the banner must be painted when the PC host comes back.
//
// Live (2026-11-12/13): "clicking flex to get viewer does not work, it just loads but never gives it to
// me". The relay log showed the PC host link flapping - `PC session host disconnected`, then `hello from
// CRACKERBARREL` a minute or two later - while every attach on that tab painted only
// `[scrollback unavailable - showing live output]`. The same attaches painted fully after muxd restarted.
//
// The mechanism: a viewer attaching while the host is away spends HOST_SB_WAIT_MS and takes State L,
// which is the DEFINED state for a host that does not answer. State L is repaired by a scrollback reply.
// The host-boundary path (which runs on every reconnect hello) was the only place such a reply gets
// asked for after the link returns - and it only asked when some viewer was still in `sbWait`. A viewer
// on the banner is not. So the banner outlived the outage: the host came back with the whole screen in
// its ring, and nothing ever asked for it again until the page was reloaded.
const test = require('node:test');
const assert = require('node:assert/strict');
const { once } = require('node:events');
const WebSocket = require('ws');
const { RelayHarness, HOST_CAPS, sleep, waitFor } = require('./harness');

const BANNER = '[scrollback unavailable - showing live output]';
const NAME = 'banner-repair';

function teed(over = {}) {
  return {
    name: NAME, alive: true, created: 1, lastOut: 1, cols: 100, rows: 30,
    hasCommand: false, shellOnly: true, ready: true, kind: 'local-tab', owner: true,
    sessionId: NAME + '-id', generationId: 'gen-1', aliases: [], streamBytes: 0, watchAck: true,
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

async function boot(t) {
  // A short banner window, so the viewer reaches State L quickly instead of racing the ack wait.
  const h = new RelayHarness({ MUX_HOST_SB_WAIT_MS: '250', MUX_WATCH_ACK_WAIT_MS: '150' });
  await h.start();
  t.after(async () => { await h.stop(); });
  const host = await h.connectHost([teed()], { caps: [...HOST_CAPS, 'watch', 'watchAck'] });
  t.after(() => host.close());
  return { h, host };
}

async function attach(t, h) {
  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=${NAME}&cols=100&rows=30`);
  const got = collect(ws);
  await once(ws, 'open');
  t.after(() => { try { ws.close(); } catch {} });
  await waitFor(() => got.texts().some(s => s[0] === 'd'), 'size frame');
  return { ws, got };
}

test('a viewer on the banner is painted when the host link returns', async t => {
  const { h, host } = await boot(t);
  const { got } = await attach(t, h);

  // The attach starts the feed and the tee answers, but the host then goes away before any ring reply
  // ever lands - exactly the flap: a snapshot promised, then silence.
  await waitFor(() => host.messages.some(m => m.t === 'watch'), 'watch frame');
  host.ws.send(JSON.stringify({ t: 'watchAck', s: NAME, snapshot: true }));
  // The ack is followed by a resync announcement (which asks for the ring) that is never answered - that
  // is what the banner's own watchdog bounds.
  host.ws.send(JSON.stringify({ t: 'resync', s: NAME }));
  await waitFor(() => got.text().includes(BANNER), 'the viewer reaches the defined banner', 4000);

  // The link drops. Nothing is owed to the viewer by the old socket.
  host.close();
  await sleep(150);
  assert.ok(got.text().includes(BANNER), 'the banner stands while the host is away - it is the defined state');

  // The PC comes back. The hello is a NEW link, so every session it names is a boundary: the model is
  // cold and the ring is the only trustworthy screen. The banner viewer must be re-armed here, not left
  // as a verdict from the outage that has now ended.
  const back = await h.connectHost([teed()], { caps: [...HOST_CAPS, 'watch', 'watchAck'] });
  t.after(() => back.close());
  await waitFor(() => back.messages.some(m => m.t === 'sb' && m.s === NAME),
    'the returning host is asked for the ring on behalf of the bannered viewer', 4000);

  // Answer it with a real screen, in the shape muxd uses for a ring reply.
  const req = back.messages.filter(m => m.t === 'sb' && m.s === NAME).at(-1);
  back.ws.send(JSON.stringify({
    t: 'sb', s: NAME, rid: req.rid, d: Buffer.from('SCREEN-AFTER-THE-OUTAGE\r\n', 'utf8').toString('base64'),
  }));
  await waitFor(() => got.text().includes('SCREEN-AFTER-THE-OUTAGE'),
    'the bannered viewer is repaired instead of staying on the banner', 4000);
  assert.equal(got.text().includes(BANNER), true,
    'the banner was there (that is what is being repaired), and the screen follows it');
  const screenAt = got.text().indexOf('SCREEN-AFTER-THE-OUTAGE');
  const bannerAt = got.text().lastIndexOf(BANNER);
  assert.ok(screenAt > bannerAt, 'the screen is painted after the banner, so the pane ends on real rows');
});

test('a bannered viewer is repaired on a SAME-link heartbeat boundary too', async t => {
  // Not every recovery is a reconnect: a link that is replaced or whose session identity changes is a
  // boundary as well, and a viewer on the banner must be re-armed there for the same reason.
  const { h, host } = await boot(t);
  const { got } = await attach(t, h);
  await waitFor(() => host.messages.some(m => m.t === 'watch'), 'watch frame');
  host.ws.send(JSON.stringify({ t: 'watchAck', s: NAME, snapshot: true }));
  host.ws.send(JSON.stringify({ t: 'resync', s: NAME }));
  await waitFor(() => got.text().includes(BANNER), 'the viewer reaches the banner', 4000);

  // A new generation on the SAME socket: the byte stream restarts, so the model is cold and the ring is
  // the only screen there is. The count is captured first, because the banner's own request from before
  // the boundary still matches `t === 'sb'` - answering THAT rid would be dropped as superseded.
  const askedBefore = host.messages.filter(m => m.t === 'sb' && m.s === NAME).length;
  host.sendHello([teed({ generationId: 'gen-2' })], { caps: [...HOST_CAPS, 'watch', 'watchAck'] });
  await waitFor(() => host.messages.filter(m => m.t === 'sb' && m.s === NAME).length > askedBefore,
    'the new generation asks for the ring again', 4000);
  const req = host.messages.filter(m => m.t === 'sb' && m.s === NAME).at(-1);
  host.ws.send(JSON.stringify({
    t: 'sb', s: NAME, rid: req.rid, d: Buffer.from('NEW-GENERATION-SCREEN\r\n', 'utf8').toString('base64'),
  }));
  await waitFor(() => got.text().includes('NEW-GENERATION-SCREEN'), 'the bannered viewer is repaired', 4000);
});
