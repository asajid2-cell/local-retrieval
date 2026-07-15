const test = require('node:test');
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const net = require('node:net');
const os = require('node:os');
const path = require('node:path');
const { once } = require('node:events');
const WebSocket = require('ws');

const REPO = path.resolve(__dirname, '..');
const HOST_CAPS = ['create', 'kill', 'rename', 'heal', 'tail', 'scrollback', 'relaunch'];

function commandSig(cmd) {
  const c = String(cmd || '').trim();
  return c ? crypto.createHash('sha256').update(c, 'utf8').digest('hex').slice(0, 16) : '';
}

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

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

async function waitFor(fn, label, timeoutMs = 4000) {
  const started = Date.now();
  let last;
  while (Date.now() - started < timeoutMs) {
    try {
      last = await fn();
      if (last) return last;
    } catch (err) {
      last = err;
    }
    await sleep(50);
  }
  throw new Error(`timed out waiting for ${label}; last=${last && last.stack || JSON.stringify(last)}`);
}

function waitForWsText(ws, regex, label, timeoutMs = 4000) {
  return new Promise((resolve, reject) => {
    let seen = '';
    const timer = setTimeout(() => {
      ws.off('message', onMessage);
      reject(new Error(`timed out waiting for ${label}; seen=${JSON.stringify(seen.slice(-1000))}`));
    }, timeoutMs);
    const onMessage = raw => {
      seen += raw.toString();
      if (regex.test(seen)) {
        clearTimeout(timer);
        ws.off('message', onMessage);
        resolve(seen);
      }
    };
    ws.on('message', onMessage);
  });
}

class RelayHarness {
  constructor() {
    this.proc = null;
    this.tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'relay-test-'));
    this.stdout = '';
    this.stderr = '';
  }

  async start() {
    this.port = await freePort();
    this.proc = childProcess.spawn(process.execPath, ['server.js'], {
      cwd: REPO,
      env: {
        ...process.env,
        PORT: String(this.port),
        MUX_HOST_TOKEN: 'test-token',
        MUX_TEST_MODE: '1',
        MUX_AUTOHEAL: '0',
        MUX_STATE_DIR: this.tmp,
        MUX_HOST_SB_WAIT_MS: '40',
        HLAUTH_BASE: 'http://127.0.0.1:1',
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    this.proc.stdout.on('data', d => { this.stdout += d.toString(); });
    this.proc.stderr.on('data', d => { this.stderr += d.toString(); });
    await waitFor(() => this.stdout.includes(`multiplex-app on 0.0.0.0:${this.port}`), 'relay start', 5000);
  }

  async stop() {
    if (this.proc && this.proc.exitCode === null) {
      this.proc.kill();
      await Promise.race([once(this.proc, 'exit'), sleep(2000)]);
      if (this.proc.exitCode === null) this.proc.kill('SIGKILL');
    }
    fs.rmSync(this.tmp, { recursive: true, force: true });
  }

  async request(method, pathName, body) {
    const opts = {
      method,
      hostname: '127.0.0.1',
      port: this.port,
      path: pathName,
      headers: {},
    };
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
          const text = Buffer.concat(chunks).toString('utf8');
          let parsed = text;
          try { parsed = text ? JSON.parse(text) : null; } catch {}
          resolve({ status: res.statusCode, body: parsed, text });
        });
      });
      req.on('error', reject);
      if (payload) req.write(payload);
      req.end();
    });
  }

  async json(method, pathName, body) {
    const res = await this.request(method, pathName, body);
    assert.ok(res.status >= 200 && res.status < 300, `${method} ${pathName} failed: ${res.status} ${res.text}`);
    return res.body;
  }

  async connectHost(sessions = []) {
    const host = new FakeHost(this.port);
    await host.connect();
    host.sendHello(sessions);
    await waitFor(async () => {
      const health = await this.json('GET', '/api/health');
      return health.host && health.host.connected && health.host.protocolOk ? health : null;
    }, 'host connected');
    return host;
  }
}

class FakeHost {
  constructor(port) {
    this.port = port;
    this.messages = [];
    this.waiters = [];
  }

  async connect() {
    this.ws = new WebSocket(`ws://127.0.0.1:${this.port}/host?token=test-token`);
    await once(this.ws, 'open');
    this.ws.on('message', raw => {
      const msg = JSON.parse(raw.toString());
      this.messages.push(msg);
      for (const waiter of [...this.waiters]) {
        if (waiter.match(msg)) {
          this.waiters.splice(this.waiters.indexOf(waiter), 1);
          waiter.resolve(msg);
        }
      }
    });
  }

