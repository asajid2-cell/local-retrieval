// Archive index: the resumable-chat catalogue the desktop bridge pushes and the browser owner reads.
// The point of these tests is the TRUST BOUNDARY, not just the JSON: reaching the socket over loopback
// is not authorization. The bridge proves itself with a scoped credential; the browser proves itself
// with an owner cookie; neither credential works on the other's route, and neither is the host token.
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

// relay/node_modules is gitignored, so a fresh clone or a recreated git worktree has none — and this
// suite spawns the real server.js, which needs express+ws. The verifier runs this file directly, with
// no `npm ci` step in front of it, so resolve-or-install here instead of dying on a missing module.
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
const WebSocket = require('ws');

const HOST_TOKEN = 'test-host-token';
const BRIDGE_TOKEN = 'test-bridge-token';
const OWNER_TOKEN = 'owner-session';

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

async function waitFor(fn, label, timeoutMs = 5000) {
  const started = Date.now();
  let last;
  while (Date.now() - started < timeoutMs) {
    try { last = await fn(); if (last) return last; } catch (err) { last = err; }
    await sleep(50);
  }
  throw new Error(`timed out waiting for ${label}; last=${last && last.stack || JSON.stringify(last)}`);
}

// Stand-in for hl-auth's internal verify oracle: only OWNER_TOKEN is the owner.
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
    this.tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'relay-archive-'));
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
        MUX_HOST_TOKEN: HOST_TOKEN,
        MUX_BRIDGE_TOKEN: BRIDGE_TOKEN,
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
    await waitFor(() => this.stdout.includes(`multiplex-app on 127.0.0.1:${this.port}`), 'relay start', 8000);
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

  // The desktop bridge: a scoped bearer credential, no cookie.
  push(body, token = BRIDGE_TOKEN) {
    const headers = token === null ? {} : { authorization: `Bearer ${token}` };
    return this.request('POST', '/api/archive-index', body, headers);
  }

  // The browser owner: an hl_session cookie arriving through nginx, no bearer.
  read(token = OWNER_TOKEN) {
    const headers = { 'x-forwarded-for': '203.0.113.7' };
    if (token !== null) headers.cookie = `hl_session=${token}`;
    return this.request('GET', '/api/archive-index', undefined, headers);
  }
}

async function bootWithAuth(t, env = {}) {
  const authBase = await startAuth(t);
  const h = new Harness({ HLAUTH_BASE: authBase, ...env });
  await h.start();
  t.after(async () => h.stop());
  return h;
}

function row(i, over = {}) {
  return {
    id: `chat-${i}`,
    title: `Chat number ${i}`,
    tool: i % 2 ? 'codex' : 'claude',
    cwd: `C:/work/proj-${i}`,
    workspaceLabel: `proj-${i}`,
    updatedAt: 1700000000000 + i,
    muxName: `mux-${i}`,
    resumable: true,
    ...over,
  };
}

function index(count, over = {}) {
  return {
    schemaVersion: 1,
    host: 'AHMED-PC',
    chats: Array.from({ length: count }, (_, i) => row(i + 1)),
    ...over,
  };
}

test('bridge pushes the index, the owner reads it back with freshness', async t => {
  const h = await bootWithAuth(t);

  const pushed = await h.push(index(2));
  assert.equal(pushed.status, 200);
  assert.deepEqual(
    { ok: pushed.body.ok, count: pushed.body.count },
    { ok: true, count: 2 },
  );
  assert.ok(pushed.body.updatedAt > 0);

  const got = await h.read();
  assert.equal(got.status, 200);
  assert.equal(got.body.schemaVersion, 1);
  assert.equal(got.body.host, 'AHMED-PC');
  assert.equal(got.body.count, 2);
  assert.equal(got.body.appLive, true);
  assert.ok(got.body.ageMs >= 0 && got.body.ageMs < 45000);
  assert.equal(got.body.updatedAt, pushed.body.updatedAt);
  // Field-for-field contract with the app-side builder (BuildArchiveIndexJson).
  assert.deepEqual(got.body.chats[0], {
    id: 'chat-1',
    title: 'Chat number 1',
    tool: 'codex',
    cwd: 'C:/work/proj-1',
    workspaceLabel: 'proj-1',
    updatedAt: 1700000000001,
    muxName: 'mux-1',
    resumable: true,
  });
});

