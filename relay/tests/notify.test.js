// Attention-episode push notifications: one ntfy POST per episode, only after the state settles.
// Everything here is real — a real relay process, a real muxd-shaped websocket host, and a real HTTP
// server standing in for ntfy. Nothing about the push path is stubbed, so a regression in server.js
// wiring, notify.js transport, or the episode ledger all show up here.
const assert = require('node:assert/strict');
const http = require('node:http');
const { test } = require('node:test');
const { once } = require('node:events');

const { RelayHarness, freePort, sleep, waitFor } = require('./harness');

const SETTLE_MS = 300;
const LINK_BASE = 'https://mux.example/app';

// Stands in for the ntfy topic. Records every POST it receives, headers included.
class CaptureNtfy {
  constructor() { this.posts = []; }

  async start() {
    this.port = await freePort();
    this.server = http.createServer((req, res) => {
      const chunks = [];
      req.on('data', c => chunks.push(c));
      req.on('end', () => {
        this.posts.push({
          method: req.method,
          headers: req.headers,
          body: Buffer.concat(chunks).toString('utf8'),
        });
        res.writeHead(200, { 'content-type': 'application/json' });
        res.end('{"id":"test"}');
      });
    });
    this.server.listen(this.port, '127.0.0.1');
    await once(this.server, 'listening');
    this.url = `http://127.0.0.1:${this.port}/mux-attention`;
  }

  async stop() {
    if (!this.server) return;
    this.server.close();
    await once(this.server, 'close').catch(() => {});
    this.server = null;
  }

  get count() { return this.posts.length; }

  async waitForCount(n, label, timeoutMs = 4000) {
    return await waitFor(() => (this.posts.length >= n ? this.posts : null), label, timeoutMs);
  }
}

function hostSession(name, agentState, extra = {}) {
  return {
    name,
    alive: true,
    hasCommand: true,
    created: Date.now() - 600000,
    lastOut: Date.now(),
    tail: '',
    agentState,
    agentLabel: agentState === 'attention' ? 'waiting for you'
      : agentState === 'stopped' ? 'agent stopped' : 'agent working',
    agentDetail: 'classified by muxd',
    agentConfidence: 'high',
    ...extra,
  };
}

async function withRelay(fn, env = {}) {
  const ntfy = new CaptureNtfy();
  await ntfy.start();
  const harness = new RelayHarness({
    MUX_NTFY_URL: ntfy.url,
    MUX_NOTIFY_SETTLE_MS: String(SETTLE_MS),
    MUX_PUBLIC_BASE: LINK_BASE,
    ...env,
  });
  await harness.start();
  try {
    await fn(harness, ntfy);
  } finally {
    await harness.stop();
    await ntfy.stop();
  }
}

test('a session that stays working never pushes', async () => {
  await withRelay(async (harness, ntfy) => {
    const host = await harness.connectHost([hostSession('alpha', 'working')]);
    for (let i = 0; i < 4; i++) {
      host.sendSessions([hostSession('alpha', 'working')]);
      await sleep(SETTLE_MS / 2);
    }
    await sleep(SETTLE_MS * 2);
    assert.equal(ntfy.count, 0, `expected no push while working; got ${JSON.stringify(ntfy.posts)}`);
    host.close();
  });
});

test('one push per episode across working -> attention -> working -> attention', async () => {
  await withRelay(async (harness, ntfy) => {
    const host = await harness.connectHost([hostSession('alpha', 'working')]);

    host.sendSessions([hostSession('alpha', 'attention')]);
    await ntfy.waitForCount(1, 'first episode push');
    await sleep(SETTLE_MS * 2);
    assert.equal(ntfy.count, 1, 'a held episode must push exactly once, not once per settle tick');

    // Still in attention, and muxd keeps announcing it: the episode is the same one, so still one push.
    for (let i = 0; i < 3; i++) {
      host.sendSessions([hostSession('alpha', 'attention')]);
      await sleep(SETTLE_MS / 2);
    }
    assert.equal(ntfy.count, 1, 'repeat session frames inside one episode must not re-push');

    // Clear the episode, then re-enter it: that is a NEW episode and earns exactly one more push.
    host.sendSessions([hostSession('alpha', 'working')]);
    await sleep(SETTLE_MS / 2);
    assert.equal(ntfy.count, 1, 'returning to working must not push');

    host.sendSessions([hostSession('alpha', 'attention')]);
    await ntfy.waitForCount(2, 'second episode push');
    await sleep(SETTLE_MS * 2);
    assert.equal(ntfy.count, 2, 'exactly one push per episode');
    host.close();
  });
});

