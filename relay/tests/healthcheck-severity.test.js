// Severity contract for relay/ops/multiplex-healthcheck.
//
// This script is the only monitor that survives the relay itself dying, so its exit code is what pages
// an operator. The first revision failed on `ok !== true` and on `projects.bridgeLive !== true` — and
// `ok` is just `!degraded`, whose disjunction INCLUDES bridgeLive. Closing the desktop app therefore
// failed the unit, ~614 times in four days, for a relay that was serving every request correctly.
//
// So the contract under test is a split, not a list of checks:
//   FAIL (exit 1) — true only when something is actually broken.
//   WARN (exit 0) — normal-but-notable, printed every run so a persistent warning is still visible.
// The rule that decides which side a check lands on: can it be true while the relay serves correctly?
// If yes it is a warning. Every case below is either the false positive that motivated this file or a
// fault that must keep failing.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { spawn } = require('node:child_process');

const posix = (p) => p.replace(/\\/g, '/');
const SCRIPT = posix(path.join(__dirname, '..', 'ops', 'multiplex-healthcheck'));
const BASH = process.platform === 'win32'
  ? [
      path.join(process.env.ProgramFiles || 'C:\\Program Files', 'Git', 'bin', 'bash.exe'),
      path.join(process.env.LOCALAPPDATA || '', 'Programs', 'Git', 'bin', 'bash.exe'),
    ].find((candidate) => candidate && fs.existsSync(candidate)) || 'bash'
  : 'bash';

// A relay that is healthy in every way that matters, with the desktop app closed. This is the exact
// shape /api/health served on the box for ~4 days while the unit failed every minute.
function healthAppClosed(overrides = {}) {
  return {
    ok: false,
    degraded: true,
    degradedReasons: ['projects bridge down'],
    legacySessions: 0,
    pendingRenameIntents: 0,
    uploadRecoveryWarnings: [],
    stateRecoveryFailures: [],
    persistence: { ok: true, detail: '', blocked: false },
    pc: { reachable: true, rttMs: 29, host: '192.168.1.162' },
    projects: { appLive: false, bridgeLive: false, pendingCommands: 0 },
    host: { connected: true, name: 'CRACKERBARREL', sessions: 0, protocol: 4, protocolOk: true, caps: ['ls', 'info', 'create', 'attach', 'kill'] },
    node: 'v24.18.0',
    ...overrides,
  };
}

// Async on purpose: spawnSync would block this very event loop, and the stub relay the script is
// supposed to reach lives in it. A blocking spawn turns every case into a 5s timeout.
function runCheck(url, env = {}) {
  return new Promise((resolve) => {
    const proc = spawn(BASH, [SCRIPT], { env: { ...process.env, MULTIPLEX_HEALTH_URL: url, ...env } });
    let stdout = '';
    let stderr = '';
    proc.stdout.on('data', (d) => { stdout += d.toString(); });
    proc.stderr.on('data', (d) => { stderr += d.toString(); });
    proc.on('close', (code) => resolve({ status: code, stdout, stderr }));
  });
}

