'use strict';
// Transport-level proof for relay/notify.js. No relay harness and no fake muxd host: the notifier
// only needs something that speaks HTTP, so every test points it at a local capture server.
const test = require('node:test');
const assert = require('node:assert');
const http = require('node:http');

const { createNotifier, createJournalNotifier, NOTIFY_TIMEOUT_MS } = require('../notify');

// A capture target standing in for the ntfy topic. `respond` shapes the reply so status-code and
// hang behaviour can be driven per test.
function captureServer(respond) {
  const received = [];
  const sockets = new Set();
  const server = http.createServer((req, res) => {
    let body = '';
    req.setEncoding('utf8');
    req.on('data', (chunk) => { body += chunk; });
    req.on('end', () => {
      const entry = { method: req.method, url: req.url, headers: req.headers, body };
      received.push(entry);
      if (respond === 'hang') return;               // accept, never answer
      const status = typeof respond === 'number' ? respond : 200;
      res.writeHead(status, { 'Content-Type': 'text/plain' });
      res.end('ok');
    });
  });
  server.on('connection', (s) => { sockets.add(s); s.on('close', () => sockets.delete(s)); });
  return new Promise((resolve) => {
    server.listen(0, '127.0.0.1', () => {
      resolve({
        received,
        url: 'http://127.0.0.1:' + server.address().port + '/mux-test',
        cutSockets() { for (const s of sockets) s.destroy(); },
        async close() { for (const s of sockets) s.destroy(); await new Promise((r) => server.close(r)); },
      });
    });
  });
}

function withoutEnvUrl(fn) {
  const saved = process.env.MUX_NTFY_URL;
  delete process.env.MUX_NTFY_URL;
  try { return fn(); } finally {
    if (saved === undefined) delete process.env.MUX_NTFY_URL; else process.env.MUX_NTFY_URL = saved;
  }
}

test('no URL configured makes push a no-op that reports disabled', async () => {
  const result = await withoutEnvUrl(async () => {
    const notifier = createNotifier();
    assert.strictEqual(notifier.enabled, false);
    return notifier.push({ title: 'nobody is listening', body: 'x' });
  });
  assert.deepStrictEqual(result, { ok: false, disabled: true });
});

test('blank or whitespace-only URL is treated as unconfigured', async () => {
  for (const url of ['', '   ', null, undefined]) {
    const notifier = await withoutEnvUrl(async () => createNotifier({ url }));
    assert.strictEqual(notifier.enabled, false, 'url ' + JSON.stringify(url) + ' should disable');
    assert.deepStrictEqual(await notifier.push({ body: 'x' }), { ok: false, disabled: true });
  }
});

test('MUX_NTFY_URL is the default topic and an explicit url overrides it', async (t) => {
  const fromEnv = await captureServer();
  const explicit = await captureServer();
  t.after(async () => { await fromEnv.close(); await explicit.close(); });

  const saved = process.env.MUX_NTFY_URL;
  process.env.MUX_NTFY_URL = fromEnv.url;
  try {
    assert.deepStrictEqual(await createNotifier().push({ body: 'env' }), { ok: true, status: 200 });
    assert.deepStrictEqual(await createNotifier({ url: explicit.url }).push({ body: 'explicit' }), { ok: true, status: 200 });
  } finally {
    if (saved === undefined) delete process.env.MUX_NTFY_URL; else process.env.MUX_NTFY_URL = saved;
  }

  assert.strictEqual(fromEnv.received.length, 1);
  assert.strictEqual(fromEnv.received[0].body, 'env');
  assert.strictEqual(explicit.received.length, 1);
  assert.strictEqual(explicit.received[0].body, 'explicit');
});

