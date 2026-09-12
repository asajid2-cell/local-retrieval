const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const vm = require('node:vm');
const { once } = require('node:events');
const WebSocket = require('ws');
const { durableJsonLoad, durableJsonWrite, recoveryWriteFailureReport } = require('../durable-state');
const {
  REPO,
  HOST_CAPS,
  sleep,
  waitFor,
  leaseCommands,
  ackLeased,
  waitForWsText,
  waitForWsFrame,
  RelayHarness,
  FakeHost,
} = require('./harness');

test('durable relay state write preserves the previous value on pre-commit failures', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'relay-durable-'));
  try {
    const file = path.join(root, 'state.json');
    const original = Buffer.from(JSON.stringify({ version: 1 }));
    fs.writeFileSync(file, original);

    for (const failedStage of ['beforeWrite', 'beforeFileFsync', 'beforeReplace']) {
      assert.throws(
        () => durableJsonWrite(file, { version: 2 }, {
          fault(stage) {
            if (stage === failedStage) throw new Error(`injected ${stage}`);
          },
        }),
        new RegExp(`injected ${failedStage}`),
      );
      assert.deepEqual(fs.readFileSync(file), original, `${failedStage} changed the accepted state`);
      assert.equal(fs.readdirSync(root).some(name => name.endsWith('.tmp')), false);
    }
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('durable relay state write does not report success after replace or read-back uncertainty', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'relay-durable-'));
  try {
    const file = path.join(root, 'state.json');
    fs.writeFileSync(file, JSON.stringify({ version: 1 }));

    durableJsonWrite(file, { version: 2 }, {
      fault(stage) {
        if (stage === 'beforeDirectoryFsync') throw new Error('injected directory fsync');
      },
    });
    assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')), { version: 2 });
    assert.deepEqual(JSON.parse(fs.readFileSync(file + '.bak', 'utf8')), { version: 1 });

    assert.throws(
      () => durableJsonWrite(file, { version: 3 }, {
        fault(stage) {
          if (stage === 'beforeReadback') fs.writeFileSync(file, '{"version":999}');
        },
      }),
      /committed state did not read back identically/,
    );
    assert.deepEqual(JSON.parse(fs.readFileSync(file + '.bak', 'utf8')), { version: 2 });
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('durable relay state load restores a corrupt primary from its backup', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'relay-durable-'));
  try {
    const file = path.join(root, 'state.json');
    fs.writeFileSync(file, '{"broken"');
    fs.writeFileSync(file + '.bak', JSON.stringify({ version: 7 }));

    assert.deepEqual(durableJsonLoad(file, { version: 0 }), { version: 7 });
    assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')), { version: 7 });
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

// A GOOD backup must survive a bad write. The recovered value has already parsed and validated, so
// republishing it as the primary is housekeeping — if that write fails (disk full, AV lock, read-only
// mount) the load must still hand back the recovered value. It previously threw, and because every
// caller in server.js runs at module scope, that turned a transient write error into a relay that
// would not start while a perfectly good .bak sat on disk.
test('a recovered backup survives a failed republish: the value is returned and the backup is kept', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'relay-durable-'));
  const faultFile = path.join(root, 'fault.json');
  const previousFaultFile = process.env.MUX_TEST_PERSIST_FAULT_FILE;
  try {
    const file = path.join(root, 'state.json');
    fs.writeFileSync(file, '{"broken"');
    fs.writeFileSync(file + '.bak', JSON.stringify({ version: 7 }));

    // Fail the republish only — the read/parse/validate of the backup all succeed first.
    process.env.MUX_TEST_PERSIST_FAULT_FILE = faultFile;
    fs.writeFileSync(faultFile, JSON.stringify({ stage: 'beforeWrite', file: 'state.json', remaining: 1 }));

    const before = recoveryWriteFailureReport().length;
    assert.deepEqual(
      durableJsonLoad(file, { version: 0 }),
      { version: 7 },
      'a failed republish must not cost us the recovered value',
    );
    // The backup is still there, so the next boot recovers again even if nothing wrote in between.
    assert.deepEqual(JSON.parse(fs.readFileSync(file + '.bak', 'utf8')), { version: 7 });
    // Recorded, not swallowed: /api/health reports this and goes degraded.
    const failures = recoveryWriteFailureReport();
    assert.equal(failures.length, before + 1, 'the failed republish must be recorded');
    assert.equal(failures[failures.length - 1].file, 'state.json');
  } finally {
    if (previousFaultFile === undefined) delete process.env.MUX_TEST_PERSIST_FAULT_FILE;
    else process.env.MUX_TEST_PERSIST_FAULT_FILE = previousFaultFile;
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('durable relay state load restores a semantically invalid primary from its backup', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'relay-durable-'));
  try {
    const file = path.join(root, 'state.json');
    fs.writeFileSync(file, JSON.stringify({ schemaVersion: 999 }));
    fs.writeFileSync(file + '.bak', JSON.stringify({ schemaVersion: 3, values: [] }));

    const loaded = durableJsonLoad(
      file,
      null,
      value => value && value.schemaVersion === 3 && Array.isArray(value.values),
    );

    assert.deepEqual(loaded, { schemaVersion: 3, values: [] });
    assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')), loaded);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('a backup commit error never claims that the primary candidate committed', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'relay-durable-'));
  try {
    const file = path.join(root, 'state.json');
    fs.writeFileSync(file, JSON.stringify({ version: 1 }));

    let error;
    try {
      durableJsonWrite(file, { version: 2 }, {
        backup: {
          fault(stage) {
            if (stage === 'beforeDirectoryFsync') throw new Error('backup durability uncertain');
          },
        },
      });
      assert.fail('expected backup persistence to fail');
    } catch (caught) {
      error = caught;
    }

    assert.match(error.message, /backup durability uncertain/);
    assert.equal(error.committed, false);
    assert.equal(error.primaryCommitted, false);
    assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')), { version: 1 });
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('post-replace verification read failure never rolls a possibly committed primary backward', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'relay-durable-'));
  const originalRead = fs.readFileSync;
  try {
    const file = path.join(root, 'state.json');
    fs.writeFileSync(file, JSON.stringify({ version: 1 }));
    let primaryReads = 0;
    fs.readFileSync = function patchedRead(target, ...args) {
      if (String(target) === file && ++primaryReads > 1)
        throw new Error('verification read unavailable');
      return originalRead.call(fs, target, ...args);
    };

    let error;
    try {
      durableJsonWrite(file, { version: 2 });
      assert.fail('expected verification uncertainty');
    } catch (caught) {
      error = caught;
    } finally {
      fs.readFileSync = originalRead;
    }

    assert.equal(error.unknown, true);
    assert.equal(error.recovered, undefined);
    assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')), { version: 2 });
    assert.deepEqual(JSON.parse(fs.readFileSync(file + '.bak', 'utf8')), { version: 1 });
  } finally {
    fs.readFileSync = originalRead;
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('retry failure preserves an already confirmed committed generation', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'relay-durable-'));
  try {
    const file = path.join(root, 'state.json');
    fs.writeFileSync(file, JSON.stringify({ version: 1 }));

    let error;
    try {
      durableJsonWrite(file, { version: 2 }, {
        fault(stage) {
          if (stage === 'beforeDirectoryFsync') throw new Error('initial durability uncertainty');
        },
        retry: {
          fault(stage) {
            if (stage === 'beforeWrite') throw new Error('retry unavailable');
          },
        },
      });
      assert.fail('expected retry failure');
    } catch (caught) {
      error = caught;
    }

    assert.match(error.message, /retry unavailable/);
    assert.equal(error.committed, true);
    assert.equal(error.mismatch, false);
    assert.equal(error.unknown, false);
    assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')), { version: 2 });
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('project projection is not published or acknowledged when durable commit fails', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());

  h.failPersistence('projects.json', 'beforeWrite');
  const failed = await h.request('POST', '/api/projects', {
    schemaVersion: 3,
    host: 'FAILED-PC',
    decks: [],
    collections: [],
    allChats: [{ id: 'must-not-publish', tool: 'codex', title: 'not durable' }],
    runningSessions: [],
    runningVerified: true,
  });
  assert.equal(failed.status, 503);
  const current = await h.json('GET', '/api/projects');
  assert.equal(current.allChats.some(chat => chat.id === 'must-not-publish'), false);

  await h.restart();
  const restarted = await h.json('GET', '/api/projects');
  assert.equal(restarted.allChats.some(chat => chat.id === 'must-not-publish'), false);
});

test('relay retries transient post-replace uncertainty before acknowledging state', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());

  h.failPersistence('projects.json', 'beforeDirectoryFsync');
  const projected = await h.request('POST', '/api/projects', {
    schemaVersion: 3,
    host: 'RETRIED-PC',
    decks: [],
    collections: [],
    allChats: [{ id: 'durable-after-retry', tool: 'codex', title: 'durable' }],
    runningSessions: [],
    runningVerified: true,
  });
  assert.equal(projected.status, 200);

  h.failPersistence('app-commands.json', 'beforeDirectoryFsync');
  const queued = await h.request('POST', '/api/app-commands', {
    type: 'kill',
    sessionId: 'single-command-after-retry',
  });
  assert.equal(queued.status, 200);

  const health = await h.json('GET', '/api/health');
  assert.equal(health.persistence.ok, true);
  await h.restart();
  const restarted = await h.json('GET', '/api/projects');
  assert.equal(restarted.allChats.some(chat => chat.id === 'durable-after-retry'), true);
  const pending = await leaseCommands(h);
  assert.equal(pending.filter(command => command.sessionId === 'single-command-after-retry').length, 1);
});

test('app command enqueue and acknowledgement fail closed on persistence errors', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());

  h.failPersistence('app-commands.json', 'beforeWrite');
  const enqueueFailed = await h.request('POST', '/api/app-commands', {
    type: 'kill',
    sessionId: 'not-durable',
  });
  assert.equal(enqueueFailed.status, 503);
  assert.deepEqual(await leaseCommands(h), []);

  const queued = await h.json('POST', '/api/app-commands', {
    type: 'kill',
    sessionId: 'durable-command',
  });
  const leased = (await leaseCommands(h))[0];
  assert.equal(leased.id, queued.id);
  h.failPersistence('app-commands.json', 'beforeWrite');
  const ackFailed = await h.request('POST', `/api/app-commands/${queued.id}/ack`, {
    leaseToken: leased.leaseToken,
    ok: true,
  }, { 'X-Mux-Command-Bridge': h.commandBridgeToken });
  assert.equal(ackFailed.status, 503);
  assert.equal((await h.json('GET', `/api/app-commands/${queued.id}`)).status, 'leased');

  await h.restart();
  const reclaimed = await waitFor(async () => {
    const commands = await leaseCommands(h, 'test-consumer-restart');
    return commands.find(command => command.id === queued.id);
  }, 'expired command lease reclaimed');
  assert.equal(reclaimed.id, queued.id);
});

test('session start maps command queue persistence failure to an explicit 503', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());

  h.failPersistence('app-commands.json', 'beforeWrite');
  const failed = await h.request('POST', '/api/sessions', {
    name: 'queue-persistence-failure',
    tool: 'codex',
    intentId: 'queue-persistence-failure-intent',
  });

  assert.equal(failed.status, 503);
  assert.equal(failed.body.error, 'state persistence failed');
  assert.match(failed.body.detail, /injected persistence failure/);
});

test('terminal PC bridge refusal is a clearable 409, while leased work remains pending', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());

  const pending = h.request('POST', '/api/sessions', {
    name: 'bridge-refusal',
    tool: 'codex',
    intentId: 'bridge-refusal-intent',
  });
  const leased = await waitFor(async () => {
    const commands = await leaseCommands(h);
    return commands.find(command => command.muxName === 'bridge-refusal');
  }, 'leased start command');

  let settled = false;
  pending.then(() => { settled = true; });
  await sleep(150);
  assert.equal(settled, false, 'leasing alone must not be mistaken for a terminal failure');

  await ackLeased(h, leased, { ok: false, detail: 'local owner still running' });
  const refused = await pending;
  assert.equal(refused.status, 409);
  assert.match(refused.body.error, /refused/i);
});

