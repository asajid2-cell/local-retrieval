// Burst / viewer-backpressure regression drill.
//
// These lock in the shape of the viewer flow-control fix so it cannot silently regress:
//   1. A viewer that stops reading is TERMINATED, never fed a gapped stream (server.js sendViewer).
//   2. Output arriving while a scrollback request is still pending is queued and then replayed
//      behind a single CLEAR, in arrival order (server.js host 'o' / 'sb' handlers).
//   3. Replay sends get exactly one extra allowance of VIEWER_REPLAY_BURST_BYTES over the
//      steady-state high-water mark -- no more, no less.
//
// Tests only: nothing here may require a server.js change to pass.

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

// relay/node_modules is gitignored, so a fresh clone or git worktree has no deps at all -- and both
// this harness and the server.js it spawns need ws/express. Without this the whole file dies at
// require time with MODULE_NOT_FOUND, which reads like a broken test rather than an unbuilt tree.
// Restores from the npm cache (~1s, no network needed once populated).
function ensureRelayDeps() {
  try {
    require.resolve('ws');
    require.resolve('express');
    return;
  } catch { /* not installed yet */ }
  const res = childProcess.spawnSync(
    'npm', ['install', '--prefer-offline', '--no-audit', '--no-fund'],
    { cwd: REPO, encoding: 'utf8', shell: true, timeout: 120000 }
  );
  if (res.status !== 0) {
    throw new Error(
      `relay deps missing and "npm install" failed in ${REPO} (status=${res.status}); ` +
      `run it by hand.\n${res.stderr || res.stdout || res.error}`
    );
  }
}
ensureRelayDeps();

const WebSocket = require('ws');

const HOST_CAPS = ['create', 'createAck', 'kill', 'rename', 'heal', 'tail', 'scrollback', 'relaunch'];

// Mirror of server.js CLEAR_SCREEN. Duplicated on purpose: if the server's prefix changes, these
// tests must fail loudly rather than silently re-deriving whatever the server now sends.
const CLEAR_SCREEN = Buffer.from(
  '\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l\x1b[?1015l\x1b[?2004l' +
  '\x1b[?1049l\x1b[?25h\x1b[0m\x1b[3J\x1b[2J\x1b[H'
);

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

async function waitFor(fn, label, timeoutMs = 8000) {
  const started = Date.now();
  let last;
  while (Date.now() - started < timeoutMs) {
    try {
      last = await fn();
      if (last) return last;
    } catch (err) {
      last = err;
    }
    await sleep(25);
  }
  throw new Error(`timed out waiting for ${label}; last=${last && last.stack || JSON.stringify(last)}`);
}

