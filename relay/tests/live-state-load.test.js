const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {
  leaseCommands,
  RelayHarness,
} = require('./harness');

test('canonical boots from the live fork raw state file shapes', async t => {
  const h = new RelayHarness();
  const now = Date.now();

  fs.writeFileSync(path.join(h.tmp, 'projects.json'), JSON.stringify({
    decks: [{ id: 'deck-live', name: 'Live Deck', ignored: true }],
    collections: [{
      id: 'collection-live',
      name: 'Live Collection',
      chats: [{
        id: 'chat-live',
        title: 'Live Chat',
        tool: 'codex',
        muxName: 'live-tab',
        muxCommand: 'codex resume chat-live',
        cwd: 'C:\\private\\workspace',
      }],
    }],
    allChats: [
      { id: '../invalid', title: 'Invalid identity is sanitized', tool: 'codex', muxName: '../bad' },
    ],
    runningSessions: [
      { sessionId: 'chat-live', pid: 4242, command: 'secret executable command' },
    ],
    muxTabChats: {
      'live-tab': {
        id: 'chat-live',
        tool: 'codex',
        title: 'Live Chat',
        muxCommand: 'codex resume chat-live',
      },
      '../bad': { id: 'bad' },
    },
    muxTabMeta: {
      'live-tab': { color: '#123456', kind: 'remote-resumed' },
    },
    host: 'LIVE-PC',
    syncedAt: now,
    runningVerified: true,
  }));

  fs.writeFileSync(path.join(h.tmp, 'pins.json'), JSON.stringify([
    ['live-tab', { deviceId: 'phone', label: 'Phone', at: now }],
    ['stale-tab', { deviceId: 'desktop', label: 'Desktop', at: now - 1000 }],
  ]));

  fs.writeFileSync(path.join(h.tmp, 'app-commands.json'), JSON.stringify([
    {
      id: 'live-command',
      type: 'transcript',
      sessionId: 'chat-live',
      tool: 'codex',
      ts: now,
      status: 'pending',
      detail: '',
      muxCommand: 'codex resume chat-live',
    },
    {
      id: '../invalid-command',
      type: 'kill',
      sessionId: '../invalid-session',
      ts: now,
      status: 'pending',
    },
  ]));

  const uploadId = 'ulive-state';
  const uploadDir = path.join(h.tmp, 'uploads', uploadId);
  fs.mkdirSync(uploadDir, { recursive: true });
  fs.writeFileSync(path.join(uploadDir, 'evidence.txt'), 'live bytes');
  fs.writeFileSync(path.join(h.tmp, 'uploads-meta.json'), JSON.stringify([
    {
      id: uploadId,
      name: 'evidence.txt',
      session: 'live-tab',
      ts: now,
      size: 10,
      keep: true,
      pcPath: 'C:\\private\\evidence.txt',
    },
  ]));

  await h.start();
  t.after(async () => h.stop());

  const health = await h.json('GET', '/api/health');
  assert.equal(health.node, process.version);

  const projects = await h.json('GET', '/api/projects');
  assert.equal(projects.collections[0].chats[0].id, 'chat-live');
  assert.equal(projects.collections[0].chats[0].muxName, 'live-tab');
  assert.equal(Object.prototype.hasOwnProperty.call(projects.collections[0].chats[0], 'muxCommand'), false);
  assert.equal(Object.prototype.hasOwnProperty.call(projects.runningSessions[0], 'command'), false);
  assert.equal(projects.allChats[0].id, '');
  assert.equal(projects.allChats[0].muxName, '');

  const leased = await leaseCommands(h, 'live-state-loader');
  const migrated = leased.find(command => command.id === 'live-command');
  assert.ok(migrated);
  assert.equal(migrated.intentId, 'live-command');
  assert.equal(migrated.status, 'leased');
  assert.equal(typeof migrated.leaseToken, 'string');
  assert.ok(migrated.leaseToken.length > 0);
  assert.equal(Object.prototype.hasOwnProperty.call(migrated, 'muxCommand'), false);
  assert.equal(leased.some(command => command.id === '../invalid-command'), false);

  const uploads = await h.json('GET', '/api/uploads');
  assert.deepEqual(uploads, [{
    id: uploadId,
    name: 'evidence.txt',
    size: 10,
    keep: true,
    ts: now,
    image: false,
    onPc: false,
  }]);

  const pins = new Map(JSON.parse(fs.readFileSync(path.join(h.tmp, 'pins.json'), 'utf8')));
  assert.equal(pins.get('live-tab').deviceId, 'phone');
});