// One server, body swapped per case: the script only cares about the response, so a shared listener
// keeps the suite fast without hiding anything.
async function withHealthServer(fn) {
  const state = { status: 200, body: JSON.stringify(healthAppClosed()) };
  const server = http.createServer((req, res) => {
    res.writeHead(state.status, { 'Content-Type': 'application/json' });
    res.end(state.body);
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const url = `http://127.0.0.1:${server.address().port}/api/health`;
  try {
    await fn({ url, state, respond: (status, body) => { state.status = status; state.body = typeof body === 'string' ? body : JSON.stringify(body); } });
  } finally {
    await new Promise((resolve) => server.close(resolve));
  }
}

test('a healthy relay with the desktop app closed is a WARNING, not a failure', async () => {
  await withHealthServer(async ({ url, respond }) => {
    respond(200, healthAppClosed());
    const res = await runCheck(url);

    assert.equal(res.status, 0,
      `closing the desktop app must not fail the unit; stderr was:\n${res.stderr}`);
    assert.match(res.stdout, /\[multiplex-healthcheck\] OK/, 'the summary line still prints');
    assert.match(res.stdout, /WARN - degraded=true \(projects bridge down\)/,
      'the reason from the payload is reported verbatim instead of re-derived');
    assert.match(res.stdout, /WARN - projects bridge stale/,
      'the operator is told staleness is expected with no desktop running');
    assert.match(res.stdout, /warnings=3/, 'the summary counts warnings without turning them into faults');
    assert.equal(res.stderr, '', 'nothing on stderr, because nothing failed');
  });
});

test('the stall is named on the failure line, not just dumped in the JSON blob', async () => {
  // The journal is what an operator actually reads. An exit code plus a 40-line JSON dump makes them
  // work out which field moved; the fault line has to say it.
  await withHealthServer(async ({ url, respond }) => {
    respond(200, healthAppClosed({
      host: { connected: true, name: 'CRACKERBARREL', sessions: 0, protocol: 4, protocolOk: true, caps: ['create', 'attach', 'kill'], frameStale: true, frameAgeMs: 259_200_000, frameStaleMs: 15000 },
    }));
    const res = await runCheck(url);

    assert.equal(res.status, 1);
    assert.match(res.stderr, /host stalled \(no status frame for 259200s; limit 15s\)/);
    assert.doesNotMatch(res.stderr, /host disconnected/, 'a live link must not be reported as a dead one');
  });
});

test('a fresh heartbeat is reported on the OK line and never fails the unit', async () => {
  await withHealthServer(async ({ url, respond }) => {
    respond(200, healthAppClosed({
      ok: true, degraded: false, degradedReasons: [],
      projects: { appLive: true, bridgeLive: true, pendingCommands: 0 },
      host: { connected: true, name: 'CRACKERBARREL', sessions: 2, protocol: 4, protocolOk: true, caps: ['create', 'attach', 'kill'], frameStale: false, frameAgeMs: 3200, frameStaleMs: 15000 },
    }));
    const res = await runCheck(url);

    assert.equal(res.status, 0, `a healthy host must pass; stderr was:\n${res.stderr}`);
    assert.match(res.stdout, /frameAge=3s/, 'the heartbeat is on the summary line an operator reads');
  });
});

test('a host with no frame to measure reports n/a, not a fresh-looking 0', async () => {
  // null is the honest value for "no link" / "nothing stamped yet"; rendering it as 0s would read as a
  // perfectly fresh heartbeat on exactly the host that has none.
  await withHealthServer(async ({ url, respond }) => {
    respond(200, healthAppClosed({
      host: { connected: true, name: 'win', sessions: 0, protocol: 4, protocolOk: true, caps: ['create', 'attach', 'kill'], frameStale: false, frameAgeMs: null, frameStaleMs: 15000 },
    }));
    const res = await runCheck(url);

    assert.equal(res.status, 0);
    assert.match(res.stdout, /frameAge=n\/a/);
  });
});

test('a relay with no reason to warn exits clean', async () => {
  await withHealthServer(async ({ url, respond }) => {
    respond(200, healthAppClosed({
      ok: true, degraded: false, degradedReasons: [],
      projects: { appLive: true, bridgeLive: true, pendingCommands: 0 },
    }));
    const res = await runCheck(url);

    assert.equal(res.status, 0);
    assert.doesNotMatch(res.stdout, /WARN/, `a green relay must be quiet; stdout was:\n${res.stdout}`);
    assert.match(res.stdout, /warnings=0/);
  });
});

// Every entry here is broken no matter what else is true, so it must still fail after the split.
const FAULTS = [
  ['the muxd host link is down', { host: { connected: false, protocol: 0, protocolOk: false, caps: [] } }],
  ['the host speaks an old protocol', { host: { connected: true, name: 'win', protocol: 1, protocolOk: true, caps: ['create', 'attach', 'kill'] } }],
  ['the host protocol did not agree', { host: { connected: true, name: 'win', protocol: 4, protocolOk: false, caps: ['create', 'attach', 'kill'] } }],
  ['the host cannot create sessions', { host: { connected: true, name: 'win', protocol: 4, protocolOk: true, caps: ['ls', 'attach', 'kill'] } }],
  // Connected but silent. This is the case the pre-existing checks cannot see: the link is up, the
  // protocol agrees and the caps are complete, so before frameStale every fault check passed while muxd
  // had not pushed a frame in three days.
  ['the host is connected but has stopped pushing frames', { host: { connected: true, name: 'win', protocol: 4, protocolOk: true, caps: ['create', 'attach', 'kill'], frameStale: true, frameAgeMs: 3 * 24 * 60 * 60 * 1000, frameStaleMs: 15000 } }],
  ['the PC is unreachable', { pc: { reachable: false, host: '192.168.1.162' } }],
  ['state persistence is failing', { persistence: { ok: false, detail: 'EACCES', blocked: false } }],
  ['a legacy tmux session survives', { legacySessions: 2 }],
  ['a rename intent never completed', { pendingRenameIntents: 1 }],
  ['an upload recovery warning is outstanding', { uploadRecoveryWarnings: ['uploads/7 has no owner'] }],
  ['a store was recovered but not rewritten', { stateRecoveryFailures: ['projects.json'] }],
];

for (const [label, overrides] of FAULTS) {
  test(`fails when ${label}`, async () => {
    await withHealthServer(async ({ url, respond }) => {
      respond(200, healthAppClosed(overrides));
      const res = await runCheck(url);

      assert.equal(res.status, 1, `expected a failure for: ${label}\nstdout:\n${res.stdout}`);
      assert.match(res.stderr, /\[multiplex-healthcheck\] FAIL/, 'the failure is marked as a failure');
      // The JSON dump is what makes a journal entry actionable: exit code alone says nothing about which
      // of a dozen fields moved.
      assert.match(res.stderr, /"host"/, 'the failing blob is dumped for the journal');
    });
  });
}

test('fails when the relay cannot be reached at all', async () => {
  // Nothing listening: the case this monitor exists for, since the relay's own alert lane cannot
  // report its own death.
  const res = await runCheck('http://127.0.0.1:1/api/health', { MULTIPLEX_HEALTH_TIMEOUT_MS: '2000' });
  assert.equal(res.status, 1);
  assert.match(res.stderr, /request failed/);
});

test('fails on a non-200 response or a non-JSON body', async () => {
  await withHealthServer(async ({ url, respond }) => {
    respond(500, { error: 'boom' });
    let res = await runCheck(url);
    assert.equal(res.status, 1);
    assert.match(res.stderr, /HTTP 500/);

    respond(200, '<html>nginx</html>');
    res = await runCheck(url);
    assert.equal(res.status, 1);
    assert.match(res.stderr, /invalid JSON/);
  });
});

test('fails when the runtime is older than the supported major', async () => {
  await withHealthServer(async ({ url, respond }) => {
    respond(200, healthAppClosed({ node: 'v18.20.4' }));
    const res = await runCheck(url);
    assert.equal(res.status, 1);
    assert.match(res.stderr, /node v18\.20\.4 < required major 20/);
  });
});