// Same spawn contract as relay.test.js RelayHarness (MUX_STATE_DIR temp dir, token-gated /host).
class RelayHarness {
  constructor(env = {}) {
    this.proc = null;
    this.env = env;
    this.tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'relay-burst-'));
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
        MUX_COMMAND_LEASE_MS: '1000',
        HLAUTH_BASE: 'http://127.0.0.1:1',
        ...this.env,
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    this.proc.stdout.on('data', d => { this.stdout += d.toString(); });
    this.proc.stderr.on('data', d => { this.stderr += d.toString(); });
    await waitFor(() => this.stdout.includes(`multiplex-app on 0.0.0.0:${this.port}`), 'relay start', 10000);
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

  async json(method, pathName, body) {
    const res = await new Promise((resolve, reject) => {
      const opts = { method, hostname: '127.0.0.1', port: this.port, path: pathName, headers: {} };
      let payload = null;
      if (body !== undefined) {
        payload = Buffer.from(JSON.stringify(body));
        opts.headers['content-type'] = 'application/json';
        opts.headers['content-length'] = payload.length;
      }
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
    this.ws.send(JSON.stringify({ t: 'hello', host: 'FAKEPC', protocol: 4, caps: HOST_CAPS, sessions }));
  }

  // Push raw PTY bytes. Awaits the send callback so the host link itself applies backpressure
  // instead of letting the test process buffer the whole flood in userland.
  sendOutput(name, buf) {
    return new Promise((resolve, reject) => {
      this.ws.send(
        JSON.stringify({ t: 'o', s: name, d: Buffer.from(buf).toString('base64') }),
        err => (err ? reject(err) : resolve()),
      );
    });
  }

  sendScrollback(name, buf) {
    const sb = [...this.messages].reverse().find(m => m.t === 'sb' && m.s === name);
    if (!sb || !sb.rid) throw new Error(`no correlated scrollback request for ${name}`);
    return new Promise((resolve, reject) => {
      this.ws.send(
        JSON.stringify({ t: 'sb', s: name, rid: sb.rid, d: Buffer.from(buf).toString('base64') }),
        err => (err ? reject(err) : resolve()),
      );
    });
  }

  async waitFor(match, label = 'host message', timeoutMs = 8000) {
    const existing = this.messages.find(match);
    if (existing) return existing;
    return await new Promise((resolve, reject) => {
      const waiter = { match, resolve };
      this.waiters.push(waiter);
      setTimeout(() => {
        const i = this.waiters.indexOf(waiter);
        if (i >= 0) this.waiters.splice(i, 1);
        reject(new Error(`timed out waiting for ${label}`));
      }, timeoutMs);
    });
  }
}

function shellSession(name) {
  return {
    name, alive: true, created: 1000, lastOut: 1000, cols: 100, rows: 30,
    hasCommand: false, shellOnly: true, ready: true, kind: 'shell', sessionId: '', aliases: [],
  };
}

// A viewer that records only BINARY frames. The relay also pushes text 'd{...}' sizing frames down
// the same socket; those are control traffic and must not pollute a byte-completeness assertion.
function attachViewer(port, session, { cols = 100, rows = 30, dev = '' } = {}) {
  const url = `ws://127.0.0.1:${port}/ws?session=${session}&cols=${cols}&rows=${rows}`
    + (dev ? `&dev=${dev}` : '');
  const ws = new WebSocket(url);
  const viewer = { ws, chunks: [], bytes: 0, closed: false, closeCode: 0, control: 0 };
  ws.on('message', (data, isBinary) => {
    if (!isBinary) { viewer.control++; return; }
    const buf = Buffer.from(data);
    viewer.chunks.push(buf);
    viewer.bytes += buf.length;
  });
  ws.on('close', code => { viewer.closed = true; viewer.closeCode = code; });
  ws.on('error', () => { viewer.closed = true; });
  viewer.received = () => Buffer.concat(viewer.chunks);
  viewer.open = once(ws, 'open');
  return viewer;
}

// ---------------------------------------------------------------------------------------------
// (a) A never-reading viewer under a >=16MiB flood is terminated; a healthy peer stays byte-complete.
// ---------------------------------------------------------------------------------------------
test('flood past the viewer high-water mark terminates the stalled viewer and keeps the healthy one byte-complete', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('floodcase')]);
  t.after(() => host.close());

  const fast = attachViewer(h.port, 'floodcase', { dev: 'fastdev' });
  const slow = attachViewer(h.port, 'floodcase', { dev: 'slowdev' });
  await Promise.all([fast.open, slow.open]);

  // Settle both viewers out of sbWait with a known replay prefix so the flood itself takes the
  // plain (non-burst) sendViewer path and the expected byte stream is fully determined.
  await host.waitFor(m => m.t === 'sb' && m.s === 'floodcase', 'scrollback request');
  const snapshot = Buffer.from('SB_SNAPSHOT');
  await host.sendScrollback('floodcase', snapshot);
  await waitFor(() => fast.bytes >= CLEAR_SCREEN.length + snapshot.length, 'replay prefix delivered');

  // The slow viewer stops draining its socket entirely: the relay's bufferedAmount for it now
  // only grows. This is the real-world "viewer wedged / laptop asleep" case.
  slow.ws.pause();
  await sleep(50);

  const CHUNK = 256 * 1024;
  const CHUNKS = 96;                       // 24MiB, comfortably past the 4MiB default high-water
  const TOTAL = CHUNK * CHUNKS;
  assert.ok(TOTAL >= 16 * 1024 * 1024, 'flood must exceed the 16MiB drill floor');

  const expected = [CLEAR_SCREEN, snapshot];
  for (let i = 0; i < CHUNKS; i++) {
    const chunk = Buffer.alloc(CHUNK, 65 + (i % 26));   // per-chunk fill => order is verifiable
    expected.push(chunk);
    await host.sendOutput('floodcase', chunk);
    if (i % 8 === 7) await sleep(0);                    // let the event loop service both viewers
  }
  const expectedBuf = Buffer.concat(expected);

  await waitFor(() => fast.bytes >= expectedBuf.length, 'healthy viewer drained the whole flood', 30000);

  // The stalled socket stopped accepting bytes long before the flood ended -- that stall is what
  // drives the relay's bufferedAmount past the high-water mark.
  const slowSocketRead = (slow.ws._socket && slow.ws._socket.bytesRead) || 0;
  assert.ok(
    slowSocketRead < TOTAL / 2,
    `stalled viewer socket should have wedged, but read ${slowSocketRead} of ${TOTAL}`,
  );

  // A paused stream cannot observe its own close, so resume to collect the verdict: whatever the
  // relay managed to push before cutting the socket, plus the close itself.
  slow.ws.resume();
  await waitFor(() => slow.closed, 'stalled viewer terminated', 20000);

  // TERMINATED, not gapped: the relay cut the socket rather than dropping bytes into a live stream.
  assert.equal(slow.closed, true, 'stalled viewer must be terminated once it passes the high-water mark');
  assert.ok(
    slow.bytes < TOTAL,
    `stalled viewer should have been cut off mid-flood, got ${slow.bytes} of ${TOTAL}`,
  );
  // The bytes it did get are a clean prefix -- sendViewer either delivers a whole frame or kills the
  // socket, so a surviving viewer never sees a hole punched in the middle of the stream.
  assert.ok(
    expectedBuf.subarray(0, slow.bytes).equals(slow.received()),
    'terminated viewer must have received an unbroken prefix, never a gapped stream',
  );

  // The healthy viewer must be untouched by its peer's death: same bytes, same order, no holes.
  assert.equal(fast.closed, false, 'healthy viewer must survive its peer being terminated');
  assert.equal(fast.bytes, expectedBuf.length, 'healthy viewer byte count');
  assert.ok(fast.received().equals(expectedBuf), 'healthy viewer received a byte-complete, in-order stream');
});

