// State retention: the global fence over the transcript page store and the app-command journal.
// Self-contained harness on purpose (same reasoning as transcript-fetch.test.js): this file must pass
// whether or not a shared tests/harness.js has landed, so it carries its own relay-spawning.
//
// The state files are fabricated on disk BEFORE the relay boots. That is deliberate: durableJsonLoad
// reads plain JSON with no checksum sidecar, so writing the file is exactly what a relay that had been
// running for months would have left behind. Booting against it exercises the real boot sweep rather
// than a hand-called function.
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
const BRIDGE_TOKEN = crypto.createHash('sha256')
  .update('mux-transcript-bridge:v1:' + HOST_TOKEN).digest('hex');
const BRIDGE_HEADER = 'x-mux-transcript-bridge';
const PRINCIPAL = { scheme: 'mux-principal-v1', subject: 'owner:test', nonce: 'n-1', sig: 'deadbeef' };

const DAY_MS = 24 * 60 * 60 * 1000;

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
    this.tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'relay-retention-'));
    this.stdout = '';
    this.stderr = '';
  }

  stateFile(name) { return path.join(this.tmp, name); }

  // Seed a state file the way a long-lived relay would have left it, before the process ever starts.
  seed(name, value) { fs.writeFileSync(this.stateFile(name), JSON.stringify(value)); }

  readState(name) {
    try { return JSON.parse(fs.readFileSync(this.stateFile(name), 'utf8')); } catch { return null; }
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

  health() { return this.json('GET', '/api/health'); }

  async retention() {
    const health = await this.health();
    assert.ok(health.retention, 'health must carry retention gauges');
    return health;
  }
}

// Seed first, then boot: the constructor makes the state dir, so the files are in place for the
// boot-time reload and the boot sweep that follows it.
async function withSeededHarness(seed, fn, env) {
  const h = new RelayHarness(env);
  try {
    if (seed) seed(h);
    await h.start();
    await fn(h);
  } finally {
    await h.stop();
  }
}

// ---- fabricators --------------------------------------------------------------------------------
// A page's byte weight is stamped exactly the way the push path stamps it, so a fabricated store
// weighs what a genuinely pushed one would.
function makePage(page, textLength) {
  const stored = { page, bytes: 0, messages: [{ role: 'assistant', text: 'x'.repeat(textLength), ts: 1 }] };
  stored.bytes = Buffer.byteLength(JSON.stringify(stored), 'utf8');
  return stored;
}

// index 0 is the NEWEST session; updatedAt walks backwards a minute at a time.
function makeTranscript(index, textLength, now) {
  const pageList = [makePage(1, textLength)];
  return {
    sessionId: `s-${String(index).padStart(3, '0')}`,
    fetchId: `f-${String(index).padStart(3, '0')}`,
    pages: 1,
    bytes: pageList[0].bytes,
    updatedAt: now - index * 60000,
    expiresAt: now + 60 * 60 * 1000,          // far outside the TTL, so nothing is dropped as expired
    pageList,
  };
}

function terminalCommand(index, now, extra = {}) {
  const at = now - (20 * DAY_MS) - index * 60000;   // every one of these is past the 14-day horizon
  return {
    id: `c-${String(index).padStart(4, '0')}`,
    intentId: `i-${String(index).padStart(4, '0')}`,
    type: 'transcript',
    sessionId: `s-old-${index}`,
    status: index % 2 === 0 ? 'done' : 'failed',
    ts: at,
    doneAt: at,
    ...extra,
  };
}

function inFlightCommand(index, now) {
  const leased = index % 2 === 1;
  return {
    id: `live-${index}`,
    intentId: `live-intent-${index}`,
    type: 'transcript',
    sessionId: `s-live-${index}`,
    status: leased ? 'leased' : 'pending',
    // Aged far past the horizon on purpose: the ONLY thing keeping these is their non-terminal state.
    ts: now - 900 * DAY_MS,
    doneAt: 0,
    ...(leased ? { leaseOwner: 'test-consumer', leaseToken: `lt-${index}`, leaseExpiresAt: now + 10 * DAY_MS } : {}),
  };
}