  close() {
    try { this.ws.close(); } catch {}
  }

  sendHello(sessions = []) {
    this.ws.send(JSON.stringify({ t: 'hello', host: 'FAKEPC', protocol: 2, caps: HOST_CAPS, sessions }));
  }

  sendSessions(list) {
    this.ws.send(JSON.stringify({ t: 'sessions', list }));
  }

  sendOutput(name, text) {
    this.ws.send(JSON.stringify({ t: 'o', s: name, d: Buffer.from(text, 'utf8').toString('base64') }));
  }

  sendScrollback(name, text = '') {
    this.ws.send(JSON.stringify({ t: 'sb', s: name, d: Buffer.from(text, 'utf8').toString('base64') }));
  }

  sendKilled(name) {
    this.ws.send(JSON.stringify({ t: 'killed', s: name }));
  }

  async waitFor(match, label = 'host message', timeoutMs = 4000) {
    const existing = this.messages.find(match);
    if (existing) return existing;
    return await new Promise((resolve, reject) => {
      const waiter = { match, resolve };
      this.waiters.push(waiter);
      setTimeout(() => {
        const i = this.waiters.indexOf(waiter);
        if (i >= 0) this.waiters.splice(i, 1);
        reject(new Error(`timed out waiting for ${label}; messages=${JSON.stringify(this.messages)}`));
      }, timeoutMs);
    });
  }

  async assertNo(match, label, windowMs = 250) {
    await sleep(windowMs);
    assert.equal(this.messages.some(match), false, `${label}; messages=${JSON.stringify(this.messages)}`);
  }
}

function shellSession(name) {
  return { name, alive: true, created: 1000, lastOut: 1000, cols: 100, rows: 30, hasCommand: false, shellOnly: true, ready: true, kind: 'shell', cmdSig: '' };
}

function commandSession(name, cmd, created = 2000) {
  return { name, alive: true, created, lastOut: created, cols: 100, rows: 30, hasCommand: true, shellOnly: false, ready: true, kind: 'command', cmdSig: commandSig(cmd), tail: `ran ${cmd}` };
}

function dormantSession(name, cmd = '') {
  return { name, alive: false, created: 1000, lastOut: 1000, cols: 100, rows: 30, hasCommand: !!cmd, shellOnly: false, ready: false, kind: 'dormant', cmdSig: commandSig(cmd) };
}

test('GET /api/sessions reports trustable attention states from host facts', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const cmd = 'codex resume abc';
  const now = Date.now();
  const host = await h.connectHost([
    { ...commandSession('activecase', cmd, now - 1000), lastOut: now - 1000, tail: 'garbled tail without magic UI words' },
    { ...commandSession('quietcase', cmd, now - 120000), lastOut: now - 120000, tail: 'Use /skills to list available skills' },
    { ...commandSession('stoppedcase', cmd, now - 120000), lastOut: now - 120000, tail: 'PS C:\\Users\\Ahmed>' },
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

test('POST /api/sessions relaunches shell-only hosted session and waits for muxd confirmation', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([shellSession('shellcase')]);
  t.after(() => host.close());

  const cmd = "Write-Output 'REAL_AGENT'";
  const post = h.request('POST', '/api/sessions', { name: 'shellcase', command: cmd });
  const create = await host.waitFor(m => m.t === 'create' && m.s === 'shellcase', 'create shellcase');
  assert.equal(create.cmd, undefined, 'opaque protocol: the relay never sends an executable command to muxd');
  assert.ok(typeof create.rid === 'string' && create.rid.length > 0, 'create carries a request id');

  let settled = false;
  post.then(() => { settled = true; });
  await sleep(150);
  assert.equal(settled, false, 'POST returned before muxd confirmed the created command-backed session');

  host.sendSessions([commandSession('shellcase', cmd)]);
  const res = await post;
  assert.equal(res.status, 200);
  assert.equal(res.body.created, true);

  const sessions = await h.json('GET', '/api/sessions');
  const row = sessions.find(s => s.name === 'shellcase');
  assert.equal(row.hasCommand, true);
  assert.equal(row.shellOnly, false);
  assert.equal(row.cmdSig, commandSig(cmd));
});

test('POST /api/sessions reuses same command and does not send create', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const cmd = "Write-Output 'SAME'";
  const host = await h.connectHost([commandSession('samecase', cmd)]);
  t.after(() => host.close());

  const res = await h.request('POST', '/api/sessions', { name: 'samecase', command: cmd });

  assert.equal(res.status, 200);
  assert.equal(res.body.created, false);
  await host.assertNo(m => m.t === 'create' && m.s === 'samecase', 'same command should not relaunch');
});

test('POST /api/sessions replaces a different command-backed session', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const oldCmd = "Write-Output 'OLD'";
  const newCmd = "Write-Output 'NEW'";
  const host = await h.connectHost([commandSession('diffcase', oldCmd)]);
  t.after(() => host.close());

  const post = h.request('POST', '/api/sessions', { name: 'diffcase', command: newCmd });
  const create = await host.waitFor(m => m.t === 'create' && m.s === 'diffcase', 'create diffcase');
  assert.equal(create.cmd, undefined, 'opaque protocol: the relay never sends an executable command to muxd');
  assert.ok(typeof create.rid === 'string' && create.rid.length > 0, 'create carries a request id');
  host.sendSessions([commandSession('diffcase', newCmd, 3000)]);

  const res = await post;
  assert.equal(res.status, 200);
  assert.equal(res.body.created, true);
  const row = (await h.json('GET', '/api/sessions')).find(s => s.name === 'diffcase');
  assert.equal(row.cmdSig, commandSig(newCmd));
});

