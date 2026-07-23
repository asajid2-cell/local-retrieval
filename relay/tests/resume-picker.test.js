// Resume-from-archive picker: the phone asking the PC to bring a past chat back.
//
// The point of these tests is that the ASK is honest end to end. The picker mints no authority — it
// rides the existing intent-fenced `startmux` command, so a double-tap must collapse at the relay
// (not behind a disabled button), a refusal must arrive as the sentence the user can act on, and an
// offline PC must produce a visible "queued" state rather than silence. The client logic is driven
// headless in a vm against a REAL relay process, so nothing here is a mock of the contract itself.
const test = require('node:test');
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const fs = require('node:fs');
const http = require('node:http');
const net = require('node:net');
const os = require('node:os');
const path = require('node:path');
const vm = require('node:vm');
const { once } = require('node:events');
const WebSocket = require('ws');

const REPO = path.resolve(__dirname, '..');
const PUBLIC = path.join(REPO, 'public');
const HOST_TOKEN = 'test-host-token';
const BRIDGE_TOKEN = 'test-bridge-token';
const OWNER_TOKEN = 'owner-session';
const HOST_CAPS = ['create', 'createAck', 'kill', 'rename', 'heal', 'tail', 'scrollback', 'relaunch'];
const OWNER_HEADERS = { cookie: `hl_session=${OWNER_TOKEN}`, 'x-forwarded-for': '203.0.113.9' };

function freePort() {
  return new Promise((resolve, reject) => {
    const srv = net.createServer();
    srv.listen(0, '127.0.0.1', () => {
      const port = srv.address().port;
      srv.close(() => resolve(port));
    });
    srv.on('error', reject);
  });
}

function sleep(ms) { return new Promise(resolve => setTimeout(resolve, ms)); }

async function waitFor(fn, label, timeoutMs = 8000) {
  const started = Date.now();
  let last;
  while (Date.now() - started < timeoutMs) {
    try { last = await fn(); if (last) return last; } catch (err) { last = err; }
    await sleep(40);
  }
  throw new Error(`timed out waiting for ${label}; last=${last && last.stack || JSON.stringify(last)}`);
}

// Stand-in for hl-auth's verify oracle: only OWNER_TOKEN is the owner.
async function startAuth(t) {
  const srv = http.createServer((req, res) => {
    const owner = req.headers['x-session-token'] === OWNER_TOKEN;
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify({ authenticated: owner, user: { isOwner: owner } }));
  });
  await new Promise(resolve => srv.listen(0, '127.0.0.1', resolve));
  t.after(() => srv.close());
  return `http://127.0.0.1:${srv.address().port}`;
}