test('app command intents deduplicate, conflict on payload reuse, and require an exclusive lease token', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());

  const intentId = 'intent-command-dedup-1';
  const first = await h.json('POST', '/api/app-commands', {
    intentId,
    type: 'rename',
    sessionId: 'durable-command',
    title: 'Stable title',
  });
  const retry = await h.json('POST', '/api/app-commands', {
    intentId,
    type: 'rename',
    sessionId: 'durable-command',
    title: 'Stable title',
  });
  assert.equal(retry.id, first.id);

  const conflict = await h.request('POST', '/api/app-commands', {
    intentId,
    type: 'rename',
    sessionId: 'durable-command',
    title: 'Different title',
  });
  assert.equal(conflict.status, 409);

  const commandHeaders = { 'X-Mux-Command-Bridge': h.commandBridgeToken };
  const leased = await h.json('POST', '/api/app-commands/lease', { owner: 'consumer-a' }, commandHeaders);
  assert.equal(leased.length, 1);
  assert.equal(leased[0].id, first.id);
  assert.equal(leased[0].intentId, intentId);
  assert.match(leased[0].leaseToken, /^[A-Za-z0-9._-]+$/);

  const competing = await h.json('POST', '/api/app-commands/lease', { owner: 'consumer-b' }, commandHeaders);
  assert.deepEqual(competing, []);

  const wrongToken = await h.request('POST', `/api/app-commands/${first.id}/ack`, {
    leaseToken: 'wrong-token',
    ok: true,
  }, commandHeaders);
  assert.equal(wrongToken.status, 409);

  await h.json('POST', `/api/app-commands/${first.id}/ack`, {
    leaseToken: leased[0].leaseToken,
    ok: true,
  }, commandHeaders);
  await h.restart();

  const afterRestart = await h.json('POST', '/api/app-commands', {
    intentId,
    type: 'rename',
    sessionId: 'durable-command',
    title: 'Stable title',
  });
  assert.equal(afterRestart.id, first.id);
  assert.equal((await h.json('GET', `/api/app-commands/${first.id}`)).status, 'done');
});

test('pending commands are never age-pruned and expired leases are durably reclaimed', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());

  const queued = await h.json('POST', '/api/app-commands', {
    intentId: 'intent-old-pending-1',
    type: 'transcript',
    sessionId: 'old-but-live',
  });
  await h.stopProcess();

  const file = path.join(h.tmp, 'app-commands.json');
  const commands = JSON.parse(fs.readFileSync(file, 'utf8'));
  const record = commands.find(command => command.id === queued.id);
  record.ts = 1;
  fs.writeFileSync(file, JSON.stringify(commands));
  await h.start();

  const commandHeaders = { 'X-Mux-Command-Bridge': h.commandBridgeToken };
  const firstLease = await h.json('POST', '/api/app-commands/lease', {
    owner: 'consumer-a',
    leaseMs: 120,
  }, commandHeaders);
  assert.equal(firstLease.some(command => command.id === queued.id), true);

  const beforeExpiry = await h.json('POST', '/api/app-commands/lease', { owner: 'consumer-b' }, commandHeaders);
  assert.equal(beforeExpiry.some(command => command.id === queued.id), false);
  await sleep(180);

  const reclaimed = await h.json('POST', '/api/app-commands/lease', { owner: 'consumer-b' }, commandHeaders);
  const retry = reclaimed.find(command => command.id === queued.id);
  assert.ok(retry);
  assert.equal(retry.attempt, 2);
  assert.notEqual(retry.leaseToken, firstLease.find(command => command.id === queued.id).leaseToken);
});

test('upload acknowledgement removes staged bytes when metadata commit fails', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());

  h.failPersistence('uploads-meta.json', 'beforeWrite');
  const failed = await h.upload('/api/upload?name=evidence.txt&keep=1', Buffer.from('important'));
  assert.equal(failed.status, 503);
  assert.deepEqual(await h.json('GET', '/api/uploads'), []);
  assert.deepEqual(fs.readdirSync(path.join(h.tmp, 'uploads')), []);
});

function shellSession(name) {
  return { name, alive: true, created: 1000, lastOut: 1000, cols: 100, rows: 30, hasCommand: false, shellOnly: true, ready: true, kind: 'shell', sessionId: '', aliases: [] };
}

function commandSession(name, sessionId = '', created = 2000, aliases = []) {
  return { name, alive: true, created, lastOut: created, cols: 100, rows: 30, hasCommand: true, shellOnly: false, ready: true, kind: 'command', sessionId, aliases, tail: 'agent running' };
}

function dormantSession(name, sessionId = '', aliases = []) {
  return { name, alive: false, created: 1000, lastOut: 1000, cols: 100, rows: 30, hasCommand: true, shellOnly: false, ready: false, kind: 'dormant', sessionId, aliases };
}

test('projection protocol is versioned and rejects executable/path-bearing fields', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());

  const missing = await h.request('POST', '/api/projects', { decks: [], collections: [], allChats: [] });
  assert.equal(missing.status, 409);

  const wrong = await h.request('POST', '/api/projects', { schemaVersion: 2, decks: [], collections: [], allChats: [] });
  assert.equal(wrong.status, 409);

  for (const forbidden of ['muxCommand', 'command', 'cmd', 'cwd', 'pcPath', 'path', 'sourcePath', 'workspace', 'exe', 'arguments']) {
    const body = {
      schemaVersion: 3,
      decks: [],
      collections: [{ id: 'c', name: 'C', chats: [{ id: 's', tool: 'codex', muxName: 'm', [forbidden]: 'leak' }] }],
      allChats: [],
      runningSessions: [],
    };
    const res = await h.request('POST', '/api/projects', body);
    assert.equal(res.status, 400, `${forbidden} must be rejected`);
  }

  for (const body of [
    { collections: [{ id: 'c', chats: [{ id: 'C:\\secret', muxName: 'm' }] }] },
    { collections: [{ id: 'c', chats: [{ id: 's', aliases: ['../../secret'], muxName: 'm' }] }] },
    { runningSessions: [{ sessionId: 'C:\\secret' }] },
    { muxTabChats: { '../tab': { id: 's' } } },
    { muxTabMeta: { '../tab': { color: '#fff', kind: 'remote-resumed' } } },
  ]) {
    const res = await h.request('POST', '/api/projects', {
      schemaVersion: 3,
      decks: [],
      collections: [],
      allChats: [],
      runningSessions: [],
      ...body,
    });
    assert.equal(res.status, 400, `path-shaped identity must be rejected: ${JSON.stringify(body)}`);
  }

  const accepted = await h.request('POST', '/api/projects', {
    schemaVersion: 3,
    host: 'FAKEPC',
    decks: [],
    collections: [{ id: 'c', name: 'C', chats: [{ id: 's', aliases: ['a'], tool: 'codex', muxName: 'm', title: 'Chat', workspaceLabel: 'repo' }] }],
    allChats: [],
    runningSessions: [{ pid: 4, sessionId: 's', tool: 'codex', startedAt: '2026-07-09T00:00:00Z' }],
    runningVerified: true,
  });
  assert.equal(accepted.status, 200);
  const pulled = await h.json('GET', '/api/projects');
  assert.equal(pulled.schemaVersion, 3);
  const serialized = JSON.stringify(pulled);
  for (const forbidden of ['muxCommand', 'command', 'cmd', 'cwd', 'pcPath', 'path', 'sourcePath', 'workspace'])
    assert.equal(serialized.includes(`"${forbidden}"`), false, `${forbidden} leaked from persisted projection`);
  assert.equal(pulled.collections[0].chats[0].workspaceLabel, 'repo');
});

test('relay boot restores an invalid projection from backup and rewrites uploads through strict allowlists', async t => {
  const h = new RelayHarness();
  fs.writeFileSync(path.join(h.tmp, 'projects.json'), JSON.stringify({
    schemaVersion: 3,
    host: 'FAKEPC',
    decks: [],
    collections: [{ id: 'c', name: 'C', chats: [{ id: 'C:\\secret', muxName: 'm' }] }],
    allChats: [],
    runningSessions: [],
    runningVerified: true,
    syncedAt: Date.now(),
    appSyncedAt: Date.now(),
    runningSyncedAt: Date.now(),
  }));
  fs.writeFileSync(path.join(h.tmp, 'projects.json.bak'), JSON.stringify({
    schemaVersion: 3,
    host: 'FAKEPC',
    decks: [{ id: 'saved-deck', name: 'Saved deck' }],
    collections: [{
      id: 'saved-collection',
      name: 'Saved collection',
      chats: [{ id: 'saved-chat', muxName: 'saved-tab', title: 'Saved chat' }],
    }],
    allChats: [{ id: 'saved-chat', muxName: 'saved-tab', title: 'Saved chat' }],
    runningSessions: [],
    runningVerified: true,
    syncedAt: Date.now(),
    appSyncedAt: Date.now(),
    runningSyncedAt: Date.now(),
  }));
  fs.writeFileSync(path.join(h.tmp, 'uploads-meta.json'), JSON.stringify([{
    id: 'uold-1',
    name: 'input.png',
    session: 'tab-one',
    ts: Date.now(),
    size: 5,
    keep: true,
    pcPath: 'C:\\Users\\Ahmed\\secret\\input.png',
  }]));
  fs.mkdirSync(path.join(h.tmp, 'uploads', 'uold-1'), { recursive: true });
  fs.writeFileSync(path.join(h.tmp, 'uploads', 'uold-1', 'input.png'), 'image');
  await h.start();
  t.after(async () => h.stop());

  const projects = await h.json('GET', '/api/projects');
  assert.equal(projects.collections.length, 1);
  assert.equal(projects.collections[0].chats[0].id, 'saved-chat');
  assert.equal(projects.runningVerified, false);
  const projectsOnDisk = fs.readFileSync(path.join(h.tmp, 'projects.json'), 'utf8');
  assert.equal(projectsOnDisk.includes('C:\\\\secret'), false);
  assert.equal(projectsOnDisk.includes('saved-chat'), true);

  const uploadsOnDisk = fs.readFileSync(path.join(h.tmp, 'uploads-meta.json'), 'utf8');
  assert.equal(uploadsOnDisk.includes('pcPath'), false);
  assert.equal(uploadsOnDisk.includes('C:\\\\Users'), false);
});

test('relay boot migrates a legacy projection without dropping collections', async t => {
  const h = new RelayHarness();
  fs.writeFileSync(path.join(h.tmp, 'projects.json'), JSON.stringify({
    host: 'LEGACYPC',
    decks: [{ id: 'deck-1', name: 'Legacy deck' }],
    collections: [{
      id: 'collection-1',
      name: 'Legacy collection',
      chats: [{
        id: 'legacy-chat',
        title: 'Preserved chat',
        tool: 'codex',
        muxName: 'legacy-tab',
        workspaceLabel: 'repo',
        cwd: 'C:\\Users\\Ahmed\\private',
        command: 'codex resume legacy-chat',
      }],
    }],
    allChats: [{ id: 'legacy-chat', title: 'Preserved chat', tool: 'codex' }],
    runningSessions: [{ pid: 44, sessionId: 'legacy-chat', tool: 'codex' }],
    runningVerified: true,
    syncedAt: Date.now(),
  }));

  await h.start();
  t.after(async () => h.stop());

  const projects = await h.json('GET', '/api/projects');
  assert.equal(projects.schemaVersion, 3);
  assert.equal(projects.decks.length, 1);
  assert.equal(projects.collections.length, 1);
  assert.equal(projects.collections[0].chats[0].id, 'legacy-chat');
  assert.equal(projects.allChats.length, 1);
  assert.equal(projects.runningSessions.length, 1);
  assert.equal(projects.runningVerified, false);

  const persisted = fs.readFileSync(path.join(h.tmp, 'projects.json'), 'utf8');
  for (const forbidden of ['command', 'cmd', 'cwd', 'path', 'workspace'])
    assert.equal(persisted.includes(`"${forbidden}"`), false, `${forbidden} survived legacy migration`);
  assert.equal(persisted.includes('C:\\\\Users'), false);
});

test('relay boot does not overwrite an unrecognized projection without a valid backup', async t => {
  const h = new RelayHarness();
  t.after(async () => h.stop());
  const persisted = JSON.stringify({
    schemaVersion: 999,
    decks: [{ id: 'future-deck', name: 'Future deck' }],
    collections: [],
    allChats: [],
    runningSessions: [],
  });
  fs.writeFileSync(path.join(h.tmp, 'projects.json'), persisted);

  await assert.rejects(
    h.start(),
    /timed out waiting for relay start/,
  );
  assert.match(h.stderr, /invalid persisted state shape/);
  assert.equal(fs.readFileSync(path.join(h.tmp, 'projects.json'), 'utf8'), persisted);
});