function walkBytes(dir) {
  let total = 0;
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) total += walkBytes(full);
    else if (entry.isFile()) { try { total += fs.statSync(full).size; } catch {} }
  }
  return total;
}

// ---- transcript page store ------------------------------------------------------------------------
test('boot sweep holds the transcript store to the newest N sessions and drops the oldest', async () => {
  const now = Date.now();
  const seeded = Array.from({ length: 25 }, (_, i) => makeTranscript(i, 200 + i * 40, now));
  await withSeededHarness(h => h.seed('transcripts.json', seeded), async h => {
    const health = await h.retention();
    const store = health.retention.stores.transcripts;
    assert.equal(store.sessions, 20, 'exactly the newest 20 sessions survive');
    assert.equal(health.retention.caps.transcriptMaxSessions, 20);
    assert.ok(store.contentBytes <= health.retention.caps.transcriptMaxBytes, 'total is under the byte cap');
    assert.equal(health.retention.dropped.transcriptSessions, 5);

    // The survivors are the newest by fetch time — s-000..s-019 — and the oldest five are gone from disk.
    const persisted = h.readState('transcripts.json');
    const kept = persisted.map(r => r.sessionId).sort();
    assert.deepEqual(kept, seeded.slice(0, 20).map(r => r.sessionId).sort());
    for (const gone of seeded.slice(20)) {
      assert.ok(!kept.includes(gone.sessionId), `${gone.sessionId} must be pruned`);
      const res = await h.request('GET', `/api/transcripts/${gone.sessionId}`);
      assert.equal(res.status, 404, 'a pruned session is unreadable');
    }
    const survivor = await h.json('GET', '/api/transcripts/s-000?page=1');
    assert.equal(survivor.sessionId, 's-000', 'a kept session still pages');
  });
});

test('the byte cap drops every older session once the budget overflows, small ones included', async () => {
  const now = Date.now();
  // Newest four are heavy, the rest are light: proves the fence does not greedily backfill small old
  // sessions behind a newer one that did not fit. An older capture can never outrank a newer one.
  const seeded = Array.from({ length: 25 }, (_, i) => makeTranscript(i, i < 4 ? 4000 : 300, now));
  const sizes = seeded.map(r => r.pageList[0].bytes);
  const cap = sizes[0] + sizes[1] + sizes[2] + 10;      // room for three, not for the fourth
  assert.ok(sizes[3] > 10, 'the fourth session must genuinely not fit');
  await withSeededHarness(
    h => h.seed('transcripts.json', seeded),
    async h => {
      const health = await h.retention();
      const store = health.retention.stores.transcripts;
      assert.equal(health.retention.caps.transcriptMaxBytes, cap);
      assert.equal(store.sessions, 3, 'only what fits the byte budget survives');
      assert.ok(store.contentBytes <= cap);
      const kept = h.readState('transcripts.json').map(r => r.sessionId);
      assert.deepEqual(kept.sort(), ['s-000', 's-001', 's-002']);
    },
    { MUX_RETENTION_TRANSCRIPT_MAX_BYTES: String(cap) },
  );
});

test('a transcript push is fenced inline at accept time, not left to the next timer tick', async () => {
  const now = Date.now();
  const page = makePage(1, 3000);
  const cap = page.bytes * 2 + 10;                       // two captures fit, a third cannot
  const seeded = [0, 1].map(i => makeTranscript(i, 3000, now));
  await withSeededHarness(
    h => h.seed('transcripts.json', seeded),
    async h => {
      const before = await h.retention();
      assert.equal(before.retention.stores.transcripts.sessions, 2);

      // Authorize and push a third session. The push path's own cap is 8 sessions, so only the global
      // byte fence can bite here — and it must bite on this request, before the response returns.
      const fetchCmd = await h.request('POST', '/api/app-commands',
        { type: 'transcriptfetch', sessionId: 's-new', principalAuth: PRINCIPAL });
      assert.equal(fetchCmd.status, 200);
      const push = await h.request('POST', '/api/transcripts/s-new',
        { schemaVersion: 1, sessionId: 's-new', page: 1, pages: 1, messages: [{ role: 'assistant', text: 'x'.repeat(3000), ts: 1 }] },
        { [BRIDGE_HEADER]: BRIDGE_TOKEN });
      assert.equal(push.status, 200, push.text);

      // Read the file straight off disk: the commit that answered the push is already at-bounds.
      const persisted = h.readState('transcripts.json');
      const bytes = persisted.reduce((n, r) => n + r.pageList.reduce((m, p) => m + p.bytes, 0), 0);
      assert.ok(bytes <= cap, `store is ${bytes} bytes, over the ${cap} cap right after the push`);
      assert.ok(persisted.some(r => r.sessionId === 's-new'), 'the accepted push is what survived');
      assert.ok(!persisted.some(r => r.sessionId === 's-001'), 'the oldest capture made room for it');
    },
    { MUX_RETENTION_TRANSCRIPT_MAX_BYTES: String(cap) },
  );
});

