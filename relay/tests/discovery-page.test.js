const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const PUBLIC = path.resolve(__dirname, '..', 'public');

function element(tagName) {
  const node = {
    tagName: String(tagName).toUpperCase(),
    type: '', className: '', textContent: '', value: '', checked: false, disabled: false,
    hidden: false, title: '', dataset: {}, children: [], parentNode: null,
    appendChild(child) { child.parentNode = node; node.children.push(child); return child; },
    removeChild(child) {
      const index = node.children.indexOf(child);
      if (index >= 0) node.children.splice(index, 1);
      child.parentNode = null;
      return child;
    },
    setAttribute(name, value) { node[name] = String(value); },
    querySelector(selector) {
      const isClass = selector.startsWith('.');
      const wanted = isClass ? selector.slice(1) : selector.toUpperCase();
      const stack = node.children.slice();
      while (stack.length) {
        const current = stack.shift();
        if (isClass && String(current.className).split(/\s+/).includes(wanted)) return current;
        if (!isClass && current.tagName === wanted) return current;
        stack.push(...current.children);
      }
      return null;
    },
  };
  Object.defineProperty(node, 'firstChild', { get: () => node.children[0] || null });
  Object.defineProperty(node, 'innerHTML', {
    get: () => node._innerHTML || '',
    set: value => { node._innerHTML = String(value); node.children.length = 0; },
  });
  return node;
}

function fakeDocument() {
  const ids = [
    'chatlist', 'status', 'state', 'tagchips', 'phrasechips', 'project', 'pager',
    'pageinfo', 'prev', 'next', 'searchform', 'query', 'agent', 'sort', 'date',
    'minimum', 'hidden', 'matchall', 'deepresults', 'deepheading', 'deeplist',
  ];
  const nodes = new Map(ids.map(id => [`#${id}`, element(id === 'searchform' ? 'form' : 'div')]));
  nodes.get('#query').value = '';
  nodes.get('#sort').value = 'recent';
  nodes.get('#minimum').value = '0';
  return {
    querySelector: selector => nodes.get(selector) || null,
    createElement: tag => element(tag),
    nodes,
  };
}

// The live filter fires on a real timer, so the harness owns the clock instead of waiting 150 ms.
function fakeClock() {
  let next = 0;
  const timers = new Map();
  return {
    setTimeout(fn, ms) { const id = ++next; timers.set(id, { fn, ms }); return id; },
    clearTimeout(id) { timers.delete(id); },
    pending() { return timers.size; },
    pendingDelay() { return Array.from(timers.values()).map(t => t.ms); },
    runAll() {
      const due = Array.from(timers.values());
      timers.clear();
      due.forEach(t => t.fn());
    },
  };
}

function loadClient(extra = {}) {
  const sandbox = {
    console, URLSearchParams, URL, Intl, Date, Set, Array, Promise,
    ...extra,
  };
  vm.runInContext(
    fs.readFileSync(path.join(PUBLIC, 'chats.js'), 'utf8'),
    vm.createContext(sandbox),
    { filename: 'chats.js' },
  );
  assert.ok(sandbox.MuxChats);
  return sandbox;
}

const PAGE = {
  query: '', offset: 0, limit: 40, total: 2, hasMore: false,
  rows: [
    {
      id: 'chat-1', title: 'Web parity implementation', tool: 'codex',
      workspaceLabel: 'mux-local-retrieval', updatedAt: '2026-08-02T10:00:00Z',
      tags: ['active', 'web'], phrases: ['web-parity'], userMsgCount: 12,
      pinned: true, resumable: true, muxName: 'web-parity-chat1',
    },
    {
      id: 'chat-2', title: 'A-very-long-title-without-breaks-that-must-wrap-cleanly-on-a-phone',
      tool: 'claude', workspaceLabel: 'long-workspace-label', updatedAt: '2026-08-01T10:00:00Z',
      tags: [], phrases: [], userMsgCount: 3, pinned: false, resumable: false, muxName: 'chat-2',
    },
  ],
};