test('relay boot reconciles upload tombstones and removes orphan upload directories', async t => {
  const h = new RelayHarness();
  const uploads = path.join(h.tmp, 'uploads');
  fs.mkdirSync(path.join(uploads, 'ukeep-1.deleting-old'), { recursive: true });
  fs.writeFileSync(path.join(uploads, 'ukeep-1.deleting-old', 'keep.txt'), 'preserved');
  fs.mkdirSync(path.join(uploads, 'uorphan-1'), { recursive: true });
  fs.writeFileSync(path.join(uploads, 'uorphan-1', 'orphan.txt'), 'orphan');
  fs.mkdirSync(path.join(uploads, 'udeleted-1.deleting-old'), { recursive: true });
  fs.writeFileSync(path.join(uploads, 'udeleted-1.deleting-old', 'deleted.txt'), 'deleted');
  fs.writeFileSync(path.join(h.tmp, 'uploads-meta.json'), JSON.stringify([{
    id: 'ukeep-1',
    name: 'keep.txt',
    session: '',
    ts: Date.now(),
    size: 9,
    keep: true,
    onPc: false,
  }]));

  await h.start();
  t.after(async () => h.stop());

  assert.equal(fs.existsSync(path.join(uploads, 'ukeep-1', 'keep.txt')), true);
  assert.equal(fs.existsSync(path.join(uploads, 'ukeep-1.deleting-old')), false);
  assert.equal(fs.existsSync(path.join(uploads, 'uorphan-1')), false);
  assert.equal(fs.existsSync(path.join(uploads, 'udeleted-1.deleting-old')), false);
  const listed = await h.json('GET', '/api/uploads');
  assert.deepEqual(listed.map(upload => upload.id), ['ukeep-1']);
});

test('relay boot quarantines missing upload bytes without blocking mux service startup', async t => {
  const h = new RelayHarness();
  fs.writeFileSync(path.join(h.tmp, 'uploads-meta.json'), JSON.stringify([{
    id: 'umissing-1',
    name: 'missing.txt',
    session: '',
    ts: Date.now(),
    size: 12,
    keep: true,
    onPc: false,
  }]));

  await h.start();
  t.after(async () => h.stop());

  assert.deepEqual(await h.json('GET', '/api/uploads'), []);
  const health = await h.json('GET', '/api/health');
  assert.equal(health.uploadRecoveryWarnings.length, 1);
  assert.match(health.uploadRecoveryWarnings[0], /umissing-1/);
  assert.deepEqual(JSON.parse(fs.readFileSync(path.join(h.tmp, 'uploads-meta.json'), 'utf8')), []);
});

test('real C# projection producer is accepted without command or path metadata', {
  skip: !process.env.MUX_PROJECTION_ARTIFACT,
}, async t => {
  const artifact = process.env.MUX_PROJECTION_ARTIFACT;
  assert.ok(fs.existsSync(artifact), `projection artifact missing: ${artifact}`);
  const projection = JSON.parse(fs.readFileSync(artifact, 'utf8'));
  const serialized = JSON.stringify(projection);
  for (const forbidden of ['muxCommand', 'command', 'cmd', 'cwd', 'pcPath', 'path', 'sourcePath', 'workspace'])
    assert.equal(serialized.includes(`"${forbidden}"`), false, `real producer leaked ${forbidden}`);
  assert.equal(serialized.includes('C:\\\\Users\\\\Ahmed'), false, 'real producer leaked a local absolute path');

  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const pushed = await h.request('POST', '/api/projects', projection);
  assert.equal(pushed.status, 200, pushed.text);
  const pulled = await h.json('GET', '/api/projects');
  assert.equal(pulled.schemaVersion, 3);
  assert.equal(pulled.collections[0].chats[0].id, 'contract-session');
  assert.equal(pulled.collections[0].chats[0].workspaceLabel, 'private-contract-workspace');
});

test('blank shell creation has no command field and waits for explicit muxd acknowledgement', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([]);
  t.after(() => host.close());

  const post = h.request('POST', '/api/sessions', { name: 'opaque-shell' });
  const create = await host.waitFor(m => m.t === 'create' && m.s === 'opaque-shell', 'opaque shell create');
  assert.ok(create.rid);
  assert.equal(Object.prototype.hasOwnProperty.call(create, 'cmd'), false);

  let settled = false;
  post.then(() => { settled = true; });
  await sleep(150);
  assert.equal(settled, false, 'relay inferred success before muxd acknowledged the create');

  const session = shellSession('opaque-shell');
  host.sendCreateResult(create, { session });
  const res = await post;
  assert.equal(res.status, 200);
  assert.equal(res.body.created, true);
});

test('muxd semantic create refusal preserves detail and maps to a clearable 409', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([]);
  t.after(() => host.close());

  const post = h.request('POST', '/api/sessions', {
    name: 'semantic-refusal',
    intentId: 'semantic-refusal-intent',
  });
  const create = await host.waitFor(
    message => message.t === 'create' && message.s === 'semantic-refusal',
    'semantic refusal create',
  );
  host.sendCreateResult(create, {
    ok: false,
    created: false,
    detail: 'session has a visible local owner',
    retryable: false,
  });

  const refused = await post;
  assert.equal(refused.status, 409);
  assert.match(refused.body.detail, /visible local owner/);
});

test('relaunch retries rebuild the same muxd frame when host size and heal state drift', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const initial = { ...dormantSession('stable-retry', 'stable-session'), cols: 100, rows: 30, heal: false };
  const host = await h.connectHost([initial]);
  t.after(() => host.close());
  await h.json('POST', '/api/projects', {
    schemaVersion: 3,
    decks: [],
    collections: [],
    allChats: [],
    runningSessions: [],
    runningVerified: true,
  });

  const body = { sessionId: 'stable-session', tool: 'codex', intentId: 'stable-retry-intent' };
  const firstPost = h.request('POST', '/api/sessions/stable-retry/relaunch', body);
  const firstFrame = await host.waitFor(
    message => message.t === 'create' && message.s === 'stable-retry',
    'first stable relaunch frame',
  );
  host.sendCreateResult(firstFrame, {
    ok: false,
    created: false,
    detail: 'outcome persistence uncertain',
    retryable: true,
  });
  assert.equal((await firstPost).status, 503);

  host.sendSessions([{ ...initial, cols: 220, rows: 55, heal: true }]);
  await sleep(100);
  host.messages = [];

  const retryPost = h.request('POST', '/api/sessions/stable-retry/relaunch', body);
  const retryFrame = await host.waitFor(
    message => message.t === 'create' && message.s === 'stable-retry',
    'retry stable relaunch frame',
  );
  assert.deepEqual(retryFrame, firstFrame);
  host.sendCreateResult(retryFrame, {
    session: { ...commandSession('stable-retry', 'stable-session'), cols: 220, rows: 55, heal: true },
  });
  assert.equal((await retryPost).status, 200);
});

test('direct host create rejects one in-flight intent reused for a different payload with 409', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([]);
  t.after(() => host.close());

  const intentId = 'direct-host-conflict-intent';
  const first = h.request('POST', '/api/sessions', { name: 'first-shell', intentId });
  const create = await host.waitFor(
    message => message.t === 'create' && message.s === 'first-shell',
    'first direct host create',
  );

  const conflict = await h.request('POST', '/api/sessions', {
    name: 'second-shell',
    intentId,
  });
  assert.equal(conflict.status, 409);
  assert.equal(conflict.body.error, 'intent id conflict');
  await host.assertNo(
    message => message.t === 'create' && message.s === 'second-shell',
    'conflicting host create must not be forwarded',
  );

  host.sendCreateResult(create, { session: shellSession('first-shell') });
  assert.equal((await first).status, 200);
});

test('hosted sessions link to projected chats by opaque session identity', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([{
    ...commandSession('renamed-tab', 'not-shared-with-relay', Date.now()),
    sessionId: 'canonical-id',
    aliases: ['child-id'],
  }]);
  t.after(() => host.close());

  await h.json('POST', '/api/projects', {
    schemaVersion: 3,
    decks: [],
    collections: [],
    allChats: [{ id: 'canonical-id', aliases: ['child-id'], tool: 'codex', title: 'Opaque Match', muxName: 'canonical-tab' }],
    runningSessions: [],
    host: 'FAKEPC',
  });

  const row = (await h.json('GET', '/api/sessions')).find(s => s.name === 'renamed-tab');
  assert.equal(row.sessionId, 'canonical-id');
  assert.equal(row.projectMuxName, 'canonical-tab');
  assert.equal(row.chatLinked, true);
  assert.equal(Object.prototype.hasOwnProperty.call(row, 'muxCommand'), false);
  assert.equal(Object.prototype.hasOwnProperty.call(row, 'cmdSig'), false);
});

test('hosted rename never reaches muxd when relay pin migration is not durable', async t => {
  const h = new RelayHarness();
  fs.writeFileSync(path.join(h.tmp, 'pins.json'), JSON.stringify([[
    'old-name',
    { deviceId: 'phone', label: 'phone', cols: 100, rows: 30, at: Date.now() },
  ]]));
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([commandSession('old-name', 'rename-session')]);
  t.after(() => host.close());
  h.failPersistence('pins.json', 'beforeWrite');

  const renamed = await h.request('PATCH', '/api/sessions/old-name', { name: 'new-name' });

  assert.equal(renamed.status, 503);
  assert.equal(host.messages.some(message => message.t === 'rename'), false);
  const persistedPins = new Map(JSON.parse(fs.readFileSync(path.join(h.tmp, 'pins.json'), 'utf8')));
  assert.equal(persistedPins.has('old-name'), true);
  assert.equal(persistedPins.has('new-name'), false);
});

test('hosted rename timeout keeps a durable intent until late host state settles it', async t => {
  const h = new RelayHarness();
  fs.writeFileSync(path.join(h.tmp, 'pins.json'), JSON.stringify([[
    'old-name',
    { deviceId: 'phone', label: 'phone', cols: 100, rows: 30, at: Date.now() },
  ]]));
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([commandSession('old-name', 'rename-session')]);
  t.after(() => host.close());

  const pending = h.request('PATCH', '/api/sessions/old-name', { name: 'new-name' });
  await host.waitFor(
    message => message.t === 'rename' && message.s === 'old-name' && message.to === 'new-name',
    'hosted rename request',
  );
  const timedOut = await pending;

  assert.equal(timedOut.status, 504);
  assert.equal(JSON.parse(fs.readFileSync(path.join(h.tmp, 'rename-intents.json'), 'utf8')).length, 1);
  host.sendSessions([commandSession('new-name', 'rename-session')]);
  await waitFor(
    () => JSON.parse(fs.readFileSync(path.join(h.tmp, 'rename-intents.json'), 'utf8')).length === 0,
    'late rename intent reconciliation',
  );
  const persistedPins = new Map(JSON.parse(fs.readFileSync(path.join(h.tmp, 'pins.json'), 'utf8')));
  assert.equal(persistedPins.has('old-name'), false);
  assert.equal(persistedPins.has('new-name'), true);
});

test('committed rename-intent retry failure keeps memory, disk, and write blocking consistent', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([commandSession('old-name', 'rename-session')]);
  t.after(() => host.close());

  const pending = h.request('PATCH', '/api/sessions/old-name', { name: 'new-name' });
  await host.waitFor(
    message => message.t === 'rename' && message.s === 'old-name' && message.to === 'new-name',
    'hosted rename request',
  );
  h.failPersistence('rename-intents.json', 'beforeDirectoryFsync', 4);
  host.sendSessions([commandSession('new-name', 'rename-session')]);

  const renamed = await pending;
  assert.equal(renamed.status, 200);
  assert.deepEqual(JSON.parse(fs.readFileSync(path.join(h.tmp, 'rename-intents.json'), 'utf8')), []);
  const health = await h.json('GET', '/api/health');
  assert.equal(health.pendingRenameIntents, 0);

  const projected = await h.request('POST', '/api/projects', {
    schemaVersion: 3,
    host: 'FAKEPC',
    decks: [],
    collections: [],
    allChats: [],
    runningSessions: [],
    runningVerified: true,
  });
  assert.equal(projected.status, 200, 'a proven committed rename must not globally block later writes');
});

test('muxd identity cannot be overwritten by stale tab projection', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([commandSession('identity-tab', 'actual-id', Date.now())]);
  t.after(() => host.close());

  await h.json('POST', '/api/projects', {
    schemaVersion: 3,
    decks: [],
    collections: [],
    allChats: [],
    runningSessions: [],
    runningVerified: true,
    muxTabChats: {
      'identity-tab': { id: 'stale-id', tool: 'codex', title: 'Stale projection', history: [] },
    },
  });

  const row = (await h.json('GET', '/api/sessions')).find(session => session.name === 'identity-tab');
  assert.equal(row.sessionId, 'actual-id');
  assert.equal(row.chatLinked, false);
  assert.equal(row.chatTitle, '');
});