test('dormant hosted session refuses websocket attach and does not create a shell', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([dormantSession('dormantcase', "Write-Output 'DORMANT'")]);
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
  const cmd = "Write-Output 'SAVED'";
  const host = await h.connectHost([dormantSession('savedcase', cmd)]);
  t.after(() => host.close());

  const post = h.request('POST', '/api/sessions/savedcase/relaunch', {});
  const create = await host.waitFor(m => m.t === 'create' && m.s === 'savedcase', 'relaunch savedcase');
  assert.equal(create.cmd, undefined, 'opaque protocol: muxd reuses its own saved command, the relay sends none');
  assert.equal(create.relaunch, true);
  assert.ok(typeof create.rid === 'string' && create.rid.length > 0, 'relaunch carries a request id');

  let settled = false;
  post.then(() => { settled = true; });
  await sleep(150);
  assert.equal(settled, false, 'relaunch returned before muxd confirmed the restarted session');

  host.sendSessions([commandSession('savedcase', cmd, 9000)]);
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
  assert.match(res.body.error, /no saved resume command/);
  await host.assertNo(m => m.t === 'create' && m.s === 'plaincase', 'plain shell should not relaunch without a command');
});

test('POST /api/sessions/:name/relaunch refuses a matching local owner', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const cmd = "Write-Output 'TAKEOVER'";
  const host = await h.connectHost([dormantSession('takeover', cmd)]);
  t.after(() => host.close());
  await h.json('POST', '/api/projects', {
    decks: [],
    collections: [{ id: 'c1', name: 'Work', chats: [{ id: 'sid1', title: 'Takeover', muxName: 'takeover', muxCommand: cmd }] }],
    runningSessions: [{ sessionId: 'sid1', pid: 1234 }],
    runningVerified: true,
    host: 'FAKEPC',
  });

  const res = await h.request('POST', '/api/sessions/takeover/relaunch', { command: cmd });
  assert.equal(res.status, 409);
  assert.match(res.body.error, /local copy is already running/i);
  assert.match(res.body.detail, /pid 1234/);
  await host.assertNo(
    m => m.t === 'create' && m.s === 'takeover',
    'a remote relaunch must not start while a local owner is verified');
});

test('POST /api/sessions/:name/relaunch refuses a local owner matched through allChats', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const cmd = "Write-Output 'ALLCHAT'";
  const host = await h.connectHost([dormantSession('allchat-takeover', cmd)]);
  t.after(() => host.close());
  await h.json('POST', '/api/projects', {
    decks: [],
    collections: [],
    allChats: [{ id: 'sid-all', title: 'All Chat Takeover', muxName: 'allchat-takeover', muxCommand: cmd }],
    runningSessions: [{ sessionId: 'sid-all', pid: 4321 }],
    runningVerified: true,
    host: 'FAKEPC',
  });

  const res = await h.request('POST', '/api/sessions/allchat-takeover/relaunch', { command: cmd });
  assert.equal(res.status, 409);
  assert.match(res.body.error, /local copy is already running/i);
  assert.match(res.body.detail, /pid 4321/);
  await host.assertNo(
    m => m.t === 'create' && m.s === 'allchat-takeover',
    'allChats identity must also block a duplicate remote writer');
});

