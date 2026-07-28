// A deploy must be invisible. server.js drains on SIGTERM: it refuses new upgrades, closes every viewer
// with 1012 'restarting', closes the muxd host link last, and exits 0 on its own well inside its 5s
// backstop. index.html turns that 1012 into a 500ms reconnect plus a toast instead of a 1006 backoff walk.
// These tests hold the server half against a real spawned process and the client half against the source.
const test = require('node:test');
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const fs = require('node:fs');
const http = require('node:http');
const net = require('node:net');
const os = require('node:os');
const path = require('node:path');
const { once } = require('node:events');
const WebSocket = require('ws');

const REPO = path.resolve(__dirname, '..');
const HOST_CAPS = ['create', 'createAck', 'kill', 'rename', 'heal', 'tail', 'scrollback'];

// Windows cannot deliver SIGTERM to a live process — libuv maps kill('SIGTERM') to TerminateProcess, so
// the child dies before any handler runs (self-signalling dies too). Production is Linux and gets the real
// signal; on win32 we open the MUX_TEST_MODE IPC door instead, which runs the identical drainForRestart.
const REAL_SIGNAL = process.platform !== 'win32';

function sleep(ms) { return new Promise(resolve => setTimeout(resolve, ms)); }

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

async function waitFor(fn, label, timeoutMs = 5000) {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    if (await fn()) return true;
    await sleep(25);
  }
  throw new Error(`timed out waiting for ${label}`);
}

class DrainHarness {
  constructor() {
    this.tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'relay-drain-'));
    this.stdout = '';
    this.exit = new Promise(resolve => { this._resolveExit = resolve; });
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
      // the ipc channel is the win32 drain door; it stays unused when a real signal is available
      stdio: ['ignore', 'pipe', 'pipe', 'ipc'],
    });
    this.proc.stdout.on('data', d => { this.stdout += d.toString(); });
    this.proc.stderr.on('data', d => { this.stdout += d.toString(); });
    this.proc.on('exit', (code, signal) => this._resolveExit({ code, signal, at: Date.now() }));
    await waitFor(() => this.stdout.includes(`multiplex-app on 0.0.0.0:${this.port}`), 'relay start');
  }

  signalDrain() {
    if (REAL_SIGNAL) this.proc.kill('SIGTERM');
    else this.proc.send({ t: 'drain' });
  }

  // agent:false matters: Node 22's global agent keeps the socket alive and would pin the drained loop open
  request(method, pathName) {
    return new Promise((resolve, reject) => {
      const req = http.request(
        { method, hostname: '127.0.0.1', port: this.port, path: pathName, agent: false },
        res => { res.resume(); res.on('end', () => resolve(res.statusCode)); },
      );
      req.on('error', reject);
      req.end();
    });
  }

  async stop() {
    if (this.proc && this.proc.exitCode === null) {
      this.proc.kill('SIGKILL');
      await Promise.race([once(this.proc, 'exit'), sleep(2000)]);
    }
    fs.rmSync(this.tmp, { recursive: true, force: true });
  }
}

