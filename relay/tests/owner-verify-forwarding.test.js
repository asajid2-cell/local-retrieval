// The relay is the only caller that knows who the browser is. hl-auth's /internal/verify binds a
// test session to the address and user-agent it was minted with, so a verify call that carries
// neither cannot authorize anything that was bound: the relay must present what nginx handed it
// (x-real-ip) as a single X-Forwarded-For hop, and the browser's UA when there is one.
//
// These tests are about what leaves the relay on the wire, not about hl-auth's answer.
const test = require('node:test');
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const fs = require('node:fs');
const http = require('node:http');
const net = require('node:net');
const os = require('node:os');
const path = require('node:path');
const { once } = require('node:events');

const REPO = path.resolve(__dirname, '..');

function ensureRelayDeps() {
  try {
    require.resolve('express', { paths: [REPO] });
    require.resolve('ws', { paths: [REPO] });
    return;
  } catch { /* not installed yet */ }
  let last = null;
  for (const args of [['ci', '--offline'], ['ci'], ['install']]) {
    last = childProcess.spawnSync('npm', [...args, '--no-audit', '--no-fund'], {
      cwd: REPO, stdio: 'ignore', shell: process.platform === 'win32', timeout: 180000,
    });
    if (last.status === 0) return;
  }
  throw new Error(`relay dependencies missing and npm install failed in ${REPO} (status ${last && last.status})`);
}
ensureRelayDeps();

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
    await sleep(50);
  }
  throw new Error(`timed out waiting for ${label}; last=${last && last.stack || JSON.stringify(last)}`);
}

// Stand-in for hl-auth: records every /internal/verify call and calls everything the owner.
async function startAuth(t) {
  const seen = [];
  const srv = http.createServer((req, res) => {
    seen.push({ url: req.url, headers: req.headers });
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify({ authenticated: true, user: { isOwner: true } }));
  });
  await new Promise(resolve => srv.listen(0, '127.0.0.1', resolve));
  t.after(() => srv.close());
  return { base: `http://127.0.0.1:${srv.address().port}`, seen };
}

class Harness {
  constructor(env = {}) {
    this.proc = null;
    this.env = env;
    this.tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'relay-verify-fwd-'));
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
        MUX_HOST_TOKEN: 'fixture-host-token',
        MUX_BRIDGE_TOKEN: 'fixture-bridge-token',
        MUX_TEST_MODE: '1',
        MUX_TEST_FIXTURE: '1',
        MUX_BIND_HOST: '127.0.0.1',
        MUX_AUTOHEAL: '0',
        MUX_STATE_DIR: this.tmp,
        HLAUTH_BASE: 'http://127.0.0.1:1',
        ...this.env,
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    this.proc.stdout.on('data', d => { this.stdout += d.toString(); });
    this.proc.stderr.on('data', d => { this.stderr += d.toString(); });
    await waitFor(() => this.stdout.includes(`multiplex-app on 127.0.0.1:${this.port}`), 'relay start');
  }

  async stop() {
    if (this.proc && this.proc.exitCode === null) {
      this.proc.kill();
      await Promise.race([once(this.proc, 'exit'), sleep(2000)]);
      if (this.proc.exitCode === null) this.proc.kill('SIGKILL');
    }
    this.proc = null;
    fs.rmSync(this.tmp, { recursive: true, force: true });
  }

  request(method, pathName, headers = {}) {
    const opts = { method, hostname: '127.0.0.1', port: this.port, path: pathName, headers };
    return new Promise((resolve, reject) => {
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
      req.end();
    });
  }
}

async function boot(t) {
  const auth = await startAuth(t);
  const h = new Harness({ HLAUTH_BASE: auth.base });
  await h.start();
  t.after(async () => h.stop());
  return { h, auth };
}

function verifyCallFor(auth, token) {
  const hit = auth.seen.filter(s => s.url === '/internal/verify' && s.headers['x-session-token'] === token);
  assert.equal(hit.length, 1, `expected one verify call for ${token}, saw ${hit.length}`);
  return hit[0];
}

test('the verify call carries the client address and UA when the request has them', async (t) => {
  const { h, auth } = await boot(t);
  const res = await h.request('GET', '/api/archive-index', {
    cookie: 'hl_session=verify-fwd-with-ip',
    'x-real-ip': '203.0.113.7',
    'user-agent': 'OwnerBrowser/1.0',
  });
  assert.equal(res.status, 200);
  const call = verifyCallFor(auth, 'verify-fwd-with-ip');
  assert.equal(call.headers['x-forwarded-for'], '203.0.113.7');
  assert.equal(call.headers['user-agent'], 'OwnerBrowser/1.0');
});

test('the verify call carries no forwarded address when the request has none', async (t) => {
  const { h, auth } = await boot(t);
  const res = await h.request('GET', '/api/archive-index', {
    cookie: 'hl_session=verify-fwd-bare',
  });
  assert.equal(res.status, 200);
  const call = verifyCallFor(auth, 'verify-fwd-bare');
  assert.equal(call.headers['x-forwarded-for'], undefined);
  // The relay does not invent a UA, but Node's fetch adds its own `user-agent: node` when the
  // header is absent, so it cannot be absent on the wire. That is harmless here: hl-auth treats
  // a missing/mismatched UA on its internal oracle as non-evidence and refuses without revoking
  // (revocable: false), so the value only decides whether a *bound* session is confirmed.
  assert.notEqual(call.headers['user-agent'], 'OwnerBrowser/1.0');
});

test('a cached verdict does not answer for a different address', async (t) => {
  const { h, auth } = await boot(t);
  const cookie = 'hl_session=verify-fwd-same-token';
  const first = await h.request('GET', '/api/archive-index', {
    cookie, 'x-real-ip': '203.0.113.7', 'user-agent': 'OwnerBrowser/1.0',
  });
  assert.equal(first.status, 200);
  const second = await h.request('GET', '/api/archive-index', {
    cookie, 'x-real-ip': '198.51.100.9', 'user-agent': 'OwnerBrowser/1.0',
  });
  assert.equal(second.status, 200);
  const calls = auth.seen.filter(s => s.url === '/internal/verify');
  assert.equal(calls.length, 2, 'the second address must not be served from the first address cache entry');
  assert.equal(calls[1].headers['x-forwarded-for'], '198.51.100.9');
});
