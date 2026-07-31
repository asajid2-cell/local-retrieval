// Ops alerting: drive synthetic health states through the watcher with a mock transport.
// The clock is a plain number we advance by hand — no fake timers, no sleeping, no network.

const test = require('node:test');
const assert = require('node:assert/strict');

const { createHealthAlerts, startHealthAlerts, degradedReasons } = require('../health-alerts');

const MIN = 60 * 1000;

// Mock of the pinned relay/notify.js transport contract:
//   createNotifier({url, fetchImpl}) -> notifier.push({title, body, tags, priority, click}) -> {ok,...}
// push() never throws.
function mockNotifier(behaviour) {
  const sent = [];
  return {
    sent,
    titles: () => sent.map(m => m.title),
    push: async (message) => {
      sent.push(message);
      if (typeof behaviour === 'function') return behaviour(message, sent.length);
      return { ok: true, id: `mock-${sent.length}` };
    },
  };
}

function health(overrides = {}) {
  return {
    ok: true,
    degraded: false,
    gaveUp: 0,
    hostedArmedDown: 0,
    legacySessions: 0,
    pendingRenameIntents: 0,
    uploadRecoveryWarnings: [],
    projects: { bridgeLive: true },
    persistence: { ok: true, detail: '', blocked: false },
    host: { connected: true, name: 'win', protocolOk: true },
    pc: { reachable: true, rttMs: 3, host: '192.168.1.146' },
    ...overrides,
  };
}

const GREEN = () => health();
const DEGRADED = () => health({ degraded: true, ok: false, legacySessions: 2 });

test('fires on the healthy -> degraded edge once the 2min dwell is met', async () => {
  const notifier = mockNotifier();
  const alerts = createHealthAlerts({ notifier });
  let t = 1_000_000;

  assert.deepEqual(await alerts.observe(GREEN(), t), [], 'green baseline is silent');

  // Degraded begins. Nothing yet — the dwell window has not elapsed.
  assert.deepEqual(await alerts.observe(DEGRADED(), t), []);
  assert.deepEqual(await alerts.observe(DEGRADED(), t + 60 * 1000), [], 'still inside the 2min dwell');
  assert.equal(notifier.sent.length, 0);

  // Crossing 2min is the edge.
  const fired = await alerts.observe(DEGRADED(), t + 2 * MIN);
  assert.equal(fired.length, 1);
  assert.equal(fired[0].key, 'degraded');
  assert.equal(fired[0].kind, 'alert');
  assert.equal(fired[0].repeat, false);
  assert.deepEqual(fired[0].result, { ok: true, id: 'mock-1' });

  assert.equal(notifier.sent.length, 1);
  const push = notifier.sent[0];
  assert.equal(push.title, 'Relay degraded');
  assert.match(push.body, /degraded for 2m/);
  assert.match(push.body, /2 legacy tmux session\(s\)/, 'body names why it is degraded');
  assert.equal(push.priority, 'high');
  assert.deepEqual(push.tags, ['warning']);

  // Edge only: staying degraded does not re-push on every sample.
  assert.deepEqual(await alerts.observe(DEGRADED(), t + 3 * MIN), []);
  assert.deepEqual(await alerts.observe(DEGRADED(), t + 10 * MIN), []);
  assert.equal(notifier.sent.length, 1, 'one push for one episode');
});

test('a 30s transient blip never alerts, and never sends a recovery either', async () => {
  const notifier = mockNotifier();
  const alerts = createHealthAlerts({ notifier });
  let t = 5_000_000;

  await alerts.observe(GREEN(), t);
  await alerts.observe(DEGRADED(), t + 1000);
  await alerts.observe(DEGRADED(), t + 15 * 1000);
  const atBlipEnd = await alerts.observe(DEGRADED(), t + 30 * 1000);
  assert.deepEqual(atBlipEnd, [], '30s of degraded is well inside the 2min dwell');

  // Recovered before the dwell elapsed: a non-event in both directions.
  const back = await alerts.observe(GREEN(), t + 31 * 1000);
  assert.deepEqual(back, [], 'no recovery push for an alert that never fired');
  assert.equal(notifier.sent.length, 0, 'mock transport was never called');

  // The blip must not have poisoned the state machine — a real sustained episode still fires.
  await alerts.observe(DEGRADED(), t + 40 * 1000);
  assert.deepEqual(await alerts.observe(DEGRADED(), t + 40 * 1000 + 2 * MIN - 1), [], 'dwell timed from the new episode');
  const fired = await alerts.observe(DEGRADED(), t + 40 * 1000 + 2 * MIN);
  assert.equal(fired.length, 1);
  assert.equal(fired[0].key, 'degraded');
});