// ---------------------------------------------------------------------------------------------
// (b) Burst arriving while scrollback is still pending: queue flush emits CLEAR + full replay, in order.
// ---------------------------------------------------------------------------------------------
test('output bursting while scrollback is pending is replayed behind one CLEAR in arrival order', async t => {
  // Long sbWait so the burst is still queued (not force-flushed by the timeout) when sb lands.
  const h = new RelayHarness({ MUX_HOST_SB_WAIT_MS: '6000' });
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('queuecase')]);
  t.after(() => host.close());

  const viewer = attachViewer(h.port, 'queuecase', { dev: 'queuedev' });
  await viewer.open;
  await host.waitFor(m => m.t === 'sb' && m.s === 'queuecase', 'scrollback request');

  // Burst while sbWait is open. Stays under both queue-flush triggers (2MB / 4000 frames) so the
  // sb reply -- not the overflow path -- is what drains the queue.
  const burst = [];
  for (let i = 0; i < 8; i++) {
    const chunk = Buffer.alloc(64 * 1024, 97 + i);
    burst.push(chunk);
    await host.sendOutput('queuecase', chunk);
  }
  const burstBuf = Buffer.concat(burst);
  assert.ok(burstBuf.length < 2000000, 'burst must stay under the queue-overflow trigger');

  await sleep(200);
  assert.equal(viewer.bytes, 0, 'nothing may reach the viewer while scrollback is still pending');

  const snapshot = Buffer.from('QUEUED_SNAPSHOT_BODY');
  await host.sendScrollback('queuecase', snapshot);

  const expected = Buffer.concat([CLEAR_SCREEN, snapshot, burstBuf]);
  await waitFor(() => viewer.bytes >= expected.length, 'queued replay delivered', 15000);
  await sleep(150);

  assert.equal(viewer.closed, false, 'the queued replay must not trip backpressure');
  assert.equal(viewer.bytes, expected.length, 'replay byte count');
  assert.ok(
    viewer.received().equals(expected),
    'replay must be exactly CLEAR + scrollback + buffered burst, in that order',
  );
});

