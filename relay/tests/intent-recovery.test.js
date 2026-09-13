const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const http = require('node:http');
const { RelayHarness, leaseCommands, ackLeased } = require('./harness');

test('metadata-only projections do not manufacture or overwrite running verification', async t => {
  const h = new RelayHarness();
  t.after(() => h.stop());
  await h.start();
  const metadata = { schemaVersion: 3, metadataOnly: true, decks: [], collections: [], allChats: [], runningSessions: [], runningVerified: false };
  await h.json('POST', '/api/projects', metadata);
  let projects = await h.json('GET', '/api/projects');
  assert.equal(projects.bridgeLive, false);
  await h.json('POST', '/api/running', { schemaVersion: 3, runningSessions: [], runningVerified: true });
  projects = await h.json('GET', '/api/projects');
  const timestamp = projects.runningSyncedAt;
  await h.json('POST', '/api/projects', metadata);
  projects = await h.json('GET', '/api/projects');
  assert.equal(projects.runningSyncedAt, timestamp);
  assert.equal(projects.runningVerified, true);
});

function command(intentId) {
  return { intentId, type: 'transcript', sessionId: 'recovery-session' };
}

function ownerHeaders() {
  return { cookie: 'hl_session=owner', 'x-forwarded-for': '203.0.113.10' };
}

async function ownerOracle(t) {
  const auth = http.createServer((req, res) => {
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify({ authenticated: true, user: { isOwner: true } }));
  });
  await new Promise(resolve => auth.listen(0, '127.0.0.1', resolve));
  t.after(() => auth.close());
  return `http://127.0.0.1:${auth.address().port}`;
}

async function recovery(h, intentId, fingerprint, headers = ownerHeaders()) {
  const query = fingerprint == null ? '' : `?fingerprint=${encodeURIComponent(fingerprint)}`;
  return h.request('GET', `/api/app-commands/by-intent/${encodeURIComponent(intentId)}${query}`, undefined, headers);
}

test('intent recovery survives enqueue, reload, and terminal ack without exposing payload or lease', async t => {
  const h = new RelayHarness({ HLAUTH_BASE: await ownerOracle(t) });
  await h.start();
  t.after(() => h.stop());

  const first = await h.json('POST', '/api/app-commands', command('reload-intent'));
  const payloadShape = JSON.stringify(first);
  assert.doesNotMatch(payloadShape, /recovery-session|leaseToken|principalAuth/);
  assert.match(first.fingerprint, /^[a-f0-9]{64}$/);
  let pending = await recovery(h, first.intentId, first.fingerprint);
  assert.equal(pending.status, 200);
  assert.deepEqual({ id: pending.body.id, intentId: pending.body.intentId, status: pending.body.status },
    { id: first.id, intentId: first.intentId, status: 'pending' });
  assert.equal(Object.prototype.hasOwnProperty.call(pending.body, 'title'), false);
  assert.equal(Object.prototype.hasOwnProperty.call(pending.body, 'leaseToken'), false);

  await h.restart();
  pending = await recovery(h, first.intentId, first.fingerprint);
  assert.equal(pending.status, 200);
  const leased = (await leaseCommands(h)).find(item => item.id === first.id);
  assert.ok(leased);
  await ackLeased(h, leased, { ok: true });
  const terminal = await recovery(h, first.intentId, first.fingerprint);
  assert.deepEqual({ id: terminal.body.id, status: terminal.body.status, detail: terminal.body.detail },
    { id: first.id, status: 'done', detail: 'transcript opened' });
});

test('resume launch mode survives restart and conflicts when changed', async t => {
  const h = new RelayHarness();
  t.after(() => h.stop());
  await h.start();
  const body = { type: 'startmux', muxName: 'mode-test', tool: 'claude', intentId: 'mode-restart', launchMode: 'gateway' };
  const queued = await h.request('POST', '/api/app-commands', body);
  assert.equal(queued.status, 200);
  await h.restart();
  const leased = (await leaseCommands(h)).find(item => item.id === queued.body.id);
  assert.equal(leased.launchMode, 'gateway');
  const conflict = await h.request('POST', '/api/app-commands', { ...body, launchMode: 'native' });
  assert.equal(conflict.status, 409);
  const invalid = await h.request('POST', '/api/app-commands', { ...body, intentId: 'invalid-mode', launchMode: 'arbitrary-command' });
  assert.equal(invalid.status, 400);
});