test('startmux command queue accepts only opaque intent', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());

  const rejected = await h.request('POST', '/api/app-commands', {
    type: 'startmux',
    muxName: 'opaque-start',
    sessionId: 'sid',
    tool: 'codex',
    muxCommand: 'codex resume sid',
  });
  assert.equal(rejected.status, 400);

  await h.json('POST', '/api/projects', {
    schemaVersion: 3,
    decks: [],
    collections: [],
    allChats: [],
    runningSessions: [],
    runningVerified: true,
  });
  const queued = await h.request('POST', '/api/app-commands', {
    type: 'startmux',
    muxName: 'opaque-start',
    sessionId: 'sid',
    tool: 'codex',
  });
  assert.equal(queued.status, 200);
  const pending = await leaseCommands(h);
  const cmd = pending.find(c => c.id === queued.body.id);
  assert.deepEqual(
    { type: cmd.type, muxName: cmd.muxName, sessionId: cmd.sessionId, tool: cmd.tool },
    { type: 'startmux', muxName: 'opaque-start', sessionId: 'sid', tool: 'codex' },
  );
  for (const forbidden of ['muxCommand', 'command', 'cwd', 'pcPath'])
    assert.equal(Object.prototype.hasOwnProperty.call(cmd, forbidden), false);
});

test('mirrorlocal queues only an exact verified running pid/session/tool tuple', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  await h.json('POST', '/api/projects', {
    schemaVersion: 3,
    decks: [],
    collections: [],
    allChats: [{ id: 'mirror-session', tool: 'codex', title: 'Mirror me', muxName: 'mirror-tab' }],
    runningSessions: [{ sessionId: 'mirror-session', pid: 4242, tool: 'codex' }],
    runningVerified: true,
  });

  const wrong = await h.request('POST', '/api/app-commands', {
    type: 'mirrorlocal',
    muxName: 'mirror-tab',
    sessionId: 'different-session',
    pid: 4242,
    tool: 'codex',
  });
  assert.equal(wrong.status, 409);
  assert.match(wrong.body.detail, /does not own session/);

  const queued = await h.request('POST', '/api/app-commands', {
    type: 'mirrorlocal',
    muxName: 'mirror-tab',
    sessionId: 'mirror-session',
    pid: 4242,
    tool: 'codex',
  });
  assert.equal(queued.status, 200);
  const command = (await leaseCommands(h)).find(item => item.id === queued.body.id);
  assert.deepEqual(
    {
      type: command.type,
      replayPolicy: command.replayPolicy,
      muxName: command.muxName,
      sessionId: command.sessionId,
      pid: command.pid,
      tool: command.tool,
    },
    {
      type: 'mirrorlocal',
      replayPolicy: 'intent-fenced',
      muxName: 'mirror-tab',
      sessionId: 'mirror-session',
      pid: 4242,
      tool: 'codex',
    },
  );
});

test('adopted host metadata reaches the session list without executable data', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([{
    ...commandSession('adopted-tab', 'adopted-session'),
    kind: 'adopted-local',
    adopted: true,
    externalOwner: true,
    childPid: 5151,
    heal: false,
  }]);
  t.after(() => host.close());

  const row = (await h.json('GET', '/api/sessions')).find(item => item.name === 'adopted-tab');
  assert.equal(row.adopted, true);
  assert.equal(row.externalOwner, true);
  assert.equal(row.kind, 'adopted-local');
  assert.equal(row.autoheal, false);
  assert.equal(Object.prototype.hasOwnProperty.call(row, 'cmd'), false);
});

test('Gateway resume mode survives relay restart without changing default resume semantics', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  await h.json('POST', '/api/projects', { schemaVersion: 3, decks: [], collections: [], allChats: [], runningSessions: [], runningVerified: true });
  const gateway = await h.json('POST', '/api/app-commands', { type: 'startmux', sessionId: 'claude-chat', tool: 'claude', muxName: 'gateway-chat', launchMode: 'gateway' });
  const ordinary = await h.json('POST', '/api/app-commands', { type: 'startmux', sessionId: 'other-chat', tool: 'claude', muxName: 'ordinary-chat' });
  const refused = await h.request('POST', '/api/app-commands', { type: 'startmux', sessionId: 'codex-chat', tool: 'codex', muxName: 'wrong-tool', launchMode: 'gateway' });
  assert.equal(refused.status, 400);
  await h.restart();
  const commands = await leaseCommands(h);
  assert.equal(commands.find(c => c.id === gateway.id).launchMode, 'gateway');
  assert.equal(commands.find(c => c.id === ordinary.id).launchMode, '');
});

test('deck order command preserves exact IDs through restart and rejects malformed orders', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  for (const deckIds of [null, ['main', 'main'], ['main', 7]]) {
    const refused = await h.request('POST', '/api/app-commands', { type: 'deckreorder', expectedRevision: 'revision-one', deckIds });
    assert.equal(refused.status, 400);
  }
  const body = { type: 'deckreorder', intentId: 'deck-order-test', expectedRevision: 'revision-one', deckIds: ['deck-b', 'main', 'deck-a'] };
  const queued = await h.json('POST', '/api/app-commands', body);
  await h.restart();
  const command = (await leaseCommands(h)).find(c => c.id === queued.id);
  assert.ok(command);
  assert.deepEqual(command.deckIds, body.deckIds);
  assert.equal(command.expectedRevision, body.expectedRevision);
  await ackLeased(h, command, { ok: true, detail: 'decks' });
  const replay = await h.json('POST', '/api/app-commands', body);
  assert.equal(replay.id, queued.id);
  assert.equal(replay.deduplicated, true);
});

test('app command acknowledgements never return or persist a local PC path', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());

  const escaped = await h.request('POST', '/api/app-commands', {
    type: 'fetchfile',
    uploadId: '../../etc/passwd',
    muxName: 'tab-one',
    insert: 'path',
  });
  assert.equal(escaped.status, 400);

  const uploaded = await h.upload('/api/upload?name=input.png&session=tab-one', Buffer.from('image'));
  assert.equal(uploaded.status, 200);
  const queued = await h.json('POST', '/api/app-commands', {
    type: 'fetchfile',
    uploadId: uploaded.body.uploadId,
    filename: '../../not-the-relay-owned-name.png',
    sessionId: 'chat-one', generationId: 'generation-one',
    muxName: 'tab-one',
    insert: 'path',
  });
  await h.restart();
  const queuedCommand = (await leaseCommands(h)).find(c => c.id === queued.id);
  assert.equal(queuedCommand.filename, 'input.png');
  assert.equal(queuedCommand.sessionId, 'chat-one');
  assert.equal(queuedCommand.generationId, 'generation-one');
  const localPath = 'C:\\Users\\Ahmed\\AppData\\Local\\secret\\input.png';
  await ackLeased(h, queuedCommand, {
    ok: true,
    detail: localPath,
    onPc: true,
  });

  const result = await h.json('GET', `/api/app-commands/${encodeURIComponent(queued.id)}`);
  assert.equal(result.status, 'done');
  assert.equal(result.detail.includes(localPath), false);
  assert.equal(JSON.stringify(result).includes('C:\\\\Users'), false);
  const pending = await leaseCommands(h);
  assert.equal(pending.length, 0);

  const kill = await h.json('POST', '/api/app-commands', { type: 'kill', sessionId: 'opaque-id' });
  const leasedKill = (await leaseCommands(h)).find(command => command.id === kill.id);
  await ackLeased(h, leasedKill, {
    ok: false,
    detail: `failed in ${localPath}: codex resume opaque-id`,
  });
  const failed = await h.json('GET', `/api/app-commands/${encodeURIComponent(kill.id)}`);
  assert.equal(failed.status, 'failed');
  assert.equal(failed.detail, 'PC bridge could not stop session');
  const commandsOnDisk = fs.readFileSync(path.join(h.tmp, 'app-commands.json'), 'utf8');
  assert.equal(commandsOnDisk.includes(localPath), false);
  assert.equal(commandsOnDisk.includes('codex resume'), false);
});

test('host protocol rejects forbidden fields outside session payloads', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([]);

  const closed = once(host.ws, 'close');
  host.ws.send(JSON.stringify({ t: 'sessions', command: 'codex resume secret', list: [] }));
  await closed;
  await waitFor(async () => {
    const health = await h.json('GET', '/api/health');
    return health.host && health.host.connected === false ? health : null;
  }, 'forbidden host frame disconnect');
});

test('auth decision cache is LRU-bounded', async t => {
  const seen = [];
  const auth = http.createServer((req, res) => {
    seen.push(req.headers['x-session-token']);
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify({ authenticated: true, user: { isOwner: true } }));
  });
  await new Promise(resolve => auth.listen(0, '127.0.0.1', resolve));
  t.after(() => auth.close());

  const h = new RelayHarness({
    HLAUTH_BASE: `http://127.0.0.1:${auth.address().port}`,
    MUX_AUTH_CACHE_MAX: '2',
    MUX_AUTH_CACHE_TTL_MS: '5000',
  });
  await h.start();
  t.after(async () => h.stop());
  const headers = token => ({
    'x-forwarded-for': '203.0.113.10',
    cookie: `hl_session=${token}`,
  });

  for (const token of ['a', 'b', 'a', 'c', 'b']) {
    assert.equal((await h.request('GET', '/api/health', undefined, headers(token))).status, 200);
  }
  assert.deepEqual(seen, ['a', 'b', 'c', 'b']);
});

test('auth decision cache expires entries', async t => {
  const seen = [];
  const auth = http.createServer((req, res) => {
    seen.push(req.headers['x-session-token']);
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify({ authenticated: true, user: { isOwner: true } }));
  });
  await new Promise(resolve => auth.listen(0, '127.0.0.1', resolve));
  t.after(() => auth.close());

  const h = new RelayHarness({
    HLAUTH_BASE: `http://127.0.0.1:${auth.address().port}`,
    MUX_AUTH_CACHE_TTL_MS: '1000',
  });
  await h.start();
  t.after(async () => h.stop());
  const headers = {
    'x-forwarded-for': '203.0.113.10',
    cookie: 'hl_session=expiry',
  };

  assert.equal((await h.request('GET', '/api/health', undefined, headers)).status, 200);
  assert.equal((await h.request('GET', '/api/health', undefined, headers)).status, 200);
  await sleep(1200);
  assert.equal((await h.request('GET', '/api/health', undefined, headers)).status, 200);
  assert.deepEqual(seen, ['expiry', 'expiry']);
});

test('malformed host candidates cannot replace an already validated host', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const healthy = await h.connectHost([shellSession('healthy-tab')]);
  t.after(() => healthy.close());

  for (const frame of [
    { t: 'sessions', list: [] },
    { t: 'hello', host: 'OLDPC', protocol: 3, caps: HOST_CAPS, sessions: [] },
    { t: 'hello', host: 'FUTUREPC', protocol: 5, caps: HOST_CAPS, sessions: [] },
    { t: 'hello', host: 'FRACTIONALPC', protocol: 4.1, caps: HOST_CAPS, sessions: [] },
    { t: 'hello', host: 'BADPC', protocol: 4, caps: HOST_CAPS, sessions: [shellSession('../bad-tab')] },
  ]) {
    const candidate = new FakeHost(h.port);
    await candidate.connect();
    const closed = once(candidate.ws, 'close');
    candidate.ws.send(JSON.stringify(frame));
    await closed;

    const health = await h.json('GET', '/api/health');
    assert.equal(health.host.connected, true);
    assert.equal(health.host.name, 'FAKEPC');
    assert.equal(health.host.protocol, 4);
    const rows = await h.json('GET', '/api/sessions');
    assert.ok(rows.some(row => row.name === 'healthy-tab'));
  }
});

test('GET /api/sessions reports trustable attention states from host facts', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const now = Date.now();
  const host = await h.connectHost([
    { ...commandSession('activecase', 'active-id', now - 1000), lastOut: now - 1000, tail: 'garbled tail without magic UI words' },
    { ...commandSession('quietcase', 'quiet-id', now - 120000), lastOut: now - 120000, tail: 'Use /skills to list available skills' },
    { ...commandSession('stoppedcase', 'stopped-id', now - 120000), lastOut: now - 120000, tail: 'PS C:\\Users\\Ahmed>' },
    shellSession('shellcase'),
  ]);
  t.after(() => host.close());

  const rows = await h.json('GET', '/api/sessions');
  const byName = Object.fromEntries(rows.map(r => [r.name, r]));
  assert.equal(byName.activecase.state, 'green');
  assert.equal(byName.activecase.agentState, 'working');
  assert.equal(byName.activecase.needsAttention, false);
  assert.equal(byName.quietcase.state, 'yellow');
  assert.equal(byName.quietcase.agentState, 'attention');
  assert.equal(byName.quietcase.needsAttention, true);
  assert.equal(byName.stoppedcase.state, 'red');
  assert.equal(byName.stoppedcase.agentState, 'stopped');
  assert.equal(byName.stoppedcase.needsAttention, true);
  assert.equal(byName.shellcase.state, 'white');
  assert.equal(byName.shellcase.agentState, 'neutral');
  assert.equal(byName.shellcase.needsAttention, false);
});

