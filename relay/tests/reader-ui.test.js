// Verifier for the reader view (relay/public/reader.html + reader.js).
//
// It vm-loads the SHIPPED reader.js against a hand-rolled document stub (no jsdom dependency), so every
// assertion below is about the file the browser actually downloads. Three things are under test:
//   1. rendering — fixture pages become one node per message, role-classed, tool calls collapsed;
//   2. refresh   — the enqueue/poll/reload round trip, with fetch + postIntent mocked;
//   3. trust     — a 200 from the enqueue is NEVER reported as success. Only a terminal command record,
//                  which only the host-credentialed bridge can set, counts as "fetched".
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const PUBLIC = path.join(__dirname, '..', 'public');
const readerSource = fs.readFileSync(path.join(PUBLIC, 'reader.js'), 'utf8');
const readerHtml = fs.readFileSync(path.join(PUBLIC, 'reader.html'), 'utf8');
const indexHtml = fs.readFileSync(path.join(PUBLIC, 'index.html'), 'utf8');
const serverSource = fs.readFileSync(path.join(__dirname, '..', 'server.js'), 'utf8');

// --- minimal document stub -------------------------------------------------------------------------
// Only what reader.js touches: createElement, className, textContent, setAttribute, appendChild.
function makeElement(tag) {
  const node = {
    tagName: String(tag).toLowerCase(),
    className: '',
    children: [],
    attrs: {},
    _text: '',
    appendChild(child) { this.children.push(child); return child; },
    setAttribute(key, value) { this.attrs[String(key)] = String(value); },
    getAttribute(key) { return this.attrs[String(key)]; },
    classList() { return String(this.className).split(/\s+/).filter(Boolean); },
    hasClass(name) { return this.classList().includes(name); },
  };
  Object.defineProperty(node, 'textContent', {
    get() { return node.children.length ? node.children.map(c => c.textContent).join('') : node._text; },
    set(value) { node._text = String(value); node.children.length = 0; },
  });
  return node;
}
const makeDoc = () => ({ createElement: makeElement });
const descend = node => node.children.flatMap(child => [child, ...descend(child)]);
const bodyOf = node => descend(node).find(c => c.hasClass('body'));

// --- loader ----------------------------------------------------------------------------------------
function loadReader(extra) {
  const sandbox = Object.assign({ console, module: { exports: {} }, setTimeout, Math, Date, JSON }, extra || {});
  sandbox.globalThis = sandbox;
  vm.createContext(sandbox);
  vm.runInContext(readerSource, sandbox, { filename: 'reader.js' });
  assert.ok(sandbox.MuxReader, 'reader.js must publish global.MuxReader');
  return sandbox.MuxReader;
}

const PAGE_ONE = {
  schemaVersion: 1, sessionId: 's1', page: 2, pages: 2, availablePages: [1, 2],
  messages: [
    { role: 'user', text: 'add a reader view', ts: 1000 },
    { role: 'assistant', text: 'on it', ts: 2000 },
    { role: 'tool_use', text: 'Read reader.js\nlines 1-40', ts: 3000 },
  ],
};
const PAGE_TWO = {
  schemaVersion: 1, sessionId: 's1', page: 1, pages: 2, availablePages: [1, 2],
  messages: [
    { role: 'system', text: 'context compacted', ts: 4000 },
    { role: 'assistant', text: 'done — newest message', ts: 5000 },
  ],
};

// ==== 1. rendering ==================================================================================

test('fixture pages render one role-classed node per message', () => {
  const reader = loadReader();
  const doc = makeDoc();
  const mount = makeElement('main');
  const result = reader.render(doc, mount, { pages: [PAGE_ONE, PAGE_TWO] });

  assert.equal(result.count, 5);
  assert.equal(mount.children.length, 5, 'one node per message');
  assert.deepEqual(
    mount.children.map(n => n.classList().find(c => c.startsWith('role-'))),
    ['role-user', 'role-assistant', 'role-tool', 'role-system', 'role-assistant'],
  );
  mount.children.forEach(n => assert.ok(n.hasClass('msg'), 'every message node carries .msg'));
  // the raw role survives for styling/debugging even when it is normalized for the class
  assert.equal(mount.children[2].getAttribute('data-role'), 'tool_use');
});

