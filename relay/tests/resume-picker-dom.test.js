// Resume-from-archive picker: proving the outcome REACHES THE SCREEN.
//
// resume-picker.test.js drives the picker's logic headless and asserts index.html's markup as strings.
// Neither one ever runs `install()`'s render()/tap() — so a resume that failed or queued could paint
// nothing at all and every existing test would still pass. That silence is the bug this file exists to
// prevent, so every assertion below reads real state off a fake DOM (the text, the tone class, the
// dialog's open flag, the rendered children) and never a spy on the picker's own internals.
//
// No relay, no network: picker.js is loaded in a vm over a minimal `document`, and fetch/postIntent are
// local stubs. install() injects no timing overrides, so the REAL defaults apply — 1200ms poll interval
// against a 4000ms offline deadline — which is why the queued case deliberately costs ~4.8s.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const REPO = path.resolve(__dirname, '..');
const PUBLIC = path.join(REPO, 'public');

// Exactly the selectors install() looks up. Anything else must come back null, so a lookup this stub
// does not model fails loudly instead of quietly handing back a shared do-nothing object.
const SELECTORS = ['#resumedlg', '#resumelist', '#resumeq', '#resumelive', '#resumestatus',
                   '#resumeclose', '#resumebtn'];

// ---- minimal DOM ------------------------------------------------------------------------------

function element(tagName) {
  const classes = new Set();
  const node = {
    tagName: String(tagName).toUpperCase(),
    type: '', className: '', textContent: '', dataset: {}, children: [], onclick: null,
    classList: {
      add: (c) => { classes.add(c); },
      remove: (c) => { classes.delete(c); },
      contains: (c) => classes.has(c),
    },
    appendChild(child) { node.children.push(child); return child; },
  };
  return node;
}

// A live element also records listeners and clears its children when innerHTML is assigned — the two
// behaviours picker.js actually leans on (`box.addEventListener('input', …)`, `list.innerHTML = ''`).
function hostElement() {
  const node = element('div');
  node.listeners = Object.create(null);
  node.addEventListener = (type, fn) => {
    (node.listeners[type] || (node.listeners[type] = [])).push(fn);
  };
  Object.defineProperty(node, 'innerHTML', {
    configurable: true,
    get() { return ''; },
    set() { node.children.length = 0; },
  });
  return node;
}

function fakeDocument() {
  const nodes = new Map();
  for (const sel of SELECTORS) nodes.set(sel, hostElement());

  const dlg = nodes.get('#resumedlg');
  dlg.open = false;
  dlg.showModal = () => { dlg.open = true; };
  dlg.close = () => { dlg.open = false; };

  nodes.get('#resumeq').value = '';

  return {
    querySelector: (sel) => (nodes.has(sel) ? nodes.get(sel) : null),
    createElement: (tag) => element(tag),
  };
}

function fire(node, type) {
  const handlers = node.listeners[type] || [];
  assert.ok(handlers.length, `nothing is listening for "${type}"`);
  for (const fn of handlers) fn({ type, target: node });
}

// ---- mounting ---------------------------------------------------------------------------------

function loadPicker(doc, fetchImpl) {
  const sandbox = { console, setTimeout, clearTimeout, document: doc, fetch: fetchImpl };
  const context = vm.createContext(sandbox);
  vm.runInContext(fs.readFileSync(path.join(PUBLIC, 'picker.js'), 'utf8'), context, { filename: 'picker.js' });
  assert.ok(sandbox.MuxResumePicker, 'picker.js must install MuxResumePicker');
  return sandbox;
}

function mount({ fetch: fetchImpl, postIntent } = {}) {
  const doc = fakeDocument();
  const sandbox = loadPicker(doc, fetchImpl);
  const calls = { flash: [], loadSessions: [], openDialog: [], posted: [], fetched: [] };

  const trackedFetch = async (url, init) => {
    calls.fetched.push(String(url));
    return fetchImpl(String(url), init);
  };
  const trackedPost = async (url, payload, kind) => {
    calls.posted.push({ url: String(url), payload, kind });
    return postIntent(String(url), payload, kind);
  };

  const host = {
    base: '',
    document: doc,
    $: (sel) => doc.querySelector(sel),
    fetch: trackedFetch,
    postIntent: trackedPost,
    loadSessions: async (name) => { calls.loadSessions.push(name); },
    flash: (message) => { calls.flash.push(message); },
    // index.html hands install() its own dialog opener; mirroring that it really opens the dialog is
    // what keeps the open/closed assertions below meaningful.
    openDialog: (sel) => { calls.openDialog.push(sel); doc.querySelector(sel).showModal(); },
  };

  const ui = sandbox.MuxResumePicker.install(host);
  assert.ok(ui, 'install() must mount against this DOM');
  return { doc, ui, calls, $: (sel) => doc.querySelector(sel) };
}

function chatRow(over = {}) {
  return {
    id: 'chat-1', title: 'Cortex planning', tool: 'claude', cwd: 'C:/work/cortex',
    workspaceLabel: 'cortex', updatedAt: 1700000000001, muxName: 'mux-1', resumable: true, ...over,
  };
}

function unexpected(url) { throw new Error(`unexpected request: ${url}`); }

// ---- outcomes reach the screen ------------------------------------------------------------------