const FACETS = {
  total: 2, hidden: 7,
  tags: [{ value: 'active', count: 1 }, { value: 'web', count: 1 }],
  phrases: [{ value: 'web-parity', count: 1 }],
  projects: [{ id: 'project-web', label: 'Web Project', count: 2 }],
};

// One chat the filtered page does NOT contain, so the deep section has something of its own to show.
const DEEP = {
  query: 'promicro venpod', limit: 40, count: 1, ran: true,
  rows: [{
    id: 'chat-9', title: 'Deep transcript match', tool: 'codex', workspaceLabel: 'deep-workspace',
    updatedAt: '2026-08-02T10:00:00Z', userMsgCount: 4, pinned: false, resumable: true,
    muxName: 'chat-9', snippet: 'promicro venpod magenta', provenance: 'chat content',
    matchedTerms: 'promicro, venpod', score: 62, revision: 'rev-9', archived: false,
  }],
};

const SEARCH_PREFIX = '/multiplex/pc/api/discovery/search';

function discoveryFetch(over = {}) {
  return async url => {
    const text = String(url);
    if (over.onCall) over.onCall(text);
    if (text.startsWith(SEARCH_PREFIX)) {
      if (over.search) return over.search(text);
      return { ok: true, status: 200, json: async () => over.deep || DEEP };
    }
    return { ok: true, status: 200, json: async () => text.includes('/facets') ? FACETS : PAGE };
  };
}

async function mount(extra = {}, over = {}) {
  const doc = fakeDocument();
  const clock = fakeClock();
  const calls = [];
  const sandbox = loadClient({
    setTimeout: clock.setTimeout, clearTimeout: clock.clearTimeout, ...extra,
  });
  const mounted = sandbox.MuxChats.install({
    document: doc,
    fetch: over.fetch || discoveryFetch({ ...over, onCall: text => calls.push(text) }),
  });
  await mounted.controller.load(true);
  return { doc, clock, calls, mounted, sandbox };
}

function submit(doc) { return doc.nodes.get('#searchform').onsubmit({ preventDefault() {} }); }

test('query parameters encode the frozen discovery contract', () => {
  const { MuxChats } = loadClient();
  const params = MuxChats.queryParams({
    q: '[web-parity]', include: new Set(['active', 'ACTIVE']), exclude: new Set(['done']),
    matchAll: true, agent: 'codex', date: 'week', minUserMessages: 5,
    showHidden: true, showAutomationWorkers: true, archived: 'archived', project: 'project-web', sort: 'created-newest',
  }, 40, 40);

  assert.equal(params.get('q'), '[web-parity]');
  assert.equal(params.get('include'), 'active,ACTIVE');
  assert.equal(params.get('exclude'), 'done');
  assert.equal(params.get('match'), 'all');
  assert.equal(params.get('agent'), 'codex');
  assert.equal(params.get('date'), 'week');
  assert.equal(params.get('minUserMessages'), '5');
  assert.equal(params.get('showHidden'), 'true');
  assert.equal(params.get('showAutomationWorkers'), 'true');
  assert.equal(params.get('archived'), 'archived');
  assert.equal(params.get('project'), 'project-web');
  assert.equal(params.get('sort'), 'created-newest');
  assert.equal(params.get('offset'), '40');
  assert.equal(params.get('limit'), '40');
});

test('status text explicitly distinguishes loading, empty, online, and PC failure', () => {
  const { MuxChats } = loadClient();
  assert.equal(MuxChats.statusFor({ loading: true }).tone, 'loading');
  assert.match(MuxChats.statusFor({ total: 0 }).text, /No chats match/);
  assert.match(MuxChats.statusFor({ total: 12 }).text, /12 matching chats/);
  assert.match(MuxChats.statusFor({ error: 'HTTP 502' }).text, /PC archive unavailable/);
});