// ---- app command journal ---------------------------------------------------------------------------
test('aged terminal commands prune to the newest-200 floor and no in-flight command is ever touched', async () => {
  const now = Date.now();
  const terminal = Array.from({ length: 500 }, (_, i) => terminalCommand(i, now));
  const inFlight = Array.from({ length: 5 }, (_, i) => inFlightCommand(i, now));
  await withSeededHarness(h => h.seed('app-commands.json', [...inFlight, ...terminal]), async h => {
    const health = await h.retention();
    const gauge = health.retention.stores.commands;
    assert.equal(health.retention.caps.commandTerminalFloor, 200);
    assert.equal(health.retention.caps.commandTerminalMaxAgeMs, 14 * DAY_MS);
    assert.equal(gauge.terminal, 200, 'the newest-200 floor holds and nothing beyond it survives');
    assert.equal(gauge.inFlight, 5, 'every non-terminal command survives');
    assert.equal(gauge.count, 205);
    assert.equal(health.retention.dropped.commands, 300);

    const persisted = h.readState('app-commands.json');
    const ids = new Set(persisted.map(c => c.id));
    for (const live of inFlight)
      assert.ok(ids.has(live.id), `${live.id} is in flight and must never be pruned, however old`);
    // "Newest 200" is by terminal time, so exactly c-0000..c-0199 survive.
    for (const c of terminal.slice(0, 200)) assert.ok(ids.has(c.id), `${c.id} is inside the floor`);
    for (const c of terminal.slice(200)) assert.ok(!ids.has(c.id), `${c.id} is past horizon and floor`);
    assert.ok(persisted.every(c => c.status !== 'done' || c.doneAt > 0));
  });
});

test('dedupe still answers inside the horizon; pruning past it is what re-opens an intent id', async () => {
  const now = Date.now();
  // c-0499 is the single oldest terminal record, so it falls outside the newest-200 floor and is pruned.
  const terminal = Array.from({ length: 500 }, (_, i) => terminalCommand(i, now));
  const reopened = terminal[499];
  await withSeededHarness(h => h.seed('app-commands.json', terminal), async h => {
    // Inside the horizon: a live enqueue re-sent under the same intent id is deduplicated, not requeued.
    const first = await h.request('POST', '/api/app-commands',
      { type: 'transcript', sessionId: 's-dedupe', intentId: 'intent-fresh' });
    assert.equal(first.status, 200);
    assert.equal(first.body.deduplicated, false);
    const replay = await h.request('POST', '/api/app-commands',
      { type: 'transcript', sessionId: 's-dedupe', intentId: 'intent-fresh' });
    assert.equal(replay.status, 200);
    assert.equal(replay.body.deduplicated, true, 'the record is still there, so the intent still dedupes');
    assert.equal(replay.body.id, first.body.id, 'and it resolves to the same command');

    // Past the horizon: the record is gone, so its intent id is replayable again. That is the documented
    // trade — the client intent journal retries over MINUTES (3 attempts, 250/750ms backoff, 256 records),
    // so 14 days is orders of magnitude beyond any redelivery that could still arrive. A 200 rather than
    // a 409 is the proof the old record is gone: a surviving record would either dedupe or conflict.
    const replayed = await h.request('POST', '/api/app-commands',
      { type: 'transcript', sessionId: reopened.sessionId, intentId: reopened.intentId });
    assert.equal(replayed.status, 200, replayed.text);
    assert.equal(replayed.body.deduplicated, false, 'a pruned intent id is free to be used again');
    assert.equal(replayed.body.status, 'pending', 'and it enqueues as fresh work');
  });
});