test('tool calls render collapsed as <details> with a summary; prose does not', () => {
  const reader = loadReader();
  const doc = makeDoc();
  const mount = makeElement('main');
  reader.render(doc, mount, { pages: [PAGE_ONE] });

  const tool = mount.children[2];
  assert.equal(tool.tagName, 'details', 'a tool call must be collapsible');
  assert.ok(tool.hasClass('tool-call'));
  const head = tool.children[0];
  assert.equal(head.tagName, 'summary', 'the collapsed head must be a <summary>');
  assert.match(head.textContent, /Read reader\.js/, 'summary previews the first line');
  assert.match(head.textContent, /tool call/);

  assert.equal(mount.children[0].tagName, 'article', 'prose messages are not collapsed');
  assert.equal(mount.children[0].children[0].tagName, 'div');
});

test('messages read newest-last regardless of page arrival order', () => {
  const reader = loadReader();
  const doc = makeDoc();
  const mount = makeElement('main');
  reader.render(doc, mount, { pages: [PAGE_TWO, PAGE_ONE] });   // out of order on purpose

  const texts = mount.children.map(n => bodyOf(n).textContent);
  assert.equal(texts[0], 'add a reader view', 'oldest first');
  assert.equal(texts[texts.length - 1], 'done — newest message', 'newest last');
});

test('transcript text is injected as text, never as markup', () => {
  const reader = loadReader();
  const doc = makeDoc();
  const mount = makeElement('main');
  const nasty = '<img src=x onerror=alert(1)> & <script>boom()</script>';
  reader.render(doc, mount, { pages: [{ page: 1, messages: [{ role: 'user', text: nasty, ts: 1 }] }] });

  assert.equal(bodyOf(mount.children[0]).textContent, nasty, 'preserved verbatim as text');
  assert.equal(mount.children[0].innerHTML, undefined, 'renderer never assigns innerHTML');
  assert.doesNotMatch(readerSource, /innerHTML\s*=/, 'reader.js must never assign innerHTML');
});

test('an empty transcript renders the explanatory empty state, not a blank page', () => {
  const reader = loadReader();
  const doc = makeDoc();
  const mount = makeElement('main');
  const result = reader.render(doc, mount, { pages: [] });

  assert.equal(result.count, 0);
  assert.equal(mount.children.length, 1);
  assert.ok(mount.children[0].hasClass('fallback'));
  assert.match(mount.children[0].textContent, /Press Refresh/);
});

// ==== 2. offline fallback ===========================================================================

test('a PC-bridge-offline view renders the helpful fallback text', () => {
  const reader = loadReader();
  const doc = makeDoc();
  const mount = makeElement('main');
  const result = reader.render(doc, mount, { offline: true, health: { bridgeLive: false, runningAgeMs: 42000 } });

  assert.equal(result.offline, true);
  assert.ok(mount.children[0].hasClass('offline'), 'offline note is class-tagged for styling');
  const text = mount.children.map(c => c.textContent).join(' ');
  assert.match(text, /PC is not reachable/);
  assert.match(text, /Refresh/, 'tells the human what to do next');
  assert.match(text, /Last seen 42s ago/, 'shows how stale the bridge is');
});

test('readHealth reports the bridge dark when /api/projects says so or is unreachable', async () => {
  const reader = loadReader();
  reader.base = '';
  reader.fetch = async () => ({ ok: true, json: async () => ({ appLive: true, bridgeLive: false, live: false }) });
  assert.equal((await reader.readHealth()).bridgeLive, false);

  reader.fetch = async () => { throw new Error('ECONNREFUSED'); };
  const dead = await reader.readHealth();
  assert.equal(dead.bridgeLive, false);
  assert.equal(dead.unreachable, true);
});

// ==== 3. refresh round trip + trust =================================================================