test('DOM smoke renders discovery rows, facets, disabled resume, and delegates resume', async () => {
  const doc = fakeDocument();
  const resumed = [];
  const fetched = [];
  const sandbox = loadClient({
    MuxResumePicker: {
      createPicker: () => ({
        state: {},
        resume: async chat => { resumed.push(chat.id); return { state: 'done', muxName: chat.muxName }; },
      }),
    },
    postIntent: async () => { throw new Error('picker stub owns resume'); },
  });
  const fetch = async url => {
    fetched.push(String(url));
    return {
      ok: true, status: 200,
      json: async () => String(url).includes('/facets') ? FACETS : PAGE,
    };
  };

  const mounted = sandbox.MuxChats.install({ document: doc, fetch, postIntent: sandbox.postIntent });
  await mounted.controller.load(true);

  assert.ok(fetched.some(url => url.startsWith('/multiplex/pc/api/discovery/chats?')));
  assert.ok(fetched.some(url => url.startsWith('/multiplex/pc/api/discovery/facets?')));
  const list = doc.nodes.get('#chatlist');
  assert.equal(list.children.length, 2);
  assert.equal(list.children[0].dataset.chatId, 'chat-1');
  assert.equal(list.children[1].querySelector('.resume').disabled, true);
  assert.equal(doc.nodes.get('#tagchips').children.length, 2);
  assert.equal(doc.nodes.get('#phrasechips').children[0].textContent, '[web-parity]');
  assert.equal(doc.nodes.get('#project').children.length, 2);
  assert.equal(doc.nodes.get('#status').className, 'status online');

  const resume = list.children[0].querySelector('.resume');
  await resume.onclick();
  assert.deepEqual(resumed, ['chat-1']);
  assert.match(list.children[0].querySelector('.rowstatus').textContent, /Resumed as web-parity-chat1/);

  await doc.nodes.get('#tagchips').children[0].onclick();
  assert.equal(mounted.controller.state.filters.include.has('active'), true);
});

test('ALL tag control preserves selected tags and resets pagination', async () => {
  const doc = fakeDocument(), fetched = [];
  const { MuxChats } = loadClient();
  const mounted = MuxChats.install({ document: doc, fetch: async url => {
    fetched.push(String(url));
    return { ok: true, json: async () => String(url).includes('/facets') ? FACETS : PAGE };
  } });
  await mounted.controller.load(true);
  mounted.controller.state.filters.include = new Set(['active', 'web']);
  mounted.controller.state.filters.exclude = new Set(['done']);
  mounted.controller.state.offset = 40;
  const control = doc.nodes.get('#matchall');
  control.checked = true;
  assert.equal(typeof control.onchange, 'function');
  control.onchange();
  const query = new URL(fetched.at(-2), 'http://fixture').searchParams;
  assert.equal(query.get('match'), 'all');
  assert.equal(query.get('include'), 'active,web');
  assert.equal(query.get('exclude'), 'done');
  assert.equal(query.get('offset'), '0');
  control.checked = false;
  control.onchange();
  assert.equal(new URL(fetched.at(-2), 'http://fixture').searchParams.get('match'), 'any');
});

test('typing filters live on the app\'s 150 ms trailing debounce', async () => {
  const { doc, clock, calls, mounted } = await mount();
  const box = doc.nodes.get('#query');
  assert.equal(typeof box.oninput, 'function', 'the query box must filter as you type');

  const before = calls.length;
  for (const value of ['w', 'we', 'web', 'web-', 'web-p']) { box.value = value; box.oninput(); }
  assert.equal(clock.pending(), 1, 'a five-key burst must collapse into one pending pass');
  assert.deepEqual(clock.pendingDelay(), [150]);
  assert.equal(calls.length, before, 'typing must not fire a request before the burst settles');

  clock.runAll();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(mounted.controller.state.filters.q, 'web-p');
  assert.equal(calls.length - before, 2, 'one settled burst is exactly one discovery pass');

  // Settling on the text that is already filtered must not cost another pass.
  box.value = 'web-p';
  box.oninput();
  clock.runAll();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(calls.length - before, 2);
});

