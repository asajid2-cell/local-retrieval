const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const PUBLIC = path.resolve(__dirname, '..', 'public');
const chatsSource = fs.readFileSync(path.join(PUBLIC, 'chats.js'), 'utf8');
const chatsHtml = fs.readFileSync(path.join(PUBLIC, 'chats.html'), 'utf8');

function element(tagName) {
  const node = {
    tagName: String(tagName).toUpperCase(),
    type: '', className: '', textContent: '', value: '', disabled: false, hidden: false,
    open: false, children: [], parentNode: null, onclick: null, onchange: null, oninput: null,
    onsubmit: null, listeners: Object.create(null),
    appendChild(child) { child.parentNode = node; node.children.push(child); return child; },
    removeChild(child) {
      const index = node.children.indexOf(child);
      if (index >= 0) node.children.splice(index, 1);
      child.parentNode = null;
      return child;
    },
    setAttribute(name, value) { node[name] = String(value); },
    addEventListener(type, listener) {
      (node.listeners[type] || (node.listeners[type] = [])).push(listener);
    },
  };
  Object.defineProperty(node, 'firstChild', { get: () => node.children[0] || null });
  return node;
}

function fakeDocument() {
  const ids = [
    'startchatbtn', 'startdlg', 'startform', 'startstatus', 'startclose', 'startcancel',
    'startsubmit', 'startname', 'startdeck', 'startcollection', 'startcollectionnew',
    'startcheckpoint', 'startcheckpointnote', 'starttool', 'startworkspace', 'startsubfolder',
    'startphrase',
  ];
  const nodes = new Map(ids.map(id => [`#${id}`, element(id === 'startform' ? 'form' : 'div')]));
  const dialog = nodes.get('#startdlg');
  dialog.showModal = () => { dialog.open = true; };
  dialog.close = () => { dialog.open = false; };
  return {
    nodes,
    querySelector(selector) { return nodes.get(selector) || null; },
    createElement(tagName) { return element(tagName); },
  };
}

function loadClient() {
  const sandbox = {
    console, URLSearchParams, Promise, Date, Array, Set, Math,
    encodeURIComponent, setTimeout, clearTimeout,
  };
  vm.runInContext(chatsSource, vm.createContext(sandbox), { filename: 'chats.js' });
  assert.ok(sandbox.MuxChats, 'chats.js must install MuxChats');
  return sandbox;
}

function fire(node, type) {
  const listener = node['on' + type];
  assert.equal(typeof listener, 'function', `${node.tagName} must handle ${type}`);
  return listener({ type, target: node, preventDefault() {} });
}

function optionsFetch(calls) {
  return async url => {
    const address = String(url);
    calls.push(address);
    if (address.endsWith('/start/decks')) {
      return {
        ok: true, status: 200,
        json: async () => ({ activeDeckId: 'deck-main', rows: [{ id: 'deck-main', label: 'Main' }] }),
      };
    }
    if (address.endsWith('/start/checkpoints')) {
      return {
        ok: true, status: 200,
        json: async () => ({
          rows: [{
            id: 'checkpoint-1', label: 'Checkpoint one', sourceTitle: 'Source chat',
            tool: 'codex', workspaceLabel: 'retrieval', createdAt: '2026-08-02T12:00:00Z',
            messageCount: 4,
          }],
        }),
      };
    }
    if (address.endsWith('/start/workspaces')) {
      return {
        ok: true, status: 200,
        json: async () => ({ rows: [{ id: 'workspace-1', label: 'retrieval', tools: ['codex'] }] }),
      };
    }
    if (address.includes('/start/collections?deckId=deck-main')) {
      return {
        ok: true, status: 200,
        json: async () => ({ deckId: 'deck-main', rows: [{ id: 'collection-1', label: 'Web parity' }] }),
      };
    }
    if (address.includes('/api/app-commands/cmd-1')) {
      return {
        ok: true, status: 200,
        json: async () => ({ status: 'done', detail: 'mux session started' }),
      };
    }
    throw new Error(`unexpected request: ${address}`);
  };
}

