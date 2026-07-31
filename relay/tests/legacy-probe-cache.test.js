// The relay probes for legacy tmux sessions with execSync, which FREEZES the event loop for the
// duration of the child process — no keystroke forwarded, no PTY output flushed, no ws frame written.
// Measured on the dev box (win32, tmux not installed, so spawn cost only, no tmux IPC): 14.9ms p50,
// 18.2ms p95, 230.7ms max per call, and loop lateness p95 rising 11.0ms -> 105.9ms while probing.
//
// The call sites make that rate proportional to load, not constant: GET /api/sessions probes, and
// every open browser tab polls it every 4 seconds. Six idle tabs is six freezes per window on the
// process whose entire job is relaying bytes promptly.
//
// These tests hold the TTL cache that collapses that. They assert the SPAWN COUNT, not elapsed time,
// because a timing assertion on a shared box measures the box, not the code.

const test = require('node:test');
const assert = require('node:assert/strict');

const { RelayHarness, sleep } = require('./harness');

test('repeated session polls share one tmux probe instead of spawning per request', async t => {
  const h = new RelayHarness({ MUX_LEGACY_TMUX_TTL_MS: '60000' });   // one window for the whole test
  await h.start();
  t.after(() => h.stop());

  const before = (await h.json('GET', '/api/health')).legacyProbes;
  assert.equal(typeof before, 'number', '/api/health must expose the tmux probe count');

  // The real pattern: several tabs polling the session list at once.
  for (let i = 0; i < 8; i++) await h.json('GET', '/api/sessions');

  const after = (await h.json('GET', '/api/health')).legacyProbes;
  assert.equal(
    after, before,
    `8 session polls inside one TTL window must not spawn tmux again (probes ${before} -> ${after}); `
    + 'each spawn is a hard event-loop freeze measured at ~15ms p50',
  );
});

test('the probe still refreshes once its TTL expires', async t => {
  // Short window: the cache must be a staleness bound, not a permanent freeze of the answer. A legacy
  // tmux session appearing later still has to become visible.
  const h = new RelayHarness({ MUX_LEGACY_TMUX_TTL_MS: '150' });
  await h.start();
  t.after(() => h.stop());

  const before = (await h.json('GET', '/api/health')).legacyProbes;
  await sleep(400);
  await h.json('GET', '/api/sessions');
  const after = (await h.json('GET', '/api/health')).legacyProbes;

  assert.ok(
    after > before,
    `a poll after the TTL expired must re-probe (probes ${before} -> ${after}); a cache that never `
    + 'refreshes would hide a legacy tmux session forever',
  );
});
