// Relay consumption of muxd's OS-level process truth (cap "agentTruth", protocol 4, additive).
//
// The pin under test: state dots and the death-recovery gate follow
//     fresh host agentTruth  >  host heuristic (agentState)  >  relay pane regex
// and heal-eligibility opens ONLY on a proven-dead process tree or an explicit dormant declaration.
// A footer that merely looks like a shell prompt must never be able to kill an agent, and a host with
// no agentTruth at all must behave exactly the way it did before any of this existed.
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
const BASE_CAPS = ['create', 'createAck', 'kill', 'rename', 'heal', 'tail', 'scrollback', 'relaunch'];
const TRUTH_CAPS = [...BASE_CAPS, 'agentTruth'];

// A footer no regex on either side would ever call dead: a live Claude status line.
const HEALTHY_FOOTER = '⏵⏵ auto mode on · sonnet 5\n> esc to interrupt (12s · 4.1k tokens)';
// The exact shape tailLooksAtShellPrompt() reads as "the command returned to a shell".
const DEATH_FOOTER = 'Session ended.\nPS C:\\Users\\Ahmed\\dev>';

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
    } catch (err) { last = err; }
    await sleep(50);
  }
  throw new Error(`timed out waiting for ${label}; last=${last && last.stack || JSON.stringify(last)}`);
}

class Harness {
  constructor() {
    this.tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'relay-truth-'));
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
        MUX_TEST_FIXTURE: '1',
        MUX_BIND_HOST: '127.0.0.1',
        MUX_AUTOHEAL: '0',
        MUX_STATE_DIR: this.tmp,
        MUX_HOST_SB_WAIT_MS: '40',
        HLAUTH_BASE: 'http://127.0.0.1:1',
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    this.proc.stdout.on('data', d => { this.stdout += d.toString(); });
    this.proc.stderr.on('data', d => { this.stderr += d.toString(); });
    try {
      await waitFor(() => this.stdout.includes(`multiplex-app on 127.0.0.1:${this.port}`), 'relay start');
    } catch (err) {
      throw new Error(`${err.message}\n--- relay stderr ---\n${this.stderr}\n--- relay stdout ---\n${this.stdout}`);
    }
  }

  async stop() {
    try { this.host && this.host.close(); } catch {}
    if (this.proc && this.proc.exitCode === null) {
      this.proc.kill();
      await Promise.race([once(this.proc, 'exit'), sleep(2000)]);
      if (this.proc.exitCode === null) this.proc.kill('SIGKILL');
    }
    fs.rmSync(this.tmp, { recursive: true, force: true });
  }

  json(pathName) {
    return new Promise((resolve, reject) => {
      const req = http.request({ method: 'GET', hostname: '127.0.0.1', port: this.port, path: pathName }, res => {
        const chunks = [];
        res.on('data', c => chunks.push(c));
        res.on('end', () => {
          const text = Buffer.concat(chunks).toString('utf8');
          try { resolve(JSON.parse(text)); } catch (err) { reject(new Error(`bad JSON from ${pathName}: ${text}`)); }
        });
      });
      req.on('error', reject);
      req.end();
    });
  }

  // Connect a fake muxd that advertises `caps` and reports `sessions`, then wait until the relay
  // accepts the link. Returns once /api/health confirms the host is up and protocol-compatible.
  async connectHost(caps, sessions) {
    this.host = new WebSocket(`ws://127.0.0.1:${this.port}/host?token=test-token`);
    this.host.on('error', () => {});
    await once(this.host, 'open');
    this.host.send(JSON.stringify({ t: 'hello', host: 'FAKEPC', protocol: 4, caps, sessions }));
    await waitFor(async () => {
      const health = await this.json('/api/health');
      return health.host && health.host.connected && health.host.protocolOk ? health : null;
    }, 'host connected');
  }

  async row(name) {
    const list = await this.json('/api/sessions');
    const row = list.find(s => s.name === name);
    assert.ok(row, `session ${name} missing from /api/sessions: ${JSON.stringify(list)}`);
    return row;
  }
}

// A live command-backed session. `created`/`lastOut` are stamped fresh so the legacy activity
// heuristic reads "working" — that way any red we observe came from a death signal, not from silence.
function liveSession(name, extra = {}) {
  const now = Date.now();
  return {
    name, alive: true, created: now - 600000, lastOut: now, cols: 100, rows: 30,
    hasCommand: true, shellOnly: false, ready: true, kind: 'command',
    sessionId: '', aliases: [], tail: HEALTHY_FOOTER, ...extra,
  };
}