// An OPEN viewer socket only needs the host link writable — a host that never answers the scrollback
// request still leaves the viewer attached, which is exactly the state a deploy has to interrupt.
async function attachedViewer(h, name) {
  const host = new WebSocket(`ws://127.0.0.1:${h.port}/host?token=test-token`);
  await once(host, 'open');
  host.send(JSON.stringify({
    t: 'hello', host: 'FAKEPC', protocol: 4, caps: HOST_CAPS,
    sessions: [{ name, alive: true }],
  }));
  await sleep(150);
  const viewer = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=${name}&cols=80&rows=24`);
  await once(viewer, 'open');
  return { host, viewer };
}

test('SIGTERM drains: viewers get 1012 restarting, new upgrades are refused, the process exits 0', async t => {
  const h = new DrainHarness();
  t.after(() => h.stop());
  await h.start();
  const { host, viewer } = await attachedViewer(h, 'drain-me');

  const viewerClosed = once(viewer, 'close');
  const hostClosed = once(host, 'close');
  const startedAt = Date.now();
  h.signalDrain();

  // (a) the viewer learns the relay is restarting — a real close frame, not a reset connection
  const [code, reason] = await Promise.race([
    viewerClosed,
    sleep(2000).then(() => { throw new Error('viewer was not closed within 2s of SIGTERM'); }),
  ]);
  assert.equal(code, 1012, 'viewer close code must be 1012 SERVICE_RESTART');
  assert.equal(reason.toString(), 'restarting');
  assert.ok(Date.now() - startedAt < 2000, 'viewer close must land within 2s');

  // the host link is closed too, and never before the viewers it feeds
  const [hostCode] = await Promise.race([
    hostClosed,
    sleep(2000).then(() => { throw new Error('host link was not closed within 2s of SIGTERM'); }),
  ]);
  assert.equal(hostCode, 1012, 'host link close code must be 1012 SERVICE_RESTART');

  // (c) a draining relay accepts no new links: the upgrade is refused, not handed a session
  const late = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=drain-me&cols=80&rows=24`);
  const lateOutcome = await Promise.race([
    once(late, 'open').then(() => 'open'),
    once(late, 'error').then(() => 'refused'),
    once(late, 'close').then(() => 'refused'),
    sleep(2000).then(() => 'hung'),
  ]);
  try { late.terminate(); } catch {}
  assert.equal(lateOutcome, 'refused', 'a post-SIGTERM upgrade must be refused');

  // (b) and it goes away on its own — cleanly, and inside the 5s hard-exit backstop
  const exit = await Promise.race([
    h.exit,
    sleep(5000).then(() => { throw new Error('relay did not exit within 5s of SIGTERM'); }),
  ]);
  assert.equal(exit.code, 0, `relay must exit 0 after draining (signal=${exit.signal})`);
  assert.ok(exit.at - startedAt < 5000, 'exit must land inside the 5s backstop');
  assert.match(h.stdout, /\[drain\] SIGTERM: refusing upgrades/);
});

test('a draining relay stops serving http as well, so a deploy never half-answers', async t => {
  const h = new DrainHarness();
  t.after(() => h.stop());
  await h.start();
  assert.equal(await h.request('GET', '/healthz'), 200);

  h.signalDrain();
  await h.exit;
  await assert.rejects(() => h.request('GET', '/healthz'), /ECONNREFUSED|ECONNRESET|socket hang up/);
});

// Static-source half (client-layout.test.js style): the drain is only invisible if the browser agrees.
test('index.html turns a 1012 close into an immediate reconnect and a restarting toast', () => {
  const source = fs.readFileSync(path.join(REPO, 'public', 'index.html'), 'utf8');
  const from = source.indexOf('sock.onclose = ev =>');
  assert.notEqual(from, -1, 'missing the viewer socket onclose handler');
  const onclose = source.slice(from, source.indexOf('sock.onerror', from));

  assert.match(onclose, /ev\.code===1012/, 'onclose must recognise 1012 SERVICE_RESTART');
  assert.match(onclose, /reconnectDelay\s*=\s*500/, '1012 must arm the 500ms reconnect fuse');
  assert.match(onclose, /flash\(/, '1012 must raise a toast');
  assert.match(onclose, /relay restarting/, 'the toast must say the relay is restarting');

  // scheduleReconnect reads reconnectDelay and then multiplies it, and sets its own 'reconnecting' status:
  // the 500ms assignment has to precede it and the toast has to follow it, or one of the two is lost.
  assert.ok(
    onclose.indexOf('reconnectDelay = 500') < onclose.indexOf('scheduleReconnect(name)'),
    'the 500ms fuse must be armed BEFORE scheduleReconnect consumes reconnectDelay',
  );
  assert.ok(
    onclose.indexOf('scheduleReconnect(name)') < onclose.indexOf('flash('),
    'the toast must be raised AFTER scheduleReconnect, which sets its own status',
  );

  // 1012 is a restart, not a refusal — it must never route into stopAttach and detach the tab
  const refusal = source.slice(source.indexOf('function closeRefusal'), source.indexOf('function scheduleReconnect'));
  assert.doesNotMatch(refusal, /1012/, 'closeRefusal must not treat 1012 as a refusal');
});