test('push POSTs the body and maps ntfy metadata onto headers', async (t) => {
  const target = await captureServer();
  t.after(() => target.close());

  const result = await createNotifier({ url: target.url }).push({
    title: 'claude needs you',
    body: 'session dev-1 is waiting for input',
    tags: ['warning', 'robot'],
    priority: 4,
    click: 'https://mux.example/#session=dev-1',
  });

  assert.deepStrictEqual(result, { ok: true, status: 200 });
  assert.strictEqual(target.received.length, 1);
  const sent = target.received[0];
  assert.strictEqual(sent.method, 'POST');
  assert.strictEqual(sent.url, '/mux-test');
  assert.strictEqual(sent.body, 'session dev-1 is waiting for input');
  assert.strictEqual(sent.headers.title, 'claude needs you');
  assert.strictEqual(sent.headers.tags, 'warning,robot');
  assert.strictEqual(sent.headers.priority, '4');
  assert.strictEqual(sent.headers.click, 'https://mux.example/#session=dev-1');
});

test('a single tag string works and omitted fields send no header', async (t) => {
  const target = await captureServer();
  t.after(() => target.close());

  assert.deepStrictEqual(await createNotifier({ url: target.url }).push({ body: 'x', tags: 'rotating_light' }), { ok: true, status: 200 });
  const sent = target.received[0];
  assert.strictEqual(sent.headers.tags, 'rotating_light');
  assert.strictEqual(sent.headers.title, undefined);
  assert.strictEqual(sent.headers.priority, undefined);
  assert.strictEqual(sent.headers.click, undefined);
});

test('header-hostile titles are sanitized instead of throwing', async (t) => {
  const target = await captureServer();
  t.after(() => target.close());

  // Emoji and CRLF in a session name would otherwise trip HTTP header validation.
  const result = await createNotifier({ url: target.url }).push({
    title: 'agent \u{1F916} done\r\nX-Injected: evil',
    body: 'utf-8 body stays intact: \u{1F680}',
  });

  assert.deepStrictEqual(result, { ok: true, status: 200 });
  const sent = target.received[0];
  assert.match(sent.headers.title, /^agent\s+done X-Injected: evil$/);
  assert.strictEqual(sent.headers['x-injected'], undefined, 'CRLF must not smuggle a header');
  assert.strictEqual(sent.body, 'utf-8 body stays intact: \u{1F680}');
});

test('a non-2xx ntfy reply reports the status without throwing', async (t) => {
  const target = await captureServer(503);
  t.after(() => target.close());

  const result = await createNotifier({ url: target.url }).push({ body: 'x' });
  assert.strictEqual(result.ok, false);
  assert.strictEqual(result.status, 503);
  assert.match(result.error, /503/);
});

test('an unreachable topic resolves to an error result, never a rejection', async () => {
  const dead = await captureServer();
  const url = dead.url;
  await dead.close();

  const result = await createNotifier({ url }).push({ body: 'x' });
  assert.strictEqual(result.ok, false);
  assert.strictEqual(typeof result.error, 'string');
  assert.ok(result.error.length > 0);
});

test('a hung topic aborts on the configured deadline', async (t) => {
  const target = await captureServer('hang');
  t.after(() => target.close());

  const started = Date.now();
  const result = await createNotifier({ url: target.url, timeoutMs: 250 }).push({ body: 'x' });
  assert.strictEqual(result.ok, false);
  assert.match(result.error, /timed out after 250ms/);
  assert.ok(Date.now() - started < 5000, 'must not wait for the default deadline');
});

test('the default deadline is 5s and is not cut short', async (t) => {
  const target = await captureServer('hang');
  t.after(() => target.close());
  assert.strictEqual(NOTIFY_TIMEOUT_MS, 5000);

  const pending = createNotifier({ url: target.url }).push({ body: 'x' });
  const raced = await Promise.race([
    pending.then(() => 'settled'),
    new Promise((r) => setTimeout(() => r('still-waiting'), 400)),
  ]);
  assert.strictEqual(raced, 'still-waiting', 'default timeout must be well above 400ms');

  target.cutSockets();          // settle the pending push instead of leaking it to force-exit
  const result = await pending;
  assert.strictEqual(result.ok, false);
});