test('POST /api/sessions starts a fresh CLI through opaque PC intent and waits until muxd reports it', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([shellSession('shellcase')]);
  t.after(() => host.close());

  const post = h.request('POST', '/api/sessions', { name: 'shellcase', tool: 'codex' });
  const queued = await waitFor(async () => {
    const commands = await leaseCommands(h);
    return commands.find(c => c.type === 'startmux' && c.muxName === 'shellcase');
  }, 'opaque startmux command');
  assert.deepEqual(
    { sessionId: queued.sessionId, tool: queued.tool, muxName: queued.muxName, replayPolicy: queued.replayPolicy },
    { sessionId: '', tool: 'codex', muxName: 'shellcase', replayPolicy: 'intent-fenced' },
  );
  assert.equal(Object.prototype.hasOwnProperty.call(queued, 'command'), false);

  let settled = false;
  post.then(() => { settled = true; });
  await sleep(150);
  assert.equal(settled, false, 'POST returned before the PC bridge and muxd confirmed the session');

  await ackLeased(h, queued, { ok: true, detail: 'started' });
  host.sendSessions([commandSession('shellcase', '', 3000)]);
  const res = await post;
  assert.equal(res.status, 200);
  assert.equal(res.body.created, true);

  const sessions = await h.json('GET', '/api/sessions');
  const row = sessions.find(s => s.name === 'shellcase');
  assert.equal(row.hasCommand, true);
  assert.equal(row.shellOnly, false);
  assert.equal(Object.prototype.hasOwnProperty.call(row, 'cmdSig'), false);
});

test('POST /api/sessions reuses a live blank shell without sending a create', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([shellSession('samecase')]);
  t.after(() => host.close());

  const res = await h.request('POST', '/api/sessions', { name: 'samecase' });

  assert.equal(res.status, 200);
  assert.equal(res.body.created, false);
  await host.assertNo(m => m.t === 'create' && m.s === 'samecase', 'live blank shell should be reused');
});

test('concurrent fresh CLI starts remain opaque and converge on one reported mux session', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([]);
  t.after(() => host.close());

  const request = { name: 'racecase', tool: 'codex', intentId: 'racecase-start-intent' };
  const first = h.request('POST', '/api/sessions', request);
  const second = h.request('POST', '/api/sessions', request);
  const queued = await waitFor(async () => {
    const commands = await leaseCommands(h);
    return commands.find(c => c.type === 'startmux' && c.muxName === 'racecase');
  }, 'deduplicated opaque start intent');
  assert.equal(Object.prototype.hasOwnProperty.call(queued, 'command'), false);
  await ackLeased(h, queued, { ok: true, detail: 'started' });
  host.sendSessions([commandSession('racecase', '', 3000)]);

  const results = await Promise.all([first, second]);
  assert.equal(results.every(r => r.status === 200), true);
  const rows = (await h.json('GET', '/api/sessions')).filter(s => s.name === 'racecase');
  assert.equal(rows.length, 1);
});

test('dormant hosted session refuses websocket attach and does not create a shell', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([dormantSession('dormantcase', 'dormant-id')]);
  t.after(() => host.close());

  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=dormantcase&cols=80&rows=24`);
  const close = await once(ws, 'close');

  assert.equal(close[0], 1013);
  assert.match(String(close[1]), /Dormant mux session/);
  await host.assertNo(m => m.t === 'create' && m.s === 'dormantcase', 'dormant attach should not create');
});

test('POST /api/sessions/:name/relaunch reuses muxd saved command for dormant session', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([dormantSession('savedcase', 'saved-id')]);
  t.after(() => host.close());
  await h.json('POST', '/api/projects', {
    schemaVersion: 3,
    decks: [],
    collections: [],
    allChats: [],
    runningSessions: [],
    runningVerified: true,
  });

  const post = h.request('POST', '/api/sessions/savedcase/relaunch', {});
  const create = await host.waitFor(m => m.t === 'create' && m.s === 'savedcase', 'relaunch savedcase');
  assert.equal(Object.prototype.hasOwnProperty.call(create, 'cmd'), false);
  assert.equal(create.relaunch, true);

  let settled = false;
  post.then(() => { settled = true; });
  await sleep(150);
  assert.equal(settled, false, 'relaunch returned before muxd confirmed the restarted session');

  host.sendCreateResult(create, { session: commandSession('savedcase', 'saved-id', 9000) });
  const res = await post;
  assert.equal(res.status, 200);
  assert.equal(res.body.relaunched, true);
  assert.equal(res.body.created, true);
});

test('POST /api/sessions/:name/relaunch refuses sessions with no saved command', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([shellSession('plaincase')]);
  t.after(() => host.close());

  const res = await h.request('POST', '/api/sessions/plaincase/relaunch', {});

  assert.equal(res.status, 400);
  assert.match(res.body.error, /session identity required/);
  await host.assertNo(m => m.t === 'create' && m.s === 'plaincase', 'plain shell should not relaunch without a command');
});

test('POST /api/sessions/:name/relaunch transfers a matching local copy through the PC bridge', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([dormantSession('takeover', 'sid1')]);
  t.after(() => host.close());
  await h.json('POST', '/api/projects', {
    schemaVersion: 3,
    decks: [],
    collections: [{ id: 'c1', name: 'Work', chats: [{ id: 'sid1', tool: 'codex', title: 'Takeover', muxName: 'takeover' }] }],
    runningSessions: [{ sessionId: 'sid1', pid: 1234 }],
    runningVerified: true,
    host: 'FAKEPC',
  });

  const post = h.request('POST', '/api/sessions/takeover/relaunch', { sessionId: 'sid1', tool: 'codex' });
  const queued = await waitFor(async () => {
    const cmds = await leaseCommands(h);
    return cmds.find(c => c.type === 'startmux' && c.sessionId === 'sid1');
  }, 'takeover startmux command');
  assert.equal(queued.takeover, true);
  await host.assertNo(m => m.t === 'create' && m.s === 'takeover', 'local-owner relaunch should not create');
  await ackLeased(h, queued, { ok: true, detail: 'ownership transferred' });
  host.sendSessions([commandSession('takeover', 'sid1', 1235)]);
  const res = await post;
  assert.equal(res.status, 200);
  assert.equal(res.body.stoppedLocal, true);
});

test('POST /api/sessions/:name/relaunch transfers a local copy matched through allChats', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([dormantSession('allchat-takeover', 'sid-all')]);
  t.after(() => host.close());
  await h.json('POST', '/api/projects', {
    schemaVersion: 3,
    decks: [],
    collections: [],
    allChats: [{ id: 'sid-all', tool: 'codex', title: 'All Chat Takeover', muxName: 'allchat-takeover' }],
    runningSessions: [{ sessionId: 'sid-all', pid: 4321 }],
    runningVerified: true,
    host: 'FAKEPC',
  });

  const post = h.request('POST', '/api/sessions/allchat-takeover/relaunch', { sessionId: 'sid-all', tool: 'codex' });
  const queued = await waitFor(async () => {
    const cmds = await leaseCommands(h);
    return cmds.find(c => c.type === 'startmux' && c.sessionId === 'sid-all');
  }, 'allChats takeover command');
  assert.equal(queued.takeover, true);
  await host.assertNo(m => m.t === 'create' && m.s === 'allchat-takeover', 'allChats local-owner relaunch should not create');
  await ackLeased(h, queued, { ok: true, detail: 'ownership transferred' });
  host.sendSessions([commandSession('allchat-takeover', 'sid-all', 4322)]);
  const res = await post;
  assert.equal(res.status, 200);
});

test('POST /api/sessions refuses generic create when the chat is already running locally', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([]);
  t.after(() => host.close());
  await h.json('POST', '/api/projects', {
    schemaVersion: 3,
    decks: [],
    collections: [{ id: 'c1', name: 'Work', chats: [{ id: 'sid-create', tool: 'codex', title: 'Create Takeover', muxName: 'create-takeover' }] }],
    runningSessions: [{ sessionId: 'sid-create', pid: 2201 }],
    runningVerified: true,
    host: 'FAKEPC',
  });

  const res = await h.request('POST', '/api/sessions', { name: 'create-takeover', tool: 'codex' });

  assert.equal(res.status, 409);
  assert.match(res.body.detail, /already running locally/);
  await host.assertNo(m => m.t === 'create' && m.s === 'create-takeover', 'generic create should not resume over local owner');
});

test('POST /api/sessions refuses resume when running-state projection is unverified', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([]);
  t.after(() => host.close());
  await h.json('POST', '/api/projects', {
    schemaVersion: 3,
    decks: [],
    collections: [{ id: 'c1', name: 'Work', chats: [{ id: 'sid-unverified', tool: 'codex', title: 'Unverified', muxName: 'unverified-takeover' }] }],
    runningSessions: [],
    runningVerified: false,
    runningVerificationDetail: 'WMI unavailable',
    host: 'FAKEPC',
  });

  const res = await h.request('POST', '/api/sessions', { name: 'unverified-takeover', tool: 'codex' });

  assert.equal(res.status, 409);
  assert.match(res.body.detail, /could not verify local running sessions/);
  assert.match(res.body.detail, /WMI unavailable/);
  await host.assertNo(m => m.t === 'create' && m.s === 'unverified-takeover', 'unverified running-state should fail closed');
});

test('relaunching a different opaque identity waits for the PC bridge and matching muxd projection', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([dormantSession('forward-ids', 'old-id')]);
  t.after(() => host.close());
  await h.json('POST', '/api/projects', {
    schemaVersion: 3,
    decks: [],
    collections: [{ id: 'c1', name: 'Work', chats: [{ id: 'parent-forward', aliases: ['child-forward'], tool: 'codex', title: 'Forward', muxName: 'forward-ids' }] }],
    runningSessions: [],
    runningVerified: true,
    host: 'FAKEPC',
  });

  const post = h.request('POST', '/api/sessions/forward-ids/relaunch', { sessionId: 'parent-forward', tool: 'codex' });
  const queued = await waitFor(async () => {
    const commands = await leaseCommands(h);
    return commands.find(c => c.type === 'startmux' && c.muxName === 'forward-ids');
  }, 'opaque relaunch command');
  assert.deepEqual(
    { sessionId: queued.sessionId, tool: queued.tool },
    { sessionId: 'parent-forward', tool: 'codex' },
  );
  assert.equal(Object.prototype.hasOwnProperty.call(queued, 'command'), false);
  await ackLeased(h, queued, { ok: true, detail: 'started' });
  host.sendSessions([commandSession('forward-ids', 'parent-forward', 8800, ['child-forward'])]);
  const res = await post;

  assert.equal(res.status, 200);
});

test('POST /api/sessions/:name/relaunch transfers a local copy matched by alias', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([dormantSession('alias-takeover', 'parent-id', ['child-id'])]);
  t.after(() => host.close());
  await h.json('POST', '/api/projects', {
    schemaVersion: 3,
    decks: [],
    collections: [{ id: 'c1', name: 'Work', chats: [{ id: 'parent-id', aliases: ['child-id'], tool: 'codex', title: 'Alias Takeover', muxName: 'alias-takeover' }] }],
    runningSessions: [{ sessionId: 'child-id', pid: 2202 }],
    runningVerified: true,
    host: 'FAKEPC',
  });

  const post = h.request('POST', '/api/sessions/alias-takeover/relaunch', { sessionId: 'parent-id', tool: 'codex' });
  const queued = await waitFor(async () => {
    const cmds = await leaseCommands(h);
    return cmds.find(c => c.type === 'startmux' && c.sessionId === 'parent-id');
  }, 'alias takeover command');
  assert.equal(queued.takeover, true);
  await host.assertNo(m => m.t === 'create' && m.s === 'alias-takeover', 'alias local-owner relaunch should not create');
  await ackLeased(h, queued, { ok: true, detail: 'ownership transferred' });
  host.sendSessions([commandSession('alias-takeover', 'parent-id', 2203, ['child-id'])]);
  const res = await post;
  assert.equal(res.status, 200);
});

test('relay source has no boot command reconstruction or terminal-injection healer', () => {
  const source = fs.readFileSync(path.join(REPO, 'server.js'), 'utf8');
  for (const forbidden of ['muxCommandFor', 'bootRecreate', 'AUTOHEAL_FILE', '/__test/autoheal-tick', '/__test/boot-recreate'])
    assert.equal(source.includes(forbidden), false, `${forbidden} must not exist in relay ownership code`);
});

test('startmux app command refuses when local copy is already running', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  await h.json('POST', '/api/projects', {
    schemaVersion: 3,
    decks: [],
    collections: [{ id: 'c1', name: 'Work', chats: [{ id: 'sid-command', tool: 'codex', title: 'Command Takeover', muxName: 'command-takeover' }] }],
    runningSessions: [{ sessionId: 'sid-command', pid: 2205 }],
    runningVerified: true,
    host: 'FAKEPC',
  });

  const res = await h.request('POST', '/api/app-commands', { type: 'startmux', muxName: 'command-takeover', sessionId: 'sid-command', tool: 'codex' });

  assert.equal(res.status, 409);
  assert.match(res.body.detail, /already running locally/);
  const pending = await leaseCommands(h);
  assert.equal(pending.filter(c => c.type === 'startmux').length, 0);
});

test('autoheal toggle delegates only boolean policy to muxd and waits for host confirmation', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([commandSession('heal-takeover', 'sid-heal', Date.now())]);
  t.after(() => host.close());
  const request = h.request('POST', '/api/sessions/heal-takeover/autoheal', { on: true });
  const heal = await host.waitFor(m => m.t === 'heal' && m.s === 'heal-takeover', 'heal policy update');
  assert.deepEqual(heal, { t: 'heal', s: 'heal-takeover', on: true });
  host.sendSessions([{ ...commandSession('heal-takeover', 'sid-heal', Date.now()), heal: true }]);
  const response = await request;
  assert.equal(response.status, 200);
  await host.assertNo(m => m.t === 'i' && m.s === 'heal-takeover', 'relay must not inject recovery input');
});

test('unknown websocket tab fails closed and never creates an implicit shell', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([]);
  t.after(() => host.close());

  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=webshell&cols=88&rows=22`);
  await once(ws, 'open');
  const [code, reason] = await once(ws, 'close');
  assert.equal(code, 1013);
  assert.match(reason.toString(), /does not exist/i);
  await host.assertNo(m => m.t === 'create' && m.s === 'webshell', 'unknown attach must not create');
});

