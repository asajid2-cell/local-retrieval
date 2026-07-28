// transcriptfetch command + the transcript page store (relay side).
// Self-contained harness on purpose: this file must pass whether or not the shared tests/harness.js
// extraction has landed yet, so it carries the minimum relay-spawning it needs and nothing more.
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

const REPO = path.resolve(__dirname, '..');
const HOST_TOKEN = 'test-token';
// Derived from the host credential exactly as server.js derives it: scoped to page pushes, never equal
// to the host token, and it buys no read back.
const BRIDGE_TOKEN = crypto.createHash('sha256')
  .update('mux-transcript-bridge:v1:' + HOST_TOKEN).digest('hex');
const BRIDGE_HEADER = 'x-mux-transcript-bridge';
const PUBLIC = { 'x-forwarded-for': '8.8.8.8' };   // defeats isTrustedLocal => the owner gate applies
const PRINCIPAL = { scheme: 'mux-principal-v1', subject: 'owner:test', nonce: 'n-1', sig: 'deadbeef' };

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
    this.env = env;
    this.tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'relay-transcript-'));
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
        MUX_TEST_MODE: '1',
        MUX_AUTOHEAL: '0',
        MUX_STATE_DIR: this.tmp,
        MUX_HOST_SB_WAIT_MS: '40',
        MUX_COMMAND_LEASE_MS: '5000',
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

const leaseCommands = (h, owner = 'test-consumer') =>
  h.json('POST', '/api/app-commands/lease', { owner, limit: 16 });

const ackLeased = (h, command, result) =>
  h.json('POST', `/api/app-commands/${encodeURIComponent(command.id)}/ack`, { leaseToken: command.leaseToken, ...result });

const enqueueFetch = (h, sessionId, extra = {}) =>
  h.request('POST', '/api/app-commands', { type: 'transcriptfetch', sessionId, principalAuth: PRINCIPAL, ...extra });

const pageBody = (sessionId, page, pages, messages) => ({ schemaVersion: 1, sessionId, page, pages, messages });

const pushPage = (h, sessionId, body, headers = { [BRIDGE_HEADER]: BRIDGE_TOKEN }) =>
  h.request('POST', `/api/transcripts/${encodeURIComponent(sessionId)}`, body, headers);

test('transcriptfetch enqueues read-only, needs sessionId + a principal envelope, and round-trips a lease/ack', async () => {
  await withHarness(async h => {
    const noSession = await h.request('POST', '/api/app-commands', { type: 'transcriptfetch', principalAuth: PRINCIPAL });
    assert.equal(noSession.status, 400);
    assert.match(noSession.body.error, /sessionId required/);

    const noPrincipal = await h.request('POST', '/api/app-commands', { type: 'transcriptfetch', sessionId: 's-alpha' });
    assert.equal(noPrincipal.status, 400);
    assert.match(noPrincipal.body.error, /principal auth envelope/);

    const ok = await enqueueFetch(h, 's-alpha');
    assert.equal(ok.status, 200);
    assert.equal(ok.body.status, 'pending');
    const failing = await enqueueFetch(h, 's-beta');
    assert.equal(failing.status, 200);

    const leased = await leaseCommands(h);
    const alpha = leased.find(c => c.id === ok.body.id);
    const beta = leased.find(c => c.id === failing.body.id);
    assert.ok(alpha && beta, 'both transcriptfetch commands lease');
    assert.equal(alpha.type, 'transcriptfetch');
    assert.equal(alpha.replayPolicy, 'read-only');
    assert.equal(alpha.sessionId, 's-alpha');
    // The principal envelope rides the lease back unchanged — the relay is custodian, not verifier.
    assert.deepEqual(alpha.principalAuth, PRINCIPAL);

    await ackLeased(h, alpha, { ok: true });
    await ackLeased(h, beta, { ok: false });
    const done = await h.json('GET', `/api/app-commands/${alpha.id}`);
    assert.equal(done.status, 'done');
    assert.equal(done.detail, 'transcript ready to read here');
    const failed = await h.json('GET', `/api/app-commands/${beta.id}`);
    assert.equal(failed.status, 'failed');
    assert.equal(failed.detail, 'PC bridge could not fetch the transcript');
  });
});