// ---- already-bounded stores: assert, never prune ------------------------------------------------------
test('the sweep asserts the bounded stores instead of pruning them, and reports a real breach', async () => {
  const now = Date.now();
  const archive = { chats: Array.from({ length: 40 }, (_, i) => ({ id: `a-${i}`, at: now - i })) };
  await withSeededHarness(h => {
    h.seed('archive-index.json', archive);
    h.seed('attention-episodes.json', { episodes: [{ id: 'e-1' }] });
  }, async h => {
    const health = await h.retention();
    const stores = health.retention.stores;
    assert.equal(stores.archiveIndex.present, true);
    assert.equal(stores.archiveIndex.count, 40);
    assert.ok(stores.archiveIndex.bytes > 0);
    assert.equal(stores.attentionEpisodes.count, 1);
    // Absent = the leaf that owns it has not landed in this build. That is not a violation.
    assert.equal(stores.deferredSends.present, false);
    assert.equal(stores.deferredSends.count, null);
    assert.equal(health.retention.invariants.ok, true);
    assert.deepEqual(health.retention.invariants.violations, []);

    // Untouched across sweeps: retention gets no second opinion on a store another leaf owns.
    const beforeBytes = fs.statSync(h.stateFile('archive-index.json')).size;
    await sleep(700);                                        // several sweeps at the 250ms test interval
    assert.deepEqual(h.readState('archive-index.json'), archive, 'the sweep prunes nothing here');
    assert.equal(fs.statSync(h.stateFile('archive-index.json')).size, beforeBytes);
    const after = await h.retention();
    assert.ok(after.retention.sweeps > health.retention.sweeps, 'sweeps really did run in between');
  });
});

test('a bounded store over its own cap surfaces as an invariant violation, still unpruned', async () => {
  const overCap = { chats: Array.from({ length: 501 }, (_, i) => ({ id: `a-${i}` })) };
  await withSeededHarness(h => h.seed('archive-index.json', overCap), async h => {
    const health = await h.retention();
    assert.equal(health.retention.invariants.ok, false);
    assert.equal(health.retention.stores.archiveIndex.count, 501);
    assert.ok(
      health.retention.invariants.violations.some(v => /archiveIndex.*501.*500/.test(v)),
      `expected an archiveIndex row-count violation, got ${JSON.stringify(health.retention.invariants.violations)}`,
    );
    assert.deepEqual(h.readState('archive-index.json'), overCap, 'observed, not silently rewritten');
  });
});