// Drives reader.refresh with a scripted relay. `statuses` is the sequence /api/app-commands/:id returns.
function harness(options) {
  const opts = { mint: { scheme: 'mux-principal-v1', proof: 'fixture-grant' }, ...(options || {}) };
  const calls = { transcripts: 0, enqueued: [], polls: 0 };
  const statuses = opts.statuses ? opts.statuses.slice() : [{ status: 'done', detail: '2 pages fetched' }];
  let clock = 1_000_000;

  const reader = loadReader({ location: { pathname: '/reader.html', origin: 'https://relay.example' } });
  reader.base = '';
  reader.now = () => clock;
  reader.sleep = ms => { clock += Number(ms) || 0; return Promise.resolve(); };   // fake clock: no real waiting

  reader.fetch = async (url, init) => {
    if (url.includes('/api/projects')) {
      return { ok: true, status: 200, json: async () => ({ bridgeLive: opts.bridgeLive !== false, appLive: true }) };
    }
    if (url.includes('/api/principal-auth')) {
      if (opts.mint) return { ok: true, status: 200, json: async () => opts.mint };
      return { ok: false, status: 404, json: async () => ({ error: 'not found' }) };
    }
    if (url.includes('/api/app-commands/')) {
      calls.polls += 1;
      const next = statuses.length > 1 ? statuses.shift() : statuses[0];
      return { ok: true, status: 200, json: async () => Object.assign({ id: 'cmd-1' }, next) };
    }
    if (url.includes('/api/transcripts/')) {
      calls.transcripts += 1;
      if (opts.noPages) return { ok: false, status: 404, json: async () => ({ error: 'no transcript pages for this session' }) };
      return { ok: true, status: 200, json: async () => (url.includes('page=2') ? PAGE_ONE : PAGE_TWO) };
    }
    throw new Error('unexpected fetch ' + url);
  };
  reader.postIntent = async (url, payload, prefix) => {
    calls.enqueued.push({ url, payload, prefix });
    if (opts.enqueueError) return { ok: false, status: 400, json: async () => ({ error: opts.enqueueError }) };
    return { ok: true, status: 200, json: async () => ({ id: 'cmd-1', status: 'queued' }) };
  };
  return { reader, calls };
}

test('refresh enqueues a transcriptfetch and re-loads every page after a bridge-acked done', async () => {
  const { reader, calls } = harness();
  const result = await reader.refresh('s1', { timeoutMs: 30000, intervalMs: 1000 });

  assert.equal(result.ok, true);
  assert.equal(result.verified, true, 'success is only claimed on a bridge-signed terminal record');
  assert.equal(calls.enqueued.length, 1, 'exactly one intent per refresh');
  assert.equal(calls.enqueued[0].payload.type, 'transcriptfetch');
  assert.equal(calls.enqueued[0].payload.sessionId, 's1');
  assert.equal(calls.enqueued[0].url, '/api/app-commands');
  assert.equal(result.pages.length, 2, 'follows availablePages and pulls page 2 as well');

  // the re-fetched pages render, which is the point of the round trip
  const doc = makeDoc();
  const mount = makeElement('main');
  assert.equal(reader.render(doc, mount, result).count, 5);
});

test('TRUST: a 200 from the enqueue is "requested", never "fetched"', async () => {
  const { reader, calls } = harness({ statuses: [{ status: 'queued' }] });   // bridge never acks
  const result = await reader.refresh('s1', { timeoutMs: 5000, intervalMs: 1000 });

  assert.equal(result.ok, false, 'an accepted enqueue must not report success');
  assert.equal(result.requested, true);
  assert.equal(result.pending, true);
  assert.notEqual(result.verified, true);
  assert.ok(calls.polls > 0, 'it actually waited on the bridge');
  assert.equal(calls.transcripts, 0, 'no page reload is claimed without a terminal record');
});

test('TRUST: a bridge-acked failure surfaces as failure, with its detail', async () => {
  const { reader } = harness({ statuses: [{ status: 'failed', detail: 'session log not found' }] });
  const result = await reader.refresh('s1', { timeoutMs: 30000, intervalMs: 1000 });

  assert.equal(result.ok, false);
  assert.equal(result.failed, true);
  assert.equal(result.requested, true);
  assert.match(result.detail, /session log not found/);
});

test('refresh does not enqueue anything while the PC bridge is offline', async () => {
  const { reader, calls } = harness({ bridgeLive: false });
  const result = await reader.refresh('s1', { timeoutMs: 30000, intervalMs: 1000 });

  assert.equal(result.ok, false);
  assert.equal(result.offline, true);
  assert.equal(calls.enqueued.length, 0, 'no intent is minted against a dark bridge');
});