test('concurrent viewers share one scrollback snapshot and each waiter receives it', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([commandSession('shared-sb', 'shared-id', Date.now())]);
  t.after(() => host.close());

  const first = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=shared-sb&cols=80&rows=24`);
  const second = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=shared-sb&cols=100&rows=30`);
  await Promise.all([once(first, 'open'), once(second, 'open')]);
  await host.waitFor(m => m.t === 'sb' && m.s === 'shared-sb', 'shared scrollback request');
  await sleep(100);
  assert.equal(host.messages.filter(m => m.t === 'sb' && m.s === 'shared-sb').length, 1);

  const firstPaint = waitForWsText(first, /SHARED_SNAPSHOT/, 'first waiter replay');
  const secondPaint = waitForWsText(second, /SHARED_SNAPSHOT/, 'second waiter replay');
  host.sendScrollback('shared-sb', 'SHARED_SNAPSHOT\r\n');
  assert.match(await firstPaint, /SHARED_SNAPSHOT/);
  assert.match(await secondPaint, /SHARED_SNAPSHOT/);
  first.close();
  second.close();
});

test('last viewer removal deletes session state so reconnect recomputes and re-requests', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([commandSession('state-cleanup', 'cleanup-id', Date.now())]);
  t.after(() => host.close());

  const first = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=state-cleanup&cols=81&rows=25`);
  await once(first, 'open');
  await host.waitFor(m => m.t === 'sb' && m.s === 'state-cleanup', 'first scrollback request');
  host.sendScrollback('state-cleanup', 'FIRST\r\n');
  await host.waitFor(m => m.t === 'resize' && m.s === 'state-cleanup' && m.cols === 81, 'first resize');
  const firstClosed = once(first, 'close');
  first.close();
  await firstClosed;

  const second = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=state-cleanup&cols=81&rows=25`);
  await once(second, 'open');
  await waitFor(
    () => host.messages.filter(m => m.t === 'sb' && m.s === 'state-cleanup').length === 2,
    'second scrollback request',
  );
  await waitFor(
    () => host.messages.filter(m => m.t === 'resize' && m.s === 'state-cleanup' && m.cols === 81).length === 2,
    'second resize after state cleanup',
  );
  second.close();
});

test('viewer dimensions are clamped before they can resize the shared PTY', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const headless = { ...commandSession('bounded-size', 'bounded-id'), cols: 0, rows: 0 };
  const host = await h.connectHost([headless]);
  t.after(() => host.close());

  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=bounded-size&cols=100000&rows=90000`);
  await once(ws, 'open');
  const initial = await host.waitFor(
    m => m.t === 'resize' && m.s === 'bounded-size',
    'bounded initial resize',
  );
  assert.deepEqual({ cols: initial.cols, rows: initial.rows }, { cols: 1000, rows: 300 });

  ws.send('v' + JSON.stringify({ cols: 999999, rows: 888888 }));
  await sleep(100);
  const resizes = host.messages.filter(m => m.t === 'resize' && m.s === 'bounded-size');
  assert.equal(resizes.every(m => m.cols <= 1000 && m.rows <= 300), true);
  ws.close();
});

test('an explicit device pin overrides the attached local terminal for every viewer', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const hosted = {
    ...commandSession('shared-pin', 'shared-pin-id'),
    cols: 160,
    rows: 44,
    localViewers: 1,
  };
  const host = await h.connectHost([hosted]);
  t.after(() => host.close());

  const narrow = new WebSocket(
    `ws://127.0.0.1:${h.port}/ws?session=shared-pin&cols=72&rows=28&dev=phone&label=Phone`,
  );
  const wide = new WebSocket(
    `ws://127.0.0.1:${h.port}/ws?session=shared-pin&cols=132&rows=40&dev=desktop&label=Desktop`,
  );
  await Promise.all([once(narrow, 'open'), once(wide, 'open')]);

  const pinnedForNarrow = waitForWsFrame(
    narrow,
    text => {
      if (text[0] !== 'd') return false;
      const frame = JSON.parse(text.slice(1));
      return frame.mode === 'pinned' && frame.local === false && frame.mine === true && frame.cols === 72 && frame.rows === 28;
    },
    'narrow viewer pinned size frame',
  );
  const pinnedForWide = waitForWsFrame(
    wide,
    text => {
      if (text[0] !== 'd') return false;
      const frame = JSON.parse(text.slice(1));
      return frame.mode === 'pinned' && frame.mine === false && frame.cols === 72 && frame.rows === 28;
    },
    'wide viewer mirrored pinned size frame',
  );
  narrow.send('P1');

  const resize = await host.waitFor(
    m => m.t === 'resize' && m.s === 'shared-pin' && m.cols === 72 && m.rows === 28,
    'pinned host resize',
  );
  assert.deepEqual(resize, { t: 'resize', s: 'shared-pin', cols: 72, rows: 28 });
  const narrowPinnedFrame = JSON.parse((await pinnedForNarrow).slice(1));
  const widePinnedFrame = JSON.parse((await pinnedForWide).slice(1));
  assert.match(narrowPinnedFrame.modeLabel, /this device/i);
  assert.match(widePinnedFrame.modeLabel, /Phone/);

  const autoForNarrow = waitForWsFrame(
    narrow,
    text => {
      if (text[0] !== 'd') return false;
      const frame = JSON.parse(text.slice(1));
      return frame.mode === 'auto' && frame.local === true && frame.cols === 72 && frame.rows === 28;
    },
    'narrow viewer auto size frame',
  );
  const autoForWide = waitForWsFrame(
    wide,
    text => {
      if (text[0] !== 'd') return false;
      const frame = JSON.parse(text.slice(1));
      return frame.mode === 'auto' && frame.local === true && frame.cols === 72 && frame.rows === 28;
    },
    'wide viewer auto size frame',
  );
  narrow.send('P0');
  assert.match(JSON.parse((await autoForNarrow).slice(1)).modeLabel, /^auto /);
  assert.match(JSON.parse((await autoForWide).slice(1)).modeLabel, /^auto /);

  narrow.close();
  wide.close();
});

test('headless auto sizing uses the per-axis minimum that fits every active viewer', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const headless = { ...commandSession('narrow-auto', 'narrow-auto-id'), cols: 0, rows: 0 };
  const host = await h.connectHost([headless]);
  t.after(() => host.close());

  const narrow = new WebSocket(
    `ws://127.0.0.1:${h.port}/ws?session=narrow-auto&cols=74&rows=60&dev=phone&label=Phone`,
  );
  const narrowAuto = waitForWsFrame(
    narrow,
    text => {
      if (text[0] !== 'd') return false;
      const frame = JSON.parse(text.slice(1));
      return frame.mode === 'auto' && frame.local === false && frame.cols === 74 && frame.rows === 60;
    },
    'narrow viewer auto frame',
  );
  await once(narrow, 'open');

  const wide = new WebSocket(
    `ws://127.0.0.1:${h.port}/ws?session=narrow-auto&cols=138&rows=26&dev=desktop&label=Desktop`,
  );
  const wideAuto = waitForWsFrame(
    wide,
    text => {
      if (text[0] !== 'd') return false;
      const frame = JSON.parse(text.slice(1));
      return frame.mode === 'auto' && frame.local === false && frame.cols === 74 && frame.rows === 26;
    },
    'wide viewer narrowest-auto frame',
  );
  await once(wide, 'open');

  await Promise.all([narrowAuto, wideAuto]);
  const resize = await host.waitFor(
    m => m.t === 'resize' && m.s === 'narrow-auto' && m.cols === 74 && m.rows === 26,
    'per-axis auto host resize',
  );
  assert.deepEqual(resize, { t: 'resize', s: 'narrow-auto', cols: 74, rows: 26 });

  narrow.close();
  wide.close();
});

test('a pin persistence failure leaves the relay alive and the accepted size unchanged', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([{
    ...commandSession('pin-fault', 'pin-fault-id'),
    cols: 120,
    rows: 36,
    localViewers: 1,
  }]);
  t.after(() => host.close());

  const ws = new WebSocket(
    `ws://127.0.0.1:${h.port}/ws?session=pin-fault&cols=76&rows=27&dev=phone&label=Phone`,
  );
  await once(ws, 'open');
  h.failPersistence('pins.json', 'beforeWrite');
  ws.send('P1');

  await sleep(150);
  const health = await h.json('GET', '/api/health');
  assert.equal(health.persistence.blocked, true);
  await host.assertNo(
    m => m.t === 'resize' && m.s === 'pin-fault' && m.cols === 76 && m.rows === 27,
    'an uncommitted pin must not resize the shared PTY',
  );
  ws.close();
});

test('a device size pin is removed durably when its last viewer disconnects', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([commandSession('pin-cleanup', 'pin-id')]);
  t.after(() => host.close());

  const ws = new WebSocket(
    `ws://127.0.0.1:${h.port}/ws?session=pin-cleanup&cols=80&rows=24&dev=phone&label=phone`,
  );
  await once(ws, 'open');
  const sb = await host.waitFor(m => m.t === 'sb' && m.s === 'pin-cleanup', 'pin cleanup replay');
  host.sendScrollback('pin-cleanup', 'READY\r\n', sb);
  ws.send('P1');
  await waitFor(() => {
    const saved = new Map(JSON.parse(fs.readFileSync(path.join(h.tmp, 'pins.json'), 'utf8')));
    return saved.has('pin-cleanup');
  }, 'pin persisted');

  const closed = once(ws, 'close');
  ws.close();
  await closed;
  await waitFor(() => {
    const saved = new Map(JSON.parse(fs.readFileSync(path.join(h.tmp, 'pins.json'), 'utf8')));
    return !saved.has('pin-cleanup');
  }, 'disconnected pin removed');
});

test('a lost scrollback response is retried with a fresh correlation id', async t => {
  const h = new RelayHarness({ MUX_HOST_SB_REQUEST_TIMEOUT_MS: '150' });
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([commandSession('sb-retry', 'sb-retry-id')]);
  t.after(() => host.close());

  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=sb-retry&cols=80&rows=24`);
  await once(ws, 'open');
  const first = await host.waitFor(m => m.t === 'sb' && m.s === 'sb-retry', 'first scrollback request');
  const second = await waitFor(() => {
    const requests = host.messages.filter(m => m.t === 'sb' && m.s === 'sb-retry');
    return requests.length >= 2 ? requests[1] : null;
  }, 'retried scrollback request');
  assert.notEqual(second.rid, first.rid);

  const painted = waitForWsText(ws, /RETRIED_SNAPSHOT/, 'retried snapshot paint');
  host.sendScrollback('sb-retry', 'RETRIED_SNAPSHOT\r\n', second);
  assert.match(await painted, /RETRIED_SNAPSHOT/);
  ws.close();
});

test('late scrollback from a disconnected viewer generation cannot paint its replacement', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([commandSession('stale-sb', 'stale-id', Date.now())]);
  t.after(() => host.close());

  const first = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=stale-sb&cols=80&rows=24`);
  await once(first, 'open');
  const requestA = await host.waitFor(m => m.t === 'sb' && m.s === 'stale-sb', 'viewer A scrollback request');
  const firstClosed = once(first, 'close');
  first.close();
  await firstClosed;

  const second = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=stale-sb&cols=80&rows=24`);
  await once(second, 'open');
  const requestB = await waitFor(() => {
    const requests = host.messages.filter(m => m.t === 'sb' && m.s === 'stale-sb');
    return requests.length === 2 ? requests[1] : null;
  }, 'viewer B scrollback request');
  assert.notEqual(requestA.rid, requestB.rid);

  let seen = '';
  second.on('message', raw => { seen += raw.toString(); });
  host.sendScrollback('stale-sb', 'STALE_GENERATION_A\r\n', requestA);
  await sleep(150);
  assert.doesNotMatch(seen, /STALE_GENERATION_A/);

  const painted = waitForWsText(second, /CURRENT_GENERATION_B/, 'matching viewer B scrollback');
  host.sendScrollback('stale-sb', 'CURRENT_GENERATION_B\r\n', requestB);
  assert.match(await painted, /CURRENT_GENERATION_B/);
  second.close();
});

test('viewer outbound high-water disconnects before queuing an oversized live frame', async t => {
  const h = new RelayHarness({ MUX_VIEWER_HIGH_WATER_BYTES: '1024' });
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([commandSession('slow-viewer', 'slow-id', Date.now())]);
  t.after(() => host.close());

  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=slow-viewer&cols=80&rows=24`);
  await once(ws, 'open');
  await host.waitFor(m => m.t === 'sb' && m.s === 'slow-viewer', 'slow viewer scrollback');
  const ready = waitForWsText(ws, /READY/, 'initial replay');
  host.sendScrollback('slow-viewer', 'READY\r\n');
  await ready;

  const closed = once(ws, 'close');
  host.sendOutput('slow-viewer', 'X'.repeat(2048));
  await closed;
});