test('intent recovery rejects unknown and conflicting fingerprints', async t => {
  const h = new RelayHarness({ HLAUTH_BASE: await ownerOracle(t) });
  await h.start();
  t.after(() => h.stop());
  const first = await h.json('POST', '/api/app-commands', command('conflict-intent'));
  const unknown = await recovery(h, 'missing-intent', first.fingerprint);
  assert.equal(unknown.status, 404);
  const conflict = await recovery(h, first.intentId, crypto.createHash('sha256').update('different').digest('hex'));
  assert.equal(conflict.status, 409);
  assert.deepEqual(conflict.body, { error: 'intent fingerprint does not match command' });
  const missingFingerprint = await recovery(h, first.intentId, null);
  assert.equal(missingFingerprint.status, 200);
  assert.equal(missingFingerprint.body.fingerprint, first.fingerprint);
});

test('intent recovery remains owner-authenticated', async t => {
  const h = new RelayHarness({ HLAUTH_BASE: 'http://127.0.0.1:1' });
  await h.start();
  t.after(() => h.stop());
  const first = await h.json('POST', '/api/app-commands', command('auth-intent'));
  const unauthenticated = await recovery(h, first.intentId, first.fingerprint, { 'x-forwarded-for': '203.0.113.10' });
  assert.equal(unauthenticated.status, 401);
  const loopbackWithoutOwner = await recovery(h, first.intentId, first.fingerprint, {});
  assert.equal(loopbackWithoutOwner.status, 401);
});

test('intent recovery bootstraps the fingerprint after a lost enqueue response', async t => {
  const h = new RelayHarness({ HLAUTH_BASE: await ownerOracle(t) });
  await h.start();
  t.after(() => h.stop());
  const first = await h.json('POST', '/api/app-commands', command('bootstrap-intent'));
  const recovered = await recovery(h, first.intentId, null);
  assert.equal(recovered.status, 200);
  assert.deepEqual(Object.keys(recovered.body).sort(), ['detail', 'fingerprint', 'id', 'intentId', 'status', 'uncertain']);
  assert.equal(recovered.body.fingerprint, first.fingerprint);
});

test('intent recovery refuses invalid fingerprints and never exposes lease or payload fields', async t => {
  const h = new RelayHarness({ HLAUTH_BASE: await ownerOracle(t) });
  await h.start();
  t.after(() => h.stop());
  const first = await h.json('POST', '/api/app-commands', command('invalid-fingerprint-intent'));
  const invalid = await recovery(h, first.intentId, 'not-a-fingerprint');
  assert.equal(invalid.status, 400);
  const pending = await recovery(h, first.intentId, first.fingerprint);
  assert.deepEqual(Object.keys(pending.body).sort(), ['detail', 'fingerprint', 'id', 'intentId', 'status', 'uncertain']);
  assert.equal(Object.prototype.hasOwnProperty.call(pending.body, 'leaseToken'), false);
  assert.equal(Object.prototype.hasOwnProperty.call(pending.body, 'sessionId'), false);
});

test('intent recovery clears the browser record only after terminal status', async () => {
  const source = require('node:fs').readFileSync(require('node:path').join(__dirname, '..', 'public', 'intent-journal.js'), 'utf8');
  const values = new Map();
  const storage = {
    getItem: key => values.has(key) ? values.get(key) : null,
    setItem: (key, value) => values.set(key, value),
  };
  let status = 'pending';
  const context = {
    console,
    crypto: { randomUUID: () => '11111111-2222-4333-8444-555555555555' },
    fetch: async () => ({ ok: true, status: 200, json: async () => ({
      id: 'cmd-journal', intentId: 'journal-intent', fingerprint: 'd'.repeat(64), status, detail: '',
    }) }),
    localStorage: storage,
    sessionStorage: null,
    setTimeout: callback => callback(),
  };
  context.globalThis = context;
  require('node:vm').runInNewContext(source, context);
  const response = await context.postIntent('/api/app-commands', { type: 'transcript', sessionId: 'journal-session' }, 'test');
  assert.equal(response.status, 200);
  assert.equal(context.intentRecords().length, 1);
  status = 'done';
  await context.recoverPendingIntents();
  assert.equal(context.intentRecords().length, 0);
});

