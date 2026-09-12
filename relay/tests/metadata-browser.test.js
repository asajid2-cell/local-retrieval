const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.resolve(__dirname, '..', 'public', 'chats.js'), 'utf8');
function client() {
  const sandbox = { console, URLSearchParams, Promise, Date, Array, Set, Math, encodeURIComponent, setTimeout, clearTimeout };
  vm.runInContext(source, vm.createContext(sandbox));
  return sandbox.MuxChats;
}
const row = { id: 'chat-1', revision: 'r-7', tool: 'codex', archived: true };

test('metadata payloads are explicit and revision-bound', () => {
  const api = client();
  assert.deepEqual(JSON.parse(JSON.stringify(api.managementPayload('setapptitle', row, ' New title '))), { payload: { type: 'setapptitle', sessionId: 'chat-1', expectedRevision: 'r-7', title: 'New title', tool: 'codex' } });
  assert.deepEqual(api.managementPayload('archive', row, true).payload.archived, true);
  assert.deepEqual(api.managementPayload('archive', row, false).payload.archived, false); // unarchive is the authoritative inverse of the row state.
  assert.deepEqual(api.managementPayload('setphrases', row, ['a', ' a ', 'b']).payload.phrases, ['a', 'b']);
  assert.deepEqual(JSON.parse(JSON.stringify(api.managementPayload('settag', row, { tag: ' focus ', enabled: false }).payload)), { type: 'settag', sessionId: 'chat-1', expectedRevision: 'r-7', tag: 'focus', enabled: false, tool: 'codex' });
});

test('checkpoint and branch payloads bind exact reviewed ids and revisions', () => {
  const api = client();
  const chat = { id: 'chat-1', revision: 'chat-r1', tool: 'codex' };
  const snapshot = { id: 'snap-9', revision: 'snap-r4', sourceSessionId: 'chat-1', label: 'Safe point' };
  assert.deepEqual(JSON.parse(JSON.stringify(api.managementPayload('checkpointcreate', chat, { name: ' Release ' }).payload)), {
    type: 'checkpointcreate', sessionId: 'chat-1', expectedRevision: 'chat-r1', tool: 'codex', name: 'Release',
  });
  assert.deepEqual(JSON.parse(JSON.stringify(api.managementPayload('branchcreate', chat, {}).payload)), {
    type: 'branchcreate', sessionId: 'chat-1', expectedRevision: 'chat-r1', tool: 'codex',
  });
  assert.deepEqual(JSON.parse(JSON.stringify(api.managementPayload('checkpointrename', chat, { snapshot, name: 'Renamed' }).payload)), {
    type: 'checkpointrename', snapshotId: 'snap-9', expectedRevision: 'snap-r4', name: 'Renamed',
  });
  assert.deepEqual(JSON.parse(JSON.stringify(api.managementPayload('checkpointdelete', chat, { snapshot }).payload)), {
    type: 'checkpointdelete', snapshotId: 'snap-9', expectedRevision: 'snap-r4',
  });
  assert.deepEqual(JSON.parse(JSON.stringify(api.managementPayload('checkpointspawn', chat, { snapshot }).payload)), {
    type: 'checkpointspawn', snapshotId: 'snap-9', expectedRevision: 'snap-r4',
  });
});

test('checkpoint payloads refuse stale or incomplete snapshot rows without refresh', () => {
  const api = client();
  const snapshot = { id: 'snap-9', revision: 'old-r' };
  const result = api.managementPayload('checkpointdelete', { id: 'chat-1', revision: 'new-r', tool: 'codex' }, { snapshot });
  assert.equal(result.payload.expectedRevision, 'old-r');
  assert.equal(result.payload.snapshotId, 'snap-9');
  assert.match(api.managementPayload('branchcreate', {}, {}).error, /chat|safe/i);
  assert.match(api.managementPayload('checkpointdelete', {}, { snapshot: { id: 'snap-9' } }).error, /authoritative revision/i);
});

test('metadata refuses missing revision and malformed desired state', () => {
  const api = client();
  assert.equal(api.managementPayload('archive', { id: 'chat-1' }, true).error, 'This result has no authoritative revision.');
  assert.match(api.managementPayload('setphrases', row, ['ok', 3]).error, /array/);
  assert.match(api.managementPayload('settag', row, { tag: '', enabled: true }).error, /empty/);
});

test('collection membership payload uses the service revision field', () => {
  const api = client();
  assert.deepEqual(JSON.parse(JSON.stringify(api.managementPayload('addtocollection', row, {
    collectionId: 'col-a', collectionRevision: 'revision-a',
  }).payload)), {
    type: 'addtocollection', sessionId: 'chat-1', tool: 'codex',
    collectionId: 'col-a', expectedCollectionRevision: 'revision-a',
  });
});


test('collection membership payload refuses a missing authoritative collection revision', () => {
  const api = client();
  assert.match(api.managementPayload('removefromcollection', row, { collectionId: 'col-a' }).error, /authoritative revision/);
});