function truth(procAlive, extra = {}) {
  return {
    agentStateSource: 'process',
    agentTruth: {
      procAlive, cpuActiveRecent: false, exe: 'claude.exe',
      checkedUtc: new Date().toISOString(), ...extra,
    },
  };
}

async function withRelay(t, fn) {
  const h = new Harness();
  // Register teardown BEFORE start(). Harness.start() rethrows when the relay misses its boot
  // window, and node does not run t.after for a test that failed before the hook was registered -
  // so start-then-register leaks the spawned server.js for the life of the test process. A leaked
  // relay then contends with every later suite run and shows up as THEIR startup timeout. stop() is
  // safe against a half-started harness: it null-checks this.proc and force-removes this.tmp.
  t.after(() => h.stop());
  await h.start();
  return await fn(h);
}

// (a) The OS says the process tree is gone while every other signal looks healthy — fresh output, a
// live agent footer, and the host's own heuristic still calling it "working". Truth wins.
test('procAlive:false beats a healthy footer and the host heuristic: red and heal-eligible', async t => {
  await withRelay(t, async h => {
    await h.connectHost(TRUTH_CAPS, [liveSession('deadproc', {
      agentState: 'working', agentLabel: 'agent working', agentConfidence: 'medium',
      ...truth(false),
    })]);
    const row = await h.row('deadproc');
    assert.equal(row.state, 'red', `expected red, got ${JSON.stringify(row)}`);
    assert.equal(row.agentState, 'stopped');
    assert.equal(row.agentConfidence, 'high');
    assert.equal(row.needsAttention, true);
    assert.equal(row.healEligible, true);
    assert.equal(row.agentStateSource, 'process');
    assert.match(row.agentDetail, /claude\.exe/);
  });
});

// (b) The pane regex's exact death signature, contradicted by the OS. No red, no heal.
test('procAlive:true suppresses the relay pane-regex death read: not red, not heal-eligible', async t => {
  await withRelay(t, async h => {
    await h.connectHost(TRUTH_CAPS, [liveSession('liveproc', { tail: DEATH_FOOTER, ...truth(true) })]);
    const row = await h.row('liveproc');
    assert.notEqual(row.state, 'red', `pane regex overrode live process truth: ${JSON.stringify(row)}`);
    assert.notEqual(row.agentState, 'stopped');
    assert.equal(row.healEligible, false);
    assert.equal(row.agentStateSource, 'process');
  });
});

// (b2) Same contradiction, but the "stopped" call comes from muxd's own footer heuristic rather than
// ours. The host heuristic is still a heuristic; a live process outranks it too.
test('procAlive:true also suppresses a host agentState of "stopped"', async t => {
  await withRelay(t, async h => {
    await h.connectHost(TRUTH_CAPS, [liveSession('livehost', {
      tail: DEATH_FOOTER, agentState: 'stopped', agentLabel: 'agent stopped', agentConfidence: 'high',
      ...truth(true),
    })]);
    const row = await h.row('livehost');
    assert.notEqual(row.state, 'red', `host heuristic overrode live process truth: ${JSON.stringify(row)}`);
    assert.notEqual(row.agentState, 'stopped');
    assert.equal(row.healEligible, false);
  });
});

// (c) No agentTruth anywhere: byte-for-byte the pre-agentTruth behavior on both sides of the regex.
test('a host that never sends agentTruth keeps exactly the legacy behavior', async t => {
  await withRelay(t, async h => {
    await h.connectHost(BASE_CAPS, [
      liveSession('legacydead', { tail: DEATH_FOOTER }),
      liveSession('legacylive', { tail: HEALTHY_FOOTER }),
    ]);
    const dead = await h.row('legacydead');
    assert.equal(dead.state, 'red', `legacy shell-prompt regex stopped firing: ${JSON.stringify(dead)}`);
    assert.equal(dead.agentState, 'stopped');
    assert.equal(dead.agentStateSource, '');
    // Legacy red is a regex read, never proof of death — it must not open the recovery gate.
    assert.equal(dead.healEligible, false);

    const live = await h.row('legacylive');
    assert.equal(live.state, 'green', `legacy activity heuristic changed: ${JSON.stringify(live)}`);
    assert.equal(live.agentState, 'working');
    assert.equal(live.agentStateSource, '');
    assert.equal(live.healEligible, false);
  });
});

// A host too old to advertise the capability does not get to drive the dots even if it sends the
// fields — the cap gate (hostSupportsCap) is what makes the rollout safe in either order.
test('agentTruth from a host that never advertised the capability is ignored', async t => {
  await withRelay(t, async h => {
    await h.connectHost(BASE_CAPS, [liveSession('nocap', { tail: DEATH_FOOTER, ...truth(true) })]);
    const row = await h.row('nocap');
    assert.equal(row.state, 'red', `uncapped agentTruth was trusted: ${JSON.stringify(row)}`);
    assert.equal(row.agentStateSource, '');
    assert.equal(row.healEligible, false);
  });
});