test('dedupe: at most one push per condition per 30min, then one nag while still broken', async () => {
  const notifier = mockNotifier();
  const alerts = createHealthAlerts({ notifier });
  const t = 9_000_000;

  await alerts.observe(GREEN(), t);
  await alerts.observe(DEGRADED(), t);
  await alerts.observe(DEGRADED(), t + 2 * MIN);          // push #1
  assert.equal(notifier.sent.length, 1);

  // Hammer the watcher for the rest of the window — silence.
  for (let m = 3; m < 32; m += 1) {
    assert.deepEqual(await alerts.observe(DEGRADED(), t + m * MIN), [], `minute ${m} must be deduped`);
  }
  assert.equal(notifier.sent.length, 1, 'still one push 29min into the window');

  // 30min after the last push, still broken -> exactly one more.
  const nag = await alerts.observe(DEGRADED(), t + 32 * MIN);
  assert.equal(nag.length, 1);
  assert.equal(nag[0].repeat, true, 'flagged as a repeat, not a fresh edge');
  assert.equal(notifier.sent.length, 2);
  assert.deepEqual(await alerts.observe(DEGRADED(), t + 40 * MIN), [], 'new window opens after the nag');
  assert.equal(notifier.sent.length, 2);
});

test('recovery push follows an alert that fired, with the episode duration', async () => {
  const notifier = mockNotifier();
  const alerts = createHealthAlerts({ notifier });
  const t = 12_000_000;

  await alerts.observe(DEGRADED(), t);
  await alerts.observe(DEGRADED(), t + 2 * MIN);          // alert
  const fired = await alerts.observe(GREEN(), t + 9 * MIN);

  assert.equal(fired.length, 1);
  assert.equal(fired[0].kind, 'recovery');
  assert.equal(fired[0].key, 'degraded');
  assert.equal(notifier.sent.length, 2);
  const recovery = notifier.sent[1];
  assert.equal(recovery.title, 'Relay recovered');
  assert.match(recovery.body, /green again after 9m/);
  assert.deepEqual(recovery.tags, ['white_check_mark']);
  assert.equal(recovery.priority, 'default');

  // Recovery is itself an edge: a second green sample says nothing.
  assert.deepEqual(await alerts.observe(GREEN(), t + 10 * MIN), []);
  assert.equal(notifier.sent.length, 2);

  // A fresh degraded episode after recovery alerts again immediately on dwell — the dedupe window
  // belongs to the episode, not to the condition forever.
  await alerts.observe(DEGRADED(), t + 11 * MIN);
  const again = await alerts.observe(DEGRADED(), t + 13 * MIN);
  assert.equal(again.length, 1);
  assert.equal(again[0].repeat, false, 'new episode is a fresh edge, not a nag');
});

test('host link down needs 5min; a 4min reconnect stays silent', async () => {
  const notifier = mockNotifier();
  const alerts = createHealthAlerts({ notifier });
  const t = 20_000_000;
  const down = () => health({ host: { connected: false, name: 'win', protocolOk: true } });

  await alerts.observe(GREEN(), t);
  await alerts.observe(down(), t);
  assert.deepEqual(await alerts.observe(down(), t + 4 * MIN), [], '4min down is under the 5min dwell');
  await alerts.observe(GREEN(), t + 4 * MIN + 1000);
  assert.equal(notifier.sent.length, 0);

  // A real outage.
  await alerts.observe(down(), t + 10 * MIN);
  const fired = await alerts.observe(down(), t + 15 * MIN);
  assert.equal(fired.length, 1);
  assert.equal(fired[0].key, 'host-link-down');
  assert.equal(notifier.sent[0].title, 'Host link down');
  assert.match(notifier.sent[0].body, /down for 5m \(win\)/);

  const back = await alerts.observe(GREEN(), t + 21 * MIN);
  assert.equal(back.length, 1);
  assert.equal(back[0].kind, 'recovery');
  assert.equal(notifier.sent[1].title, 'Host link restored');
});