test('DONE: the tap paints the resumed name, flashes, selects the tab, and closes the dialog', async () => {
  const chat = chatRow();
  const { ui, calls, $ } = mount({
    postIntent: async () => ({ ok: true, status: 200, json: async () => ({ id: 'cmd-1', intentId: 'i-1' }) }),
    fetch: async (url) => (url.includes('/api/app-commands/cmd-1')
      ? { ok: true, status: 200, json: async () => ({ status: 'done', detail: '' }) }
      : unexpected(url)),
  });

  const dlg = $('#resumedlg');
  dlg.showModal();
  const outcome = await ui.tap(chat);

  assert.equal(outcome.state, 'done');
  const status = $('#resumestatus');
  assert.ok(status.textContent.includes('mux-1'), `status must name the mux: ${status.textContent}`);
  assert.ok(status.className.includes('ok'), `tone must be ok: ${status.className}`);
  assert.equal(calls.flash.length, 1);
  assert.deepEqual(calls.loadSessions, ['mux-1'], 'the freshly hosted tab must be selected');
  assert.equal(dlg.open, false, 'a finished resume must get out of the way');
  assert.equal(calls.posted[0].payload.type, 'startmux');
});

test('FAILED: a refusal reaches the screen whole — both halves, dialog left open', async () => {
  const chat = chatRow();
  const { ui, $ } = mount({
    postIntent: async () => ({
      ok: false,
      status: 409,
      json: async () => ({ error: 'local copy is already running', detail: 'chat-1 is open on AHMED-PC (pid 4242)' }),
    }),
    fetch: async (url) => unexpected(url),
  });

  const dlg = $('#resumedlg');
  dlg.showModal();
  const outcome = await ui.tap(chat);

  assert.equal(outcome.state, 'failed');
  const text = $('#resumestatus').textContent;
  assert.ok(text.includes('local copy is already running'), `lost the error: ${text}`);
  assert.ok(text.includes('chat-1 is open on AHMED-PC (pid 4242)'),
    `the actionable detail must survive, not flatten to a status code: ${text}`);
  assert.ok($('#resumestatus').className.includes('bad'), $('#resumestatus').className);
  assert.equal(dlg.open, true, 'a refused resume must leave the picker up so the user can act');
});

test('QUEUED: an offline PC produces a visible queued sentence, never silence', async () => {
  const chat = chatRow();
  const { ui, $ } = mount({
    postIntent: async () => ({ ok: true, status: 200, json: async () => ({ id: 'cmd-2', intentId: 'i-2' }) }),
    // The command never resolves: this is the PC that is not listening.
    fetch: async () => ({ ok: true, status: 200, json: async () => ({ status: 'pending', detail: '' }) }),
  });

  const outcome = await ui.tap(chat);

  assert.equal(outcome.state, 'queued');
  const status = $('#resumestatus');
  assert.ok(status.textContent.startsWith('Queued: '), `must read as queued: ${status.textContent}`);
  assert.ok(status.textContent.includes('offline'), `must say why: ${status.textContent}`);
  assert.ok(status.className.includes('warn'), status.className);
});

// ---- the list itself reaches the screen ---------------------------------------------------------

const INDEX_ROWS = [
  chatRow({ id: 'chat-1', title: 'Cortex planning', workspaceLabel: 'cortex', cwd: 'C:/work/cortex', muxName: 'mux-1' }),
  chatRow({ id: 'chat-2', title: 'Relay hardening', tool: 'codex', workspaceLabel: 'relay', cwd: 'C:/work/relay', muxName: 'mux-2' }),
  chatRow({ id: 'chat-3', title: 'Half-imported chat', workspaceLabel: 'broken', cwd: 'C:/work/broken', muxName: 'mux-3', resumable: false }),
];

function archiveMount() {
  return mount({
    postIntent: async () => unexpected('postIntent'),
    fetch: async (url) => (url.includes('/remote/api/discovery/chats')
      ? { ok: true, status: 200, json: async () => ({ rows: INDEX_ROWS, total: INDEX_ROWS.length, offset: 0, limit: 100, hasMore: false }) }
      : unexpected(url)),
  });
}

test('RENDER: only offered rows are drawn, typing narrows them, and a dead query still says something', async () => {
  const { ui, calls, $ } = archiveMount();
  const list = $('#resumelist');

  await ui.open();

  assert.deepEqual(calls.openDialog, ['#resumedlg']);
  assert.equal($('#resumedlg').open, true);
  assert.equal(list.children.length, 2, 'a chat the app could not build a resume for must not be offered');
  assert.deepEqual(list.children.map((c) => c.tagName), ['BUTTON', 'BUTTON']);
  assert.deepEqual(list.children.map((c) => c.dataset.chatId), ['chat-1', 'chat-2']);
  assert.equal(list.children[0].children[0].textContent, 'Cortex planning', 'the row must carry its title');
  assert.ok($('#resumelive').textContent.includes('connected'), $('#resumelive').textContent);

  const box = $('#resumeq');
  box.value = 'relay';
  fire(box, 'input');
  assert.equal(list.children.length, 1);
  assert.equal(list.children[0].dataset.chatId, 'chat-2');

  box.value = 'nothing-matches-this';
  fire(box, 'input');
  assert.equal(list.children.length, 1, 'an empty result must still render a line');
  assert.equal(list.children[0].tagName, 'P');
  assert.ok(list.children[0].textContent.includes('No chat matches'), list.children[0].textContent);
});

test('WIRING: the close button actually closes the dialog it was handed', async () => {
  const { ui, $ } = archiveMount();
  await ui.open();
  assert.equal($('#resumedlg').open, true);
  assert.equal(typeof $('#resumebtn').onclick, 'function', 'the open button must be wired');

  $('#resumeclose').onclick();
  assert.equal($('#resumedlg').open, false);
});