test('bounded initial replay is not rejected by the lower live-output high-water mark', async t => {
  const h = new RelayHarness({ MUX_VIEWER_HIGH_WATER_BYTES: '1024' });
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([commandSession('replay-burst', 'replay-burst-id')]);
  t.after(() => host.close());

  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=replay-burst&cols=80&rows=24`);
  await once(ws, 'open');
  const request = await host.waitFor(m => m.t === 'sb' && m.s === 'replay-burst', 'large replay request');
  const marker = 'REPLAY_SURVIVED';
  const painted = waitForWsText(ws, new RegExp(marker), 'large replay marker');
  host.sendScrollback('replay-burst', 'X'.repeat(2048) + marker, request);
  assert.match(await painted, new RegExp(marker));
  assert.equal(ws.readyState, WebSocket.OPEN);
  ws.close();
});

test('late scrollback after the sbWait timeout still paints the screen (black-screen regression)', async t => {
  // The relay blanks the terminal (CLEAR_SCREEN) on attach and repaints it from muxd's ring replay.
  // On an idle session the sb reply routinely lands AFTER the HOST_SB_WAIT_MS timeout (40ms here). The
  // old code cleared on timeout and then DROPPED the late sb (guarded on c.sbWait) -> permanent black
  // terminal until the agent emitted a byte. This asserts the replay is delivered even when late.
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([commandSession('idlecase', 'idle-id', Date.now())]);
  t.after(() => host.close());

  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=idlecase&cols=80&rows=24`);
  await once(ws, 'open');
  await host.waitFor(m => m.t === 'sb' && m.s === 'idlecase', 'scrollback request');

  await sleep(150);                                   // let the 40ms sbWait timeout elapse (idle: no output)
  const painted = waitForWsText(ws, /IDLE_SB_MARKER/, 'late scrollback replay reaches the client');
  host.sendScrollback('idlecase', 'IDLE_SB_MARKER screen contents\r\n');
  assert.match(await painted, /IDLE_SB_MARKER/);      // old code: dropped -> this times out (screen stays black)
  ws.close();
});

test('viewer replay resets leaked alternate-screen and mouse modes before painting', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([commandSession('mode-reset', 'mode-reset-id', Date.now())]);
  t.after(() => host.close());

  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=mode-reset&cols=80&rows=24`);
  await once(ws, 'open');
  await host.waitFor(m => m.t === 'sb' && m.s === 'mode-reset', 'mode reset scrollback request');
  const reset = waitForWsText(ws, /\x1b\[\?1049l/, 'terminal private-mode reset');
  const painted = waitForWsText(ws, /VISIBLE_AFTER_RESET/, 'mode reset replay');
  host.sendScrollback('mode-reset', 'VISIBLE_AFTER_RESET\r\n');
  assert.match(await reset, /\x1b\[\?1000l/);
  assert.match(await painted, /VISIBLE_AFTER_RESET/);
  ws.close();
});

test('burst output during attach never blanks-and-drops (busy-session regression)', async t => {
  // If output floods while the client is still waiting for scrollback, the relay must go live WITHOUT
  // losing the screen. The old overflow path set sbWait=false, orphaned the queued frames, skipped the
  // clear, and then dropped the sb -> lost output on a busy attach. This asserts flooded output arrives.
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([commandSession('busycase', 'busy-id', Date.now())]);
  t.after(() => host.close());

  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=busycase&cols=80&rows=24`);
  await once(ws, 'open');
  await host.waitFor(m => m.t === 'sb' && m.s === 'busycase', 'scrollback request');

  const got = waitForWsText(ws, /BURST_LINE_0007/, 'flooded output frames reach the client');
  for (let i = 0; i < 10; i++) host.sendOutput('busycase', `BURST_LINE_${String(i).padStart(4, '0')}\r\n`);
  host.sendScrollback('busycase', 'SB\r\n');
  assert.match(await got, /BURST_LINE_0007/);
  ws.close();
});

test('projects sync preserves decks and app commands preserve collection deck target', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());

  const project = {
    schemaVersion: 3,
    host: 'FAKEPC',
    decks: [{ id: 'main', name: 'Main' }, { id: 'client-a', name: 'Client A' }],
    collections: [{ id: 'client-a--ops', name: 'Ops', deckId: 'client-a', deckName: 'Client A', chats: [] }],
    allChats: [{ id: 'old1', tool: 'codex', title: 'Old Chat', muxName: 'old-chat' }],
    runningSessions: [],
  };
  const pushed = await h.request('POST', '/api/projects', project);
  assert.equal(pushed.status, 200);
  const pulled = await h.json('GET', '/api/projects');
  assert.deepEqual(pulled.decks, project.decks);
  assert.equal(pulled.collections[0].deckId, 'client-a');
  assert.equal(pulled.collections[0].deckName, 'Client A');
  assert.equal(pulled.allChats.length, 1);
  assert.equal(pulled.allChats[0].muxName, 'old-chat');

  const queued = await h.request('POST', '/api/app-commands', {
    type: 'addtocollection',
    intentId: 'collection-deck-target',
    sessionId: 'chat-one',
    expectedRevision: 'chat-revision',
    expectedCollectionRevision: 'collection-revision',
    muxName: 'chat-one',
    collectionId: 'client-a--ops',
    collection: 'Ops',
    deckId: 'client-a',
    deckName: 'Client A',
  });
  assert.equal(queued.status, 200);
  const pending = await leaseCommands(h);
  assert.equal(pending.length, 1);
  assert.equal(pending[0].collectionId, 'client-a--ops');
  assert.equal(pending[0].deckId, 'client-a');
  assert.equal(pending[0].deckName, 'Client A');
});

test('GET /api/sessions annotates renamed mux tabs with projected chat identity', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([commandSession('short-tab-name', 'sid-proj', Date.now())]);
  t.after(() => host.close());

  await h.json('POST', '/api/projects', {
    schemaVersion: 3,
    decks: [{ id: 'main', name: 'Main' }],
    collections: [],
    allChats: [{ id: 'sid-proj', tool: 'codex', title: 'Projected Chat', muxName: 'canonical-projected-sid-proj' }],
    runningSessions: [],
    host: 'FAKEPC',
  });

  const sessions = await h.json('GET', '/api/sessions');
  const row = sessions.find(s => s.name === 'short-tab-name');
  assert.ok(row);
  assert.equal(row.sessionId, 'sid-proj');
  assert.equal(row.projectMuxName, 'canonical-projected-sid-proj');
  assert.equal(row.chatLinked, true);
});