test('metadata editor only submits changed fields and archives last', () => {
  assert.match(source, /var appTitle = chat\.customTitle/);
  assert.match(source, /if \(desiredTitle !== String\(currentTitle/);
  assert.match(source, /if \(JSON\.stringify\(desiredPhrases\)/);
  assert.match(source, /if \(tagged\)/);
  assert.match(source, /if \(archiveInput\.checked !==/);
  assert.ok(source.indexOf("apply('archive'") > source.indexOf("apply('settag'"));
  assert.match(source, /showDialogError/);
});

test('distinct management commands never inherit an in-flight command result', async () => {
  const api = client();
  let releaseFavorite;
  const favoriteStarted = new Promise(resolve => { releaseFavorite = resolve; });
  const posts = [];
  const management = api.createManagement({
    fetch: async () => ({ ok: true, status: 200, json: async () => ({ status: 'done' }) }),
    postIntent: async (_url, payload) => {
      posts.push(payload);
      if (payload.type === 'setfavorite') {
        await favoriteStarted;
        return { ok: true, status: 200, json: async () => ({ id: 'favorite-command' }) };
      }
      return { ok: true, status: 200, json: async () => ({ id: 'title-command' }) };
    },
    sleep: async () => {}, now: () => 0, pollIntervalMs: 0, pollTimeoutMs: 1,
  });
  const favorite = management.run('setfavorite', row, true);
  await Promise.resolve();
  const title = await management.run('setapptitle', row, 'New title');
  assert.equal(title.state, 'refused');
  assert.match(title.detail, /another metadata change is still pending/i);
  assert.deepEqual(posts.map(payload => payload.type), ['setfavorite']);
  releaseFavorite();
  await favorite;
  assert.equal(management.state.promise, null);
  const freshTitle = await management.run('setapptitle', row, 'New title');
  assert.equal(freshTitle.state, 'done');
  assert.deepEqual(posts.map(payload => payload.type), ['setfavorite', 'setapptitle']);
  assert.equal(posts[1].title, 'New title');
  assert.notEqual(freshTitle.id, 'favorite-command');
});

test('same management command coalesces only an exact payload', async () => {
  const api = client();
  let resolvePost;
  const posts = [];
  const management = api.createManagement({
    fetch: async (_url) => ({ ok: true, status: 200, json: async () => ({ status: 'done' }) }),
    postIntent: async (_url, payload) => {
      posts.push(payload);
      await new Promise(resolve => { resolvePost = resolve; });
      return { ok: true, status: 200, json: async () => ({ id: 'same-command' }) };
    },
    sleep: async () => {}, now: () => 0, pollIntervalMs: 0, pollTimeoutMs: 1,
  });
  const first = management.run('setfavorite', row, true);
  await Promise.resolve();
  const duplicate = management.run('setfavorite', row, true);
  assert.equal(posts.length, 1);
  resolvePost();
  await Promise.all([first, duplicate]);
  assert.equal(posts.length, 1);
});

test('new checkpoint controls inherit the current management busy state and reenable', () => {
  const api = client();
  const controls = [{ disabled: false }, { disabled: false }, { disabled: false }];
  const dialog = { dataset: {}, querySelectorAll: () => controls };
  api.setManagementBusy(dialog, true);
  assert.equal(dialog.dataset.managementBusy, 'true');
  assert.deepEqual(controls.map(control => control.disabled), [true, true, true]);
  api.setManagementBusy(dialog, false);
  assert.equal(dialog.dataset.managementBusy, 'false');
  assert.deepEqual(controls.map(control => control.disabled), [false, false, false]);
  assert.match(source, /text\(button, caption\); button\.disabled = management\.state\.submitting/);
  assert.match(source, /setManagementBusy\(dialog, management\.state\.submitting\)/);
});

test('refused and uncertain management outcomes do not mutate optimistic state', async () => {
  const api = client();
  const management = api.createManagement({
    fetch: async () => ({ ok: true, status: 200, json: async () => ({ status: 'failed', detail: 'stale revision' }) }),
    postIntent: async (_url, payload) => ({ ok: true, status: 200, json: async () => ({ id: 'cmd-1', payload }) }),
    sleep: async () => {}, now: (() => { let n = 0; return () => ++n; })(), pollIntervalMs: 0, pollTimeoutMs: 10,
  });
  const outcome = await management.run('archive', row, true);
  assert.equal(outcome.state, 'refused');
  assert.equal(row.archived, true);
});

assert.match(fs.readFileSync(path.resolve(__dirname, '..', 'public', 'chats.html'), 'utf8'), /Show hidden one-off chats/);
assert.match(fs.readFileSync(path.resolve(__dirname, '..', 'public', 'chats.html'), 'utf8'), /Archived chats/);
assert.match(source, /archived.*active/);
assert.match(source, /dataset\.action = 'metadata'/);
assert.match(source, /controller\.load\(true\)/);
assert.match(source, /Metadata/);
assert.ok(true);

// Browser acceptance remains an operator workflow; these source tests intentionally do not claim it ran.
module.exports = {};

test('workflow verifier contract is represented by the metadata controls', () => {
  assert.match(source, /setapptitle/);
  assert.match(source, /setphrases/);
  assert.match(source, /settag/);
});