test('unknown websocket tab creates PC-local shell, bridges scrollback, input, and output', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([]);
  t.after(() => host.close());

  const ws = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=webshell&cols=88&rows=22`);
  await once(ws, 'open');
  const create = await host.waitFor(m => m.t === 'create' && m.s === 'webshell', 'create webshell');
  assert.equal(create.cmd, '');
  assert.equal(create.cols, 88);
  assert.equal(create.rows, 22);
  host.sendSessions([shellSession('webshell')]);

  const sb = await host.waitFor(m => m.t === 'sb' && m.s === 'webshell', 'scrollback request');
  assert.equal(sb.max, 800000);
  host.sendScrollback('webshell', 'SCROLLBACK\n');

  ws.send('iWrite-Output WEB_OK\r');
  const input = await host.waitFor(m => m.t === 'i' && m.s === 'webshell', 'input bridge');
  assert.equal(Buffer.from(input.d, 'base64').toString('utf8'), 'Write-Output WEB_OK\r');

  const output = waitForWsText(ws, /WEB_OK/, 'webshell live output frame');
  host.sendOutput('webshell', 'WEB_OK\r\n');
  assert.match(await output, /WEB_OK/);
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
  const cmd = "codex resume idle";
  const host = await h.connectHost([commandSession('idlecase', cmd, Date.now())]);
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

test('burst output during attach never blanks-and-drops (busy-session regression)', async t => {
  // If output floods while the client is still waiting for scrollback, the relay must go live WITHOUT
  // losing the screen. The old overflow path set sbWait=false, orphaned the queued frames, skipped the
  // clear, and then dropped the sb -> lost output on a busy attach. This asserts flooded output arrives.
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const cmd = "codex resume busy";
  const host = await h.connectHost([commandSession('busycase', cmd, Date.now())]);
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
    host: 'FAKEPC',
    decks: [{ id: 'main', name: 'Main' }, { id: 'client-a', name: 'Client A' }],
    collections: [{ id: 'client-a--ops', name: 'Ops', deckId: 'client-a', deckName: 'Client A', chats: [] }],
    allChats: [{ id: 'old1', title: 'Old Chat', muxName: 'old-chat', muxCommand: 'codex resume old1' }],
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
    muxName: 'chat-one',
    collectionId: 'client-a--ops',
    collection: 'Ops',
    deckId: 'client-a',
    deckName: 'Client A',
  });
  assert.equal(queued.status, 200);
  const pending = await h.json('GET', '/api/app-commands');
  assert.equal(pending.length, 1);
  assert.equal(pending[0].collectionId, 'client-a--ops');
  assert.equal(pending[0].deckId, 'client-a');
  assert.equal(pending[0].deckName, 'Client A');
});

test('GET /api/sessions annotates renamed mux tabs with projected chat identity', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const cmd = "codex resume --include-non-interactive sid-proj";
  const host = await h.connectHost([commandSession('short-tab-name', cmd, Date.now())]);
  t.after(() => host.close());

  await h.json('POST', '/api/projects', {
    decks: [{ id: 'main', name: 'Main' }],
    collections: [],
    allChats: [{ id: 'sid-proj', tool: 'codex', title: 'Projected Chat', muxName: 'canonical-projected-sid-proj', muxCommand: cmd }],
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
  assert.match(html, /id="wsdecknewrow" hidden/);
  assert.match(html, /#wscoldlg\s+\.dlgrow\s*\{[^}]*display:flex/);
  assert.match(html, /#wscoldlg\s+\.dlgrow\[hidden\]\s*\{[^}]*display:none/);
  assert.match(html, /sessionId:s\.sessionId\|\|''/);
  assert.match(html, /muxName:s\.projectMuxName\|\|s\.name/);
  assert.match(html, /sessionId:tab\.sessionId\|\|''/);
});

test('terminal add-to-collection dialog supports decks and stable chat identity', () => {
  const html = fs.readFileSync(path.join(REPO, 'public', 'index.html'), 'utf8');
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

test('DELETE /api/sessions sends kill and waits until hosted row is gone', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([shellSession('killcase')]);
  t.after(() => host.close());

  const del = h.request('DELETE', '/api/sessions/killcase');
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
