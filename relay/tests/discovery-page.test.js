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
    'minimum', 'hidden',
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

test('query parameters encode the frozen discovery contract', () => {
  const { MuxChats } = loadClient();
  const params = MuxChats.queryParams({
    q: '[web-parity]', include: new Set(['active', 'ACTIVE']), exclude: new Set(['done']),
    matchAll: true, agent: 'codex', date: 'week', minUserMessages: 5,
    showHidden: true, project: 'project-web', sort: 'created-newest',
  }, 40, 40);

  assert.equal(params.get('q'), '[web-parity]');
  assert.equal(params.get('include'), 'active,ACTIVE');
  assert.equal(params.get('exclude'), 'done');
  assert.equal(params.get('match'), 'all');
  assert.equal(params.get('agent'), 'codex');
  assert.equal(params.get('date'), 'week');
  assert.equal(params.get('minUserMessages'), '5');
  assert.equal(params.get('showHidden'), 'true');
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

  assert.ok(fetched.some(url => url.startsWith('/remote/api/discovery/chats?')));
  assert.ok(fetched.some(url => url.startsWith('/remote/api/discovery/facets?')));
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

test('page markup exposes every primary control and loads scripts in dependency order', () => {
  const html = fs.readFileSync(path.join(PUBLIC, 'chats.html'), 'utf8');
  for (const id of [
    'searchform', 'query', 'filters', 'agent', 'sort', 'date', 'minimum', 'project',
    'hidden', 'tagchips', 'phrasechips', 'status', 'state', 'chatlist', 'pager', 'prev', 'next',
  ]) assert.ok(html.includes(`id="${id}"`), `missing #${id}`);
  assert.ok(html.indexOf('intent-journal.js') < html.indexOf('picker.js'));
  assert.ok(html.indexOf('picker.js') < html.indexOf('chats.js'));
  assert.ok(html.includes('name="viewport"'));
});