test('start controller loads four pickers, sends opaque identity payload, polls, and navigates', async () => {
  const calls = [];
  const posted = [];
  const navigated = [];
  const sandbox = loadClient();
  const controller = sandbox.MuxChats.createStartChat({
    base: '',
    discoveryBase: '/multiplex/pc/api/discovery',
    fetch: optionsFetch(calls),
    postIntent: async (url, payload, prefix) => {
      posted.push({ url, payload, prefix });
      return { ok: true, status: 200, json: async () => ({ id: 'cmd-1', intentId: 'startchat-stable' }) };
    },
    navigate: async muxName => { navigated.push(muxName); },
    pollIntervalMs: 1,
  });

  await controller.load();
  assert.deepEqual(
    calls.filter(url => url.includes('/start/')).sort(),
    [
      '/multiplex/pc/api/discovery/start/checkpoints',
      '/multiplex/pc/api/discovery/start/collections?deckId=deck-main',
      '/multiplex/pc/api/discovery/start/decks',
      '/multiplex/pc/api/discovery/start/workspaces',
    ].sort(),
  );

  controller.setField('name', 'phone-start');
  controller.setField('tool', 'codex');
  controller.setField('workspaceId', 'workspace-1');
  controller.setField('subfolder', 'fresh-folder');
  controller.setField('phrase', 'web parity');
  controller.setField('collection', 'New collection');

  const outcome = await controller.submit();
  assert.equal(outcome.state, 'done');
  assert.deepEqual(JSON.parse(JSON.stringify(posted[0])), {
    url: '/api/app-commands',
    prefix: 'startchat',
    payload: {
      type: 'startchat',
      muxName: 'phone-start',
      title: 'phone-start',
      tool: 'codex',
      checkpointId: '',
      workspaceId: 'workspace-1',
      subfolder: 'fresh-folder',
      deckId: 'deck-main',
      collectionId: '',
      collection: 'New collection',
      phrase: 'web parity',
    },
  });
  assert.deepEqual(navigated, ['phone-start']);
  assert.doesNotMatch(JSON.stringify(posted[0].payload), /command|cwd|path|exe|snapshot|transcript/i);
});

test('checkpoint mode clears and disables blank-chat controls, then restores them', async () => {
  const calls = [];
  const sandbox = loadClient();
  const doc = fakeDocument();
  const ui = sandbox.MuxChats.installStartChat({
    document: doc,
    $: selector => doc.querySelector(selector),
    apiBase: '/multiplex/pc/api/discovery',
    fetch: optionsFetch(calls),
    postIntent: async () => ({ ok: true, status: 200, json: async () => ({ id: 'cmd-1' }) }),
    navigate: async () => {},
    pollIntervalMs: 1,
  });
  assert.ok(ui, 'start dialog must mount');
  ui.open();
  for (let i = 0; i < 12 && ui.controller.state.loading; i += 1) {
    await new Promise(resolve => setTimeout(resolve, 0));
  }
  assert.equal(ui.controller.state.loading, false);

  const tool = doc.nodes.get('#starttool');
  const workspace = doc.nodes.get('#startworkspace');
  const subfolder = doc.nodes.get('#startsubfolder');
  const checkpoint = doc.nodes.get('#startcheckpoint');

  tool.value = 'codex';
  workspace.value = 'workspace-1';
  subfolder.value = 'folder-that-must-clear';
  checkpoint.value = 'checkpoint-1';
  fire(checkpoint, 'change');

  assert.equal(tool.disabled, true);
  assert.equal(workspace.disabled, true);
  assert.equal(subfolder.disabled, true);
  assert.equal(tool.value, '');
  assert.equal(workspace.value, '');
  assert.equal(subfolder.value, '');
  assert.deepEqual(JSON.parse(JSON.stringify(ui.controller.payloadFor(ui.controller.state.form))), {
    type: 'startchat',
    muxName: '',
    title: '',
    tool: '',
    checkpointId: 'checkpoint-1',
    workspaceId: '',
    subfolder: '',
    deckId: 'deck-main',
    collectionId: '',
    collection: '',
    phrase: '',
  });

  checkpoint.value = '';
  fire(checkpoint, 'change');
  assert.equal(tool.disabled, false);
  assert.equal(workspace.disabled, false);
  assert.equal(subfolder.disabled, false);
});