test('an unknown/absent host block is not treated as a down link', async () => {
  const notifier = mockNotifier();
  const alerts = createHealthAlerts({ notifier });
  const t = 30_000_000;
  const noHost = health({ host: undefined });
  delete noHost.host;

  await alerts.observe(noHost, t);
  await alerts.observe(noHost, t + 30 * MIN);
  assert.equal(notifier.sent.length, 0, 'missing evidence is not evidence of an outage');
});

test('persistenceBlocked alerts on sight — it is latched until an operator clears it', async () => {
  const notifier = mockNotifier();
  const alerts = createHealthAlerts({ notifier });
  const t = 40_000_000;
  const blocked = () => health({
    degraded: true,
    ok: false,
    persistence: { ok: false, detail: 'EROFS: read-only file system', blocked: 'EROFS: read-only file system' },
  });

  await alerts.observe(GREEN(), t);
  const fired = await alerts.observe(blocked(), t);
  const keys = fired.map(f => f.key);
  assert.ok(keys.includes('persistence-blocked'), 'no dwell — fires on the first sample');
  assert.equal(fired.find(f => f.key === 'persistence-blocked').heldMs, 0);

  const push = notifier.sent.find(m => m.title === 'Persistence BLOCKED');
  assert.ok(push, 'a persistence push was sent');
  assert.equal(push.priority, 'urgent');
  assert.match(push.body, /EROFS: read-only file system/);
  assert.match(push.body, /pending operator recovery/);

  // Degraded is also true here but is a separate condition on its own dwell + dedupe budget.
  assert.ok(!keys.includes('degraded'), 'degraded still waits out its own 2min dwell');
  const later = await alerts.observe(blocked(), t + 2 * MIN);
  assert.deepEqual(later.map(f => f.key), ['degraded']);

  const cleared = await alerts.observe(GREEN(), t + 5 * MIN);
  assert.deepEqual(cleared.map(f => f.key).sort(), ['degraded', 'persistence-blocked']);
  assert.ok(notifier.sent.some(m => m.title === 'Persistence unblocked'));
});

test('heal gave-up hook: inert against the current stub, fires the moment muxd reports a count', async () => {
  const notifier = mockNotifier();
  const alerts = createHealthAlerts({ notifier });
  const t = 50_000_000;

  // Today /api/health.gaveUp is hardcoded 0 (server.js:1170) — the condition can never trip.
  assert.ok(alerts.conditions.includes('heal-gave-up'), 'the hook is wired, not deleted');
  await alerts.observe(health({ gaveUp: 0 }), t);
  await alerts.observe(health({ gaveUp: 0 }), t + 60 * MIN);
  assert.equal(notifier.sent.length, 0, 'the stub value must never alert');

  // When muxd starts reporting real give-ups, this needs no code change.
  const fired = await alerts.observe(health({ gaveUp: 2, degraded: true, ok: false }), t + 61 * MIN);
  const gave = fired.find(f => f.key === 'heal-gave-up');
  assert.ok(gave, 'a real count fires immediately');
  assert.equal(gave.message.priority, 'urgent');
  assert.match(gave.message.body, /2 session\(s\) exhausted their heal attempts/);
});

test('conditions are independent: each keeps its own dwell, dedupe, and recovery', async () => {
  const notifier = mockNotifier();
  const alerts = createHealthAlerts({ notifier });
  const t = 60_000_000;
  const both = () => health({
    degraded: true,
    ok: false,
    host: { connected: false, name: 'win', protocolOk: true },
  });

  await alerts.observe(both(), t);
  const at2 = await alerts.observe(both(), t + 2 * MIN);
  assert.deepEqual(at2.map(f => f.key), ['degraded'], 'host link is still inside its own 5min dwell');
  const at5 = await alerts.observe(both(), t + 5 * MIN);
  assert.deepEqual(at5.map(f => f.key), ['host-link-down']);

  const snapshot = alerts.snapshot();
  assert.equal(snapshot.degraded.count, 1);
  assert.equal(snapshot['host-link-down'].count, 1);
  assert.equal(snapshot['persistence-blocked'].activeSince, null);
});

