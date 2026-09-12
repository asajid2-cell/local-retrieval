// Long-poll on /api/app-commands/lease: a waitMs poll against an empty queue is HELD OPEN and
// answered the instant a command is enqueued — that is the whole feature (each PC-side poll costs
// a full ssh handshake, so held answers replace fast polling). Everything here runs against a real
// relay process via the harness; timings are asserted with generous margins, never exact.
const assert = require('node:assert/strict');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { test } = require('node:test');

(function ensureRelayDeps() {
  const relayDir = path.resolve(__dirname, '..');
  try {
    require.resolve('ws', { paths: [relayDir] });
    return;
  } catch { /* not installed yet */ }
  const isWindows = process.platform === 'win32';
  try {
    execFileSync(isWindows ? 'npm.cmd' : 'npm', ['ci', '--no-audit', '--no-fund'], {
      cwd: relayDir,
      stdio: ['ignore', 'pipe', 'pipe'],
      shell: isWindows,
    });
  } catch (err) {
    const detail = [err.stdout, err.stderr].map(b => (b ? b.toString() : '')).join('').trim();
    throw new Error(`could not install relay deps (npm ci in ${relayDir}): ${err.message}\n${detail}`);
  }
})();

const { RelayHarness, sleep, ackLeased } = require('./harness');

function lease(h, body) {
  return h.json(
    'POST',
    '/api/app-commands/lease',
    { owner: 'longpoll-consumer', limit: 16, ...body },
    { 'X-Mux-Command-Bridge': h.commandBridgeToken },
  );
}

function enqueue(h, sessionId) {
  return h.json('POST', '/api/app-commands', { type: 'kill', sessionId });
}

test('lease long-poll', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => { await h.stop(); });

  await t.test('no waitMs answers immediately even when the queue is empty', async () => {
    const started = Date.now();
    const leased = await lease(h, {});
    assert.deepEqual(leased, []);
    assert.ok(Date.now() - started < 2000, 'classic contract must not be held open');
  });

  await t.test('waitMs=0 is the classic contract too', async () => {
    const started = Date.now();
    assert.deepEqual(await lease(h, { waitMs: 0 }), []);
    assert.ok(Date.now() - started < 2000);
  });

  await t.test('a queue with work answers instantly regardless of waitMs', async () => {
    await enqueue(h, 'lp-already-pending');
    const started = Date.now();
    const leased = await lease(h, { waitMs: 20000 });
    assert.equal(leased.length, 1);
    assert.equal(leased[0].sessionId, 'lp-already-pending');
    assert.ok(Date.now() - started < 2000, 'must not wait when commands are pending');
    await ackLeased(h, leased[0], { ok: true });   // terminal, so the 1s test lease can't re-pend it into later subtests
  });

  await t.test('an empty-queue poll is held and fulfilled the moment a command lands', async () => {
    const started = Date.now();
    const held = lease(h, { waitMs: 20000 });
    await sleep(400);   // let the waiter park before the enqueue
    await enqueue(h, 'lp-wakes-waiter');
    const leased = await held;
    const elapsed = Date.now() - started;
    assert.equal(leased.length, 1);
    assert.equal(leased[0].sessionId, 'lp-wakes-waiter');
    assert.ok(elapsed >= 350, `answered before the enqueue could have happened (${elapsed}ms)`);
    assert.ok(elapsed < 5000, `held long after the enqueue (${elapsed}ms) — waiter was not woken`);
    await ackLeased(h, leased[0], { ok: true });
  });

  await t.test('an unfulfilled wait times out with an empty list', async () => {
    const started = Date.now();
    const leased = await lease(h, { waitMs: 700 });
    const elapsed = Date.now() - started;
    assert.deepEqual(leased, []);
    assert.ok(elapsed >= 600, `timed out early (${elapsed}ms)`);
    assert.ok(elapsed < 5000, `timeout wildly late (${elapsed}ms)`);
  });

  await t.test('waiters are FIFO: first parked poll gets the command, second times out empty', async () => {
    const first = lease(h, { owner: 'lp-first', waitMs: 20000 });
    await sleep(300);
    const second = lease(h, { owner: 'lp-second', waitMs: 1200 });
    await sleep(300);
    await enqueue(h, 'lp-fifo');
    const firstLeased = await first;
    assert.equal(firstLeased.length, 1);
    assert.equal(firstLeased[0].leaseOwner, 'lp-first');
    assert.deepEqual(await second, []);
    await ackLeased(h, firstLeased[0], { ok: true });
  });

  await t.test('one enqueue wakes exactly one limit=1 waiter per command', async () => {
    const a = lease(h, { owner: 'lp-a', limit: 1, waitMs: 20000 });
    const b = lease(h, { owner: 'lp-b', limit: 1, waitMs: 20000 });
    await sleep(300);
    await enqueue(h, 'lp-two-1');
    await enqueue(h, 'lp-two-2');
    const [fromA, fromB] = await Promise.all([a, b]);
    assert.equal(fromA.length, 1);
    assert.equal(fromB.length, 1);
    assert.notEqual(fromA[0].id, fromB[0].id, 'both waiters must not lease the same command');
    await ackLeased(h, fromA[0], { ok: true });
    await ackLeased(h, fromB[0], { ok: true });
  });

  await t.test('waitMs is capped server-side, never trusted', { timeout: 60000 }, async () => {
    const started = Date.now();
    const leased = await lease(h, { waitMs: 10 * 60 * 1000 });
    const elapsed = Date.now() - started;
    assert.deepEqual(leased, []);
    assert.ok(elapsed < 40000, `cap not applied (${elapsed}ms)`);
  });
});
