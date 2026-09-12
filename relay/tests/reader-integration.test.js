// Reader view <-> LIVE relay. tests/reader-ui.test.js proves the reader's shape against a fake document
// and a stubbed fetch; nothing there ever spoke to a server, so a drifted request body would have stayed
// green while 400ing in production. This file closes that hole: it spawns the real relay/server.js and
// drives the SHIPPED relay/public/reader.js against it over real HTTP.
//
// The load-bearing one is 'the enqueue body the reader mints is accepted by the real relay' — the
// principalAuth envelope has to survive the relay's non-empty-object check, its 4096-byte JSON cap and its
// RECURSIVE forbidden-key scan, none of which a stubbed fetch can enforce. The other load-bearing one is
// the trust amendment: ok:true may only ever come from a bridge-signed terminal ack, never from the
// enqueue's own 200 — proven here by parking real pages and then acking FAILED.
//
// Self-contained harness on purpose, copied in shape from tests/transcript-fetch.test.js (which owns that
// file and is left untouched): this must pass whether or not the shared tests/harness.js has landed.
const test = require('node:test');
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const net = require('node:net');
const os = require('node:os');
const path = require('node:path');
const vm = require('node:vm');
const { once } = require('node:events');

const REPO = path.resolve(__dirname, '..');
const READER_SOURCE = fs.readFileSync(path.join(REPO, 'public', 'reader.js'), 'utf8');
const HOST_TOKEN = 'test-token';
const BRIDGE_HEADER = 'x-mux-transcript-bridge';
const PROJECTION_SCHEMA_VERSION = 3;
// The relay's own list (server.js FORBIDDEN_REMOTE_KEYS), mirrored so a drift names the offending key
// instead of only surfacing as an opaque 400.
const FORBIDDEN_REMOTE_KEYS = new Set([
  'muxcommand', 'command', 'cmd', 'cwd', 'pcpath', 'path', 'sourcepath',
  'workspace', 'workingdirectory', 'exe', 'executable', 'arguments',
]);
const PRINCIPAL_AUTH_MAX_BYTES = 4096;

// reader.js runs inside a vm context, so the objects and arrays it returns carry that context's
// intrinsics. assert/strict's deepEqual compares prototypes by reference, so a structurally identical
// reader value never matches a host-side literal. Re-root the value in this realm before comparing.
const plain = value => JSON.parse(JSON.stringify(value));

