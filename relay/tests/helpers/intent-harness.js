// Minimal relay test harness, extracted verbatim from relay/tests/relay.test.js
// (lines 78-294) so sibling test files can drive a real relay child without
// re-declaring it. relay.test.js keeps its own private copy — this is a lift, not
// a refactor. Files under tests/helpers/ are not matched by node's test globs, so
// this module is never collected as a test.
//
// REPO is resolved two levels up (tests/helpers -> tests -> relay) so the spawned
// `server.js` cwd stays the relay package root, exactly as relay.test.js intends.
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const fs = require('node:fs');
const http = require('node:http');
const net = require('node:net');
const os = require('node:os');
const path = require('node:path');
const { once } = require('node:events');

const REPO = path.resolve(__dirname, '..', '..');
const HOST_CAPS = ['create', 'createAck', 'kill', 'rename', 'heal', 'tail', 'scrollback', 'relaunch'];

// This harness can run from a git worktree that has no node_modules of its own:
// the relay deps (`ws`, `express`, …) live only in the main checkout's
// relay/node_modules, which sits in a PARALLEL tree (<root>/relay/node_modules
// vs <root>/.orch/worktrees/<node>/relay). Locate a node_modules dir that
// actually contains `ws` by walking up from the relay package root, probing both
// `<dir>/node_modules` and `<dir>/relay/node_modules` at each level. The parent
// test process requires `ws` from there; the spawned child (server.js) gets it
// via NODE_PATH. Returns null when deps are already resolvable in-place.
function findVendorDir() {
  let dir = REPO;
  for (;;) {
    for (const cand of [path.join(dir, 'node_modules'), path.join(dir, 'relay', 'node_modules')]) {
      if (fs.existsSync(path.join(cand, 'ws', 'package.json'))) return cand;
    }
    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return null;
}

const VENDOR = findVendorDir();

function requireDep(name) {
  try {
    return require(name);
  } catch (err) {
    if (VENDOR) return require(path.join(VENDOR, name));
    throw err;
  }
}

const WebSocket = requireDep('ws');

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

class RelayHarness {
  constructor(env = {}) {
    this.proc = null;
    this.env = env;
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
        // Let the child resolve relay deps from the main checkout when the
        // worktree has no node_modules (parent uses VENDOR directly, above).
        ...(VENDOR ? { NODE_PATH: [VENDOR, process.env.NODE_PATH].filter(Boolean).join(path.delimiter) } : {}),
        PORT: String(this.port),
        MUX_HOST_TOKEN: 'test-token',
        MUX_TEST_MODE: '1',
        MUX_AUTOHEAL: '0',
        MUX_STATE_DIR: this.tmp,
        MUX_TEST_PERSIST_FAULT_FILE: path.join(this.tmp, '.persist-fault.json'),
        MUX_HOST_SB_WAIT_MS: '40',
        MUX_COMMAND_LEASE_MS: '1000',
        HLAUTH_BASE: 'http://127.0.0.1:1',
        ...this.env,
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    this.proc.stdout.on('data', d => { this.stdout += d.toString(); });
    this.proc.stderr.on('data', d => { this.stderr += d.toString(); });
    await waitFor(() => this.stdout.includes(`multiplex-app on 0.0.0.0:${this.port}`), 'relay start', 5000);
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
    const opts = {
      method,
      hostname: '127.0.0.1',
      port: this.port,
      path: pathName,
      headers: { ...headers },
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

  async upload(pathName, bytes) {
    const payload = Buffer.from(bytes);
    return await new Promise((resolve, reject) => {
      const req = http.request({
        method: 'POST',
        hostname: '127.0.0.1',
        port: this.port,
        path: pathName,
        headers: {
          'content-type': 'application/octet-stream',
          'content-length': payload.length,
        },
      }, res => {
        const chunks = [];
        res.on('data', chunk => chunks.push(chunk));
        res.on('end', () => {
          const text = Buffer.concat(chunks).toString('utf8');
          let body = text;
          try { body = text ? JSON.parse(text) : null; } catch {}
          resolve({ status: res.statusCode, body, text });
        });
      });
      req.on('error', reject);
      req.end(payload);
    });
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

  failPersistence(file, stage, remaining = 1) {
    fs.writeFileSync(
      path.join(this.tmp, '.persist-fault.json'),
      JSON.stringify({ file, stage, remaining }),
    );
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
    this.ws.send(JSON.stringify({ t: 'hello', host: 'FAKEPC', protocol: 4, caps: HOST_CAPS, sessions }));
  }

  sendSessions(list) {
    this.ws.send(JSON.stringify({ t: 'sessions', list }));
  }

  sendOutput(name, text) {
    this.ws.send(JSON.stringify({ t: 'o', s: name, d: Buffer.from(text, 'utf8').toString('base64') }));
  }

  sendScrollback(name, text = '', request = null) {
    const sb = request || [...this.messages].reverse().find(message => message.t === 'sb' && message.s === name);
    if (!sb || !sb.rid) throw new Error(`no correlated scrollback request for ${name}`);
    this.ws.send(JSON.stringify({ t: 'sb', s: name, rid: sb.rid, d: Buffer.from(text, 'utf8').toString('base64') }));
  }

  sendKilled(name) {
    this.ws.send(JSON.stringify({ t: 'killed', s: name }));
  }

  sendCreateResult(request, { ok = true, created = true, session = null, detail = '', retryable = false } = {}) {
    this.ws.send(JSON.stringify({
      t: 'createResult',
      rid: request.rid,
      s: request.s,
      ok,
      created,
      detail,
      retryable,
      session,
    }));
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

module.exports = { RelayHarness, FakeHost };