// Evidence expires. A probe from ten minutes ago cannot prove anything about now, so the decision
// falls back to the legacy path instead of freezing the dot at a stale answer.
test('a stale or heuristic-degraded probe falls back to the legacy path', async t => {
  await withRelay(t, async h => {
    const stale = new Date(Date.now() - 600000).toISOString();
    await h.connectHost(TRUTH_CAPS, [
      liveSession('staletruth', { tail: DEATH_FOOTER, ...truth(true, { checkedUtc: stale }) }),
      liveSession('degraded', {
        tail: DEATH_FOOTER, agentStateSource: 'heuristic',
        agentTruth: { procAlive: true, cpuActiveRecent: false, exe: '', checkedUtc: '' },
      }),
    ]);
    for (const name of ['staletruth', 'degraded']) {
      const row = await h.row(name);
      assert.equal(row.state, 'red', `${name} trusted a non-fresh probe: ${JSON.stringify(row)}`);
      assert.equal(row.healEligible, false);
    }
  });
});

// Explicit host dormancy is the other death signal that may open the gate: muxd is telling us there
// is no process at all, which needs no probe to believe.
test('explicit host dormancy is heal-eligible without any probe', async t => {
  await withRelay(t, async h => {
    await h.connectHost(BASE_CAPS, [{
      name: 'dormantsess', alive: false, created: 1000, lastOut: 1000, cols: 100, rows: 30,
      hasCommand: true, shellOnly: false, ready: false, kind: 'dormant', sessionId: '', aliases: [],
    }]);
    const row = await h.row('dormantsess');
    assert.equal(row.state, 'dormant');
    assert.equal(row.healEligible, true);
  });
});

// A quiet terminal whose process is still burning CPU is thinking, not waiting for the human. Only
// the OS can tell those apart, and the legacy silence timer gets this one wrong.
test('cpuActiveRecent keeps a silent but busy agent green', async t => {
  await withRelay(t, async h => {
    const old = Date.now() - 600000;
    await h.connectHost(TRUTH_CAPS, [
      liveSession('busyquiet', { created: old, lastOut: old, tail: HEALTHY_FOOTER, ...truth(true, { cpuActiveRecent: true }) }),
      liveSession('idlequiet', { created: old, lastOut: old, tail: HEALTHY_FOOTER, ...truth(true) }),
    ]);
    const busy = await h.row('busyquiet');
    assert.equal(busy.state, 'green', `CPU-active agent read as idle: ${JSON.stringify(busy)}`);
    assert.equal(busy.agentState, 'working');
    const idle = await h.row('idlequiet');
    assert.equal(idle.state, 'yellow', `idle agent should still ask for attention: ${JSON.stringify(idle)}`);
  });
});

// Regression guard for a real cross-slice break: "exe" is a FORBIDDEN_REMOTE_KEY, so an agentTruth
// payload used to trip the executable-smuggling scan and close the host link with 1008 — muxd would
// have been unable to stay connected at all. The subtree is scanned separately and sanitized instead.
test('agentTruth.exe does not trip the forbidden-remote-key scan or drop the host link', async t => {
  await withRelay(t, async h => {
    await h.connectHost(TRUTH_CAPS, [liveSession('exefield', { ...truth(true) })]);
    const row = await h.row('exefield');
    assert.equal(row.hosted, true);
    assert.equal(row.agentStateSource, 'process');
    const health = await h.json('/api/health');
    assert.equal(health.host.connected, true, 'host link was closed by the forbidden-key scan');
  });
});

// The sanitizer is the reason the exemption above is safe: a path-shaped or command-shaped exe is
// dropped, so the relay can never end up holding something launchable that the PC pushed at it.
test('a path-shaped or command-shaped exe is dropped, not stored', async t => {
  await withRelay(t, async h => {
    await h.connectHost(TRUTH_CAPS, [liveSession('pathexe', {
      ...truth(false, { exe: 'C:\\Windows\\System32\\cmd.exe /c whoami' }),
    })]);
    const row = await h.row('pathexe');
    assert.equal(row.state, 'red');
    assert.equal(row.healEligible, true);
    assert.doesNotMatch(row.agentDetail, /cmd\.exe|System32|whoami|\\/);
    assert.doesNotMatch(JSON.stringify(row), /System32|whoami/);
  });
});