test('double submit shares one in-flight start request and keeps the same command outcome', async () => {
  const calls = [];
  const posted = [];
  const sandbox = loadClient();
  const controller = sandbox.MuxChats.createStartChat({
    base: '',
    discoveryBase: '/multiplex/pc/api/discovery',
    fetch: optionsFetch(calls),
    postIntent: async (url, payload, prefix) => {
      posted.push({ url, payload, prefix });
      return { ok: true, status: 200, json: async () => ({ id: 'cmd-1', intentId: 'stable-intent' }) };
    },
    navigate: async () => {},
    pollIntervalMs: 1,
  });
  await controller.load();
  controller.setField('name', 'double-tap');
  controller.setField('tool', 'codex');
  controller.setField('workspaceId', 'workspace-1');

  const first = controller.submit();
  const second = controller.submit();
  const outcomes = await Promise.all([first, second]);

  assert.equal(posted.length, 1);
  assert.equal(outcomes[0].state, 'done');
  assert.equal(outcomes[1].state, 'done');
  assert.equal(outcomes[0].muxName, 'double-tap');
  assert.equal(outcomes[1].muxName, 'double-tap');
});

test('queued retry polls the same command instead of enqueueing a second start', async () => {
  const calls = [];
  const posted = [];
  let polls = 0;
  const sandbox = loadClient();
  const fetchImpl = async url => {
    const address = String(url);
    if (address.includes('/api/app-commands/cmd-queued')) {
      polls += 1;
      return {
        ok: true,
        status: 200,
        json: async () => ({ status: polls > 1 ? 'done' : 'pending', detail: '' }),
      };
    }
    return optionsFetch(calls)(url);
  };
  let now = 0;
  const controller = sandbox.MuxChats.createStartChat({
    base: '',
    discoveryBase: '/multiplex/pc/api/discovery',
    fetch: fetchImpl,
    postIntent: async (url, payload, prefix) => {
      posted.push({ url, payload, prefix });
      return { ok: true, status: 200, json: async () => ({ id: 'cmd-queued', intentId: 'stable-intent' }) };
    },
    navigate: async () => {},
    sleep: async () => { now += 2; },
    now: () => now,
    pollIntervalMs: 1,
    pollTimeoutMs: 1,
  });
  await controller.load();
  controller.setField('name', 'queued-retry');
  controller.setField('tool', 'codex');
  controller.setField('workspaceId', 'workspace-1');

  const queued = await controller.submit();
  const done = await controller.submit();

  assert.equal(queued.state, 'queued');
  assert.equal(done.state, 'done');
  assert.equal(posted.length, 1, 'retry must keep polling the original command');
  assert.equal(done.id, 'cmd-queued');
});

test('chats page exposes the plus entry point and start dialog contract', () => {
  for (const id of [
    'startchatbtn', 'startdlg', 'startform', 'startname', 'startdeck', 'startcollection',
    'startcollectionnew', 'startcheckpoint', 'starttool', 'startworkspace', 'startsubfolder',
    'startphrase', 'startstatus', 'startsubmit',
  ]) assert.match(chatsHtml, new RegExp(`id="${id}"`), `missing #${id}`);
  assert.match(chatsHtml, /<script src="intent-journal\.js"><\/script>/);
  assert.ok(chatsHtml.indexOf('intent-journal.js') < chatsHtml.indexOf('picker.js'));
  assert.ok(chatsHtml.indexOf('picker.js') < chatsHtml.indexOf('chats.js'));
  assert.match(chatsHtml, /name="viewport"[^>]*width=device-width/);
});