test('an injected fetchImpl replaces the network and receives the full request', async () => {
  const calls = [];
  const notifier = createNotifier({
    url: 'https://ntfy.example/mux',
    fetchImpl: async (url, init) => { calls.push({ url, init }); return { ok: true, status: 200 }; },
  });

  assert.deepStrictEqual(await notifier.push({ title: 'hi', body: 'there', tags: ['bell'] }), { ok: true, status: 200 });
  assert.strictEqual(calls.length, 1);
  assert.strictEqual(calls[0].url, 'https://ntfy.example/mux');
  assert.strictEqual(calls[0].init.method, 'POST');
  assert.strictEqual(calls[0].init.body, 'there');
  assert.strictEqual(calls[0].init.headers.Title, 'hi');
  assert.strictEqual(calls[0].init.headers.Tags, 'bell');
  assert.ok(calls[0].init.signal, 'a timeout signal must be supplied to the transport');
});

test('a throwing or non-ok fetchImpl still resolves to an error result', async () => {
  const boom = createNotifier({ url: 'https://ntfy.example/mux', fetchImpl: async () => { throw new Error('socket exploded'); } });
  assert.deepStrictEqual(await boom.push({ body: 'x' }), { ok: false, error: 'socket exploded' });

  const rejected = createNotifier({ url: 'https://ntfy.example/mux', fetchImpl: async () => ({ ok: false, status: 429 }) });
  const result = await rejected.push({ body: 'x' });
  assert.strictEqual(result.ok, false);
  assert.strictEqual(result.status, 429);
});

test('push tolerates a missing or empty message', async (t) => {
  const target = await captureServer();
  t.after(() => target.close());
  const notifier = createNotifier({ url: target.url });

  assert.deepStrictEqual(await notifier.push(), { ok: true, status: 200 });
  assert.deepStrictEqual(await notifier.push(null), { ok: true, status: 200 });
  assert.deepStrictEqual(await notifier.push({}), { ok: true, status: 200 });
  assert.strictEqual(target.received.length, 3);
  assert.strictEqual(target.received[0].body, '');
});

// ── the journal sink ─────────────────────────────────────────────────────────────────────────────
// createJournalNotifier exists so an alert lane with no ntfy topic is still OBSERVABLE. These tests
// pin the two properties that make that true: it reports itself as not-a-push, and it prints the whole
// message somewhere readable — because "nothing was printed" is the failure mode being eliminated.

test('the journal notifier never claims to be a push and writes the message where it can be read', async () => {
  const lines = [];
  const notifier = createJournalNotifier({ log: (line) => lines.push(line) });

  assert.strictEqual(notifier.enabled, false, 'a journal sink must not read as a configured push target');
  assert.strictEqual(notifier.url, '');

  const result = await notifier.push({
    title: 'Relay degraded',
    body: 'Health has been degraded for 3m.\n- projects bridge down\n- PC unreachable (192.168.1.162)',
    tags: ['warning'],
    priority: 'high',
    click: 'https://mux.example/',
  });

  assert.deepStrictEqual(result, { ok: true, journal: true });
  assert.strictEqual(lines.length, 1, 'one push is one journal entry');
  const entry = lines[0];
  assert.match(entry, /\[ops-alert\]\[journal\]/, 'the entry is greppable as journal-only');
  assert.match(entry, /high/);
  assert.match(entry, /Relay degraded/);
  for (const line of ['- projects bridge down', '- PC unreachable (192.168.1.162)', 'https://mux.example/']) {
    assert.ok(entry.includes(line), `the operator needs "${line}" in the entry; got: ${entry}`);
  }
});

test('a journal entry stays one entry even when a title carries newlines', async () => {
  const lines = [];
  const notifier = createJournalNotifier({ log: (line) => lines.push(line) });
  const result = await notifier.push({ title: 'bad\nTITLE: forged\nPriority: urgent', body: 'x' });

  assert.deepStrictEqual(result, { ok: true, journal: true });
  assert.strictEqual(lines.length, 1);
  assert.strictEqual(lines[0].split('\n').length, 2, 'a title may not inject extra lines into the journal');
  assert.ok(!/^Priority: urgent/m.test(lines[0]), 'a title may not forge a second header line');
});

test('a journal notifier with a broken logger still honours the never-throws contract', async () => {
  const notifier = createJournalNotifier({ log: () => { throw new Error('journal write failed'); } });
  const result = await notifier.push({ title: 'x', body: 'y' });
  assert.deepStrictEqual(result, { ok: true, journal: true });
});