test('the enqueue carries a principalAuth envelope the relay will accept', async () => {
  // Mirrors the relay's own guards: non-empty object, <=4096B of JSON, and no forbidden path/command key
  // anywhere in the body (server.js scans FORBIDDEN_REMOTE_KEYS recursively over the whole request).
  const forbiddenBlock = serverSource.match(/const FORBIDDEN_REMOTE_KEYS = new Set\(\[([\s\S]*?)\]\)/);
  assert.ok(forbiddenBlock, 'server.js must still declare FORBIDDEN_REMOTE_KEYS');
  const forbidden = new Set((forbiddenBlock[1].match(/'([^']+)'/g) || []).map(s => s.slice(1, -1)));
  assert.ok(forbidden.has('command') && forbidden.has('path'), 'sanity: parsed the real key set');

  const walk = value => {
    if (!value || typeof value !== 'object') return [];
    if (Array.isArray(value)) return value.flatMap(walk);
    return Object.entries(value).flatMap(([k, v]) => [String(k).toLowerCase(), ...walk(v)]);
  };

  for (const mint of [{ scheme: 'mux-principal-v1', proof: 'sig:abc', issuedAt: 1 }]) {
    const { reader, calls } = harness(mint ? { mint } : {});
    await reader.refresh('s1', { timeoutMs: 30000, intervalMs: 1000 });
    const payload = calls.enqueued[0].payload;

    assert.equal(typeof payload.principalAuth, 'object');
    assert.ok(payload.principalAuth && Object.keys(payload.principalAuth).length, 'envelope must be non-empty');
    assert.ok(JSON.stringify(payload).length <= 4096, 'stays under the relay 4096B envelope cap');
    const hit = walk(payload).find(key => forbidden.has(key));
    assert.equal(hit, undefined, 'no forbidden path/command-shaped key: ' + hit);
  }
});

test('missing transcript authorization refuses without enqueue or polling', async () => {
  const { reader, calls } = harness({ mint: null });
  const result = await reader.refresh('s1', { timeoutMs: 30000, intervalMs: 1000 });
  assert.equal(result.ok, false);
  assert.match(result.error, /authorization unavailable/i);
  assert.equal(calls.enqueued.length, 0);
  assert.equal(calls.polls, 0);
  assert.equal(calls.transcripts, 0);
});

test('loadPages treats a missing transcript as expired rather than an error', async () => {
  const { reader } = harness({ noPages: true });
  const view = await reader.loadPages('s1');
  assert.equal(view.pages.length, 0);        // not deepEqual: the array is born in the vm realm
  assert.equal(view.expired, true);

  const doc = makeDoc();
  const mount = makeElement('main');
  reader.render(doc, mount, view);
  assert.match(mount.children[0].textContent, /expired/i);
});

test('failed older page cannot silently produce a successful partial transcript', async () => {
  const reader = loadReader();
  reader.base = '';
  reader.fetch = async url => url.includes('?page=2')
    ? { ok: false, status: 503 }
    : { ok: true, json: async () => PAGE_TWO };
  await assert.rejects(reader.loadPages('s1'), /transcript page 2 HTTP 503/);
});

// ==== 4. the page itself, and its entry points ======================================================

test('reader.html is a standalone owner-gated page wired to reader.js', () => {
  assert.ok(fs.existsSync(path.join(PUBLIC, 'reader.html')), 'served by express.static from public/');
  assert.match(readerHtml, /<script src="reader\.js"><\/script>/);
  assert.match(readerHtml, /<script src="intent-journal\.js"><\/script>/, 'refresh goes through postIntent');
  // strip comments first — reader.html *explains* in prose that it is not the terminal page
  const code = readerHtml.replace(/<!--[\s\S]*?-->/g, '').replace(/^\s*\/\/.*$/gm, '');
  assert.doesNotMatch(code, /xterm|new WebSocket/, 'the reader must not drag in the live terminal');
  assert.match(readerHtml, /name="viewport"[^>]*width=device-width/, 'it is read on a phone');
  // the role classes reader.js emits must all be styled, or bubbles render undifferentiated
  for (const cls of ['role-user', 'role-assistant', 'role-tool', 'role-system']) {
    assert.ok(readerHtml.includes('.' + cls), 'reader.html styles .' + cls);
  }
  assert.match(readerHtml, /details\.tool-call/, 'collapsed tool calls are styled');
  assert.match(readerHtml, /plaintext/i, 'the plaintext-exposure decision is disclosed to the reader');
});

test('index.html offers "Read as transcript" from a session tab and from a chat row', () => {
  assert.match(indexHtml, /function openReader\(/, 'the entry point is defined');
  assert.match(indexHtml, /reader\.html\?session=/, 'it navigates to the reader page with the session id');

  // tab kebab menu
  assert.match(indexHtml, /data-a="reader"/);
  assert.match(indexHtml, /a==='reader'\s*\)\s*openReader\(/);
  // chat/history picker rows
  assert.match(indexHtml, /textContent='Read as transcript'/);
  assert.equal((indexHtml.match(/openReader\(/g) || []).length, 3, 'defined once, invoked from both entry points');
});