test('a bridge-credentialed page push is stored and served owner-gated', async () => {
  await withHarness(async h => {
    const sessionId = 's-pages';
    assert.equal((await enqueueFetch(h, sessionId)).status, 200);

    const first = await pushPage(h, sessionId, pageBody(sessionId, 1, 2, [
      { role: 'user', text: 'hello there', ts: 1700000000000 },
      { role: 'assistant', text: 'hi' },
    ]));
    assert.equal(first.status, 200, first.text);
    assert.equal(first.body.page, 1);
    assert.equal(first.body.pages, 2);
    assert.deepEqual(first.body.storedPages, [1]);

    const second = await pushPage(h, sessionId, pageBody(sessionId, 2, 2, [{ role: 'user', text: 'page two' }]));
    assert.equal(second.status, 200, second.text);
    assert.deepEqual(second.body.storedPages, [1, 2]);

    const page2 = await h.json('GET', `/api/transcripts/${sessionId}?page=2`);
    assert.equal(page2.schemaVersion, 1);
    assert.equal(page2.page, 2);
    assert.equal(page2.pages, 2);
    assert.deepEqual(page2.availablePages, [1, 2]);
    assert.deepEqual(page2.messages, [{ role: 'user', text: 'page two' }]);

    const dflt = await h.json('GET', `/api/transcripts/${sessionId}`);
    assert.equal(dflt.page, 1);
    assert.deepEqual(dflt.messages[0], { role: 'user', text: 'hello there', ts: 1700000000000 });

    const missing = await h.request('GET', `/api/transcripts/${sessionId}?page=7`);
    assert.equal(missing.status, 404);
    const unknown = await h.request('GET', '/api/transcripts/s-never-asked');
    assert.equal(unknown.status, 404);

    // The read side is owner-only: a public caller is turned away, and the bridge credential buys nothing here.
    const publicRead = await h.request('GET', `/api/transcripts/${sessionId}`, undefined, PUBLIC);
    assert.equal(publicRead.status, 401);
    const bridgeRead = await h.request('GET', `/api/transcripts/${sessionId}`, undefined,
      { ...PUBLIC, [BRIDGE_HEADER]: BRIDGE_TOKEN });
    assert.equal(bridgeRead.status, 401);
  });
});

test('page pushes without the scoped credential, or with the wrong one, are rejected', async () => {
  await withHarness(async h => {
    const sessionId = 's-authz';
    assert.equal((await enqueueFetch(h, sessionId)).status, 200);
    const body = pageBody(sessionId, 1, 1, [{ role: 'user', text: 'secret' }]);

    const bare = await pushPage(h, sessionId, body, {});
    assert.equal(bare.status, 403);
    assert.match(bare.body.error, /scoped transcript bridge credential/);

    const wrong = await pushPage(h, sessionId, body, { [BRIDGE_HEADER]: 'a'.repeat(BRIDGE_TOKEN.length) });
    assert.equal(wrong.status, 403);

    // The host token itself is NOT the bridge credential.
    assert.equal((await pushPage(h, sessionId, body, { [BRIDGE_HEADER]: HOST_TOKEN })).status, 403);

    // From off-box the global owner gate fires first, before the route is ever reached.
    assert.equal((await pushPage(h, sessionId, body, PUBLIC)).status, 401);
    assert.equal((await pushPage(h, sessionId, body, { ...PUBLIC, [BRIDGE_HEADER]: 'nope' })).status, 401);
    // ...but a correctly credentialed push from off-box is exactly what the bypass is for.
    assert.equal((await pushPage(h, sessionId, body, { ...PUBLIC, [BRIDGE_HEADER]: BRIDGE_TOKEN })).status, 200);

    assert.equal((await h.json('GET', `/api/transcripts/${sessionId}`)).messages.length, 1);
  });
});

test('a page for a session nobody requested is refused, and bad shapes are rejected', async () => {
  await withHarness(async h => {
    const unasked = await pushPage(h, 's-unasked', pageBody('s-unasked', 1, 1, [{ role: 'user', text: 'x' }]));
    assert.equal(unasked.status, 409);
    assert.match(unasked.body.error, /no live transcriptfetch/);

    const sessionId = 's-shapes';
    assert.equal((await enqueueFetch(h, sessionId)).status, 200);
    const bad = [
      [{ schemaVersion: 9, sessionId, page: 1, pages: 1, messages: [] }, /schema version/],
      [pageBody('s-other', 1, 1, []), /session identity/],
      [pageBody(sessionId, 0, 1, []), /page must be an integer/],
      [pageBody(sessionId, 2, 1, []), /page must be an integer/],
      [pageBody(sessionId, 1, 99, []), /pages must be an integer/],
      [pageBody(sessionId, 1, 1, 'nope'), /messages must be an array/],
      [pageBody(sessionId, 1, 1, [{ text: 'no role' }]), /role/],
      [pageBody(sessionId, 1, 1, [{ role: 'user', text: 'x', ts: 'soon' }]), /ts must be numeric/],
    ];
    for (const [body, pattern] of bad) {
      const res = await pushPage(h, sessionId, body);
      assert.equal(res.status, 400, `expected 400 for ${JSON.stringify(body).slice(0, 80)}: got ${res.text}`);
      assert.match(res.body.error, pattern);
    }
    assert.equal((await h.request('GET', `/api/transcripts/${sessionId}`)).status, 404);
  });
});