test('reaching the socket is not authorization: every wrong principal is refused', async t => {
  const h = await bootWithAuth(t);
  const body = index(1);

  // Push route: bridge credential or nothing. Loopback alone, the owner cookie, and the host-link
  // token are all refused — an owner's browser must not be able to forge a bridge push.
  assert.equal((await h.push(body, null)).status, 403);
  assert.equal((await h.push(body, 'not-the-bridge-token')).status, 403);
  assert.equal((await h.push(body, HOST_TOKEN)).status, 403);
  assert.equal((await h.push(body, BRIDGE_TOKEN.toUpperCase())).status, 403);
  assert.equal(
    (await h.request('POST', '/api/archive-index', body, { cookie: `hl_session=${OWNER_TOKEN}` })).status,
    403,
  );

  // Read route: owner cookie or nothing. Loopback alone and the bridge credential are refused.
  // Refusal splits the way HTTP means it to: 401 when no owner credential was presented at all,
  // 403 when one was presented and is not the owner's. Presenting a bridge bearer instead of a
  // cookie is the former — the bridge credential buys nothing on a browser route.
  assert.equal((await h.read(null)).status, 401);
  assert.equal((await h.read('some-other-user')).status, 403);
  assert.equal(
    (await h.request('GET', '/api/archive-index', undefined, {
      'x-forwarded-for': '203.0.113.7',
      authorization: `Bearer ${BRIDGE_TOKEN}`,
    })).status,
    401,
  );

  // The load-bearing one for the read route: a BARE loopback GET carries no forwarding header, so the
  // process-wide gate waves it through as "trusted local". The route itself must still refuse it —
  // otherwise deleting its owner check would leave the whole suite green. Same for a bare loopback push.
  assert.equal((await h.request('GET', '/api/archive-index', undefined, {})).status, 403);
  assert.equal((await h.request('POST', '/api/archive-index', body, {})).status, 403);

  // None of those refusals may have left state behind.
  const got = await h.read();
  assert.equal(got.status, 200);
  assert.deepEqual(got.body.chats, []);
  assert.equal(got.body.updatedAt, 0);
  assert.equal(got.body.ageMs, null);
  assert.equal(got.body.appLive, false);
});

test('the bridge credential cannot open the host link', async t => {
  const h = await bootWithAuth(t);

  const rejected = new WebSocket(`ws://127.0.0.1:${h.port}/host?token=${BRIDGE_TOKEN}`);
  const outcome = await new Promise(resolve => {
    rejected.on('open', () => resolve('open'));
    rejected.on('error', () => resolve('rejected'));
    rejected.on('close', () => resolve('rejected'));
  });
  assert.equal(outcome, 'rejected');

  // Control: the real host token still opens it, so the refusal above is about the credential.
  const accepted = new WebSocket(`ws://127.0.0.1:${h.port}/host?token=${HOST_TOKEN}`);
  await once(accepted, 'open');
  accepted.close();
});

test('the index survives a relay restart; liveness does not', async t => {
  const h = await bootWithAuth(t);
  const pushed = await h.push(index(3));
  assert.equal(pushed.status, 200);

  await h.restart();

  const after = await h.read();
  assert.equal(after.status, 200);
  assert.equal(after.body.count, 3);
  assert.deepEqual(after.body.chats.map(c => c.id), ['chat-1', 'chat-2', 'chat-3']);
  assert.equal(after.body.host, 'AHMED-PC');
  // Data recency survives the restart; the claim that the app is answering does not.
  assert.equal(after.body.updatedAt, pushed.body.updatedAt);
  assert.ok(after.body.ageMs >= 0);
  assert.equal(after.body.appLive, false);

  const repushed = await h.push(index(1));
  assert.equal(repushed.status, 200);
  const live = await h.read();
  assert.equal(live.body.appLive, true);
  assert.equal(live.body.count, 1);
});