// ---------------------------------------------------------------------------------------------
// (c) Replay allowance boundary: VIEWER_REPLAY_BURST_BYTES over the high-water mark, and not a byte more.
// ---------------------------------------------------------------------------------------------
test('replay sends are allowed exactly one VIEWER_REPLAY_BURST_BYTES over the high-water mark', async t => {
  const HIGH_WATER = 1024;          // server clamps to a 1024 floor
  const HOST_SB_BYTES = 1024;
  const h = new RelayHarness({
    MUX_HOST_SB_WAIT_MS: '6000',
    MUX_VIEWER_HIGH_WATER_BYTES: String(HIGH_WATER),
    MUX_HOST_SB_BYTES: String(HOST_SB_BYTES),
  });
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('undercase'), shellSession('overcase')]);
  t.after(() => host.close());

  // server.js: VIEWER_REPLAY_BURST_BYTES = HOST_SB_BYTES + 2000000 + CLEAR_SCREEN.length
  //            replay limit = VIEWER_HIGH_WATER_BYTES + VIEWER_REPLAY_BURST_BYTES
  const REPLAY_BURST = HOST_SB_BYTES + 2000000 + CLEAR_SCREEN.length;
  const LIMIT = HIGH_WATER + REPLAY_BURST;
  // CLEAR_SCREEN goes out first, so the snapshot is measured against a socket already holding it.
  // The margin absorbs whether that 77-byte frame (plus the small 'd' sizing frame) has drained yet.
  const MARGIN = 512;

  const cases = [
    { session: 'undercase', size: LIMIT - CLEAR_SCREEN.length - MARGIN, survives: true },
    { session: 'overcase', size: LIMIT + MARGIN, survives: false },
  ];

  for (const c of cases) {
    // Both payloads dwarf the steady-state high-water mark: only the replay-burst allowance can
    // explain the under-case surviving, so this really does pin VIEWER_REPLAY_BURST_BYTES.
    assert.ok(c.size > HIGH_WATER * 100, 'boundary payload must be far past the plain high-water mark');

    const viewer = attachViewer(h.port, c.session, { dev: `bd-${c.session}` });
    await viewer.open;
    await host.waitFor(m => m.t === 'sb' && m.s === c.session, `scrollback request for ${c.session}`);
    await sleep(150);                                   // let the 'd' sizing frame drain first

    const snapshot = Buffer.alloc(c.size, 0x2e);
    await host.sendScrollback(c.session, snapshot);

    if (c.survives) {
      const expected = Buffer.concat([CLEAR_SCREEN, snapshot]);
      await waitFor(() => viewer.bytes >= expected.length, `${c.session} replay delivered`, 20000);
      await sleep(150);
      assert.equal(viewer.closed, false, `${c.session}: replay just under the burst allowance must survive`);
      assert.equal(viewer.bytes, expected.length, `${c.session} replay byte count`);
      assert.ok(viewer.received().equals(expected), `${c.session}: under-limit replay must be byte-complete`);
    } else {
      await waitFor(() => viewer.closed, `${c.session} viewer terminated`, 20000);
      assert.equal(viewer.closed, true, `${c.session}: replay past the burst allowance must terminate the viewer`);
      assert.ok(
        viewer.bytes <= CLEAR_SCREEN.length,
        `${c.session}: the oversized snapshot must never be delivered, got ${viewer.bytes} bytes`,
      );
    }
    try { viewer.ws.terminate(); } catch {}
  }
});