test('an oversized page is rejected and leaves the stored capture intact', async () => {
  await withHarness(async h => {
    const sessionId = 's-huge';
    assert.equal((await enqueueFetch(h, sessionId)).status, 200);
    assert.equal((await pushPage(h, sessionId, pageBody(sessionId, 1, 2, [{ role: 'user', text: 'small' }]))).status, 200);

    const huge = await pushPage(h, sessionId, pageBody(sessionId, 2, 2, [{ role: 'assistant', text: 'z'.repeat(300 * 1024) }]));
    assert.equal(huge.status, 413, huge.text);
    assert.match(huge.body.error, /too large/);

    const kept = await h.json('GET', `/api/transcripts/${sessionId}`);
    assert.deepEqual(kept.availablePages, [1]);
    assert.equal(kept.messages[0].text, 'small');
  });
});

test('stored pages survive a relay restart', async () => {
  await withHarness(async h => {
    const sessionId = 's-durable';
    assert.equal((await enqueueFetch(h, sessionId)).status, 200);
    assert.equal((await pushPage(h, sessionId, pageBody(sessionId, 1, 2, [{ role: 'user', text: 'before restart' }]))).status, 200);
    assert.equal((await pushPage(h, sessionId, pageBody(sessionId, 2, 2, [{ role: 'assistant', text: 'second', ts: 42 }]))).status, 200);

    await h.restart();

    const after = await h.json('GET', `/api/transcripts/${sessionId}?page=2`);
    assert.equal(after.pages, 2);
    assert.deepEqual(after.availablePages, [1, 2]);
    assert.deepEqual(after.messages, [{ role: 'assistant', text: 'second', ts: 42 }]);
    assert.equal((await h.json('GET', `/api/transcripts/${sessionId}?page=1`)).messages[0].text, 'before restart');

    // The authorizing command survived too, so a further page still lands on the same capture.
    assert.equal((await pushPage(h, sessionId, pageBody(sessionId, 1, 2, [{ role: 'user', text: 'repaged' }]))).status, 200);
    assert.equal((await h.json('GET', `/api/transcripts/${sessionId}?page=1`)).messages[0].text, 'repaged');
  });
});

test('a newer transcriptfetch discards the previous capture for that session', async () => {
  await withHarness(async h => {
    const sessionId = 's-refetch';
    assert.equal((await enqueueFetch(h, sessionId)).status, 200);
    assert.equal((await pushPage(h, sessionId, pageBody(sessionId, 1, 2, [{ role: 'user', text: 'old capture' }]))).status, 200);
    assert.equal((await pushPage(h, sessionId, pageBody(sessionId, 2, 2, [{ role: 'user', text: 'old page two' }]))).status, 200);

    await sleep(5);
    assert.equal((await enqueueFetch(h, sessionId, { intentId: 'refetch-1' })).status, 200);
    const fresh = await pushPage(h, sessionId, pageBody(sessionId, 1, 1, [{ role: 'user', text: 'new capture' }]));
    assert.equal(fresh.status, 200);
    assert.deepEqual(fresh.body.storedPages, [1]);

    const read = await h.json('GET', `/api/transcripts/${sessionId}`);
    assert.deepEqual(read.availablePages, [1]);
    assert.equal(read.messages[0].text, 'new capture');
    assert.equal((await h.request('GET', `/api/transcripts/${sessionId}?page=2`)).status, 404);
  });
});

test('a capture is dropped once its retention window closes', async () => {
  await withHarness(async h => {
    const sessionId = 's-ttl';
    assert.equal((await enqueueFetch(h, sessionId)).status, 200);
    assert.equal((await pushPage(h, sessionId, pageBody(sessionId, 1, 1, [{ role: 'user', text: 'ephemeral' }]))).status, 200);
    assert.equal((await h.request('GET', `/api/transcripts/${sessionId}`)).status, 200);
    await waitFor(async () => (await h.request('GET', `/api/transcripts/${sessionId}`)).status === 404,
      'transcript capture to expire', 8000);
    // With the authorizing fetch aged out too, nothing may re-park pages for that session.
    const late = await pushPage(h, sessionId, pageBody(sessionId, 1, 1, [{ role: 'user', text: 'too late' }]));
    assert.equal(late.status, 409);
  }, { MUX_TRANSCRIPT_TTL_MS: '1500' });
});