test('overridable windows, title label, and click-through are threaded into the push', async () => {
  const notifier = mockNotifier();
  const alerts = createHealthAlerts({
    notifier,
    degradedSustainMs: 10 * 1000,
    dedupeMs: 60 * 1000,
    label: 'mux',
    click: 'https://relay.example/health',
  });
  const t = 70_000_000;

  await alerts.observe(DEGRADED(), t);
  const fired = await alerts.observe(DEGRADED(), t + 10 * 1000);
  assert.equal(fired.length, 1);
  assert.equal(notifier.sent[0].title, '[mux] Relay degraded');
  assert.equal(notifier.sent[0].click, 'https://relay.example/health');

  assert.deepEqual(await alerts.observe(DEGRADED(), t + 50 * 1000), [], 'inside the shortened dedupe window');
  const nag = await alerts.observe(DEGRADED(), t + 71 * 1000);
  assert.equal(nag.length, 1, 'shortened dedupe window honoured');
});

test('a failing or throwing transport is reported, never propagated', async () => {
  const failing = mockNotifier(() => ({ ok: false, status: 503 }));
  const alerts = createHealthAlerts({ notifier: failing });
  const t = 80_000_000;
  await alerts.observe(DEGRADED(), t);
  const fired = await alerts.observe(DEGRADED(), t + 2 * MIN);
  assert.deepEqual(fired[0].result, { ok: false, status: 503 });

  // The contract says push() never throws; if a broken build does anyway, the watcher survives.
  const throwing = {
    push: async () => { throw new Error('socket hang up'); },
  };
  const guarded = createHealthAlerts({ notifier: throwing });
  await guarded.observe(DEGRADED(), t);
  const fired2 = await guarded.observe(DEGRADED(), t + 2 * MIN);
  assert.equal(fired2.length, 1);
  assert.equal(fired2[0].result.ok, false);
  assert.match(fired2[0].result.error, /socket hang up/);
});

test('a malformed health blob does not wedge the watcher', async () => {
  const notifier = mockNotifier();
  const alerts = createHealthAlerts({ notifier });
  const t = 90_000_000;
  assert.deepEqual(await alerts.observe(null, t), []);
  assert.deepEqual(await alerts.observe(undefined, t), []);
  assert.deepEqual(await alerts.observe('nonsense', t), []);
  assert.deepEqual(await alerts.observe({}, t), []);
  assert.equal(notifier.sent.length, 0);

  await alerts.observe(DEGRADED(), t);
  assert.equal((await alerts.observe(DEGRADED(), t + 2 * MIN)).length, 1, 'still functional afterwards');
});

test('degradedReasons mirrors the disjunction the server computes', () => {
  const reasons = degradedReasons(health({
    degraded: true,
    legacySessions: 1,
    pendingRenameIntents: 3,
    uploadRecoveryWarnings: ['upload-7 orphaned'],
    projects: { bridgeLive: false },
    pc: { reachable: false, host: '192.168.1.154' },
    host: { connected: false, protocolOk: false, name: 'win' },
    persistence: { ok: false, detail: 'ENOSPC', blocked: '' },
  }));
  assert.deepEqual(reasons, [
    'host link down',
    'host protocol mismatch',
    'PC unreachable (192.168.1.154)',
    '1 legacy tmux session(s)',
    'projects bridge down',
    'persistence failing: ENOSPC',
    '3 pending rename intent(s)',
    '1 upload recovery warning(s)',
  ]);
  assert.deepEqual(degradedReasons(health()), [], 'a green blob has no reasons');
});

test('createHealthAlerts refuses to run without an injected transport', () => {
  assert.throws(() => createHealthAlerts({}), /requires a notifier/);
  assert.throws(() => createHealthAlerts({ notifier: {} }), /requires a notifier/);
});

test('startHealthAlerts polls a health producer and never holds the process open', async () => {
  const notifier = mockNotifier();
  let sample = DEGRADED();
  const runner = startHealthAlerts({
    notifier,
    getHealth: () => sample,
    intervalMs: 60_000,
    degradedSustainMs: 0,
  });
  try {
    await runner.tick();
    assert.equal(notifier.sent.length, 1, 'a manual tick observes immediately');
    sample = GREEN();
    await runner.tick();
    assert.equal(notifier.sent[1].title, 'Relay recovered');

    // A throwing producer must not escape the loop.
    const boom = startHealthAlerts({ notifier, getHealth: () => { throw new Error('health blew up'); } });
    await boom.tick();
    boom.stop();
  } finally {
    runner.stop();
  }
});
