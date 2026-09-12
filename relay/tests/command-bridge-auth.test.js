const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { isDeepStrictEqual } = require('node:util');
const { RelayHarness } = require('./harness');

const randomCredential = () => crypto.randomBytes(32).toString('hex');
const header = token => ({ 'X-Mux-Command-Bridge': token });
const leasePath = '/api/app-commands/lease';
const leaseBody = { owner: 'auth-test-consumer', limit: 1 };

async function fixture(t, { configured = true, testMode = false } = {}) {
  const token = randomCredential();
  const owner = randomCredential();
  const other = randomCredential();
  const identity = http.createServer((req, res) => {
    res.setHeader('content-type', 'application/json');
    const authorized = req.url === '/internal/verify' && req.headers['x-session-token'] === owner;
    res.end(JSON.stringify({ authenticated: authorized, user: { isOwner: authorized } }));
  });
  await new Promise(resolve => identity.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => identity.close(resolve)));
  const h = new RelayHarness({
    MUX_TEST_MODE: testMode ? '1' : undefined,
    MUX_COMMAND_BRIDGE_TOKEN: configured ? token : '',
    MUX_COMMAND_LEASE_MS: '120000',
    MUX_HOST_TOKEN: other,
    MUX_BRIDGE_TOKEN: randomCredential(),
    MUX_TRANSCRIPT_BRIDGE_TOKEN: randomCredential(),
    MUX_DISPATCH_KEY: randomCredential(),
    HLAUTH_BASE: `http://127.0.0.1:${identity.address().port}`,
  });
  t.after(() => h.stop());
  await h.start();
  // Explicit request headers only: this suite must never acquire harness auto-auth defaults.
  return { h, token, ownerHeaders: { cookie: `hl_session=${owner}` } };
}

async function enqueue(h, ownerHeaders) {
  const response = await h.request('POST', '/api/app-commands', {
    type: 'setfavorite', sessionId: 'auth-fixture-session', favorite: true,
    intentId: crypto.randomUUID(),
  }, ownerHeaders);
  assert.equal(response.status, 200, 'owner can enqueue the positive-control command');
  return response.body.id;
}

const snapshot = h => fs.readFileSync(path.join(h.tmp, 'app-commands.json'));
function unchanged(h, before) {
  // Compare as a boolean: assertion failures must not print persisted lease capabilities.
  assert.ok(snapshot(h).equals(before), 'unauthorized request must not mutate persisted commands');
}

test('production command bridge requires a dedicated local credential; rejected requests cannot change outcomes', async t => {
  const { h, token, ownerHeaders } = await fixture(t);
  const id = await enqueue(h, ownerHeaders);
  const pending = snapshot(h);
  const rejectedHeaders = [
    {}, ownerHeaders, header(randomCredential()),
    { ...header(token), 'x-forwarded-for': '203.0.113.7' },
    ...['MUX_HOST_TOKEN', 'MUX_BRIDGE_TOKEN', 'MUX_TRANSCRIPT_BRIDGE_TOKEN', 'MUX_DISPATCH_KEY']
      .map(name => header(h.env[name])),
    { authorization: `Bearer ${token}` },
  ];
  for (const headers of rejectedHeaders) {
    const result = await h.request('POST', leasePath, { ...leaseBody, waitMs: 25000 }, headers);
    assert.equal(result.status, 403);
    unchanged(h, pending);
  }
  for (const route of ['/API/APP-COMMANDS/LEASE/', '/api/app-commands/lease/?probe=1']) {
    assert.equal((await h.request('POST', route, leaseBody, ownerHeaders)).status, 403);
  }
  const claimed = await h.request('POST', leasePath, leaseBody, header(token));
  assert.equal(claimed.status, 200);
  assert.equal(claimed.body.length, 1);
  assert.equal(claimed.body[0].id, id);
  const command = claimed.body[0];
  const ackPath = `/api/app-commands/${encodeURIComponent(id)}/ack`;
  const ack = { leaseToken: command.leaseToken, ok: true };
  const leased = snapshot(h);
  for (const headers of rejectedHeaders) {
    assert.equal((await h.request('POST', ackPath, ack, headers)).status, 403);
    unchanged(h, leased);
  }
  await h.restart();
  const reloaded = snapshot(h);
  const beforeRestart = JSON.parse(leased.toString('utf8'));
  const afterRestart = JSON.parse(reloaded.toString('utf8'));
  // Startup reconstructs commands in loader property order, then saves them. Enqueue places
  // expectedDeletedRevision/expectedRecentlyDeletedRevision/takeover before expectedRevision;
  // the loader places those revision fields after expectedCollectionRevision, and takeover
  // after tabs. No value, field presence, or array order may change across restart.
  assert.ok(isDeepStrictEqual(afterRestart, beforeRestart),
    'restart must preserve every command field and value, including the lease and outcome');
  const changedFields = [...new Set([
    ...Object.keys(beforeRestart[0]), ...Object.keys(afterRestart[0]),
  ])].filter(key => !Object.hasOwn(beforeRestart[0], key)
    || !Object.hasOwn(afterRestart[0], key)
    || !isDeepStrictEqual(beforeRestart[0][key], afterRestart[0][key]));
  assert.equal(changedFields.length, 0, 'restart must not add, remove, or alter any command field');
  t.diagnostic(`restart normalization: changed field values=${changedFields.length}; property order changed=${!isDeepStrictEqual(Object.keys(beforeRestart[0]), Object.keys(afterRestart[0]))}`);
  // Recheck the forbidden operation against the freshly loaded state as well.
  assert.equal((await h.request('POST', ackPath, ack, {})).status, 403);
  unchanged(h, reloaded);
  const status = await h.request('GET', `/api/app-commands/${id}`, undefined, ownerHeaders);
  assert.equal(status.body.status, 'leased');
  assert.equal((await h.request('POST', ackPath, { ...ack, leaseToken: randomCredential() }, header(token))).status, 409);
  const completed = await h.request('POST', ackPath, ack, header(token));
  assert.equal(completed.status, 200);
  assert.equal(completed.body.status, 'done');
  const terminal = snapshot(h);
  for (const headers of rejectedHeaders) {
    assert.equal((await h.request('POST', ackPath, ack, headers)).status, 403);
    unchanged(h, terminal);
  }
  const duplicate = await h.request('POST', ackPath, ack, header(token));
  assert.equal(duplicate.status, 200);
  assert.equal(duplicate.body.deduplicated, true);
  assert.equal((await h.request('POST', ackPath, { ...ack, ok: false }, header(token))).status, 409);
});

