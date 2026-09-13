const test = require('node:test');
const assert = require('node:assert/strict');
const { RelayHarness, ackLeased, leaseCommands } = require('./harness');

test('reclaim requires explicit confirmation and preserves its fence across restart', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const payload = { type: 'reclaim', intentId: 'reclaim-test-1', sessionId: 'exact-chat',
    tool: 'codex', expectedRevision: 'revision-1', confirmed: true };
  for (const patch of [{ confirmed: false }, { confirmed: 'true' }, { expectedRevision: '' }, { intentId: '' }, { cmd: 'forbidden' }]) {
    assert.equal((await h.request('POST', '/api/app-commands', { ...payload, ...patch })).status, 400);
  }
  const first = await h.json('POST', '/api/app-commands', payload);
  await h.restart();
  const replay = await h.json('POST', '/api/app-commands', payload);
  assert.equal(replay.id, first.id);
  assert.equal(replay.deduplicated, true);
  const command = (await leaseCommands(h)).find(c => c.id === first.id);
  assert.equal(command.confirmed, true);
  assert.equal(command.expectedRevision, 'revision-1');
  assert.equal(command.replayPolicy, 'intent-fenced');
  assert.equal((await h.request('POST', '/api/app-commands', { ...payload, expectedRevision: 'revision-2' })).status, 409);
});

function blankStartChat(overrides = {}) {
  return {
    type: 'startchat',
    intentId: 'startchat-intent-1',
    muxName: 'phone-chat',
    title: 'Phone chat',
    tool: 'codex',
    checkpointId: '',
    workspaceId: 'workspace-main',
    subfolder: 'mobile',
    deckId: 'main',
    collectionId: '',
    collection: 'Web Parity',
    phrase: 'web-parity',
    ...overrides,
  };
}

test('handoff source and Gateway mode survive normalization restart and replay', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const payload = blankStartChat({ tool: 'claude', launchMode: 'gateway', handoffFromId: 'codex-source' });
  const first = await h.json('POST', '/api/app-commands', payload);
  await h.restart();
  const command = (await leaseCommands(h)).find(c => c.id === first.id);
  assert.equal(command.handoffFromId, 'codex-source');
  assert.equal(command.launchMode, 'gateway');
  assert.equal((await h.json('POST', '/api/app-commands', payload)).id, first.id);
  assert.equal((await h.request('POST', '/api/app-commands', { ...payload, handoffFromId: 'different-source' })).status, 409);
  assert.equal((await h.request('POST', '/api/app-commands', { ...payload, tool: 'codex' })).status, 400);
});

test('startchat is identity-only, intent-fenced, normalized, and durably replayed', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());

  const first = await h.json('POST', '/api/app-commands', blankStartChat({
    intentId: 'startchat-normalized-1',
    muxName: '  phone-chat  ',
    title: '  Phone chat  ',
    tool: ' CODEX ',
    workspaceId: ' workspace-main ',
    subfolder: ' mobile ',
    collection: ' Web Parity ',
    phrase: ' web-parity ',
  }));
  const retry = await h.json('POST', '/api/app-commands', blankStartChat({
    intentId: 'startchat-normalized-1',
  }));
  assert.equal(retry.id, first.id);
  assert.equal(retry.deduplicated, true);

  const command = (await leaseCommands(h)).find(item => item.id === first.id);
  assert.ok(command);
  assert.equal(command.replayPolicy, 'intent-fenced');
  assert.deepEqual(
    {
      type: command.type,
      muxName: command.muxName,
      title: command.title,
      tool: command.tool,
      checkpointId: command.checkpointId,
      workspaceId: command.workspaceId,
      subfolder: command.subfolder,
      deckId: command.deckId,
      collectionId: command.collectionId,
      collection: command.collection,
      phrase: command.phrase,
    },
    {
      type: 'startchat',
      muxName: 'phone-chat',
      title: 'Phone chat',
      tool: 'codex',
      checkpointId: '',
      workspaceId: 'workspace-main',
      subfolder: 'mobile',
      deckId: 'main',
      collectionId: '',
      collection: 'Web Parity',
      phrase: 'web-parity',
    },
  );
  assert.match(command.fingerprint, /^[a-f0-9]{64}$/);
  for (const forbidden of ['command', 'cwd', 'path', 'workspacePath', 'checkpointPath', 'executable']) {
    assert.equal(Object.prototype.hasOwnProperty.call(command, forbidden), false, forbidden);
  }

  const conflict = await h.request('POST', '/api/app-commands', blankStartChat({
    intentId: 'startchat-normalized-1',
    phrase: 'different-phrase',
  }));
  assert.equal(conflict.status, 409);

  await ackLeased(h, command, { ok: true, detail: 'C:\\Users\\Ahmed\\secret\\chat' });
  const outcome = await h.json('GET', `/api/app-commands/${first.id}`);
  assert.deepEqual(
    { status: outcome.status, detail: outcome.detail },
    { status: 'done', detail: 'chat started' },
  );
  assert.equal(JSON.stringify(outcome).includes('C:\\Users'), false);

  await h.restart();
  const afterRestart = await h.json('POST', '/api/app-commands', blankStartChat({
    intentId: 'startchat-normalized-1',
    title: ' Phone chat ',
  }));
  assert.equal(afterRestart.id, first.id);
  assert.equal(afterRestart.deduplicated, true);
});

