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
const HOST_CAPS = ['create', 'kill', 'rename', 'heal', 'tail', 'scrollback'];

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

test('POST /api/sessions relaunches shell-only hosted session and waits for muxd confirmation', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const host = await h.connectHost([shellSession('shellcase')]);
  t.after(() => host.close());

  const cmd = "Write-Output 'REAL_AGENT'";
  const post = h.request('POST', '/api/sessions', { name: 'shellcase', command: cmd });
  const create = await host.waitFor(m => m.t === 'create' && m.s === 'shellcase', 'create shellcase');
  assert.equal(create.cmd, cmd);

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
  assert.equal(create.cmd, newCmd);
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

test('projects sync preserves decks and app commands preserve collection deck target', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());

  const project = {
    host: 'FAKEPC',
    decks: [{ id: 'main', name: 'Main' }, { id: 'client-a', name: 'Client A' }],
    collections: [{ id: 'client-a--ops', name: 'Ops', deckId: 'client-a', deckName: 'Client A', chats: [] }],
    runningSessions: [],
  };
  const pushed = await h.request('POST', '/api/projects', project);
  assert.equal(pushed.status, 200);
  const pulled = await h.json('GET', '/api/projects');
  assert.deepEqual(pulled.decks, project.decks);
  assert.equal(pulled.collections[0].deckId, 'client-a');
  assert.equal(pulled.collections[0].deckName, 'Client A');

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