test('projects save-tabs dialog keeps new-deck row hidden until selected', () => {
  const html = fs.readFileSync(path.join(REPO, 'public', 'projects.html'), 'utf8');
  assert.equal(html.includes('muxCommand'), false);
  assert.equal(html.includes('{command:'), false);
  assert.match(html, /id="wsdecknewrow" hidden/);
  assert.match(html, /#wscoldlg\s+\.dlgrow\s*\{[^}]*display:flex/);
  assert.match(html, /#wscoldlg\s+\.dlgrow\[hidden\]\s*\{[^}]*display:none/);
  // Filing a tab must use the AUTHORITATIVE session id. The older `sessionId:s.sessionId||''` form filed
  // the tab against a merely-reported identity, so pin its absence instead of the obsolete literal.
  assert.equal(html.includes("sessionId:s.sessionId||''"), false);
  assert.ok(html.includes("generationId:s.generationId||''"));
  assert.ok(html.includes("sessionId:s.authoritativeSessionId??s.sessionId??''"));
});

test('terminal add-to-collection dialog supports decks and stable chat identity', () => {
  const html = fs.readFileSync(path.join(REPO, 'public', 'index.html'), 'utf8');
  assert.equal(html.includes('muxCommand'), false);
  assert.equal(html.includes('pcPath'), false);
  assert.equal(html.includes('{command:'), false);
  assert.match(html, /id="adddecksel"/);
  assert.match(html, /id="adddecknewrow" hidden/);
  assert.match(html, /dialog\s+\.row\[hidden\]\s*\{\s*display:none/);
  assert.match(html, /_appDecks=Array\.isArray\(p\.decks\)\?p\.decks:\[\]/);
  assert.match(html, /chatBackedSession\(s\)/);
  assert.match(html, /muxName:s\.projectMuxName\|\|s\.muxName\|\|s\.name/);
  assert.match(html, /sessionId:s\.sessionId\|\|''/);
  assert.match(html, /deckId:choice\.deckId\|\|''/);
  assert.match(html, /deckName:choice\.deckName\|\|''/);
  assert.match(html, /pollUploadCmd\(queued\.id,\s*20000\)/);
  assert.match(html, /type:'fetchfile'[^}]*\.\.\.destination[^}]*insert:insert\|\|'path'/);
  assert.match(html, /generationId: selected\.generationId/);
});

test('terminal tab strip converts hovered wheel input to horizontal scrolling', () => {
  const html = fs.readFileSync(path.join(REPO, 'public', 'index.html'), 'utf8');
  assert.match(html, /#tabs\s*\{[^}]*overflow-x:auto;[^}]*overscroll-behavior:contain/);
  assert.match(html, /wrap\.addEventListener\('wheel'/);
  assert.match(html, /if\(e\.ctrlKey\) return/);
  assert.match(html, /flexDirection/);
  assert.match(html, /tabs\.scrollLeft \+= px/);
  assert.match(html, /\}, \{passive:false\}\);/);
});

test('terminal attachment boundaries reset emulator and alternate-buffer scrolling', () => {
  const html = fs.readFileSync(path.join(REPO, 'public', 'index.html'), 'utf8');
  assert.match(html, /function scrollAlternate\(deltaY\)/);
  assert.match(html, /deltaY<0 \? '\\x1b\[5~' : '\\x1b\[6~'/);
  assert.match(html, /if \(!isRetry\) term\.reset\(\);\s*resetTerminalSurface\(\)/);
  assert.match(html, /autoFreeze=false;\s*selectMode=false;\s*_frozenBuf=\[\]/);
  assert.match(html, /\\x1b\[\?1049l\\x1b\[\?25h/);
});

test('terminal boot does not synthesize or attach a missing URL session', () => {
  const html = fs.readFileSync(path.join(REPO, 'public', 'index.html'), 'utf8');
  assert.doesNotMatch(html, /list\.push\(\{name:select/);
  assert.match(html, /session not found/);
});

test('browser mutations use the shared durable intent journal', () => {
  for (const file of ['public/index.html', 'public/projects.html']) {
    const source = fs.readFileSync(path.join(REPO, file), 'utf8');
    assert.match(source, /<script src="intent-journal\.js"><\/script>/);
    assert.doesNotMatch(source, /async function postIntent\(/);
    assert.doesNotMatch(source, /fetch\([^;\n]*api\/app-commands[^;\n]*method:'POST'/);
    assert.doesNotMatch(source, /fetch\([^;\n]*api\/sessions[^;\n]*relaunch[^;\n]*method:'POST'/);
  }
});

test('browser intent journal retains one intent across retryable failures and later retries', async () => {
  const source = fs.readFileSync(path.join(REPO, 'public', 'intent-journal.js'), 'utf8');
  const values = new Map();
  const storage = {
    getItem: key => values.has(key) ? values.get(key) : null,
    setItem: (key, value) => values.set(key, value),
  };
  const requests = [];
  const statuses = [503, 504, 503];
  const context = {
    console,
    crypto: { randomUUID: () => '11111111-2222-4333-8444-555555555555' },
    fetch: async (_url, options) => {
      requests.push(JSON.parse(options.body));
      return { status: statuses.shift() || 200 };
    },
    localStorage: storage,
    sessionStorage: null,
    setTimeout: callback => callback(),
  };
  context.globalThis = context;
  vm.runInNewContext(source, context);

  const first = await context.postIntent('/api/sessions', { name: 'durable-tab' }, 'create');
  assert.equal(first.status, 503);
  assert.equal(requests.length, 3);
  assert.equal(new Set(requests.map(request => request.intentId)).size, 1);
  assert.equal(JSON.parse(values.get('mux.intent-journal.v1')).records.length, 1);

  const completed = await context.postIntent('/api/sessions', { name: 'durable-tab' }, 'create');
  assert.equal(completed.status, 200);
  assert.equal(requests[3].intentId, requests[0].intentId);
  assert.equal(JSON.parse(values.get('mux.intent-journal.v1')).records.length, 0);
});

test('browser intent journal clears terminal 409 refusals so a corrected retry gets a new intent', async () => {
  const source = fs.readFileSync(path.join(REPO, 'public', 'intent-journal.js'), 'utf8');
  const values = new Map();
  const storage = {
    getItem: key => values.has(key) ? values.get(key) : null,
    setItem: (key, value) => values.set(key, value),
  };
  const requests = [];
  let nextId = 0;
  const context = {
    console,
    crypto: { randomUUID: () => `11111111-2222-4333-8444-${String(++nextId).padStart(12, '0')}` },
    fetch: async (_url, options) => {
      requests.push(JSON.parse(options.body));
      return { status: requests.length === 1 ? 409 : 200 };
    },
    localStorage: storage,
    sessionStorage: null,
    setTimeout: callback => callback(),
  };
  context.globalThis = context;
  vm.runInNewContext(source, context);

  assert.equal((await context.postIntent('/api/sessions/work/relaunch', {}, 'relaunch')).status, 409);
  assert.equal((await context.postIntent('/api/sessions/work/relaunch', {}, 'relaunch')).status, 200);
  assert.notEqual(requests[0].intentId, requests[1].intentId);
});

test('browser intent journal fails closed when no durable browser storage is available', async () => {
  const source = fs.readFileSync(path.join(REPO, 'public', 'intent-journal.js'), 'utf8');
  let fetched = false;
  const unavailable = {
    getItem() { throw new Error('storage disabled'); },
    setItem() { throw new Error('storage disabled'); },
  };
  const context = {
    console,
    crypto: { randomUUID: () => '11111111-2222-4333-8444-555555555555' },
    fetch: async () => { fetched = true; return { status: 200 }; },
    localStorage: unavailable,
    sessionStorage: unavailable,
    setTimeout: callback => callback(),
  };
  context.globalThis = context;
  vm.runInNewContext(source, context);

  await assert.rejects(
    context.postIntent('/api/sessions', { name: 'must-not-send' }, 'create'),
    /could not durably record remote operation intent/,
  );
  assert.equal(fetched, false);
});

test('health reports pending and leased command work without calling a removed pruner', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());

  await h.json('POST', '/api/app-commands', {
    type: 'transcript',
    sessionId: 'health-pending-session',
    intentId: 'health-pending-intent',
  });
  assert.equal((await h.json('GET', '/api/health')).projects.pendingCommands, 1);
  await leaseCommands(h);
  assert.equal((await h.json('GET', '/api/health')).projects.pendingCommands, 1);
});

test('terminal command retention is bounded without pruning pending work', async t => {
  const h = new RelayHarness();
  await h.stopProcess();
  const commandsFile = path.join(h.tmp, 'app-commands.json');
  const backupFile = commandsFile + '.bak';
  if (fs.existsSync(backupFile)) fs.unlinkSync(backupFile);
  fs.writeFileSync(commandsFile, JSON.stringify([
    {
      id: 'cold-terminal',
      intentId: 'cold-terminal-intent',
      type: 'transcript',
      replayPolicy: 'read-only',
      sessionId: 'cold-session',
      ts: 1,
      status: 'done',
      doneAt: 1,
      leaseToken: 'cold-token',
      fingerprint: 'a'.repeat(64),
    },
    {
      id: 'old-pending',
      intentId: 'old-pending-intent',
      type: 'transcript',
      replayPolicy: 'read-only',
      sessionId: 'pending-session',
      ts: 1,
      status: 'pending',
      fingerprint: 'b'.repeat(64),
    },
  ]));
  await h.start();
  t.after(async () => h.stop());

  assert.equal((await h.request('GET', '/api/app-commands/cold-terminal')).status, 404);
  const leased = await leaseCommands(h);
  assert.equal(leased.some(command => command.id === 'old-pending'), true);
});

test('running projection preserves unresolved identity evidence without claiming absence', async t => {
  const h = new RelayHarness();
  t.after(() => h.stop());
  await h.start();
  const response = await h.request('POST', '/api/running', {
    schemaVersion: 3, runningVerified: false, runningVerificationDetail: 'handle lookup unavailable',
    runningSessions: [{ pid: 456, tool: 'codex', sessionId: 'known-id', sessionAliases: ['other-id'],
      identityStatus: 'unverifiable', identitySource: 'open transcript' }],
  });
  assert.equal(response.status, 200);
  const projects = await h.json('GET', '/api/projects');
  assert.equal(projects.runningVerified, false);
  assert.equal(projects.runningSessions.length, 1);
  assert.deepEqual(projects.runningSessions[0].sessionAliases, ['other-id']);
  assert.equal(projects.runningSessions[0].identityStatus, 'unverifiable');
});

// The GUI-active lane pushes the FULL /api/projects projection (collections + running rows together),
// the closed-GUI lane pushes the light /api/running partial. Both must land the same identity evidence
// in the stored projection, or the web's "identity unresolved/unverifiable" label and its alias-based
// liveness would depend on which lane happened to push last. This is the full-projection counterpart of
// the test above, and it also pins that a running push never clobbers the collections projection.
test('full projection preserves unresolved running identity evidence alongside collections', async t => {
  const h = new RelayHarness();
  t.after(() => h.stop());
  await h.start();
  const posted = await h.json('POST', '/api/projects', {
    schemaVersion: 3, host: 'PC',
    decks: [{ id: 'deck-1', name: 'Main' }],
    collections: [{
      id: 'collection-1', name: 'Cortex', deckId: 'deck-1', deckName: 'Main',
      chats: [{ id: 'known-id', title: 'Known chat', tool: 'codex', muxName: 'known-tab' }],
    }],
    allChats: [{ id: 'known-id', title: 'Known chat', tool: 'codex', muxName: 'known-tab' }],
    runningVerified: false, runningVerificationDetail: 'handle lookup unavailable',
    runningSessions: [{ pid: 456, tool: 'codex', sessionId: 'known-id', sessionAliases: ['other-id'],
      identityStatus: 'unverifiable', identitySource: 'open transcript' }],
  });
  assert.equal(posted.ok, true);
  const projects = await h.json('GET', '/api/projects');
  assert.equal(projects.collections.length, 1);
  assert.equal(projects.collections[0].chats[0].id, 'known-id');
  assert.equal(projects.runningVerified, false);
  assert.equal(projects.runningSessions.length, 1);
  assert.equal(projects.runningSessions[0].identityStatus, 'unverifiable');
  assert.equal(projects.runningSessions[0].identitySource, 'open transcript');
  assert.deepEqual(projects.runningSessions[0].sessionAliases, ['other-id']);
});

test('hosted kill rejects a stale generation before sending any stop', async t => {
  const h = new RelayHarness();
  t.after(() => h.stop());
  await h.start();
  const host = await h.connectHost([{ ...shellSession('kill-replaced'), generationId: 'replacement-generation' }]);
  t.after(() => host.close());
  const response = await h.request('DELETE', '/api/sessions/kill-replaced', {
    sessionId: '', generationId: 'original-generation',
  });
  assert.equal(response.status, 409);
  assert.equal(host.messages.filter(m => m.t === 'kill').length, 0);
  assert.equal((await h.json('GET', '/api/sessions'))[0].generationId, 'replacement-generation');
});

test('hosted kill carries exact shell identity and generation to muxd', async t => {
  const h = new RelayHarness();
  t.after(() => h.stop());
  await h.start();
  const host = await h.connectHost([{ ...shellSession('kill-fenced'), generationId: 'shell-generation' }]);
  t.after(() => host.close());
  const deletion = h.request('DELETE', '/api/sessions/kill-fenced', { sessionId: '', generationId: 'shell-generation' });
  const kill = await host.waitFor(m => m.t === 'kill', 'fenced kill');
  // Supply a response even against the old server so the counterexample leaves no pending request.
  host.ws.send(JSON.stringify({ t: 'killed', s: kill.s, rid: kill.rid, sessionId: '', generationId: 'shell-generation' }));
  const response = await deletion;
  assert.equal(kill.sessionId, '');
  assert.equal(kill.generationId, 'shell-generation');
  assert.ok(kill.rid, 'stop completion must be correlated');
  assert.equal(response.status, 200);
});

test('late killed frame cannot remove a same-name replacement projection', async t => {
  const h = new RelayHarness();
  t.after(() => h.stop());
  await h.start();
  const host = await h.connectHost([{ ...shellSession('kill-late'), generationId: 'replacement-generation' }]);
  t.after(() => host.close());
  // A correlated tail response is a host-link barrier, not a scheduling sleep.
  const tail = h.request('GET', '/api/sessions/kill-late/tail');
  const request = await host.waitFor(m => m.t === 'tail', 'host barrier');
  host.ws.send(JSON.stringify({ t: 'killed', s: 'kill-late', rid: 'old-kill', sessionId: '', generationId: 'original-generation' }));
  host.sendTail('kill-late', '', { request });
  await tail;
  assert.equal((await h.json('GET', '/api/sessions')).find(s => s.name === 'kill-late')?.generationId, 'replacement-generation');
});

test('hosted kill rejects missing identity and does not infer success from disappearance', async t => {
  const h = new RelayHarness();
  t.after(() => h.stop());
  await h.start();
  const session = { ...shellSession('kill-unknown'), generationId: 'unknown-generation' };
  const host = await h.connectHost([session]);
  t.after(() => host.close());
  assert.equal((await h.request('DELETE', '/api/sessions/kill-unknown')).status, 400);
  const deletion = h.request('DELETE', '/api/sessions/kill-unknown', { sessionId: '', generationId: session.generationId });
  await host.waitFor(m => m.t === 'kill', 'pending kill');
  host.sendSessions([]);
  host.close();
  assert.equal((await deletion).status, 503, 'disconnect after disappearance is not confirmed completion');
});

test('hosted kill completion preserves a replacement and fingerprints generation', async t => {
  const h = new RelayHarness();
  t.after(() => h.stop());
  await h.start();
  const session = { ...shellSession('kill-race'), generationId: 'first-generation' };
  const host = await h.connectHost([session]);
  t.after(() => host.close());
  const body = { intentId: 'kill-race-intent', sessionId: '', generationId: session.generationId };
  const deletion = h.request('DELETE', '/api/sessions/kill-race', body);
  const request = await host.waitFor(m => m.t === 'kill', 'original stop');
  host.sendSessions([{ ...session, generationId: 'second-generation' }]);
  host.ws.send(JSON.stringify({ ...request, t: 'killed' }));
  assert.equal((await deletion).status, 200);
  assert.equal((await h.json('GET', '/api/sessions'))[0].generationId, 'second-generation');
  assert.equal((await h.request('DELETE', '/api/sessions/kill-race', body)).status, 200);
  assert.equal((await h.request('DELETE', '/api/sessions/kill-race', { ...body, generationId: 'second-generation' })).status, 409);
  assert.equal(host.messages.filter(m => m.t === 'kill').length, 1);
});

test('DELETE /api/sessions sends kill and waits until hosted row is gone', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([{ ...shellSession('killcase'), generationId: 'killcase-generation' }]);
  t.after(() => host.close());

  const del = h.request('DELETE', '/api/sessions/killcase', { sessionId: '', generationId: 'killcase-generation' });
  const kill = await host.waitFor(m => m.t === 'kill' && m.s === 'killcase', 'kill request');
  assert.equal(kill.s, 'killcase');

  let settled = false;
  del.then(() => { settled = true; });
  await sleep(150);
  assert.equal(settled, false, 'DELETE returned before muxd confirmed kill');

  host.sendKilled('killcase');
  const res = await del;
  assert.equal(res.status, 200);
  assert.equal(res.body.ok, true);
  assert.deepEqual(await h.json('GET', '/api/sessions'), []);
});
