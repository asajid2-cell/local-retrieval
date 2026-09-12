// The reader page has two pieces of glue that no other suite EXECUTES: the inline bootstrap IIFE at the
// bottom of public/reader.html (query param -> MuxReader -> status text -> #refresh wiring) and openReader()
// in public/index.html (the 'Read as transcript' entry point). reader-ui.test.js deliberately fakes only what
// reader.js touches, so a typo in either would ship silently. This file runs BOTH against a strict DOM shim:
// every shim object is Proxy-backed and THROWS on any property it does not implement, so a real-DOM-only API
// or a misspelled id fails loudly instead of quietly evaluating to undefined. No new dependency (no jsdom).
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const readerHtml = fs.readFileSync(path.join(__dirname, '..', 'public', 'reader.html'), 'utf8');
const indexHtml = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');

// ---- extraction: the shipped bytes, not a copy ----------------------------------------------------------
function extractBootstrap(){
  const include = readerHtml.indexOf('<script src="reader.js">');
  assert.notEqual(include, -1, 'reader.html no longer includes reader.js');
  const open = readerHtml.indexOf('<script>', include);
  assert.notEqual(open, -1, 'no inline <script> after the reader.js include');
  const bodyStart = open + '<script>'.length;
  const close = readerHtml.indexOf('</script>', bodyStart);
  assert.notEqual(close, -1, 'unterminated inline <script> in reader.html');
  const body = readerHtml.slice(bodyStart, close);
  assert.match(body, /MuxReader/, 'extracted the wrong <script> block');
  return body;
}

// Same brace-matching technique wheel-scroll.test.js uses on index.html.
function extractIndexFn(name){
  const start = indexHtml.indexOf('function ' + name + '(');
  assert.notEqual(start, -1, 'missing function ' + name + ' in index.html');
  let depth = 0, end = -1;
  for(let i = indexHtml.indexOf('{', start); i < indexHtml.length; i++){
    if(indexHtml[i] === '{') depth++;
    else if(indexHtml[i] === '}'){ depth--; if(depth === 0){ end = i + 1; break; } }
  }
  assert.notEqual(end, -1, 'unbalanced braces for ' + name);
  return indexHtml.slice(start, end);
}

// ---- strict DOM shim ------------------------------------------------------------------------------------
// A get/set on anything not modelled here is a test failure, by design.
function strict(target, label){
  return new Proxy(target, {
    get(t, prop){
      if (typeof prop === 'symbol') return t[prop];
      if (!(prop in t)) throw new Error(label + ': unimplemented DOM property read "' + String(prop) + '"');
      return t[prop];
    },
    set(t, prop, value){
      if (typeof prop === 'symbol'){ t[prop] = value; return true; }
      if (!(prop in t)) throw new Error(label + ': unimplemented DOM property write "' + String(prop) + '"');
      t[prop] = value; return true;
    },
    has(t, prop){ return prop in t; },
  });
}

function makeElement(id, listeners){
  const classes = new Set();
  const raw = Object.create(null);
  Object.assign(raw, {
    id,
    tagName: 'DIV',
    textContent: '',
    className: '',
    innerHTML: '',
    disabled: false,
    value: 'all',
    querySelectorAll: () => [],
    dataset: Object.create(null),
    children: [],
    attrs: Object.create(null),
    classList: strict(Object.assign(Object.create(null), {
      add: (...c) => c.forEach(x => classes.add(x)),
      remove: (...c) => c.forEach(x => classes.delete(x)),
      toggle: (c, on) => { const want = on === undefined ? !classes.has(c) : !!on; if (want) classes.add(c); else classes.delete(c); return want; },
      contains: (c) => classes.has(c),
    }), 'classList(' + id + ')'),
    setAttribute: (k, v) => { raw.attrs[k] = String(v); },
    appendChild: (child) => { raw.children.push(child); return child; },
    replaceChildren: (...kids) => { raw.children = kids; },
    addEventListener: (type, fn) => {
      const key = id + ':' + type;
      if (!listeners.has(key)) listeners.set(key, []);
      listeners.get(key).push(fn);
    },
  });
  return strict(raw, 'element#' + id);
}

// ids the reader page is entitled to look up; anything else returns null (and blows up on first use).
const READER_IDS = ['transcript', 'status', 'refresh', 'title', 'role', 'start', 'previoususer'];

function makeDom(){
  const listeners = new Map();
  const byId = new Map(READER_IDS.map(id => [id, makeElement(id, listeners)]));
  const doc = strict(Object.assign(Object.create(null), {
    createElement: (tag) => { const el = makeElement('created:' + tag, listeners); return el; },
    getElementById: (id) => byId.has(id) ? byId.get(id) : null,
    querySelector: (sel) => sel.startsWith('#') && byId.has(sel.slice(1)) ? byId.get(sel.slice(1)) : null,
    addEventListener: (type, fn) => {
      const key = 'document:' + type;
      if (!listeners.has(key)) listeners.set(key, []);
      listeners.get(key).push(fn);
    },
  }), 'document');
  const fire = (id, type, ev) => {
    const fns = listeners.get(id + ':' + type) || [];
    assert.ok(fns.length, 'nothing listening for ' + type + ' on #' + id);
    return Promise.all(fns.map(fn => fn(ev || { preventDefault(){}, stopPropagation(){} })));
  };
  return { doc, byId, listeners, fire };
}

const flush = () => new Promise(r => setImmediate(r));