test('Enter filters and scans whole transcripts in one deliberate pass', async () => {
  const { doc, calls, mounted } = await mount();
  doc.nodes.get('#query').value = 'promicro venpod';
  await submit(doc);

  const deepCalls = calls.filter(url => url.startsWith(SEARCH_PREFIX));
  assert.equal(deepCalls.length, 1, 'only Enter pays for the transcript scan');
  const params = new URL(deepCalls[0], 'http://fixture').searchParams;
  assert.equal(params.get('q'), 'promicro venpod');
  assert.equal(params.get('limit'), '40');
  assert.equal(params.get('showHidden'), null);

  assert.equal(mounted.controller.state.deep.query, 'promicro venpod');
  assert.equal(mounted.controller.state.deep.count, 1);
  assert.equal(mounted.controller.state.deep.ran, true);

  assert.equal(doc.nodes.get('#deepresults').hidden, false);
  const extras = doc.nodes.get('#deeplist');
  assert.equal(extras.children.length, 1);
  assert.equal(extras.children[0].dataset.chatId, 'chat-9');
  assert.equal(extras.children[0].querySelector('.snippet').textContent, 'promicro venpod magenta');
  assert.match(extras.children[0].querySelector('.meta').textContent, /matched in chat content/);
  assert.match(extras.children[0].querySelector('.meta').textContent, /words: promicro, venpod/);
  assert.match(doc.nodes.get('#deepheading').textContent, /1 more chat matched "promicro venpod" inside the transcript/);
});

test('deep search never dresses a non-answer up as "no matches"', async () => {
  // Too short to be a phrase: nothing is sent at all.
  {
    const { doc, calls } = await mount();
    doc.nodes.get('#query').value = 'short';
    await submit(doc);
    assert.equal(calls.filter(url => url.startsWith(SEARCH_PREFIX)).length, 0);
    assert.match(doc.nodes.get('#deepheading').textContent, /at least 8 characters/);
  }
  // An older PC build does not have the route.
  {
    const { doc } = await mount({}, {
      fetch: discoveryFetch({ search: () => ({ ok: false, status: 404, json: async () => ({}) }) }),
    });
    doc.nodes.get('#query').value = 'promicro venpod';
    await submit(doc);
    assert.match(doc.nodes.get('#deepheading').textContent, /cannot search inside transcripts/);
    assert.doesNotMatch(doc.nodes.get('#deepheading').textContent, /No chat contains/);
  }
  // The query had no words worth scanning, so the scan never ran.
  {
    const { doc } = await mount({}, {
      fetch: discoveryFetch({ deep: { query: 'the and you are', limit: 40, count: 0, ran: false, rows: [] } }),
    });
    doc.nodes.get('#query').value = 'the and you are';
    await submit(doc);
    assert.match(doc.nodes.get('#deepheading').textContent, /No full-transcript scan ran/);
    assert.doesNotMatch(doc.nodes.get('#deepheading').textContent, /No chat contains/);
  }
  // The scan ran and matched nothing — only THIS case may say so.
  {
    const { doc } = await mount({}, {
      fetch: discoveryFetch({ deep: { query: 'zeppelin airship', limit: 40, count: 0, ran: true, rows: [] } }),
    });
    doc.nodes.get('#query').value = 'zeppelin airship';
    await submit(doc);
    assert.match(doc.nodes.get('#deepheading').textContent, /No chat contains "zeppelin airship"/);
  }
});

test('a new keystroke drops results that belonged to the previous phrase', async () => {
  const { doc, clock, mounted } = await mount();
  doc.nodes.get('#query').value = 'promicro venpod';
  await submit(doc);
  assert.equal(mounted.controller.state.deep.rows.length, 1);

  const box = doc.nodes.get('#query');
  box.value = 'promicro venpod x';
  box.oninput();
  clock.runAll();
  assert.equal(mounted.controller.state.deep.query, '');
  assert.equal(mounted.controller.state.deep.rows.length, 0);
  assert.equal(doc.nodes.get('#deepresults').hidden, true);
  assert.equal(doc.nodes.get('#deeplist').children.length, 0);
});