test('oversized and malformed pushes are rejected without clobbering the good index', async t => {
  const h = await bootWithAuth(t);
  assert.equal((await h.push(index(2))).status, 200);

  assert.equal((await h.push(index(1, { schemaVersion: 2 }))).status, 409);
  assert.equal((await h.push({ schemaVersion: 1 })).status, 400);
  assert.equal((await h.push({ schemaVersion: 1, chats: { id: 'x' } })).status, 400);

  // Row cap: 500 is the contract, 501 is a refusal — not a silent truncation.
  const tooMany = await h.push(index(501));
  assert.equal(tooMany.status, 413);
  assert.equal(tooMany.body.maxRows, 500);

  // Byte cap, checked before any row work.
  const oversized = await h.push({
    schemaVersion: 1,
    chats: [row(1, { title: 'A'.repeat(3 * 1024 * 1024) })],
  });
  assert.equal(oversized.status, 413);
  assert.ok(oversized.body.maxBytes > 0);

  // Required fields, and ids that are not opaque.
  for (const bad of [
    { id: '' },
    { id: '../../etc/passwd' },
    { id: 'has space' },
    { title: '' },
    { tool: '' },
    { updatedAt: 0 },
    { updatedAt: 'yesterday' },
  ]) {
    const res = await h.push({ schemaVersion: 1, chats: [row(1, bad)] });
    assert.equal(res.status, 400, `expected 400 for ${JSON.stringify(bad)}, got ${res.status}`);
  }

  // Executable/local-path fields stay out, loudly.
  for (const bad of [{ command: 'rm -rf /' }, { muxCommand: 'codex resume' }, { exe: 'cmd.exe' }, { path: 'C:/secrets' }]) {
    const res = await h.push({ schemaVersion: 1, chats: [row(1, bad)] });
    assert.equal(res.status, 400, `expected 400 for ${JSON.stringify(bad)}`);
  }

  const still = await h.read();
  assert.equal(still.body.count, 2);
  assert.deepEqual(still.body.chats.map(c => c.id), ['chat-1', 'chat-2']);
});

test('exactly the cap is accepted', async t => {
  const h = await bootWithAuth(t);
  const res = await h.push(index(500));
  assert.equal(res.status, 200);
  assert.equal(res.body.count, 500);
  assert.equal((await h.read()).body.count, 500);
});

test('unknown fields are dropped rather than mirrored back', async t => {
  const h = await bootWithAuth(t);
  const res = await h.push({
    schemaVersion: 1,
    host: 'AHMED-PC',
    chats: [row(1, { secretNote: 'do not mirror', muxName: 'not a mux name' })],
  });
  assert.equal(res.status, 200);
  const got = await h.read();
  assert.equal('secretNote' in got.body.chats[0], false);
  assert.equal(got.body.chats[0].muxName, '');   // strictMuxName rejects, it does not mangle
});

test('bridge routes fail closed when the credential is unset or shares the host token', async t => {
  const unset = await bootWithAuth(t, { MUX_BRIDGE_TOKEN: '' });
  const a = await unset.push(index(1), null);
  assert.equal(a.status, 503);
  assert.equal((await unset.push(index(1), BRIDGE_TOKEN)).status, 503);
  assert.deepEqual((await unset.read()).body.chats, []);

  // Interchangeable credentials are not scoped credentials.
  const shared = await bootWithAuth(t, { MUX_BRIDGE_TOKEN: HOST_TOKEN });
  assert.equal((await shared.push(index(1), HOST_TOKEN)).status, 503);
});