// Run the shipped bootstrap against a recording MuxReader and a stub location.
function bootReader(search, muxOverrides){
  const dom = makeDom();
  const MuxReader = Object.assign({
    loadPages: async (id) => ({ pages: [{ text: 'hi' }] }),
    refresh: async (id) => ({ ok: true, pages: [] }),
    render: (d, mount, view) => {},
  }, muxOverrides || {});
  const rec = { loadPages: [], refresh: [], render: [] };
  const sandbox = {
    document: dom.doc,
    MuxReader: {
      loadPages: (id) => { rec.loadPages.push(id); return MuxReader.loadPages(id); },
      refresh: (id) => { rec.refresh.push(id); return MuxReader.refresh(id); },
      render: (d, m, v) => { rec.render.push({ mount: m, view: v }); return MuxReader.render(d, m, v); },
    },
    location: { search, href: 'http://relay.local/reader.html' + search },
    URLSearchParams,
    console,
    setTimeout,
  };
  vm.runInNewContext(extractBootstrap(), sandbox, { filename: 'reader.html#bootstrap' });
  return { dom, rec, status: () => dom.byId.get('status'), btn: () => dom.byId.get('refresh') };
}

// ---------------------------------------------------------------------------------------------------------
test('(a) ?session=abc123 loads exactly that session and clears the Loading… placeholder', async () => {
  const b = bootReader('?session=abc123&title=My%20Chat');
  await flush();
  assert.deepEqual(b.rec.loadPages, ['abc123'], 'bootstrap did not load exactly the query session id');
  assert.equal(b.rec.refresh.length, 0, 'initial load must not enqueue a PC fetch');
  assert.notEqual(b.status().textContent, 'Loading…', 'status still shows the HTML placeholder');
  assert.match(b.status().textContent, /1 page loaded\./);
  assert.equal(b.dom.byId.get('title').textContent, 'My Chat', '?title= not applied to the heading');
  assert.equal(b.rec.render.length, 1, 'transcript never rendered');
  assert.equal(b.rec.render[0].mount, b.dom.byId.get('transcript'), 'rendered into the wrong mount');
});

test('(b) no session param: empty state, guidance, #refresh disabled, no refresh call', async () => {
  const b = bootReader('');
  await flush();
  assert.equal(b.rec.loadPages.length, 0, 'loaded pages with no session id');
  assert.equal(b.rec.refresh.length, 0, 'asked the PC for a transcript with no session id');
  assert.equal(b.rec.render.length, 1, 'empty state not rendered');
  // Normalize the vm-created object so strict comparison is safe across realms.
  assert.deepEqual(JSON.parse(JSON.stringify(b.rec.render[0].view)), { pages: [] }, 'empty state view is not an empty page list');
  assert.match(b.status().textContent, /No chat selected/);
  assert.equal(b.btn().disabled, true, '#refresh left enabled with nothing to refresh');
});

test('(c) clicking #refresh calls MuxReader.refresh once and cycles the button disabled -> enabled', async () => {
  let release;
  const gate = new Promise(res => { release = res; });
  const b = bootReader('?session=s1', { refresh: () => gate });
  await flush();

  const clicked = b.dom.fire('refresh', 'click');
  await flush();
  assert.deepEqual(b.rec.refresh, ['s1'], 'click did not refresh exactly this session, exactly once');
  assert.equal(b.btn().disabled, true, 'button not disabled while the refresh is in flight');
  assert.match(b.status().textContent, /Asking your PC/);

  release({ ok: true, pages: [], detail: 'Transcript fetched.' });
  await clicked; await flush();
  assert.equal(b.btn().disabled, false, 'button never re-enabled after a successful refresh');
  assert.equal(b.rec.refresh.length, 1, 'refresh fired more than once for one click');
  assert.match(b.status().textContent, /Transcript fetched\./);
});

test('(d) a rejecting refresh surfaces the error AND still re-enables the button (finally path)', async () => {
  const b = bootReader('?session=s1', { refresh: async () => { throw new Error('boom'); } });
  await flush();

  await b.dom.fire('refresh', 'click');
  await flush();
  assert.match(b.status().textContent, /boom/, 'refresh failure never reached the status line');
  assert.equal(b.status().className, 'err', 'failure not styled as an error');
  assert.equal(b.btn().disabled, false, 'button stuck disabled after a rejected refresh');
});

test('(e) openReader builds an encoded reader URL, and refuses to navigate without a session', () => {
  const opened = [];
  const flashed = [];
  const sandbox = {
    base: 'http://relay.local',
    flash: (m) => flashed.push(m),
    window: { open: (url, target) => { opened.push({ url, target }); return { closed: false }; } },
    location: { href: 'http://relay.local/' },
    encodeURIComponent,
    String,
  };
  vm.runInNewContext(extractIndexFn('openReader') + '\nthis._openReader = openReader;', sandbox, { filename: 'index.html#openReader' });

  sandbox._openReader('a/b c', 'My Chat');
  assert.equal(opened.length, 1, 'openReader did not open the reader');
  assert.match(opened[0].url, /reader\.html\?session=/, 'not a reader.html?session= URL');
  assert.match(opened[0].url, /session=a%2Fb%20c/, 'session id not percent-encoded');
  assert.match(opened[0].url, /&title=My%20Chat/, 'title not passed/encoded');
  assert.ok(opened[0].url.startsWith('http://relay.local/'), 'openReader lost the `base` it depends on');
  assert.equal(flashed.length, 0);

  sandbox._openReader('');
  assert.equal(opened.length, 1, 'openReader navigated with no session id');
  assert.equal(sandbox.location.href, 'http://relay.local/', 'openReader fell back to a bare navigation');
  assert.equal(flashed.length, 1, 'no guidance flashed for a tab with no linked chat');
  assert.match(flashed[0], /no linked chat/);
});
