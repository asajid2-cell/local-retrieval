// /ws AUTHORIZATION GATE — the security half of the test-mode exemption.
//
// server.js grants a TEST-ONLY bypass (testModeLocalTrust: MUX_TEST_MODE=1 AND a loopback peer) so the
// suite can attach viewers without an hl-auth session. Commit aa74650 exists because ambient loopback
// trust on this exact path was WRONG: containers on this host run with network_mode: host and share
// 127.0.0.1, so "the peer is 127.0.0.1" proves nothing about who is calling.
//
// These tests are the fence around that bypass. They must fail if either half of the condition is ever
// dropped, so a future edit cannot quietly turn a test convenience back into a production hole:
//
//   (a) MUX_TEST_MODE unset  -> a loopback caller with no owner cookie is refused 1008.
//   (b) X-Forwarded-For set  -> refused 1008 even under MUX_TEST_MODE, because that request came
//                               through nginx and is therefore public traffic, not a local caller.
//   (c) positive control     -> under MUX_TEST_MODE, from loopback, WITHOUT that header, attach works.
//
// (c) is not decoration: without it, (a) and (b) would keep passing if /ws broke outright and refused
// everybody, which is precisely how 19 suites came to look broken while the gate looked fine.

const test = require('node:test');
const assert = require('node:assert/strict');
const { once } = require('node:events');
const WebSocket = require('ws');
const http = require('node:http');

const { RelayHarness, sleep } = require('./harness');

// A real deploy only ever sees browser upgrades, which always carry an Origin. wsOriginOk() answers
// TEST_MODE for a MISSING Origin, so a no-Origin probe would be destroyed before the handshake and
// never reach the authorization check we are testing. Presenting an allowlisted Origin isolates the
// auth gate from the CSWSH gate: whatever we observe here is the owner check talking, nothing else.
const ORIGIN = 'https://relay-auth-gate.test';

function shellSession(name) {
  return {
    name, alive: true, created: 1000, lastOut: 1000, cols: 100, rows: 30,
    hasCommand: false, shellOnly: true, ready: true, kind: 'shell', sessionId: '', aliases: [],
  };
}

// Resolves with how the relay answered the upgrade: a close code, or 'open' if it stayed up.
async function attachOutcome(port, session, headers = {}) {
  const ws = new WebSocket(
    `ws://127.0.0.1:${port}/ws?session=${session}&cols=80&rows=24`,
    { headers: { origin: ORIGIN, ...headers } },
  );
  const outcome = await new Promise(resolve => {
    let settled = false;
    const done = value => { if (!settled) { settled = true; resolve(value); } };
    // A refusal closes within a round trip; the grace period is for the accept case, where we want
    // to be sure no late close is coming before we call it open.
    ws.on('close', code => done({ code }));
    ws.on('error', err => done({ error: String(err && err.message || err) }));
    ws.on('open', () => setTimeout(() => done({ code: 'open' }), 300));
  });
  try { ws.terminate(); } catch {}
  return outcome;
}

// MUX_TEST_MODE must be absent, not falsy-but-present, or a future `'0'`-style check could disagree
// with `=== '1'`. The harness sets it, so this deletes it from the spawned child's environment.
const NO_TEST_MODE = { MUX_TEST_MODE: undefined, ALLOWED_WS_ORIGINS: ORIGIN };
const TEST_MODE_ON = { ALLOWED_WS_ORIGINS: ORIGIN };