test('deep rows already in the filtered list are not shown twice', async () => {
  const { doc } = await mount({}, {
    fetch: discoveryFetch({ deep: { ...DEEP, rows: [{ ...DEEP.rows[0], id: 'chat-1' }] } }),
  });
  doc.nodes.get('#query').value = 'promicro venpod';
  await submit(doc);
  assert.equal(doc.nodes.get('#deeplist').children.length, 0);
  assert.match(doc.nodes.get('#deepheading').textContent, /already listed above/);
});

test('an empty filtered list does not deny a full-transcript match on the same screen', async () => {
  const empty = { query: '', offset: 0, limit: 40, total: 0, hasMore: false, rows: [] };
  const { doc } = await mount({}, {
    fetch: async url => {
      const text = String(url);
      if (text.startsWith(SEARCH_PREFIX)) return { ok: true, status: 200, json: async () => DEEP };
      return {
        ok: true, status: 200,
        json: async () => text.includes('/facets')
          ? { ...FACETS, total: 0, hidden: 0, tags: [], phrases: [], projects: [] }
          : empty,
      };
    },
  });
  doc.nodes.get('#query').value = 'promicro venpod';
  await submit(doc);
  assert.equal(doc.nodes.get('#deeplist').children.length, 1);
  assert.match(doc.nodes.get('#state').innerHTML, /No chats match these filters/);
  assert.doesNotMatch(doc.nodes.get('#state').innerHTML, /No matching chats/);
});

test('deep search follows the show-hidden choice the list is using', async () => {
  const { doc, calls, mounted } = await mount();
  mounted.controller.state.filters.showHidden = true;
  doc.nodes.get('#query').value = 'promicro venpod';
  await submit(doc);
  const call = calls.find(url => url.startsWith(SEARCH_PREFIX));
  assert.equal(new URL(call, 'http://fixture').searchParams.get('showHidden'), 'true');
});

test('ordinary copy preserves stored launch mode while explicit Gateway copy overrides it', async () => {
  const doc = fakeDocument(), requests = [], clipboard = [];
  const sandbox = loadClient({
    navigator: { clipboard: { writeText: async value => clipboard.push(value) } },
    fetch: async (url, options) => {
      const body = JSON.parse(options.body); requests.push(body);
      return { ok: true, json: async () => ({ ...body, payload: 'exact command' }) };
    },
  });
  const mounted = sandbox.MuxChats.install({ document: doc, fetch: async url => ({ ok: true, json: async () => String(url).includes('/facets') ? FACETS : PAGE }) });
  await mounted.controller.load(true);
  const menu = doc.nodes.get('#chatlist').children[1].querySelector('details');
  await menu.children.find(node => node.textContent === 'Copy resume command').onclick();
  await menu.children.find(node => node.textContent === 'Copy Gateway command').onclick();
  assert.equal(Object.hasOwn(requests[0], 'launchMode'), false);
  assert.equal(requests[1].launchMode, 'gateway');
  assert.equal(requests[0].sessionId, 'chat-2');
  assert.equal(clipboard.length, 2);
});

test('page markup exposes every primary control and loads scripts in dependency order', () => {
  const html = fs.readFileSync(path.join(PUBLIC, 'chats.html'), 'utf8');
  for (const id of [
    'searchform', 'query', 'filters', 'agent', 'sort', 'date', 'minimum', 'project',
    'hidden', 'automation', 'matchall', 'archived', 'tagchips', 'phrasechips', 'status', 'state', 'chatlist',
    'deepresults', 'deepheading', 'deeplist', 'pager', 'prev', 'next',
  ]) assert.ok(html.includes(`id="${id}"`), `missing #${id}`);
  assert.ok(html.indexOf('intent-journal.js') < html.indexOf('picker.js'));
  assert.ok(html.indexOf('picker.js') < html.indexOf('chats.js'));
  assert.ok(html.includes('name="viewport"'));
});