class Harness {
  constructor(env = {}) {
    this.proc = null;
    this.env = env;
    this.tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'relay-resume-'));
    this.stdout = '';
    this.stderr = '';
  }

  async start() {
    if (!this.port) this.port = await freePort();
    this.proc = childProcess.spawn(process.execPath, ['server.js'], {
      cwd: REPO,
      env: {
        ...process.env,
        PORT: String(this.port),
        MUX_HOST_TOKEN: HOST_TOKEN,
        MUX_BRIDGE_TOKEN: BRIDGE_TOKEN,
        MUX_TEST_MODE: '1',
        MUX_AUTOHEAL: '0',
        MUX_STATE_DIR: this.tmp,
        HLAUTH_BASE: 'http://127.0.0.1:1',
        ...this.env,
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    this.proc.stdout.on('data', d => { this.stdout += d.toString(); });
    this.proc.stderr.on('data', d => { this.stderr += d.toString(); });
    await waitFor(() => this.stdout.includes(`multiplex-app on 0.0.0.0:${this.port}`), 'relay start', 8000);
  }

  async stopProcess() {
    if (this.proc && this.proc.exitCode === null) {
      this.proc.kill();
      await Promise.race([once(this.proc, 'exit'), sleep(2000)]);
      if (this.proc.exitCode === null) this.proc.kill('SIGKILL');
    }
    this.proc = null;
  }

  async stop() {
    await this.stopProcess();
    fs.rmSync(this.tmp, { recursive: true, force: true });
  }

  async restart() {
    await this.stopProcess();
    this.stdout = '';
    this.stderr = '';
    await this.start();
  }

  async request(method, pathName, body, headers = {}) {
    const opts = { method, hostname: '127.0.0.1', port: this.port, path: pathName, headers: { ...headers } };
    let payload = null;
    if (body !== undefined) {
      payload = Buffer.from(JSON.stringify(body));
      opts.headers['content-type'] = 'application/json';
      opts.headers['content-length'] = payload.length;
    }
    return await new Promise((resolve, reject) => {
      const req = http.request(opts, res => {
        const chunks = [];
        res.on('data', c => chunks.push(c));
        res.on('end', () => {
          const txt = Buffer.concat(chunks).toString('utf8');
          let parsed = txt;
          try { parsed = txt ? JSON.parse(txt) : null; } catch {}
          resolve({ status: res.statusCode, body: parsed, text: txt });
        });
      });
      req.on('error', reject);
      if (payload) req.write(payload);
      req.end();
    });
  }

  // The desktop bridge's scoped credential — the only way the chat catalogue gets in.
  pushIndex(chats, over = {}) {
    return this.request(
      'POST', '/api/archive-index',
      { schemaVersion: 1, host: 'AHMED-PC', chats, ...over },
      { authorization: `Bearer ${BRIDGE_TOKEN}` },
    );
  }

  // The running-session projection. `startmux` refuses outright unless local running state is
  // VERIFIED, so every resume test has to establish it the same way the real bridge does.
  pushProjects(runningSessions = []) {
    return this.request('POST', '/api/projects', {
      schemaVersion: 3, decks: [], collections: [], allChats: [],
      runningSessions, runningVerified: true,
    });
  }

  lease(owner = 'fake-host') {
    return this.request('POST', '/api/app-commands/lease', { owner, limit: 8, leaseMs: 20000 });
  }

  ack(id, leaseToken, ok) {
    return this.request('POST', `/api/app-commands/${id}/ack`, { leaseToken, ok, onPc: true });
  }
}

// The PC host process, over the same websocket the real muxd uses — this is what publishes tabs.
class FakeHost {
  constructor(port) { this.port = port; }
  async connect() {
    this.ws = new WebSocket(`ws://127.0.0.1:${this.port}/host?token=${HOST_TOKEN}`);
    await once(this.ws, 'open');
    this.ws.on('message', () => {});
    this.ws.send(JSON.stringify({ t: 'hello', host: 'FAKEPC', protocol: 4, caps: HOST_CAPS, sessions: [] }));
  }
  sendSessions(list) { this.ws.send(JSON.stringify({ t: 'sessions', list })); }
  close() { try { this.ws.close(); } catch {} }
}

function session(name, sessionId = '') {
  return { name, alive: true, created: 2000, lastOut: 2000, cols: 100, rows: 30,
           hasCommand: true, shellOnly: false, ready: true, kind: 'command', sessionId, aliases: [], tail: '' };
}

function memoryStorage() {
  const map = new Map();
  return {
    getItem: k => (map.has(k) ? map.get(k) : null),
    setItem: (k, v) => { map.set(k, String(v)); },
    removeItem: k => { map.delete(k); },
  };
}

// Load the REAL browser files headless. picker.js keeps every decision in a dependency-injected
// factory precisely so the phone's logic can be driven with no DOM in the room.
function loadClient(fetchImpl) {
  const sandbox = {
    console, setTimeout, clearTimeout, Date, Math, JSON, Promise, URL,
    crypto: require('node:crypto').webcrypto,
    localStorage: memoryStorage(),
    sessionStorage: memoryStorage(),
    fetch: fetchImpl,
  };
  const context = vm.createContext(sandbox);
  for (const file of ['intent-journal.js', 'picker.js'])
    vm.runInContext(fs.readFileSync(path.join(PUBLIC, file), 'utf8'), context, { filename: file });
  assert.ok(sandbox.MuxResumePicker, 'picker.js must install MuxResumePicker without a DOM');
  assert.equal(typeof sandbox.postIntent, 'function');
  return sandbox;
}

function browserFetch(h) {
  return async (url, init) => {
    const target = new URL(url, `http://127.0.0.1:${h.port}`);
    const method = (init && init.method) || 'GET';
    const body = init && init.body ? JSON.parse(init.body) : undefined;
    const res = await h.request(method, target.pathname + target.search, body, { ...OWNER_HEADERS });
    return {
      ok: res.status >= 200 && res.status < 300,
      status: res.status,
      json: async () => res.body,
    };
  };
}

function row(i, over = {}) {
  return {
    id: `chat-${i}`, title: `Chat number ${i}`, tool: i % 2 ? 'codex' : 'claude',
    cwd: `C:/work/proj-${i}`, workspaceLabel: `proj-${i}`,
    updatedAt: 1700000000000 + i, muxName: `mux-${i}`, resumable: true, ...over,
  };
}

async function bootPicker(t, { chats = [row(1), row(2), row(3)], running = [], deps = {} } = {}) {
  const authBase = await startAuth(t);
  const h = new Harness({ HLAUTH_BASE: authBase });
  await h.start();
  t.after(async () => h.stop());
  assert.equal((await h.pushProjects(running)).status, 200);
  assert.equal((await h.pushIndex(chats)).status, 200);
  const client = loadClient(browserFetch(h));
  const picker = client.MuxResumePicker.createPicker({
    base: '', fetch: browserFetch(h), postIntent: client.postIntent,
    pollIntervalMs: 40, pollTimeoutMs: 6000, offlineTimeoutMs: 300, ...deps,
  });
  return { h, client, picker };
}

// ---- pure logic, no relay -------------------------------------------------------------------

test('search narrows on every visible field and every token must match', () => {
  const { MuxResumePicker } = loadClient(async () => { throw new Error('no fetch here'); });
  const chats = [
    row(1, { title: 'Cortex planning', workspaceLabel: 'cortex' }),
    row(2, { title: 'Relay hardening', workspaceLabel: 'relay' }),
    row(3, { title: 'Cortex terminal', workspaceLabel: 'cortex', tool: 'claude' }),
  ];
  assert.equal(MuxResumePicker.filterChats(chats, '').length, 3);
  assert.equal(MuxResumePicker.filterChats(chats, '   ').length, 3);
  // Two tokens narrow rather than widen: "cortex claude" is an AND, not an OR.
  assert.deepEqual(MuxResumePicker.filterChats(chats, 'cortex claude').map(c => c.id), ['chat-3']);
  assert.deepEqual(MuxResumePicker.filterChats(chats, 'CORTEX').map(c => c.id), ['chat-1', 'chat-3']);
  // Fields the user can see are all searchable: cwd and muxName included.
  assert.deepEqual(MuxResumePicker.filterChats(chats, 'proj-2').map(c => c.id), ['chat-2']);
  assert.deepEqual(MuxResumePicker.filterChats(chats, 'mux-1').map(c => c.id), ['chat-1']);
  assert.deepEqual(MuxResumePicker.filterChats(chats, 'nothing here'), []);
});

test('the resume payload only ever carries names the relay will accept', () => {
  const { MuxResumePicker } = loadClient(async () => { throw new Error('no fetch here'); });
  assert.equal(MuxResumePicker.muxNameFor({ muxName: 'cortex.1', id: 'a' }), 'cortex.1');
  // A pushed name the relay's strictMuxName would reject arrives as '' — derive rather than dead-end.
  assert.equal(MuxResumePicker.muxNameFor({ muxName: '', tool: 'codex', id: 'abc' }), 'codex-abc');
  assert.equal(MuxResumePicker.muxNameFor({ muxName: 'has space', tool: 'claude', id: 'x1' }), 'claude-x1');
  assert.equal(MuxResumePicker.muxNameFor({ muxName: '', tool: '', id: '' }), 'chat-');
  assert.equal(MuxResumePicker.muxNameFor({ muxName: '!!!', tool: '!!!', id: '!!!' }), '');
  assert.match(MuxResumePicker.muxNameFor({ muxName: 'x'.repeat(80), id: 'q' }), /^x{48}$/);
  // The relay only accepts claude|codex; anything else must travel as '' so the row still resumes
  // on its sessionId instead of being refused with a 400 the user cannot act on.
  assert.equal(MuxResumePicker.toolFor({ tool: 'CLAUDE' }), 'claude');
  assert.equal(MuxResumePicker.toolFor({ tool: 'gemini' }), '');
  assert.equal(MuxResumePicker.toolFor({}), '');
});

test('every archive state produces a visible line — the picker is never silent', () => {
  const { MuxResumePicker } = loadClient(async () => { throw new Error('no fetch here'); });
  const states = [
    [{ loaded: false, chats: [] }, 'loading'],
    [{ loaded: true, chats: [] }, 'empty'],
    [{ loaded: true, chats: [row(1)], appLive: true }, 'live'],
    [{ loaded: true, chats: [row(1)], appLive: false }, 'offline'],
    [{ loaded: true, chats: [row(1)], error: 'could not load your chat archive (HTTP 500)' }, 'error'],
  ];
  for (const [state, expected] of states) {
    const fresh = MuxResumePicker.freshness(state);
    assert.equal(fresh.state, expected);
    assert.ok(fresh.label && fresh.label.length > 8, `${expected} must carry a sentence, got ${fresh.label}`);
  }
  assert.equal(MuxResumePicker.freshness({ loaded: true, chats: [row(1)], appLive: true }).live, true);
  assert.equal(MuxResumePicker.freshness({ loaded: true, chats: [row(1)], appLive: false }).live, false);
});

// ---- against a real relay -------------------------------------------------------------------

test('the picker reads the pushed catalogue and filters it on the phone', async t => {
  const { picker } = await bootPicker(t, {
    chats: [row(1), row(2), row(3, { resumable: false })],
  });

  await picker.load();
  // resumable:false is the app saying it could not build a trusted resume — offering it would be a
  // promise the PC cannot keep.
  assert.deepEqual(picker.visible().map(c => c.id), ['chat-1', 'chat-2']);
  assert.equal(picker.state.host, 'AHMED-PC');
  assert.equal(picker.freshness().state, 'live');

  assert.deepEqual(picker.setQuery('proj-2').map(c => c.id), ['chat-2']);
  assert.equal(picker.visible().length, 1);
  assert.deepEqual(picker.setQuery('').map(c => c.id), ['chat-1', 'chat-2']);
});

test('a double-tap collapses into one resume at the relay, not behind a disabled button', async t => {
  const { h, picker } = await bootPicker(t, { deps: { pollTimeoutMs: 400, offlineTimeoutMs: 400 } });
  await picker.load();
  const chat = picker.visible()[0];

  // Both taps are issued before either answer lands — exactly what a fat-fingered phone does.
  const [first, second] = await Promise.all([picker.resume(chat), picker.resume(chat)]);

  assert.equal(first.id, second.id, 'a double-tap must not create two commands');
  assert.equal(first.intentId, second.intentId);
  assert.equal(
    [first, second].filter(r => r.deduplicated).length, 1,
    'exactly one of the two enqueues must report deduplicated:true',
  );
  const queued = await h.lease();
  assert.equal(queued.body.length, 1, 'the relay must hold ONE startmux, not two');
  assert.equal(queued.body[0].type, 'startmux');
  assert.equal(queued.body[0].muxName, 'mux-1');
  assert.equal(queued.body[0].sessionId, 'chat-1');
  // The picker mints no authority: the payload is ids and display-safe names, nothing executable.
  assert.equal(queued.body[0].title, '');
  assert.equal(queued.body[0].insert, '');
});

test('a leased+acked resume ends with the new tab in the list and selected', async t => {
  const { h, picker } = await bootPicker(t);
  const host = new FakeHost(h.port);
  await host.connect();
  t.after(() => host.close());

  const selected = [];
  let tabs = [];
  const withTabs = h; // the stub does what index.html's loadSessions does: refresh, then select.
  const pickerWithTabs = (await bootPicker(t, {
    deps: {
      loadSessions: async name => {
        selected.push(name);
        const res = await withTabs.request('GET', '/api/sessions', undefined, OWNER_HEADERS);
        tabs = Array.isArray(res.body) ? res.body : [];
      },
    },
  }));
  // Drive the picker that shares this relay+host pair.
  const p = pickerWithTabs.picker;
  const hostForP = new FakeHost(pickerWithTabs.h.port);
  await hostForP.connect();
  t.after(() => hostForP.close());
  const withTabsH = pickerWithTabs.h;

  await p.load();
  const chat = p.visible()[0];

  const resuming = p.resume(chat);
  // The real host starts the session first, then acks — so the tab exists by the time we refresh.
  const leased = await waitFor(async () => {
    const res = await withTabsH.lease();
    return res.body.length ? res.body[0] : null;
  }, 'startmux lease');
  assert.equal(leased.type, 'startmux');
  hostForP.sendSessions([session('mux-1', 'chat-1')]);
  await sleep(150);
  const acked = await withTabsH.ack(leased.id, leased.leaseToken, true);
  assert.equal(acked.status, 200);

  const outcome = await resuming;
  assert.equal(outcome.state, 'done');
  assert.equal(outcome.detail, 'mux session started');
  assert.deepEqual(selected, ['mux-1'], 'the resume must SELECT the tab it just created');
  assert.ok(tabs.some(s => s.name === 'mux-1'), `tab list must contain mux-1; got ${JSON.stringify(tabs.map(s => s.name))}`);
  picker; host; tabs;
});

test('a refused resume surfaces the PC-side detail, not a bare status code', async t => {
  // The local copy of chat-1 is running on the PC — resuming it remotely would create a second writer.
  const { picker } = await bootPicker(t, { running: [{ sessionId: 'chat-1', tool: 'codex', pid: 4242 }] });
  await picker.load();

  const outcome = await picker.resume(picker.visible()[0]);
  assert.equal(outcome.state, 'failed');
  assert.equal(outcome.status, 409);
  assert.match(outcome.detail, /local copy is already running/);
  assert.ok(outcome.detail.length > 'local copy is already running'.length,
    `the actionable half of the refusal must survive; got ${outcome.detail}`);
});

test('with the desktop app offline the resume queues visibly instead of failing silently', async t => {
  const { h, picker } = await bootPicker(t);
  await picker.load();
  assert.equal(picker.state.appLive, true);

  // Liveness is process-local by doctrine: the rows survive a restart, the claim that the PC is
  // ANSWERING does not. This is the honest stand-in for "the desktop app is not open".
  await h.restart();
  assert.equal((await h.pushProjects()).status, 200);

  await picker.load();
  assert.equal(picker.state.appLive, false, 'a restart must drop liveness while keeping the rows');
  assert.equal(picker.visible().length, 3, 'the catalogue itself must survive the restart');
  assert.equal(picker.freshness().state, 'offline');

  const outcome = await picker.resume(picker.visible()[0]);
  assert.equal(outcome.state, 'queued');
  assert.match(outcome.detail, /offline/);
  assert.ok(outcome.id, 'the command must really be sitting on the relay, not merely reported as queued');

  const still = await h.request('GET', `/api/app-commands/${outcome.id}`, undefined, OWNER_HEADERS);
  assert.equal(still.status, 200);
  assert.equal(still.body.status, 'pending');
});

// ---- the page actually wires it -------------------------------------------------------------

test('index.html hands the picker the page helpers and hosts its dialog', () => {
  const source = fs.readFileSync(path.join(PUBLIC, 'index.html'), 'utf8');
  assert.ok(source.indexOf('<script src="picker.js"></script>') > 0, 'picker.js must be loaded');
  assert.ok(
    source.indexOf('<script src="intent-journal.js"></script>') < source.indexOf('<script src="picker.js"></script>'),
    'the intent journal must be installed before the picker that rides it',
  );
  for (const id of ['resumebtn', 'resumedlg', 'resumeq', 'resumelist', 'resumelive', 'resumestatus', 'resumeclose'])
    assert.ok(source.includes(`id="${id}"`), `index.html must host #${id}`);
  const install = source.slice(source.indexOf('MuxResumePicker.install({'));
  const call = install.slice(0, install.indexOf('}'));
  for (const dep of ['base', 'postIntent', 'loadSessions', 'flash', 'openDialog'])
    assert.ok(call.includes(dep), `install() must receive ${dep}; got ${call}`);
});