test('startchat validates launch-source exclusivity, picker identities, and local subfolders', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());

  const invalid = [
    ['missing intent', { intentId: undefined }],
    ['missing mux name', { muxName: '' }],
    ['missing deck', { deckId: '' }],
    ['missing workspace', { workspaceId: '' }],
    ['invalid tool', { tool: 'shell' }],
    ['checkpoint with blank-chat fields', { checkpointId: 'checkpoint-1', tool: 'codex' }],
    ['both collection targets', { collectionId: 'collection-1', collection: 'New collection' }],
    ['path-shaped workspace identity', { workspaceId: 'C:\\Users\\Ahmed\\workspace' }],
    ['path-shaped checkpoint identity', { checkpointId: '../checkpoint' }],
    ['non-string title', { title: 42 }],
    ['unknown path field', { workspacePath: 'C:\\Users\\Ahmed\\workspace' }],
  ];
  for (const [label, patch] of invalid) {
    const body = blankStartChat(patch);
    if (patch.intentId === undefined) delete body.intentId;
    const response = await h.request('POST', '/api/app-commands', body);
    assert.equal(response.status, 400, label);
  }

  for (const subfolder of ['.', '..', '../child', 'child/name', 'C:\\child', 'bad:name', 'CON']) {
    const response = await h.request('POST', '/api/app-commands', blankStartChat({
      intentId: `startchat-subfolder-${subfolder.replace(/[^A-Za-z0-9]/g, '') || 'invalid'}`,
      subfolder,
    }));
    assert.equal(response.status, 400, `subfolder ${subfolder}`);
  }

  const checkpoint = await h.json('POST', '/api/app-commands', blankStartChat({
    intentId: 'startchat-checkpoint-1',
    title: 'Checkpoint chat',
    tool: '',
    checkpointId: 'checkpoint-1',
    checkpointRevision: 'checkpoint-revision-1',
    collectionRevision: 'collection-revision-1',
    workspaceId: '',
    subfolder: '',
    collection: '',
    collectionId: 'collection-1',
  }));
  await h.restart();
  const changedRevision = await h.request('POST', '/api/app-commands', blankStartChat({
    intentId: 'startchat-checkpoint-1', title: 'Checkpoint chat', tool: '',
    checkpointId: 'checkpoint-1', checkpointRevision: 'checkpoint-revision-2',
    collectionRevision: 'collection-revision-1', workspaceId: '', subfolder: '',
    collection: '', collectionId: 'collection-1',
  }));
  assert.equal(changedRevision.status, 409, 'revision change must collide after restart');
  const command = (await leaseCommands(h)).find(item => item.id === checkpoint.id);
  assert.ok(command);
  assert.deepEqual(
    {
      checkpointId: command.checkpointId,
      tool: command.tool,
      workspaceId: command.workspaceId,
      subfolder: command.subfolder,
      collectionId: command.collectionId,
    },
    {
      checkpointId: 'checkpoint-1',
      tool: '',
      workspaceId: '',
      subfolder: '',
      collectionId: 'collection-1',
    },
  );
  assert.equal(command.checkpointRevision, 'checkpoint-revision-1');
  assert.equal(command.collectionRevision, 'collection-revision-1');
  await ackLeased(h, command, { ok: false, detail: 'local path and command must not surface' });
  const outcome = await h.json('GET', `/api/app-commands/${checkpoint.id}`);
  assert.deepEqual(
    { status: outcome.status, detail: outcome.detail },
    { status: 'failed', detail: 'PC bridge could not start chat' },
  );
});