test('production middleware distinguishes anonymous, non-owner, and owner over HTTP and WebSocket', async t => {
  const verified = [];
  const identity = http.createServer((req, res) => {
    if (req.url !== '/internal/verify') { res.writeHead(404); res.end(); return; }
    const token = req.headers['x-session-token'];
    verified.push(token);
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify({ authenticated: token === 'fixture-owner' || token === 'fixture-member', user: { isOwner: token === 'fixture-owner' } }));
  });
  await new Promise(resolve => identity.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => { identity.closeAllConnections(); identity.close(resolve); }));
  const h = new RelayHarness({ ...NO_TEST_MODE, HLAUTH_BASE: `http://127.0.0.1:${identity.address().port}` });
  t.after(() => h.stop());
  await h.start();
  const host = await h.connectHost([shellSession('identitycase')]);
  t.after(() => host.close());
  for (const [token, expected] of [[null, 401], ['fixture-member', 403], ['fixture-owner', 200]]) {
    const headers = { 'x-forwarded-for': '203.0.113.7', ...(token ? { cookie: `hl_session=${token}` } : {}) };
    const response = await h.request('GET', '/api/sessions', undefined, headers);
    assert.equal(response.status, expected, `HTTP authorization for ${token || 'anonymous'}`);
    const outcome = await attachOutcome(h.port, 'identitycase', headers);
    assert.deepEqual(outcome, { code: token === 'fixture-owner' ? 'open' : 1008 });
    if (token !== 'fixture-owner') {
      await host.assertNo(m => m.t === 'sb' && m.s === 'identitycase', 'unauthorized identity must not reach host');
      for (const route of ['/api/app-commands', '/api/app-commands/lease', '/api/app-commands/forged/ack']) {
        const rejected = await h.request('POST', route, { type: 'setfavorite', sessionId: 'fixture', favorite: true }, headers);
        assert.equal(rejected.status, route === '/api/app-commands' ? expected : 403, `unauthorized identity must not access ${route}`);
      }
    }
  }
  const { chromium } = require('playwright');
  const browser = await chromium.launch({ headless: true });
  t.after(() => browser.close());
  for (const [token, expected] of [[null, 401], ['fixture-member', 403], ['fixture-owner', 200]]) {
    const context = await browser.newContext();
    try {
      if (token) await context.addCookies([{ name: 'hl_session', value: token, url: `http://127.0.0.1:${h.port}` }]);
      const page = await context.newPage();
      await page.goto(`http://127.0.0.1:${h.port}/`);
      const result = await page.evaluate(async () => {
        const response = await fetch('/api/sessions');
        return { status: response.status, body: await response.text() };
      });
      assert.equal(result.status, expected, 'real browser cookie authorization');
      if (token === 'fixture-owner') assert.ok(JSON.stringify(result.body).includes('identitycase'));
      const mutation = await page.evaluate(async () => {
        const response = await fetch('/api/app-commands', { method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ type: 'setfavorite', sessionId: 'fixture-auth-chat', favorite: true, expectedRevision: 'fixture-revision', intentId: 'browser-owner-mutation' }) });
        return { status: response.status, text: await response.text() };
      });
      assert.equal(mutation.status, expected, 'browser mutation requires owner cookie');
      const upload = await page.evaluate(async () => {
        const response = await fetch('/api/upload?name=authorization-fixture.txt', { method: 'POST',
          headers: { 'Content-Type': 'application/octet-stream' }, body: 'isolated browser authorization fixture' });
        return { status: response.status, text: await response.text() };
      });
      assert.equal(upload.status, expected, 'browser upload requires owner cookie');
      if (token === 'fixture-owner') {
        const queued = JSON.parse(mutation.text);
        assert.ok(queued.id, 'owner mutation receives a durable queue receipt');
        const lease = await h.request('POST', '/api/app-commands/lease', { owner: 'fixture-pc-authority', limit: 1 },
          { 'x-mux-command-bridge': h.commandBridgeToken });
        assert.equal(lease.status, 200);
        assert.equal(lease.body[0].id, queued.id);
        assert.equal(lease.body[0].sessionId, 'fixture-auth-chat');
        assert.equal(lease.body[0].intentId, 'browser-owner-mutation');
      }
    } finally { await context.close(); }
  }
  assert.ok(verified.includes('fixture-member'), 'member identity reached actual middleware verification');
  assert.ok(verified.includes('fixture-owner'), 'owner positive control reached actual middleware verification');
  await host.waitFor(m => m.t === 'sb' && m.s === 'identitycase', 'authorized owner screen request');
});