// ---- gauges -------------------------------------------------------------------------------------------
test('health exposes stateDirBytes plus accurate per-store gauges, plaintext split from signed envelopes', async () => {
  const now = Date.now();
  const seeded = Array.from({ length: 3 }, (_, i) => makeTranscript(i, 500, now));
  const contentBytes = seeded.reduce((n, r) => n + r.pageList[0].bytes, 0);
  await withSeededHarness(h => h.seed('transcripts.json', seeded), async h => {
    // One command carrying a signed envelope, so the custody gauge has something real to count.
    const enqueued = await h.request('POST', '/api/app-commands',
      { type: 'transcriptfetch', sessionId: 's-000', principalAuth: PRINCIPAL });
    assert.equal(enqueued.status, 200);

    const lower = walkBytes(h.tmp);
    const health = await h.health();
    const upper = walkBytes(h.tmp);
    const r = health.retention;

    assert.equal(typeof health.stateDirBytes, 'number');
    assert.equal(health.stateDirBytes, r.stateDirBytes, 'the top-level field mirrors the gauge');
    assert.ok(r.stateDirBytes >= lower && r.stateDirBytes <= upper,
      `stateDirBytes ${r.stateDirBytes} outside the observed [${lower}, ${upper}]`);
    assert.ok(r.stateDirFiles >= 2);

    // Per-store size and count, checked against the bytes actually on disk.
    assert.equal(r.stores.transcripts.sessions, 3);
    assert.equal(r.stores.transcripts.pages, 3);
    assert.equal(r.stores.transcripts.contentBytes, contentBytes);
    assert.equal(r.stores.transcripts.bytes, fs.statSync(h.stateFile('transcripts.json')).size);
    assert.equal(r.stores.commands.count, 1);
    assert.equal(r.stores.commands.inFlight, 1);
    assert.equal(r.stores.commands.terminal, 0);
    assert.equal(r.stores.commands.bytes, fs.statSync(h.stateFile('app-commands.json')).size);
    for (const name of ['projects', 'pins', 'renameIntents', 'uploadsMeta', 'uploads'])
      assert.ok(r.stores[name], `missing gauge for ${name}`);

    // TRUST AMENDMENT: plaintext chat content and opaque signed-envelope custody are reported as two
    // separate numbers. Collapsing them would hide the only one that is actually sensitive.
    assert.equal(r.exposure.plaintextBytes, contentBytes, 'plaintext exposure is the transcript weight');
    assert.ok(r.exposure.signedEnvelopeBytes > 0, 'the envelope we hold in custody is counted');
    assert.notEqual(r.exposure.plaintextBytes, r.exposure.signedEnvelopeBytes);

    // Nothing that could be a key or a reusable credential is parked in the journal.
    const journal = JSON.stringify(h.readState('app-commands.json'));
    for (const forbidden of ['privateKey', 'pairingSecret', 'identitySecret', 'BEGIN PRIVATE KEY', HOST_TOKEN])
      assert.ok(!journal.includes(forbidden), `journal must not persist ${forbidden}`);

    assert.equal(r.intervalMs, 250, 'the sweep interval is shrinkable under MUX_TEST_MODE');
    assert.ok(r.sweeps >= 1, 'the boot sweep already ran');
    assert.equal(r.lastError, '');
  });
});

// ---- convergence ----------------------------------------------------------------------------------------
test('restart mid-state converges to the same bounds', async () => {
  const now = Date.now();
  const transcripts = Array.from({ length: 25 }, (_, i) => makeTranscript(i, 400, now));
  const commands = [
    ...Array.from({ length: 5 }, (_, i) => inFlightCommand(i, now)),
    ...Array.from({ length: 500 }, (_, i) => terminalCommand(i, now)),
  ];
  await withSeededHarness(h => {
    h.seed('transcripts.json', transcripts);
    h.seed('app-commands.json', commands);
  }, async h => {
    const first = (await h.retention()).retention;
    assert.equal(first.stores.transcripts.sessions, 20);
    assert.equal(first.stores.commands.count, 205);

    // Mid-state: shove the over-cap state back onto disk under a stopped relay — a restore from an old
    // backup, or a relay deploy landing on state a previous build wrote. The next boot must re-converge.
    await h.stopProcess();
    h.seed('transcripts.json', transcripts);
    h.seed('app-commands.json', commands);
    h.stdout = '';
    h.stderr = '';
    await h.start();

    const second = (await h.retention()).retention;
    assert.equal(second.stores.transcripts.sessions, first.stores.transcripts.sessions);
    assert.equal(second.stores.transcripts.contentBytes, first.stores.transcripts.contentBytes);
    assert.equal(second.stores.commands.count, first.stores.commands.count);
    assert.equal(second.stores.commands.inFlight, 5);
    assert.equal(second.stores.commands.terminal, 200);
    assert.equal(second.invariants.ok, true);

    // Idempotent from here: a converged store is a fixed point, so further sweeps drop nothing.
    const settled = (await h.retention()).retention;
    await sleep(700);
    const later = (await h.retention()).retention;
    assert.ok(later.sweeps > settled.sweeps, 'the timer kept sweeping');
    assert.equal(later.dropped.transcriptSessions, settled.dropped.transcriptSessions);
    assert.equal(later.dropped.commands, settled.dropped.commands);
    assert.equal(later.stores.transcripts.sessions, 20);
    assert.equal(later.stores.commands.count, 205);
  });
});