test('browser explicit retry reconciles the same uncertain intent without enqueueing again', async t => {
  const auth = require('node:http').createServer((req, res) => {
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify({ authenticated: true, user: { isOwner: req.headers['x-session-token'] === 'owner' } }));
  });
  await new Promise(resolve => auth.listen(0, '127.0.0.1', resolve));
  t.after(() => auth.close());
  const h = new RelayHarness({ HLAUTH_BASE: `http://127.0.0.1:${auth.address().port}` });
  await h.start();
  t.after(() => h.stop());
  const vm = require('node:vm');
  const fs = require('node:fs');
  const path = require('node:path');
  const saved = new Map();
  const calls = [];
  const context = vm.createContext({
    console, setTimeout, crypto: require('node:crypto').webcrypto,
    localStorage: { getItem: key => saved.get(key) || null, setItem: (key, value) => saved.set(key, value) },
    fetch: async (url, options = {}) => {
      calls.push([options.method || 'GET', url]);
      const response = await h.request(options.method || 'GET', url, options.body ? JSON.parse(options.body) : undefined,
        { cookie: 'hl_session=owner', 'x-forwarded-for': '203.0.113.10' });
      return { ok: response.status >= 200 && response.status < 300, status: response.status,
        json: async () => response.body, clone() { return this; } };
    },
  });
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../public/intent-journal.js'), 'utf8'), context);
  const body = blankStartChat();
  delete body.intentId;
  const first = await (await context.postIntent('/api/app-commands', body, 'web')).json();
  const [leased] = await leaseCommands(h);
  await ackLeased(h, leased, { ok: false, uncertain: true });
  for (const cookie of ['', 'hl_session=non-owner']) {
    const denied = await h.request('POST', `/api/app-commands/${first.id}/reconcile`,
      { fingerprint: first.fingerprint }, { cookie, 'x-forwarded-for': '203.0.113.10' });
    assert.ok([401, 403].includes(denied.status));
    assert.equal((await h.json('GET', `/api/app-commands/${first.id}`)).status, 'failed');
  }
  await context.recoverPendingIntents({});
  assert.equal(calls.filter(([method, url]) => method === 'POST' && url.endsWith('/reconcile')).length, 0);
  // Model a lost enqueue response: retain durable intent but discard server-assigned metadata.
  const journal = JSON.parse(saved.get('mux.intent-journal.v1'));
  delete journal.records[0].commandId;
  delete journal.records[0].fingerprint;
  saved.set('mux.intent-journal.v1', JSON.stringify(journal));
  const retry = await (await context.postIntent('/api/app-commands', body, 'web')).json();
  assert.equal(retry.intentId, first.intentId);
  assert.equal(calls.filter(([method, url]) => method === 'POST' && url === '/api/app-commands').length, 1);
  assert.equal(calls.filter(([method, url]) => method === 'POST' && url.endsWith('/reconcile')).length, 1);
  assert.equal(context.intentRecords().length, 1);
  const pendingRetry = await (await context.postIntent('/api/app-commands', body, 'web')).json();
  assert.equal(pendingRetry.id, first.id);
  assert.equal(pendingRetry.status, 'pending');
  const recoveryLease = await h.request('POST', '/api/app-commands/lease',
    { owner: 'browser-recovery', commandTypes: ['startchat'], reconcileStartChat: true },
    { 'X-Mux-Command-Bridge': h.commandBridgeToken });
  assert.equal(recoveryLease.status, 200);
  await ackLeased(h, recoveryLease.body[0], { ok: true });
  const doneRetry = await (await context.postIntent('/api/app-commands', body, 'web')).json();
  assert.equal(doneRetry.id, first.id);
  assert.equal(doneRetry.status, 'done');
  assert.equal(context.intentRecords().length, 0);
  assert.equal(calls.filter(([method, url]) => method === 'POST' && url === '/api/app-commands').length, 1);
});

