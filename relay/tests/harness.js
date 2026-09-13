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
const HOST_CAPS = ['create', 'createAck', 'kill', 'relayKillFence', 'rename', 'heal', 'tail', 'scrollback', 'relaunch'];

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

async function leaseCommands(h, owner = 'test-consumer') {
  const pathName = '/api/app-commands/lease';
  const res = await h.request(
    'POST',
    pathName,
    { owner, limit: 16 },
    { 'X-Mux-Command-Bridge': h.commandBridgeToken },
  );
  assert.ok(res.status >= 200 && res.status < 300, `POST ${pathName} failed: ${res.status} ${res.text}`);
  return res.body;
}

async function ackLeased(h, command, result) {
  const pathName = `/api/app-commands/${encodeURIComponent(command.id)}/ack`;
  const res = await h.request(
    'POST',
    pathName,
    { leaseToken: command.leaseToken, ...result },
    { 'X-Mux-Command-Bridge': h.commandBridgeToken },
  );
  assert.ok(res.status >= 200 && res.status < 300, `POST ${pathName} failed: ${res.status} ${res.text}`);
  return res.body;
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

function waitForWsFrame(ws, match, label, timeoutMs = 4000) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      ws.off('message', onMessage);
      reject(new Error(`timed out waiting for ${label}`));
    }, timeoutMs);
    const onMessage = raw => {
      const text = raw.toString();
      if (!match(text)) return;
      clearTimeout(timer);
      ws.off('message', onMessage);
      resolve(text);
    };
    ws.on('message', onMessage);
  });
}

class RelayHarness {
  constructor(env = {}) {
    this.proc = null;
    this.commandBridgeToken = crypto.randomBytes(32).toString('hex');
    this.env = env;
    this.tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'relay-test-'));
    this.stdout = '';
    this.stderr = '';
  }

  async start() {
    this.port = this.fixedPort || await freePort();
    this.proc = childProcess.spawn(process.execPath, ['server.js'], {
      cwd: REPO,
      env: {
        ...process.env,
        PORT: String(this.port),
        MUX_HOST_TOKEN: 'test-token',
        MUX_TEST_MODE: '1',
        MUX_TEST_FIXTURE: '1',
        MUX_BIND_HOST: '127.0.0.1',
        MUX_AUTOHEAL: '0',
        MUX_STATE_DIR: this.tmp,
        MUX_TEST_PERSIST_FAULT_FILE: path.join(this.tmp, '.persist-fault.json'),
        MUX_HOST_SB_WAIT_MS: '40',
        MUX_COMMAND_LEASE_MS: '1000',
        MUX_COMMAND_BRIDGE_TOKEN: this.commandBridgeToken,
        HLAUTH_BASE: 'http://127.0.0.1:1',
        ...this.env,
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    this.proc.stdout.on('data', d => { this.stdout += d.toString(); });
    this.proc.stderr.on('data', d => { this.stderr += d.toString(); });
    try {
      await waitFor(() => this.stdout.includes(`multiplex-app on 127.0.0.1:${this.port}`), 'relay start', 5000);
    } catch (error) {
      error.message += `; pid=${this.proc.pid}; exit=${this.proc.exitCode}; signal=${this.proc.signalCode}; stdoutBytes=${Buffer.byteLength(this.stdout)}; stderrBytes=${Buffer.byteLength(this.stderr)}`;
      throw error;
    }
  }

  async stopProcess() {
    const proc = this.proc;
    if (proc && proc.exitCode === null) {
      proc.kill();
      // Wait for 'close', not 'exit': the process object can be gone while its stdio pipes are still
      // draining, and tearing the tmp dir down underneath them is how a teardown turns into a flake.
      await Promise.race([once(proc, 'close'), sleep(2000)]);
      if (proc.exitCode === null) {
        proc.kill('SIGKILL');
        await Promise.race([once(proc, 'close'), sleep(1000)]);
      }
    }
    if (proc) {                       // release the pipes even if the child never reported close
      proc.stdout?.destroy();
      proc.stderr?.destroy();
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

  async json(method, pathName, body, headers = {}) {
    const res = await this.request(method, pathName, body, headers);
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

  // NOTE the wire shape differs from scrollback: `tailr` carries PLAIN text, not base64 `d`, plus an
  // optional muxd signature the relay passes through without validating.
  sendTail(name, text = '', { sig = '', request = null } = {}) {
    const req = request || [...this.messages].reverse().find(message => message.t === 'tail' && message.s === name);
    if (!req || !req.rid) throw new Error(`no correlated tail request for ${name}`);
    this.ws.send(JSON.stringify({ t: 'tailr', s: name, rid: req.rid, text, sig }));
  }

  sendKilled(name) {
    const request = [...this.messages].reverse().find(m => m.t === 'kill' && m.s === name);
    if (!request?.rid) throw new Error('no correlated kill request for ' + name);
    this.ws.send(JSON.stringify({ ...request, t: 'killed' }));
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

module.exports = {
  REPO,
  HOST_CAPS,
  freePort,
  sleep,
  waitFor,
  leaseCommands,
  ackLeased,
  waitForWsText,
  waitForWsFrame,
  RelayHarness,
  FakeHost,
};
