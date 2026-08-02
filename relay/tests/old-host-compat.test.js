const test = require('node:test');
const assert = require('node:assert/strict');
const { once } = require('node:events');
const WebSocket = require('ws');
const {
  waitFor,
  waitForWsText,
  RelayHarness,
  FakeHost,
} = require('./harness');

const PRODUCTION_MUXD_CAPS = [
  'ls',
  'info',
  'create',
  'createAck',
  'bind',
  'input',
  'open',
  'attach',
  'kill',
  'rename',
  'heal',
  'tail',
  'scrollback',
  'resize',
  'owner',
  'relaunch',
];

function shellSession(name) {
  return {
    name,
    alive: true,
    created: 1000,
    lastOut: 1000,
    cols: 100,
    rows: 30,
    hasCommand: false,
    shellOnly: true,
    ready: true,
    kind: 'shell',
    sessionId: '',
    aliases: [],
  };
}

test('canonical fully bridges the production protocol-4 16-cap muxd', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());

  const host = new FakeHost(h.port);
  await host.connect();
  t.after(() => host.close());
  host.ws.send(JSON.stringify({
    t: 'hello',
    host: 'PRODUCTION-MUXD',
    protocol: 4,
    caps: PRODUCTION_MUXD_CAPS,
    sessions: [],
  }));

  const health = await waitFor(async () => {
    const current = await h.json('GET', '/api/health');
    return current.host && current.host.connected && current.host.protocolOk ? current : null;
  }, 'production muxd hello accepted');
  assert.equal(health.host.protocol, 4);
  assert.deepEqual(health.host.caps, PRODUCTION_MUXD_CAPS);

  const createResponse = h.request('POST', '/api/sessions', { name: 'production-compat' });
  const create = await host.waitFor(
    message => message.t === 'create' && message.s === 'production-compat',
    'blank shell create request',
  );
  const session = shellSession('production-compat');
  host.sendCreateResult(create, { session });
  const created = await createResponse;
  assert.equal(created.status, 200);
  assert.equal(created.body.created, true);

  const viewer = new WebSocket(
    `ws://127.0.0.1:${h.port}/ws?session=production-compat&cols=90&rows=28`,
  );
  await once(viewer, 'open');
  const viewerClosed = once(viewer, 'close');
  const scrollback = await host.waitFor(
    message => message.t === 'sb' && message.s === 'production-compat',
    'viewer scrollback request',
  );

  const replay = waitForWsText(viewer, /REPLAYED_FROM_MUXD/, 'scrollback replay');
  host.sendScrollback('production-compat', 'REPLAYED_FROM_MUXD\r\n', scrollback);
  assert.match(await replay, /REPLAYED_FROM_MUXD/);

  const output = waitForWsText(viewer, /LIVE_FROM_MUXD/, 'live host output');
  host.sendOutput('production-compat', 'LIVE_FROM_MUXD\r\n');
  assert.match(await output, /LIVE_FROM_MUXD/);

  viewer.send('ityped through canonical');
  const input = await host.waitFor(
    message => message.t === 'i' && message.s === 'production-compat',
    'unsigned viewer input',
  );
  assert.equal(input.t, 'i');
  assert.equal(input.s, 'production-compat');
  assert.ok(input.channelId);
  assert.equal(input.d, Buffer.from('typed through canonical', 'utf8').toString('base64'));

  const killResponse = h.request('DELETE', '/api/sessions/production-compat');
  await host.waitFor(
    message => message.t === 'kill' && message.s === 'production-compat',
    'kill request',
  );
  host.sendKilled('production-compat');
  const killed = await killResponse;
  assert.equal(killed.status, 200);
  assert.equal(killed.body.ok, true);

  const [closeCode] = await viewerClosed;
  assert.equal(closeCode, 1013);
  assert.deepEqual(await h.json('GET', '/api/sessions'), []);
});