test('explicit uncertain recovery survives restart and excludes old consumers', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(() => h.stop());
  const queued = await h.json('POST', '/api/app-commands', blankStartChat());
  const [first] = await leaseCommands(h);
  await ackLeased(h, first, { ok: false, uncertain: true });
  const route = `/api/app-commands/${queued.id}/reconcile`;
  assert.equal((await h.request('POST', route, { fingerprint: 'wrong' })).status, 409);
  h.failPersistence('app-commands.json', 'beforeWrite');
  assert.equal((await h.request('POST', route, { fingerprint: queued.fingerprint })).status, 503);
  const unchanged = await h.json('GET', `/api/app-commands/${queued.id}`);
  assert.equal(unchanged.status, 'failed');
  assert.equal(unchanged.uncertain, true);
  const legacyLease = await h.request('POST', '/api/app-commands/lease',
    { owner: 'recovery-aware', commandTypes: ['startchat'], reconcileStartChat: true },
    { 'X-Mux-Command-Bridge': h.commandBridgeToken });
  assert.equal(legacyLease.status, 200);
  assert.equal(legacyLease.body.length, 0);
  assert.equal((await h.request('POST', route, { fingerprint: queued.fingerprint })).status, 200);
  await h.restart();
  assert.equal((await leaseCommands(h)).length, 0, 'legacy consumers must never receive recovery work');
  const recoveryLease = await h.request('POST', '/api/app-commands/lease',
    { owner: 'recovery-aware', commandTypes: ['startchat'], reconcileStartChat: true },
    { 'X-Mux-Command-Bridge': h.commandBridgeToken });
  assert.equal(recoveryLease.status, 200);
  const [recovery] = recoveryLease.body;
  assert.equal(recovery.reconcileOnly, true);
  assert.equal(recovery.intentId, first.intentId);
  assert.equal(recovery.fingerprint, queued.fingerprint);
  assert.notEqual(recovery.leaseToken, first.leaseToken);
  assert.equal((await h.request('POST', `/api/app-commands/${queued.id}/ack`, {
    leaseToken: first.leaseToken, ok: true,
  }, { 'X-Mux-Command-Bridge': h.commandBridgeToken })).status, 409);
  await ackLeased(h, recovery, { ok: false });
  const unresolved = await h.json('GET', `/api/app-commands/${queued.id}`);
  assert.equal(unresolved.status, 'failed');
  assert.equal(unresolved.uncertain, true, 'generic recovery failure must not erase original uncertainty');
  assert.equal((await h.request('POST', route, { fingerprint: queued.fingerprint })).status, 200);
  const againLease = await h.request('POST', '/api/app-commands/lease',
    { owner: 'recovery-aware', commandTypes: ['startchat'], reconcileStartChat: true },
    { 'X-Mux-Command-Bridge': h.commandBridgeToken });
  assert.equal(againLease.status, 200);
  await ackLeased(h, againLease.body[0], { ok: true });
  assert.equal((await h.json('GET', `/api/app-commands/${queued.id}`)).status, 'done');
});

test('confirmed start awaiting filing re-leases the same intent after restart', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(() => h.stop());
  const queued = await h.json('POST', '/api/app-commands', blankStartChat());
  const [first] = await leaseCommands(h);
  await ackLeased(h, first, { ok: false, retryable: true, detail: 'startchat launch confirmed; authoritative filing is pending' });
  const pending = await h.json('GET', `/api/app-commands/${queued.id}`);
  assert.match(pending.detail, /Chat launched; waiting for its first transcript/);
  await h.restart();
  const [second] = await leaseCommands(h);
  assert.equal(second.id, queued.id);
  assert.equal(second.intentId, first.intentId);
  assert.notEqual(second.leaseToken, first.leaseToken);
  const stale = await h.request('POST', `/api/app-commands/${queued.id}/ack`, {
    leaseToken: first.leaseToken, ok: true,
  }, { 'X-Mux-Command-Bridge': h.commandBridgeToken });
  assert.equal(stale.status, 409, 'expired filing lease must not settle the new delivery');
  const active = await h.json('GET', `/api/app-commands/${queued.id}`);
  assert.equal(active.status, 'leased');
  await ackLeased(h, second, { ok: true });
  const status = await h.json('GET', `/api/app-commands/${queued.id}`);
  assert.equal(status.status, 'done');
  assert.equal((await leaseCommands(h)).length, 0);
});

test('startchat launch modes survive restart and remain fingerprint-bound', async t => {
  const h = new RelayHarness();
  t.after(() => h.stop());
  await h.start();
  for (const launchMode of ['native', 'gateway', 'deepseek', 'luna']) {
    const body = blankStartChat({ intentId: `start-mode-${launchMode}`, launchMode });
    const queued = await h.request('POST', '/api/app-commands', body);
    assert.equal(queued.status, 200);
  }
  await h.restart();
  const commands = await leaseCommands(h);
  for (const launchMode of ['native', 'gateway', 'deepseek', 'luna']) {
    const intentId = `start-mode-${launchMode}`;
    assert.equal(commands.find(command => command.intentId === intentId).launchMode, launchMode);
    const replay = await h.json('POST', '/api/app-commands', blankStartChat({ intentId, launchMode }));
    assert.equal(replay.deduplicated, true);
    const conflict = await h.request('POST', '/api/app-commands', blankStartChat({
      intentId, launchMode: launchMode === 'native' ? 'gateway' : 'native',
    }));
    assert.equal(conflict.status, 409);
  }
  for (const launchMode of ['arbitrary-command', '', null, 123]) {
    const invalid = await h.request('POST', '/api/app-commands', blankStartChat({
      intentId: 'invalid-mode', launchMode,
    }));
    assert.equal(invalid.status, 400);
  }
});
