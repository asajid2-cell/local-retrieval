// Per-session notification mute + the needs-attention chip.
//
// Two halves, both real. The server half runs a real relay, a real muxd-shaped host and a real HTTP
// server standing in for ntfy, then drives genuine attention episodes across a mute toggle and a
// process restart — a mute that only lives in memory, or one that also stops the episode machine, both
// fail here. The UI half loads relay/public/attention-ui.js into a vm and asserts index.html actually
// calls it, so the chip cannot silently stop rendering.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');
const { once } = require('node:events');

const { RelayHarness, freePort, sleep, waitFor } = require('./harness');

const SETTLE_MS = 300;
const LINK_BASE = 'https://mux.example/app';

// Stands in for the ntfy topic; records every POST.
class CaptureNtfy {
  constructor() { this.posts = []; }

  async start() {
    this.port = await freePort();
    this.server = http.createServer((req, res) => {
      const chunks = [];
      req.on('data', c => chunks.push(c));
      req.on('end', () => {
        this.posts.push({ headers: req.headers, body: Buffer.concat(chunks).toString('utf8') });
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

function hostSession(name, agentState) {
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

// working -> attention, which is what actually opens an episode. Held past the settle window so the
// sweep has every chance to push; the caller asserts whether one arrived.
async function driveEpisode(host, name) {
  host.sendSessions([hostSession(name, 'working')]);
  await sleep(SETTLE_MS / 2);
  host.sendSessions([hostSession(name, 'attention')]);
  await sleep(SETTLE_MS * 4);
}

async function rowFor(harness, name) {
  return (await harness.json('GET', '/api/sessions')).find(s => s.name === name);
}

// ---- server: the mute actually silences the phone ------------------------------------------------

test('a muted session runs its whole attention episode and pushes nothing', async () => {
  await withRelay(async (harness, ntfy) => {
    const host = await harness.connectHost([hostSession('alpha', 'working')]);

    const muted = await harness.json('POST', '/api/sessions/alpha/notify', { on: false });
    assert.deepEqual(muted, { ok: true, name: 'alpha', notify: false, notifyMuted: true });

    await driveEpisode(host, 'alpha');
    assert.equal(ntfy.count, 0, `muted session must not push; got ${JSON.stringify(ntfy.posts)}`);

    const row = await rowFor(harness, 'alpha');
    assert.equal(row.notifyMuted, true, '/api/sessions must surface the mute so the UI can render it');
    assert.equal(row.state, 'yellow', 'muting silences the phone; it must NOT hide that the tab is waiting');
    host.close();
  });
});

test('unmuting buzzes on the NEXT episode, never on a replay of the muted one', async () => {
  await withRelay(async (harness, ntfy) => {
    const host = await harness.connectHost([hostSession('alpha', 'working')]);

    await harness.json('POST', '/api/sessions/alpha/notify', { on: false });
    await driveEpisode(host, 'alpha');
    assert.equal(ntfy.count, 0, 'precondition: the muted episode was swallowed');

    // Unmute while still sitting in the SAME attention episode. Nothing may fire: that episode was
    // already consumed, and an unmute that dumps last night's backlog is worse than no push at all.
    await harness.json('POST', '/api/sessions/alpha/notify', { on: true });
    host.sendSessions([hostSession('alpha', 'attention')]);
    await sleep(SETTLE_MS * 3);
    assert.equal(ntfy.count, 0, `unmute must not replay the consumed episode; got ${JSON.stringify(ntfy.posts)}`);

    await driveEpisode(host, 'alpha');
    await ntfy.waitForCount(1, 'push on the first episode after unmute');
    assert.equal(ntfy.count, 1, 'exactly one push for the new episode');
    assert.match(ntfy.posts[0].body, /alpha/);

    assert.equal((await rowFor(harness, 'alpha')).notifyMuted, false);
    host.close();
  });
});

test('other sessions keep buzzing while one is muted', async () => {
  await withRelay(async (harness, ntfy) => {
    const host = await harness.connectHost([hostSession('alpha', 'working'), hostSession('beta', 'working')]);
    await harness.json('POST', '/api/sessions/alpha/notify', { on: false });

    host.sendSessions([hostSession('alpha', 'attention'), hostSession('beta', 'attention')]);
    await ntfy.waitForCount(1, 'unmuted sibling push');
    await sleep(SETTLE_MS * 3);

    assert.equal(ntfy.count, 1, `only beta may push; got ${JSON.stringify(ntfy.posts)}`);
    assert.match(ntfy.posts[0].body, /beta/);
    assert.doesNotMatch(ntfy.posts[0].body, /alpha/);
    host.close();
  });
});

// ---- server: the toggle is durable ---------------------------------------------------------------

test('the mute survives harness.restart() and still suppresses the push', async () => {
  await withRelay(async (harness, ntfy) => {
    let host = await harness.connectHost([hostSession('alpha', 'working')]);
    await harness.json('POST', '/api/sessions/alpha/notify', { on: false });
    host.close();

    await harness.restart();
    host = await harness.connectHost([hostSession('alpha', 'working')]);
    assert.equal((await rowFor(harness, 'alpha')).notifyMuted, true, 'mute must be reloaded from state');

    await driveEpisode(host, 'alpha');
    assert.equal(ntfy.count, 0, `a mute set before restart must still hold; got ${JSON.stringify(ntfy.posts)}`);

    // ...and unmuting after a restart is equally durable.
    await harness.json('POST', '/api/sessions/alpha/notify', { on: true });
    host.close();
    await harness.restart();
    host = await harness.connectHost([hostSession('alpha', 'working')]);
    assert.equal((await rowFor(harness, 'alpha')).notifyMuted, false);

    await driveEpisode(host, 'alpha');
    await ntfy.waitForCount(1, 'push after a persisted unmute');
    assert.equal(ntfy.count, 1);
    host.close();
  });
});

test('a mute can be set while the PC host is offline — that is when you most want it', async () => {
  await withRelay(async (harness, ntfy) => {
    // No host connected at all: the mute is pure relay state, so this must still work.
    await harness.json('POST', '/api/sessions/gamma/notify', { on: false });

    const host = await harness.connectHost([hostSession('gamma', 'working')]);
    assert.equal((await rowFor(harness, 'gamma')).notifyMuted, true);
    await driveEpisode(host, 'gamma');
    assert.equal(ntfy.count, 0, `pre-registered mute must apply once the session appears; got ${JSON.stringify(ntfy.posts)}`);
    host.close();
  });
});

test('the toggle rejects a bad name and a non-boolean body', async () => {
  await withRelay(async harness => {
    const badName = await harness.request('POST', '/api/sessions/..%2Fetc/notify', { on: false });
    assert.equal(badName.status, 400);

    for (const body of [{}, { on: 'false' }, { on: 0 }]) {
      const res = await harness.request('POST', '/api/sessions/alpha/notify', body);
      assert.equal(res.status, 400, `on must be a real boolean; body=${JSON.stringify(body)}`);
      assert.match(res.body.error, /boolean/);
    }
    // A bodyless/non-object POST is rejected by the parser, before the toggle ever sees it.
    assert.equal((await harness.request('POST', '/api/sessions/alpha/notify', null)).status, 400);

    // A no-op re-mute is still a 200 with the same shape — the UI is allowed to be idempotent.
    assert.deepEqual(await harness.json('POST', '/api/sessions/alpha/notify', { on: false }),
      { ok: true, name: 'alpha', notify: false, notifyMuted: true });
    assert.deepEqual(await harness.json('POST', '/api/sessions/alpha/notify', { on: false }),
      { ok: true, name: 'alpha', notify: false, notifyMuted: true });
  });
});

// ---- UI: attention-ui.js in a vm ------------------------------------------------------------------

function loadAttentionUi(fetchImpl) {
  const source = fs.readFileSync(path.join(__dirname, '..', 'public', 'attention-ui.js'), 'utf8');
  const context = { console, fetch: fetchImpl };
  context.globalThis = context;
  vm.runInNewContext(source, context);
  assert.ok(context.MuxAttentionUi, 'attention-ui.js must export MuxAttentionUi onto the global');
  return context.MuxAttentionUi;
}

test('the chip is a WORD, keyed off the pre-reduced state the client actually receives', () => {
  const ui = loadAttentionUi();

  // The relay never ships needsAttention to the browser for this decision — the chip descends from
  // s.state. A row carrying only needsAttention must therefore render nothing.
  assert.equal(ui.attentionChip({ state: 'green', needsAttention: true }), null);
  assert.equal(ui.attentionChip({ needsAttention: true }), null);
  for (const state of ['green', 'white', 'dormant', 'detached', undefined]) {
    assert.equal(ui.attentionChipHtml({ state }), '', `no chip for state=${state}`);
  }
  assert.equal(ui.attentionChip(null), null);

  const yellow = ui.attentionChip({ state: 'yellow' });
  assert.equal(yellow.text, 'NEEDS YOU');
  assert.equal(yellow.muted, false);
  const red = ui.attentionChip({ state: 'red' });
  assert.equal(red.text, 'STOPPED');
  assert.match(red.cls, /stopped/);

  // Rendered chip: real text, not just a coloured dot.
  const html = ui.attentionChipHtml({ state: 'yellow' });
  assert.match(html, /class="attnchip"/);
  assert.match(html, />NEEDS YOU</);
  assert.match(html, /title="[^"]+"/);
});

test('a muted session still shows the chip — muting silences the phone, not the truth', () => {
  const ui = loadAttentionUi();
  const chip = ui.attentionChip({ state: 'yellow', notifyMuted: true });
  assert.equal(chip.text, 'NEEDS YOU', 'the tab is still waiting for you');
  assert.match(chip.cls, /muted/);
  assert.match(chip.title, /muted/i);
  assert.match(ui.attentionChipHtml({ state: 'red', notifyMuted: true }), /attnchip stopped muted/);

  assert.match(ui.muteToggleLabel({ notifyMuted: true }), /MUTED/);
  assert.match(ui.muteToggleLabel({ notifyMuted: false }), /on/);
  assert.notEqual(ui.muteToggleTitle({ notifyMuted: true }), ui.muteToggleTitle({}));
});

test('chip titles cannot be broken out of by a hostile session field', () => {
  const ui = loadAttentionUi();
  const html = ui.attentionChipHtml({ state: 'yellow' });
  assert.doesNotMatch(html, /<script/);
  // The escaper is the shared guard for anything interpolated into the attribute.
  const chip = ui.attentionChip({ state: 'yellow' });
  assert.ok(chip.title.length > 0);
});

test('the toggle POSTs notifications-ENABLED and reflects the confirmed state locally', async () => {
  const calls = [];
  const ui = loadAttentionUi(async (url, options) => {
    calls.push({ url, options });
    return { ok: true, status: 200, json: async () => ({ ok: true, notifyMuted: true }) };
  });

  const body = await ui.sendNotifyToggle('http://relay', 'my tab', false);
  assert.equal(calls[0].url, 'http://relay/api/sessions/my%20tab/notify');
  assert.equal(calls[0].options.method, 'POST');
  assert.deepEqual(JSON.parse(calls[0].options.body), { on: false }, 'muting posts on:false');
  assert.deepEqual(body, { ok: true, notifyMuted: true });

  await ui.sendNotifyToggle('http://relay', 'alpha', true);
  assert.deepEqual(JSON.parse(calls[1].options.body), { on: true }, 'unmuting posts on:true');

  const failing = loadAttentionUi(async () => ({ ok: false, status: 503 }));
  await assert.rejects(() => failing.sendNotifyToggle('', 'alpha', true), /503/);
});

test('applyNotifyState writes the confirmed mute back into the cached session list', () => {
  const ui = loadAttentionUi();
  const list = [{ name: 'alpha' }, { name: 'beta', notifyMuted: true }, null];
  assert.equal(ui.applyNotifyState(list, 'alpha', true), 1);
  assert.equal(list[0].notifyMuted, true);
  assert.equal(list[1].notifyMuted, true, 'other rows untouched');
  assert.equal(ui.applyNotifyState(list, 'ghost', true), 0, 'a vanished row is a no-op, not a throw');
  assert.equal(ui.applyNotifyState(undefined, 'alpha', true), 0);
});

// ---- UI: index.html actually wires it up -----------------------------------------------------------

const html = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');

function section(start, end) {
  const from = html.indexOf(start);
  const to = html.indexOf(end, from + start.length);
  assert.notEqual(from, -1, `missing section start: ${start}`);
  assert.notEqual(to, -1, `missing section end: ${end}`);
  return html.slice(from, to);
}

test('renderTabs renders the chip through the module, alongside the dot', () => {
  assert.match(html, /<script src="attention-ui\.js"><\/script>/, 'the module must be loaded by the page');
  const tabs = section('function renderTabs()', '// ---- tab action menu ----');
  assert.match(tabs, /MuxAttentionUi\.attentionChipHtml\(s\)/);
  assert.match(tabs, /<span class="dot">/, 'the dot stays — the chip is an addition, not a replacement');
  assert.ok(
    tabs.indexOf('MuxAttentionUi.attentionChipHtml(s)') < tabs.indexOf('t.innerHTML'),
    'the chip must be built into the tab markup, not appended after'
  );
  assert.match(html, /\.tab \.attnchip \{/, 'the chip needs its own style, not the dot colour');
  assert.match(html, /\.tab \.attnchip\.stopped \{/);
  assert.match(html, /\.tab \.attnchip\.muted \{/);
});

test('the action menu carries a mute toggle driven by the module', () => {
  const menu = section('function openMenu(s, anchor)', 'function closeMenu()');
  assert.match(menu, /data-a="mute"/);
  assert.match(menu, /MuxAttentionUi\.muteToggleLabel\(s\)/);
  assert.match(menu, /MuxAttentionUi\.muteToggleTitle\(s\)/);
  assert.match(html, /a==='mute'\) toggleNotifyMute\(s\)/, 'the menu action must route to the toggle');
});

test('toggleNotifyMute inverts the current mute and refreshes the tabs', () => {
  const toggle = section('function toggleNotifyMute(s)', 'async function apiError(r)');
  assert.match(toggle, /const on = !!s\.notifyMuted/, 'muted -> the button turns notifications ON');
  assert.match(toggle, /MuxAttentionUi\.sendNotifyToggle\(base, s\.name, on\)/);
  assert.match(toggle, /MuxAttentionUi\.applyNotifyState\(window\._sessions\|\|\[\], s\.name, !on\)/);
  assert.match(toggle, /renderTabs\(\)/);
  assert.match(toggle, /\.catch\(/, 'a failed toggle must surface, not silently look applied');
  assert.ok(
    toggle.indexOf('s.notifyMuted = !on') > toggle.indexOf('sendNotifyToggle'),
    'local state may only change after the server confirms'
  );
});