test('the push carries session name, agent label and a deep link', async () => {
  await withRelay(async (harness, ntfy) => {
    const host = await harness.connectHost([hostSession('alpha', 'working')]);
    host.sendSessions([hostSession('alpha', 'attention')]);
    const [post] = await ntfy.waitForCount(1, 'attention push');

    assert.equal(post.method, 'POST');
    assert.match(post.headers.title, /alpha/);
    assert.match(post.headers.title, /waiting for you/);
    assert.match(post.body, /alpha/);
    assert.match(post.body, /waiting for you/);
    assert.equal(post.headers.click, `${LINK_BASE}/?s=alpha`);
    assert.match(post.headers.tags || '', /bell/);
    host.close();
  });
});

test('a stopped session is an episode too, and carries the stopped tags/priority', async () => {
  await withRelay(async (harness, ntfy) => {
    const host = await harness.connectHost([hostSession('beta', 'working')]);
    host.sendSessions([hostSession('beta', 'stopped')]);
    const [post] = await ntfy.waitForCount(1, 'stopped push');
    assert.match(post.headers.title, /beta/);
    assert.equal(post.headers.priority, 'high');
    assert.match(post.headers.tags || '', /octagonal_sign/);
    host.close();
  });
});

test('a flap shorter than the settle window pushes nothing', async () => {
  await withRelay(async (harness, ntfy) => {
    const host = await harness.connectHost([hostSession('alpha', 'working')]);
    for (let i = 0; i < 5; i++) {
      host.sendSessions([hostSession('alpha', 'attention')]);
      await sleep(SETTLE_MS / 5);
      host.sendSessions([hostSession('alpha', 'working')]);
      await sleep(SETTLE_MS / 5);
    }
    await sleep(SETTLE_MS * 3);
    assert.equal(ntfy.count, 0, `sub-settle flapping must stay silent; got ${JSON.stringify(ntfy.posts)}`);
    host.close();
  }, { MUX_NOTIFY_SETTLE_MS: '1200' });
});

test('a relay restart mid-episode does not re-fire the push', async () => {
  await withRelay(async (harness, ntfy) => {
    const host = await harness.connectHost([hostSession('alpha', 'working')]);
    host.sendSessions([hostSession('alpha', 'attention')]);
    await ntfy.waitForCount(1, 'episode push before restart');
    assert.equal(ntfy.count, 1);
    host.close();

    // The relay dies and comes back on the same state dir while the session is STILL waiting; muxd
    // redials and re-announces it. The persisted episode has to suppress the duplicate buzz.
    await harness.restart();
    const revived = await harness.connectHost([hostSession('alpha', 'attention')]);
    await sleep(SETTLE_MS * 4);
    assert.equal(ntfy.count, 1, `restart mid-episode must not duplicate; got ${JSON.stringify(ntfy.posts)}`);

    // And the episode ledger is still live: clearing and re-entering after the restart still pushes.
    revived.sendSessions([hostSession('alpha', 'working')]);
    await sleep(SETTLE_MS / 2);
    revived.sendSessions([hostSession('alpha', 'attention')]);
    await ntfy.waitForCount(2, 'post-restart new episode push');
    assert.equal(ntfy.count, 2);
    revived.close();
  });
});

test('losing the host link is not an attention episode', async () => {
  await withRelay(async (harness, ntfy) => {
    const host = await harness.connectHost([hostSession('alpha', 'working')]);
    host.close();
    await waitFor(async () => {
      const health = await harness.json('GET', '/api/health');
      return health.host && !health.host.connected ? health : null;
    }, 'host link down');
    await sleep(SETTLE_MS * 4);
    assert.equal(ntfy.count, 0, `host-link-down belongs to the ops-health lane; got ${JSON.stringify(ntfy.posts)}`);
  });
});

test('a shell-only or dormant session never becomes an episode', async () => {
  await withRelay(async (harness, ntfy) => {
    const host = await harness.connectHost([hostSession('alpha', 'working')]);
    host.sendSessions([
      { name: 'alpha', alive: true, hasCommand: false, shellOnly: true, tail: '' },
      { name: 'beta', alive: false, hasCommand: true, tail: '' },
    ]);
    await sleep(SETTLE_MS * 4);
    assert.equal(ntfy.count, 0, `neutral and dormant sessions must not push; got ${JSON.stringify(ntfy.posts)}`);
    host.close();
  });
});

test('with MUX_NTFY_URL unset the detector is a silent no-op', async () => {
  const ntfy = new CaptureNtfy();
  await ntfy.start();
  const harness = new RelayHarness({
    MUX_NTFY_URL: '',
    MUX_NOTIFY_SETTLE_MS: String(SETTLE_MS),
    MUX_PUBLIC_BASE: LINK_BASE,
  });
  await harness.start();
  try {
    const host = await harness.connectHost([hostSession('alpha', 'working')]);
    host.sendSessions([hostSession('alpha', 'attention')]);
    await sleep(SETTLE_MS * 4);
    assert.equal(ntfy.count, 0);
    // Silent means silent: no crash, no stderr noise, relay still serving.
    const health = await harness.json('GET', '/api/health');
    assert.equal(health.host.connected, true);
    host.close();
  } finally {
    await harness.stop();
    await ntfy.stop();
  }
});