test('owner browser mutation reaches real headless archive and survives reload', async t => {
  const fs = require('node:fs'), path = require('node:path'), cp = require('node:child_process'), crypto = require('node:crypto');
  const { freePort, waitFor } = require('./harness');
  const identity = http.createServer((req, res) => {
    res.setHeader('content-type', 'application/json');
    const token = req.headers['x-session-token'];
    res.end(JSON.stringify({ authenticated: ['fixture-owner', 'fixture-member'].includes(token), user: { isOwner: token === 'fixture-owner' } }));
  });
  await new Promise(resolve => identity.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => { identity.closeAllConnections(); identity.close(resolve); }));
  const h = new RelayHarness({ ...NO_TEST_MODE, HLAUTH_BASE: `http://127.0.0.1:${identity.address().port}` });
  let server;
  const stopServer = async () => {
    if (server && server.exitCode === null && server.signalCode === null) { const exited = once(server, 'exit'); server.kill(); await exited; }
  };
  t.after(async () => { await stopServer(); await h.stop(); });
  await h.start();
  const port = await freePort(), token = crypto.randomBytes(32).toString('hex');
  const storePath = path.join(h.tmp, 'archive.json'), sources = path.join(h.tmp, 'claude-sources');
  fs.mkdirSync(sources);
  const session = id => ({ id, title: id, tool: 'claude', sourcePath: '', workspace: h.tmp, userMessageCount: 2, messageCount: 4, tags: [], aliases: [], pinned: false });
  fs.writeFileSync(storePath, JSON.stringify({ sessions: { target: session('target'), unrelated: session('unrelated') }, settings: { sources: [], multiplexSshTarget: 'loopback', multiplexApiPort: h.port }, collections: {} }));
  const dll = path.resolve(__dirname, '../../app/native/CodexLocalRetrieval.Server/bin/Debug/net8.0/CodexLocalRetrieval.Server.dll');
  assert.ok(fs.existsSync(dll), 'build headless Server before connected acceptance');
  const startServer = () => {
    server = cp.spawn('dotnet', [dll], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'], env: { ...process.env,
      CLR_REMOTE_TOKEN: token, CLR_REMOTE_PORT: String(port), CLR_REMOTE_BIND: '127.0.0.1', CLR_REMOTE_STORE: storePath,
      CLR_CLAUDE_PROJECTS: sources, CLR_REMOTE_SYNC: '0', CLR_REMOTE_BRIDGE: '1', CLR_REMOTE_TEST_PROFILE: '1',
      CLR_REMOTE_TEST_RELAY_PORT: String(h.port), CLR_REMOTE_TEST_COMMAND_BRIDGE_TOKEN: h.commandBridgeToken,
      MUX_TEST_FIXTURE: '1', MUX_BIND_HOST: '127.0.0.1', CLR_REMOTE_ALLOW_LAUNCH: '0', CLR_FLEET: '0', CLR_REMOTE_IDLE_UNLOAD_SEC: '0' } });
    server.stderr.resume();
  };
  const archiveGet = async route => {
    const response = await fetch(`http://127.0.0.1:${port}${route}`, { headers: { authorization: `Bearer ${token}` } });
    assert.equal(response.status, 200); return response.json();
  };
  startServer();
  await waitFor(async () => { try { return await archiveGet('/healthz'); } catch { return false; } }, 'headless authority', 30000);
  const row = (await archiveGet('/api/discovery/chats?showHidden=true&archived=all')).rows.find(row => row.id === 'target');
  const beforeUnrelated = (await archiveGet('/api/discovery/chats?showHidden=true&archived=all')).rows.find(row => row.id === 'unrelated');
  const browser = await require('playwright').chromium.launch({ headless: true });
  t.after(() => browser.close());
  let commandId;
  for (const [cookie, expected] of [[null, 401], ['fixture-member', 403], ['fixture-owner', 200]]) {
    const context = await browser.newContext();
    try {
      if (cookie) await context.addCookies([{ name: 'hl_session', value: cookie, url: `http://127.0.0.1:${h.port}` }]);
      const page = await context.newPage(); await page.goto(`http://127.0.0.1:${h.port}/`);
      const response = await page.evaluate(async revision => {
        const r = await fetch('/api/app-commands', { method: 'POST', headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ type: 'setfavorite', sessionId: 'target', favorite: true, expectedRevision: revision, intentId: 'owner-to-archive' }) });
        return { status: r.status, text: await r.text() };
      }, row.revision);
      assert.equal(response.status, expected);
      if (cookie !== 'fixture-owner') assert.equal(JSON.parse(fs.readFileSync(storePath)).sessions.target.pinned, false);
      else {
        commandId = JSON.parse(response.text).id;
        await waitFor(async () => page.evaluate(async id => (await (await fetch('/api/app-commands/' + id)).json()).status === 'done', commandId), 'PC-applied owner mutation', 45000);
        await page.reload();
        assert.equal(await page.evaluate(async id => (await (await fetch('/api/app-commands/' + id)).json()).status, commandId), 'done');
      }
    } finally { await context.close(); }
  }
  await stopServer(); startServer();
  await waitFor(async () => { try { return (await archiveGet('/api/discovery/chats?showHidden=true&archived=all')).rows.find(row => row.id === 'target')?.pinned; } catch { return false; } }, 'durable favorite after Server restart', 30000);
  const after = JSON.parse(fs.readFileSync(storePath));
  const afterUnrelated = (await archiveGet('/api/discovery/chats?showHidden=true&archived=all')).rows.find(row => row.id === 'unrelated');
  assert.deepEqual(afterUnrelated, beforeUnrelated);
  assert.equal(after.sessions.target.pinned, true);
});