test('created container identity is scoped, sanitized and durable across restart', async t => {
  const { h, token, ownerHeaders } = await fixture(t);
  for (const [type, ok, resultId, expected] of [
    ['deckcreate', true, 'created-deck', 'created-deck'],
    ['collectioncreate', true, 'created-collection', 'created-collection'],
    ['deckcreate', true, '../private/path', undefined],
    ['deckcreate', false, 'not-created', undefined],
    ['setfavorite', true, 'unrelated-result', undefined],
  ]) {
    const queued = await h.request('POST', '/api/app-commands', {
      type, name: 'Fixture', deckId: 'main', sessionId: 'fixture', favorite: true,
      intentId: crypto.randomUUID(),
    }, ownerHeaders);
    assert.equal(queued.status, 200);
    const claimed = await h.request('POST', leasePath, leaseBody, header(token));
    const command = claimed.body[0];
    assert.equal(command.id, queued.body.id);
    const ack = await h.request('POST', `/api/app-commands/${command.id}/ack`, {
      leaseToken: command.leaseToken, ok, resultId, detail: 'private diagnostic text',
    }, header(token));
    assert.equal(ack.status, 200);
    const read = () => h.request('GET', `/api/app-commands/${command.id}`, undefined, ownerHeaders);
    assert.equal((await read()).body.resultId, expected);
    assert.ok(!(await read()).body.detail.includes('private diagnostic'));
    await h.restart();
    assert.equal((await read()).body.resultId, expected);
  }
});

test('production command credential grants no owner, transcript, or dispatch scope', async t => {
  const { h, token } = await fixture(t);
  for (const [method, route, body] of [
    ['GET', '/api/sessions'],
    ['GET', '/api/app-commands/unknown'],
    ['POST', '/api/app-commands', { type: 'setfavorite', sessionId: 'fixture', favorite: true }],
    ['POST', '/api/transcripts/fixture', {}],
    ['POST', '/api/archive-index', {}],
    ['POST', '/api/sessions/fixbot-worker/relaunch', {}],
  ]) {
    assert.equal((await h.request(method, route, body, header(token))).status, 401);
  }
});

for (const testMode of [false, true]) {
  test(`command auth fails closed without configuration (test mode ${testMode})`, async t => {
    const { h, token, ownerHeaders } = await fixture(t, { configured: false, testMode });
    for (const route of [leasePath, '/api/app-commands/unknown/ack']) {
      for (const headers of [{}, ownerHeaders, header(token)]) {
        assert.equal((await h.request('POST', route, leaseBody, headers)).status, 503);
      }
    }
  });
}

test('TEST_MODE cannot bypass configured command authentication', async t => {
  const { h, token, ownerHeaders } = await fixture(t, { testMode: true });
  for (const route of [leasePath, '/api/app-commands/unknown/ack']) {
    for (const headers of [{}, ownerHeaders, header(randomCredential())]) {
      assert.equal((await h.request('POST', route, leaseBody, headers)).status, 403);
    }
  }
  assert.equal((await h.request('POST', leasePath, leaseBody, header(token))).status, 200);
});