test('browser journal preserves an explicitly uncertain terminal command for local recovery', async () => {
  const source = require('node:fs').readFileSync(require('node:path').join(__dirname, '..', 'public', 'intent-journal.js'), 'utf8');
  const values = new Map();
  const storage = {
    getItem: key => values.has(key) ? values.get(key) : null,
    setItem: (key, value) => values.set(key, value),
  };
  const intentId = 'reclaim-11111111222243338444555555555555';
  const fingerprint = 'f'.repeat(64);
  let rejectPost = false;
  const context = {
    console,
    crypto: { randomUUID: () => '11111111-2222-4333-8444-555555555555' },
    fetch: async (url, options) => {
      if (options && options.method === 'POST') {
        if (rejectPost) return { ok: false, status: 409, json: async () => ({ error: 'target no longer exists' }) };
        const body = { id: 'cmd-uncertain', intentId, fingerprint, status: 'pending', detail: '', uncertain: false };
        return { ok: true, status: 200, clone: () => ({ json: async () => body }), json: async () => body };
      }
      return { ok: true, status: 200, json: async () => ({
        id: 'cmd-uncertain', intentId, fingerprint, status: 'failed',
        detail: 'reclaim outcome is uncertain; automatic replay is fenced', uncertain: true,
      }) };
    },
    localStorage: storage,
    sessionStorage: null,
    setTimeout: callback => callback(),
  };
  context.globalThis = context;
  require('node:vm').runInNewContext(source, context);
  await context.postIntent('/api/app-commands', { type: 'reclaim', sessionId: 'chat-one' }, 'reclaim');

  const result = await context.pollIntent(intentId, { timeoutMs: 20, intervalMs: 10 });
  assert.equal(result.status, 'failed');
  assert.equal(result.uncertain, true);
  assert.equal(context.intentRecords().length, 1);
  assert.equal(context.intentRecords()[0].intentId, intentId);
  rejectPost = true;
  const rejected = await context.postIntent('/api/app-commands', { type: 'reclaim', sessionId: 'chat-one' }, 'reclaim');
  assert.equal(rejected.status, 409);
  assert.equal(context.intentRecords().length, 1);
  assert.equal(context.intentRecords()[0].intentId, intentId);
});

test('bounded intent polling preserves timeout uncertainty and clears only terminal failure', async () => {
  const source = require('node:fs').readFileSync(require('node:path').join(__dirname, '..', 'public', 'intent-journal.js'), 'utf8');
  const values = new Map();
  const storage = {
    getItem: key => values.has(key) ? values.get(key) : null,
    setItem: (key, value) => values.set(key, value),
  };
  let status = 'pending';
  let current = 0;
  let lookups = 0;
  const intentId = 'poll-11111111222243338444555555555555';
  const fingerprint = 'e'.repeat(64);
  const context = {
    console,
    crypto: { randomUUID: () => '11111111-2222-4333-8444-555555555555' },
    fetch: async (url, options) => {
      if (options && options.method === 'POST') {
        const body = { id: 'cmd-poll', intentId, fingerprint, status: 'pending', detail: '' };
        return { ok: true, status: 200, clone: () => ({ json: async () => body }), json: async () => body };
      }
      assert.match(String(url), /\/api\/app-commands\/by-intent\/poll-11111111222243338444555555555555\?fingerprint=/);
      lookups++;
      return { ok: true, status: 200, json: async () => ({
        id: 'cmd-poll', intentId, fingerprint, status, detail: status === 'failed' ? 'PC refused it' : '',
      }) };
    },
    localStorage: storage,
    sessionStorage: null,
    setTimeout: callback => callback(),
  };
  context.globalThis = context;
  require('node:vm').runInNewContext(source, context);
  await context.postIntent('/api/app-commands', { type: 'transcript', sessionId: 'poll-session' }, 'poll');

  const timedOut = await context.pollIntent(intentId, {
    timeoutMs: 20, intervalMs: 10, now: () => current, sleep: async ms => { current += ms; },
  });
  assert.equal(timedOut.status, 'pending');
  assert.equal(timedOut.timedOut, true);
  assert.equal(lookups, 3);
  assert.equal(context.intentRecords().length, 1);

  status = 'failed';
  const failed = await context.pollIntent(intentId, { timeoutMs: 20, intervalMs: 10 });
  assert.equal(failed.status, 'failed');
  assert.equal(failed.detail, 'PC refused it');
  assert.equal(context.intentRecords().length, 0);
});