// ---------------------------------------------------------------------------------------------
// (a) THE NEGATIVE. No test mode => loopback buys nothing, even for a session that really exists.
// ---------------------------------------------------------------------------------------------
test('without MUX_TEST_MODE, a loopback viewer with no owner cookie is refused 1008', async t => {
  const h = new RelayHarness(NO_TEST_MODE);
  await h.start();
  t.after(() => h.stop());

  // /host is token-gated, not owner-gated, so the session below exists for real. That matters: it
  // rules out "refused because the session is unknown" and pins the refusal to authorization.
  const host = await h.connectHost([shellSession('gatecase')]);
  t.after(() => host.close());

  const outcome = await attachOutcome(h.port, 'gatecase');
  assert.deepEqual(
    outcome, { code: 1008 },
    'a loopback peer is not a credential: /ws must refuse 1008 unauthorized when MUX_TEST_MODE is unset',
  );

  // And it must refuse without asking muxd for anything — an unauthorized caller may not make the
  // relay do work on its behalf, nor learn whether the session exists from a side effect.
  await host.assertNo(m => m.t === 'sb' && m.s === 'gatecase', 'a refused viewer must not request scrollback');
});

// ---------------------------------------------------------------------------------------------
// (b) The loopback half is load-bearing too: nginx traffic is not local traffic.
// ---------------------------------------------------------------------------------------------
test('even under MUX_TEST_MODE, an X-Forwarded-For upgrade is refused 1008', async t => {
  const h = new RelayHarness(TEST_MODE_ON);
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('xffcase')]);
  t.after(() => host.close());

  // nginx overwrites X-Forwarded-For with $remote_addr on every proxied request, so its presence is
  // the marker of a request that came from the public internet rather than from this box.
  const outcome = await attachOutcome(h.port, 'xffcase', { 'x-forwarded-for': '203.0.113.7' });
  assert.deepEqual(
    outcome, { code: 1008 },
    'a forwarded (public) request must never satisfy the local-trust half of the test exemption',
  );
});

// ---------------------------------------------------------------------------------------------
// (c) POSITIVE CONTROL. The bypass actually works, so (a) and (b) are refusals and not an outage.
// ---------------------------------------------------------------------------------------------
test('under MUX_TEST_MODE, a direct loopback viewer attaches', async t => {
  const h = new RelayHarness(TEST_MODE_ON);
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('okcase')]);
  t.after(() => host.close());

  const outcome = await attachOutcome(h.port, 'okcase');
  assert.deepEqual(
    outcome, { code: 'open' },
    'MUX_TEST_MODE + direct loopback must attach; if this fails the two refusals above prove nothing',
  );
  // Attaching means the viewer reached the hosted path and the relay asked muxd for its screen.
  await host.waitFor(m => m.t === 'sb' && m.s === 'okcase', 'scrollback request for the accepted viewer');
});

// ---------------------------------------------------------------------------------------------
// (d) The exemption is scoped to authorization only — it must not disable the CSWSH origin gate.
// ---------------------------------------------------------------------------------------------
test('the test exemption does not admit a foreign Origin', async t => {
  const h = new RelayHarness(TEST_MODE_ON);
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('origincase')]);
  t.after(() => host.close());

  // Destroyed before the handshake, so the client sees a transport error rather than a close code.
  const outcome = await attachOutcome(h.port, 'origincase', { origin: 'https://evil.example' });
  assert.equal(outcome.code, undefined, `a foreign Origin must never complete the handshake: ${JSON.stringify(outcome)}`);
  assert.ok(outcome.error, 'the upgrade must be destroyed, not politely closed');
  await sleep(50);
});
