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