function forbiddenKeyIn(value, trail = '') {
  if (!value || typeof value !== 'object') return '';
  if (Array.isArray(value)) return value.map((v, i) => forbiddenKeyIn(v, `${trail}[${i}]`)).find(Boolean) || '';
  for (const [key, child] of Object.entries(value)) {
    if (FORBIDDEN_REMOTE_KEYS.has(String(key).toLowerCase())) return `${trail}.${key}`;
    const deeper = forbiddenKeyIn(child, `${trail}.${key}`);
    if (deeper) return deeper;
  }
  return '';
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

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

async function waitFor(fn, label, timeoutMs = 5000) {
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
    this.commandBridgeToken = crypto.randomBytes(32).toString('hex');
    this.env = env;
    this.tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'relay-reader-'));
    this.stdout = '';
    this.stderr = '';
  }

  get origin() { return `http://127.0.0.1:${this.port}`; }

  async start() {
    this.port = await freePort();
    this.proc = childProcess.spawn(process.execPath, ['server.js'], {
      cwd: REPO,
      env: {
        ...process.env,
        PORT: String(this.port),
        MUX_HOST_TOKEN: HOST_TOKEN,
        MUX_TEST_MODE: '1',
        MUX_TEST_FIXTURE: '1',
        MUX_BIND_HOST: '127.0.0.1',
        MUX_COMMAND_BRIDGE_TOKEN: this.commandBridgeToken,
        MUX_AUTOHEAL: '0',
        MUX_STATE_DIR: this.tmp,
        MUX_HOST_SB_WAIT_MS: '40',
        MUX_COMMAND_LEASE_MS: '30000',
        HLAUTH_BASE: 'http://127.0.0.1:1',
        ...this.env,
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    this.proc.stdout.on('data', d => { this.stdout += d.toString(); });
    this.proc.stderr.on('data', d => { this.stderr += d.toString(); });
    try {
      await waitFor(() => this.stdout.includes(`multiplex-app on 127.0.0.1:${this.port}`), 'relay start', 15000);
    } catch (error) {
      throw new Error(`${error.message}\n--- relay stderr ---\n${this.stderr}`);
    }
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

  async json(method, pathName, body, headers) {
    const res = await this.request(method, pathName, body, headers);
    assert.ok(res.status >= 200 && res.status < 300, `${method} ${pathName} failed: ${res.status} ${res.text}`);
    return res.body;
  }
}

async function withHarness(fn, env) {
  const h = new RelayHarness(env);
  await h.start();
  try {
    await fn(h);
  } finally {
    await h.stop();
  }
}

// --- the reader, loaded exactly as the browser gets it ------------------------------------------------
// Two deliberate departures from reader-ui.test.js's loader: fetch is node's REAL fetch (not a stub), and
// the origin the reader derives its api base from points at the live relay. `base` is reader.js's own
// documented injectable seam (reader.js:18) — a browser served from the relay resolves the empty base
// relatively, which node's fetch cannot do, so the seam is what stands in for that.
function loadReader(h, postRecord) {
  const record = postRecord || { calls: [], statuses: [] };
  const sandbox = {
    console, module: { exports: {} }, setTimeout, clearTimeout, Math, Date, JSON,
    fetch: (...args) => fetch(...args),
    crypto: { randomUUID: () => crypto.randomUUID() },
    location: { origin: h.origin, pathname: '/reader.html', href: `${h.origin}/reader.html` },
  };
  sandbox.globalThis = sandbox;
  vm.createContext(sandbox);
  vm.runInContext(READER_SOURCE, sandbox, { filename: 'reader.js' });
  const reader = sandbox.MuxReader;
  assert.ok(reader, 'reader.js must publish global.MuxReader');
  reader.base = h.origin;
  // intent-journal.js needs localStorage, which does not exist under node, so the reader's postIntent seam
  // gets a thin REAL post instead — same wire shape, and it records what the reader actually sent.
  reader.postIntent = async (url, body, label) => {
    record.calls.push({ url, body, label });
    const response = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify(body),
    });
    record.statuses.push(response.status);
    return response;
  };
  reader.posts = record;
  return reader;
}

// The always-on side of the PC link pushing its running list is what lights `bridgeLive`.
const markBridgeLive = h => h.json('POST', '/api/running',
  { schemaVersion: PROJECTION_SCHEMA_VERSION, runningSessions: [], runningVerified: true, host: 'test-pc' });

const leaseCommands = (h, owner = 'reader-bridge') =>
  h.json(
    'POST',
    '/api/app-commands/lease',
    { owner, limit: 16 },
    { 'X-Mux-Command-Bridge': h.commandBridgeToken },
  );

const ackLeased = (h, command, result) =>
  h.json(
    'POST',
    `/api/app-commands/${encodeURIComponent(command.id)}/ack`,
    { leaseToken: command.leaseToken, ...result },
    { 'X-Mux-Command-Bridge': h.commandBridgeToken },
  );

const pageBody = (sessionId, page, pages, messages) => ({ schemaVersion: 1, sessionId, page, pages, messages });

const pushPage = (h, sessionId, body, credential) =>
  h.request('POST', `/api/transcripts/${encodeURIComponent(sessionId)}`, body, { [BRIDGE_HEADER]: credential });

// Acts as the PC bridge for one transcriptfetch: lease it, park the pages with the scoped credential, then
// sign the terminal outcome. Runs concurrently with the reader's own refresh().
async function bridgeServe(h, sessionId, pages, ackOk) {
  const command = await waitFor(async () => {
    const leased = await leaseCommands(h);
    return leased.find(c => c.type === 'transcriptfetch' && c.sessionId === sessionId) || null;
  }, `the bridge to lease a transcriptfetch for ${sessionId}`, 20000);
  assert.equal(command.replayPolicy, 'read-only');
  assert.ok(command.principalAuth && typeof command.principalAuth === 'object',
    'the principal envelope must ride the lease out to the bridge');
  for (const body of pages) {
    const pushed = await pushPage(h, sessionId, body, command.bridgeToken);
    assert.equal(pushed.status, 200, `page push rejected: ${pushed.text}`);
  }
  await ackLeased(h, command, { ok: ackOk });
  return command;
}

test('readHealth() reports exactly what the live /api/projects says about the bridge', async () => {
  await withHarness(async h => {
    const reader = loadReader(h);

    const rawDark = await h.json('GET', '/api/projects');
    assert.equal(rawDark.bridgeLive, false, 'no bridge has synced yet');
    const dark = await reader.readHealth();
    assert.equal(dark.bridgeLive, rawDark.bridgeLive);
    assert.equal(dark.bridgeLive, false);
    assert.ok(!dark.unreachable, `the relay answered, so nothing is unreachable: ${JSON.stringify(dark)}`);
    assert.equal(dark.runningAgeMs, null);

    await markBridgeLive(h);

    const rawLive = await h.json('GET', '/api/projects');
    assert.equal(rawLive.bridgeLive, true);
    const lit = await reader.readHealth();
    assert.equal(lit.bridgeLive, rawLive.bridgeLive);
    assert.equal(lit.bridgeLive, true);
    assert.ok(Number.isFinite(lit.runningAgeMs) && lit.runningAgeMs >= 0 && lit.runningAgeMs < 45000,
      `runningAgeMs should track the live sync: ${lit.runningAgeMs}`);
  });
});

test('loadPages() surfaces the absent/expired state from the real 404 instead of throwing', async () => {
  await withHarness(async h => {
    const reader = loadReader(h);
    const sessionId = 's-nothing-parked';

    const raw = await h.request('GET', `/api/transcripts/${sessionId}`);
    assert.equal(raw.status, 404);
    assert.match(raw.body.error, /no transcript pages for this session/);

    const absent = await reader.loadPages(sessionId);
    assert.deepEqual(plain(absent), { pages: [], expired: true });

    // ...and the same shape is what render() turns into the "expired, press Refresh" surface.
    const doc = { createElement: tag => ({ tag, className: '', textContent: '', children: [], appendChild(c) { this.children.push(c); return c; }, setAttribute() {} }) };
    const mount = doc.createElement('div');
    assert.deepEqual(plain(reader.render(doc, mount, absent)), { count: 0, expired: true });
  });
});

test('the enqueue body refresh() mints is accepted by the real relay and stored verbatim', async () => {
  await withHarness(async h => {
    await markBridgeLive(h);
    const reader = loadReader(h);
    const sessionId = 's-envelope';

    // Nobody acks, so this settles as "requested, still pending" — but the POST itself is the assertion.
    const result = await reader.refresh(sessionId, { timeoutMs: 700, intervalMs: 50 });

    assert.equal(reader.posts.calls.length, 1, 'refresh must enqueue exactly once');
    const sent = reader.posts.calls[0];
    assert.equal(sent.url, `${h.origin}/api/app-commands`);
    assert.equal(sent.label, 'transcript-read');
    assert.equal(sent.body.type, 'transcriptfetch');
    assert.equal(sent.body.sessionId, sessionId);

    const envelope = sent.body.principalAuth;
    assert.ok(envelope && typeof envelope === 'object' && !Array.isArray(envelope) && Object.keys(envelope).length,
      'the relay rejects an empty or non-object principal envelope');
    assert.ok(Buffer.byteLength(JSON.stringify(envelope), 'utf8') <= PRINCIPAL_AUTH_MAX_BYTES);
    assert.equal(forbiddenKeyIn(sent.body, 'body'), '', 'the enqueue body must carry no command/path-shaped key');
    assert.equal(envelope.sessionId, sessionId);
    assert.equal(envelope.scheme, 'mux-owner-read-v1');
    assert.match(envelope.subject, /^[a-f0-9]{64}$/);
    assert.match(envelope.proof, /^[a-f0-9]{64}$/);
    assert.ok(envelope.expiresAt > Date.now());

    assert.equal(reader.posts.statuses[0], 200, 'the REAL relay accepted the enqueue');
    assert.equal(result.ok, false);
    assert.equal(result.requested, true);
    assert.equal(result.pending, true);
    assert.ok(result.id, 'the queued command id comes back');

    // The relay is custodian: the envelope reaches the bridge byte-identical to what the reader minted.
    const leased = await leaseCommands(h);
    const queued = leased.find(c => c.id === result.id);
    assert.ok(queued, `the enqueued command must lease: ${JSON.stringify(leased)}`);
    assert.equal(queued.type, 'transcriptfetch');
    assert.equal(queued.replayPolicy, 'read-only');
    assert.equal(queued.sessionId, sessionId);
    assert.deepEqual(queued.principalAuth, plain(envelope));

    // Non-vacuity: the same endpoint really does refuse a drifted envelope, so the 200 above is earned.
    const empty = await h.request('POST', '/api/app-commands',
      { type: 'transcriptfetch', sessionId, principalAuth: {} });
    assert.equal(empty.status, 403);
    assert.match(empty.body.error, /owner transcript grant/);
    const forbidden = await h.request('POST', '/api/app-commands',
      { type: 'transcriptfetch', sessionId, principalAuth: { ...envelope, cwd: 'C:/Users/me' } });
    assert.equal(forbidden.status, 400);
    assert.match(forbidden.body.error, /executable commands and local paths are forbidden/);
    const oversized = await h.request('POST', '/api/app-commands',
      { type: 'transcriptfetch', sessionId, principalAuth: { ...envelope, pad: 'x'.repeat(PRINCIPAL_AUTH_MAX_BYTES) } });
    assert.equal(oversized.status, 403);
  });
});

test('refresh() round-trips through a real bridge lease/push/ack and loads the pages newest-last', async () => {
  await withHarness(async h => {
    await markBridgeLive(h);
    const reader = loadReader(h);
    const sessionId = 's-roundtrip';

    // Page 2 is pushed FIRST and its messages carry OLDER timestamps than page 1's: page number has to
    // dominate both storage order and ts, or the transcript reads scrambled.
    const one = pageBody(sessionId, 1, 2, [
      { role: 'user', text: 'first', ts: 5000 },
      { role: 'assistant', text: 'second', ts: 6000 },
    ]);
    const two = pageBody(sessionId, 2, 2, [
      { role: 'tool', text: 'third', ts: 1000 },
      { role: 'assistant', text: 'fourth', ts: 2000 },
    ]);

    const [result] = await Promise.all([
      reader.refresh(sessionId, { timeoutMs: 25000, intervalMs: 50 }),
      bridgeServe(h, sessionId, [two, one], true),
    ]);

    assert.equal(result.ok, true, `refresh should succeed: ${JSON.stringify(result)}`);
    assert.equal(result.verified, true);
    assert.equal(result.detail, 'transcript ready to read here');   // relay-minted, bridge-signed
    assert.equal(result.pages.length, 2, 'both parked pages are loaded, not just the first');
    assert.equal(result.totalPages, 2);
    assert.ok(Number.isFinite(result.expiresAt) && result.expiresAt > Date.now());
    assert.deepEqual(plain(reader.orderedMessages(result.pages).map(m => m.text)),
      ['third', 'fourth', 'first', 'second']);

    const doc = { createElement: tag => ({ tag, className: '', textContent: '', children: [], appendChild(c) { this.children.push(c); return c; }, setAttribute() {} }) };
    const mount = doc.createElement('div');
    assert.deepEqual(plain(reader.render(doc, mount, result)), { count: 4, empty: false });
  });
});

test('a bridge-signed FAILED ack is never shown as success, even with pages already parked', async () => {
  await withHarness(async h => {
    await markBridgeLive(h);
    const reader = loadReader(h);
    const sessionId = 's-failed-ack';
    const parked = pageBody(sessionId, 1, 1, [{ role: 'assistant', text: 'partial capture', ts: 9000 }]);

    const [result, command] = await Promise.all([
      reader.refresh(sessionId, { timeoutMs: 25000, intervalMs: 50 }),
      bridgeServe(h, sessionId, [parked], false),
    ]);
    assert.equal((await h.json('GET', `/api/app-commands/${command.id}`)).status, 'failed');

    assert.equal(reader.posts.statuses[0], 200, 'the enqueue itself was accepted — that must not become success');
    assert.notEqual(result.ok, true, `a failed ack may never report ok:true: ${JSON.stringify(result)}`);
    assert.equal(result.ok, false);
    assert.equal(result.requested, true);
    assert.equal(result.failed, true);
    assert.equal(result.detail, 'PC bridge could not fetch the transcript');

    // The failure is not an artefact of missing data: the pages ARE readable, the reader just refuses to
    // call the fetch done without a bridge-signed 'done'.
    const stored = await reader.loadPages(sessionId);
    assert.equal(stored.pages.length, 1);
    assert.deepEqual(plain(reader.orderedMessages(stored.pages).map(m => m.text)), ['partial capture']);
  });
});
