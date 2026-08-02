'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { once } = require('node:events');
const WebSocket = require('ws');
const { RelayHarness, FakeHost } = require('./harness');
const { createLeaseConduit } = require('../lease-conduit');

const BASE_CAPS = ['ls','info','create','createAck','bind','input','open','attach','kill','rename','heal','tail','scrollback','resize','owner','relaunch'];
const session = {
  name: 'durable', alive: true, created: 1, lastOut: 1, cols: 80, rows: 24,
  hasCommand: false, shellOnly: true, ready: true, kind: 'shell',
  sessionId: '', sessionUuid: 's-123', aliases: [],
};

test('conduit forwards the client proof as one t:i frame', () => {
  const conduit = createLeaseConduit();
  const frame = { t: 'i', auth: { principalId: 'p', keyId: 'k', sig: 'proof', bodyB64: 'eA==' } };
  const out = conduit.forwardSignedInput('durable', JSON.stringify(frame));
  assert.deepEqual(out, { ...frame, s: 'durable' });
  assert.equal('e' in out, false);
});

test('new relay downgrades signed input only for an old-capability muxd', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = new FakeHost(h.port);
  await host.connect();
  t.after(() => host.close());
  host.ws.send(JSON.stringify({ t: 'hello', host: 'old', protocol: 4, caps: BASE_CAPS, sessions: [session] }));
  const viewer = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=durable`);
  await once(viewer, 'open');
  const sb = await host.waitFor(m => m.t === 'sb', 'scrollback');
  host.sendScrollback('durable', '', sb);
  viewer.send('I' + JSON.stringify({
    t: 'i', auth: { principalId: 'p', keyId: 'k', sig: 'proof', bodyB64: 'dHlwZWQ=' },
  }));
  const input = await host.waitFor(m => m.t === 'i', 'downgraded input');
  assert.equal(input.d, 'dHlwZWQ=');
  assert.equal(input.auth, undefined);
  viewer.close();
});

test('muxd refusal closes the originating viewer with a readable reason', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = new FakeHost(h.port);
  await host.connect();
  t.after(() => host.close());
  host.ws.send(JSON.stringify({ t: 'hello', host: 'new', protocol: 4, caps: [...BASE_CAPS, 'inputDurable'], sessions: [session] }));
  const viewer = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=durable`);
  await once(viewer, 'open');
  const sb = await host.waitFor(m => m.t === 'sb', 'scrollback');
  host.sendScrollback('durable', '', sb);
  const observer = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=durable`);
  await once(observer, 'open');
  const observerSb = await host.waitFor(m => m.t === 'sb', 'observer scrollback');
  host.sendScrollback('durable', '', observerSb);
  t.after(() => observer.close());
  viewer.send('I' + JSON.stringify({
    t: 'i', auth: { principalId: 'p', keyId: 'k', intentId: 'intent-refused', sig: 'proof', bodyB64: 'eA==' },
  }));
  const input = await host.waitFor(m => m.t === 'i', 'signed input');
  host.ws.send(JSON.stringify({
    t: 'err', s: 'durable', intentId: 'intent-refused',
    code: 'principal-proof-invalid', m: 'signature did not verify',
  }));
  const [code, reason] = await once(viewer, 'close');
  assert.equal(code, 1008);
  assert.match(reason.toString(), /principal-proof-invalid.*signature did not verify/);
  await new Promise(resolve => setTimeout(resolve, 50));
  assert.equal(observer.readyState, WebSocket.OPEN);
});
