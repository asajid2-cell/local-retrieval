// Input continuity across ws reconnects.
//
// The thing under test is the reconnect buffer that lives in the TRUSTED CLIENT ORIGIN
// (relay/public/input-continuity.js). Its whole job is to make a dropped socket not eat your keystrokes,
// WITHOUT making reconnection a laundering machine for stale input and without parking plaintext or
// signing authority at the relay. Every test below is one of those two halves.
//
// No npm deps on purpose: relay/node_modules is absent in a worktree, which is why relay.test.js dies on
// "Cannot find module 'ws'". This suite runs on node core only.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const MODULE_PATH = path.join(__dirname, '..', 'public', 'input-continuity.js');
const INDEX_PATH = path.join(__dirname, '..', 'public', 'index.html');
const moduleSource = fs.readFileSync(MODULE_PATH, 'utf8');
const indexSource = fs.readFileSync(INDEX_PATH, 'utf8');
const { createInputContinuity, utf8Len } = require(MODULE_PATH);

// ---- harness -------------------------------------------------------------------------------------

function makeClock(start = 1_000_000) {
  let t = start;
  return { now: () => t, advance: ms => { t += ms; }, set: ms => { t = ms; } };
}

// Records every (channelId, seq) it is asked to sign and the clock fields it was handed.
function makeSigner() {
  const calls = [];
  return {
    calls,
    async signInput(req) {
      calls.push({ ...req });
      return { ...req, signature: 'sig(' + req.channelId + '#' + req.seq + ')' };
    },
  };
}

// `channelIds` is the script of ids channel.open will hand back, in order.
function makeTransport(channelIds, opts = {}) {
  const sent = [];
  const opens = [];
  const replays = [];
  let n = 0;
  return {
    sent, opens, replays,
    reject: opts.reject || null,     // (frame, index) => Error | null
    async openChannel() {
      const channelId = channelIds[Math.min(n, channelIds.length - 1)];
      n += 1;
      opens.push(channelId);
      return { channelId, accept: { channelId, sig: 'accept:' + channelId } };
    },
    async requestAttachReplay(ctx) {
      replays.push(ctx.channelId);
      if (opts.replayFails) throw new Error('replay unavailable');
      return { channelId: ctx.channelId, checkpoint: 42, sig: 'replay:' + ctx.channelId };
    },
    async sendSigned(frame) {
      const err = this.reject && this.reject(frame, sent.length);
      if (err) throw err;
      sent.push(frame);
    },
  };
}

function build(over = {}) {
  const clock = over.clock || makeClock();
  const signer = over.signer === undefined ? makeSigner() : over.signer;
  const transport = over.transport || makeTransport(over.channelIds || ['ch-A', 'ch-B', 'ch-C']);
  const notices = [];
  const queue = createInputContinuity({
    now: clock.now,
    ttlMs: over.ttlMs || 10_000,
    capBytes: over.capBytes || 4096,
    signer,
    transport,
    verifyAccept: over.verifyAccept || (async (accept, ctx) => !!accept && accept.sig === 'accept:' + ctx.channelId),
    verifyAttachReplay: over.verifyAttachReplay || (async (r, ctx) => !!r && r.sig === 'replay:' + ctx.channelId),
    notice: n => notices.push(n),
  });
  return { queue, clock, signer, transport, notices };
}

const drops = notices => notices.filter(n => n.kind === 'drop');

// ---- 1. fresh-channel reset ----------------------------------------------------------------------

test('reconnect opens a FRESH channel, verifies accept, verifies attach replay, and only then flushes', async () => {
  const { queue, signer, transport } = build();

  assert.equal(await queue.capture('ls -la'), 'queued', 'input on a dead socket must be queued, not lost');
  assert.equal(signer.calls.length, 0, 'nothing may be signed while there is no channel');
  assert.equal(transport.sent.length, 0);

  const res = await queue.reconnect();

  assert.equal(res.channelId, 'ch-A');
  assert.deepEqual(transport.opens, ['ch-A'], 'exactly one channel.open');
  assert.deepEqual(transport.replays, ['ch-A'], 'attach replay must be requested for the NEW channel');
  assert.equal(res.flushed, 1);
  assert.equal(queue.pendingCount(), 0);
  assert.equal(transport.sent[0].bytes, 'ls -la');
  assert.equal(queue.sequence(), 1, 'a fresh channel starts its sequence at 1');
});

test('a failed accept verification aborts BEFORE the flush and holds the buffer', async () => {
  const { queue, signer, transport } = build({ verifyAccept: async () => false });
  await queue.capture('rm -rf /tmp/x');

  await assert.rejects(() => queue.reconnect(), /channel\.accept verification failed/);
  assert.equal(transport.replays.length, 0, 'must not request attach replay on an unverified channel');
  assert.equal(signer.calls.length, 0, 'must not sign for an unverified channel');
  assert.equal(transport.sent.length, 0, 'must not write into a session we cannot vouch for');
  assert.equal(queue.pendingCount(), 1, 'buffered input is held, not silently discarded');
});

test('a failed attach-replay verification aborts BEFORE the flush and holds the buffer', async () => {
  const { queue, signer, transport } = build({ verifyAttachReplay: async () => false });
  await queue.capture('deploy');

  await assert.rejects(() => queue.reconnect(), /attach replay verification failed/);
  assert.deepEqual(transport.replays, ['ch-A'], 'replay was requested');
  assert.equal(signer.calls.length, 0, 'unverified replay => nothing signed');
  assert.equal(transport.sent.length, 0);
  assert.equal(queue.pendingCount(), 1);
});

// ---- 2. prior-channel invalidation ---------------------------------------------------------------

test('the prior channel id and its sequence are burned — never reused after a reconnect', async () => {
  const { queue, signer, transport } = build();
  await queue.reconnect();
  await queue.capture('a');
  await queue.capture('b');
  assert.equal(queue.channelId(), 'ch-A');
  assert.equal(queue.sequence(), 2);

  queue.disconnect('socket closed');
  assert.equal(queue.channelId(), null, 'no live channel after a disconnect');
  assert.equal(await queue.capture('c'), 'queued', 'post-disconnect input buffers instead of signing');

  await queue.reconnect();
  assert.equal(queue.channelId(), 'ch-B', 'a reconnect must not resume the old channel');

  const onB = signer.calls.filter(c => c.channelId === 'ch-B');
  assert.equal(onB.length, 1);
  assert.equal(onB[0].seq, 1, 'the new channel restarts its own sequence; it does not continue ch-A\'s');
  const pairs = signer.calls.map(c => c.channelId + '#' + c.seq);
  assert.equal(new Set(pairs).size, pairs.length, '(channel, seq) must never repeat');
  assert.deepEqual(transport.sent.map(f => f.bytes), ['a', 'b', 'c']);
  assert.ok(queue.retiredChannelIds().includes('ch-A'));
});

test('a channel id the host tries to hand back a second time is refused', async () => {
  const { queue, signer, transport } = build({ channelIds: ['ch-A', 'ch-A'] });
  await queue.reconnect();
  queue.disconnect();
  await queue.capture('x');

  await assert.rejects(() => queue.reconnect(), /channel id reuse refused: ch-A/);
  assert.equal(transport.replays.length, 1, 'the reused channel never got as far as attach replay');
  assert.equal(signer.calls.length, 0, 'a replayed channel id must never be signed for');
  assert.equal(queue.pendingCount(), 1);
});

// ---- 3. original-expiry preservation -------------------------------------------------------------

test('re-signed frames carry the ORIGINAL capturedAt/expiresAt — reconnecting cannot refresh stale input', async () => {
  const { queue, clock, signer } = build({ ttlMs: 10_000 });
  const capturedAt = clock.now();
  await queue.capture('git push --force');

  clock.advance(9_000);                     // still inside its original window, but 9s of wall clock later
  await queue.reconnect();

  assert.equal(signer.calls.length, 1);
  const signed = signer.calls[0];
  assert.equal(signed.capturedAt, capturedAt, 'capturedAt was rewritten to reconnect time');
  assert.equal(signed.expiresAt, capturedAt + 10_000, 'expiry was extended by the reconnect');
  assert.ok(signed.expiresAt < clock.now() + 10_000, 'the frame must NOT get a full fresh TTL');
  assert.equal(signed.channelId, 'ch-A', 're-signed for the new channel');
});

test('bytes that aged out while the socket was down are dropped, not delivered late', async () => {
  const { queue, clock, signer, transport, notices } = build({ ttlMs: 5_000 });
  await queue.capture('stale-command');
  clock.advance(5_001);
  await queue.capture('fresh-command');

  const res = await queue.reconnect();

  assert.equal(res.expired, 1);
  assert.equal(res.flushed, 1);
  assert.deepEqual(transport.sent.map(f => f.bytes), ['fresh-command'], 'the stale byte reached the PTY');
  assert.equal(signer.calls.length, 1, 'an expired frame must never even be signed');
  assert.equal(drops(notices).length, 1, 'the drop must be visible');
  assert.match(drops(notices)[0].message, /expired/i);
});

test('items that age out mid-flush are dropped mid-drain', async () => {
  const clock = makeClock();
  const transport = makeTransport(['ch-A']);
  const slow = transport.sendSigned.bind(transport);
  transport.sendSigned = async frame => { clock.advance(3_000); return slow(frame); };
  const { queue, notices } = build({ clock, transport, ttlMs: 5_000 });

  await queue.capture('one');
  await queue.capture('two');
  await queue.capture('three');
  const res = await queue.reconnect();

  assert.equal(res.flushed, 2, 'the first two are still fresh when their turn comes');
  assert.equal(res.expired, 1, 'the third aged out while the first two were draining');
  assert.deepEqual(transport.sent.map(f => f.bytes), ['one', 'two']);
  assert.ok(drops(notices).some(n => /expired/i.test(n.message)));
});

// ---- 4. ordered flush + stop on sequence/lease rejection ------------------------------------------

test('flush is in capture order with a strictly increasing sequence', async () => {
  const { queue, signer, transport } = build();
  for (const c of ['first ', 'second ', 'third ', 'fourth']) await queue.capture(c);

  const res = await queue.reconnect();

  assert.equal(res.flushed, 4);
  assert.deepEqual(transport.sent.map(f => f.bytes), ['first ', 'second ', 'third ', 'fourth']);
  assert.deepEqual(signer.calls.map(c => c.seq), [1, 2, 3, 4]);
  const times = signer.calls.map(c => c.capturedAt);
  assert.deepEqual(times, [...times].sort((a, b) => a - b), 'capture order was not preserved');
});

for (const reason of ['sequence', 'lease']) {
  test(`flush stops dead on a ${reason} rejection and keeps the rest buffered`, async () => {
    const transport = makeTransport(['ch-A', 'ch-B'], {
      reject: (frame, i) => (i === 1 ? new Error(reason + ' rejected by host') : null),
    });
    const { queue, notices } = build({ transport });
    for (const c of ['a', 'b', 'c', 'd']) await queue.capture(c);

    const res = await queue.reconnect();

    assert.equal(res.flushed, 1, 'must not skip past a rejected frame and keep writing');
    assert.equal(res.halted, true);
    assert.deepEqual(transport.sent.map(f => f.bytes), ['a']);
    assert.equal(queue.pendingCount(), 3, 'b, c and d stay buffered in capture order');
    assert.deepEqual(queue.peek().map(i => i.bytes), ['b', 'c', 'd']);
    assert.equal(queue.isLive(), false, 'a rejected sequence/lease means the channel is no longer trusted');
    assert.ok(notices.some(n => n.kind === 'halt' && n.detail.reason === reason), 'the halt must be visible');
  });
}

// ---- 5. cap eviction -----------------------------------------------------------------------------

test('the buffer is capped at 4096 bytes, evicting oldest-first with a visible notice', async () => {
  const { queue, transport, notices } = build({ capBytes: 4096, ttlMs: 10_000 });
  assert.equal(queue.capBytes, 4096, 'default cap is 4096 bytes');

  const chunk = 'x'.repeat(1000);
  for (let i = 0; i < 6; i++) await queue.capture(String(i) + chunk.slice(1));   // 6 x 1000B into a 4096B cap
  assert.ok(queue.pendingBytes() <= 4096, 'cap breached: ' + queue.pendingBytes());
  assert.equal(queue.pendingCount(), 4);

  const kept = queue.peek().map(i => i.bytes[0]);
  assert.deepEqual(kept, ['2', '3', '4', '5'], 'the OLDEST entries must be the ones evicted');
  assert.ok(drops(notices).length >= 2, 'each eviction must be visible');
  assert.ok(drops(notices).some(n => /buffer full/i.test(n.message)));

  await queue.reconnect();
  assert.deepEqual(transport.sent.map(f => f.bytes[0]), ['2', '3', '4', '5']);
});

test('the cap counts UTF-8 bytes, not JS string length', async () => {
  const { queue } = build({ capBytes: 64, ttlMs: 10_000 });
  assert.equal(utf8Len('é'), 2);
  assert.equal(utf8Len('→'), 3);
  await queue.capture('→'.repeat(20));            // 60 bytes, 20 chars
  assert.equal(queue.pendingBytes(), 60);
  await queue.capture('abcde');                   // 65 > 64 => the older entry must go
  assert.equal(queue.pendingCount(), 1);
  assert.equal(queue.pendingBytes(), 5);
});

test('a single input larger than the whole cap is dropped visibly, not truncated', async () => {
  const { queue, notices } = build({ capBytes: 4096 });
  assert.equal(await queue.capture('z'.repeat(5000)), 'dropped');
  assert.equal(queue.pendingCount(), 0);
  assert.ok(drops(notices).some(n => /too large/i.test(n.message)));
});

// ---- 6. non-input frames are never buffered ------------------------------------------------------

test('non-input frames are never buffered', async () => {
  const { queue, transport } = build();
  assert.equal(await queue.capture('{"cols":100}', { kind: 'resize' }), 'dropped');
  assert.equal(await queue.capture('{"vis":true}', { kind: 'heartbeat' }), 'dropped');
  assert.equal(queue.pendingCount(), 0, 'a resize/heartbeat replayed minutes late is noise');

  await queue.reconnect();
  assert.equal(transport.sent.length, 0, 'nothing was held to flush');
  assert.equal(await queue.capture('{"cols":100}', { kind: 'resize' }), 'sent', 'but they pass through when live');
});

test('index.html only ever buffers the input frame type', () => {
  const send = indexSource.slice(indexSource.indexOf('const send = (t,d) =>'));
  const body = send.slice(0, send.indexOf('};') + 2);
  assert.match(body, /t==='i'\s*&&\s*inputQueue/, 'the queue must be gated on the input frame type');
  assert.match(body, /r='dropped'/, 'every other frame type falls through to a drop');
  assert.equal((body.match(/inputQueue\.capture/g) || []).length, 1, 'exactly one buffered frame type');
});

// ---- 6b. the buffer is scoped to the session it was typed into -----------------------------------
// The relay forwards a signed frame to whichever session the socket is attached to, so an unscoped
// buffer would deliver the bytes you typed into chat A into chat B the moment you switched.

test('input typed into one session is held, not flushed, when another session attaches', async () => {
  const { queue, transport, notices } = build({ channelIds: ['ch-B', 'ch-A'] });
  assert.equal(await queue.capture('run the migration', { scope: 'chat-a' }), 'queued');
  assert.equal(queue.pendingCount(), 1);

  const r = await queue.reconnect('chat-b');
  assert.equal(r.flushed, 0, 'chat-a input must not be delivered into chat-b');
  assert.equal(r.held, 1, 'the held bytes must be reported');
  assert.equal(transport.sent.length, 0, 'no frame may leave for the wrong session');
  assert.equal(queue.pendingCount(), 1, 'the bytes are still held, not discarded');
  assert.ok(notices.some(n => /another session/i.test(n.message)), 'the hold must be visible in the log');
});

test('returning to the original session delivers exactly what was typed there', async () => {
  const { queue, transport } = build({ channelIds: ['ch-B', 'ch-A'] });
  await queue.capture('alpha', { scope: 'chat-a' });
  await queue.reconnect('chat-b');
  assert.equal(transport.sent.length, 0);
  queue.disconnect('switched back');
  const r = await queue.reconnect('chat-a');
  assert.equal(r.flushed, 1);
  assert.deepEqual(transport.sent.map(f => f.bytes), ['alpha']);
  assert.equal(queue.pendingCount(), 0);
});

test('a head item for another session stops the drain instead of being overtaken', async () => {
  const { queue, transport } = build({ channelIds: ['ch-A'] });
  await queue.capture('for-a', { scope: 'a' });
  await queue.capture('for-b', { scope: 'b' });
  const r = await queue.reconnect('b');
  assert.equal(r.flushed, 0, 'later input must never overtake an earlier item for another session');
  assert.equal(r.held, 1);
  assert.equal(transport.sent.length, 0);
});

test('unscoped input still flushes to an unscoped attach (the single-session case)', async () => {
  const { queue, transport } = build();
  await queue.capture('plain');
  await queue.reconnect();
  assert.deepEqual(transport.sent.map(f => f.bytes), ['plain']);
});

test('a rename retargets held input instead of stranding it', async () => {
  const { queue, transport } = build({ channelIds: ['ch-B'] });
  await queue.capture('half-typed', { scope: 'old-name' });
  assert.equal(queue.rescope('old-name', 'new-name'), 1, 'the held item must follow the rename');
  assert.equal(queue.rescope('old-name', 'new-name'), 0, 'nothing left under the old name');
  const r = await queue.reconnect('new-name');
  assert.equal(r.flushed, 1, 'the text must reach the renamed chat');
  assert.deepEqual(transport.sent.map(f => f.bytes), ['half-typed']);
});

test('a held item expires on its own clock like any other', async () => {
  const { queue, clock, notices } = build({ ttlMs: 10_000, channelIds: ['ch-B', 'ch-A'] });
  await queue.capture('stale', { scope: 'chat-a' });
  await queue.reconnect('chat-b');                    // held, not delivered
  clock.advance(10_001);
  queue.disconnect('later');
  const r = await queue.reconnect('chat-a');
  assert.equal(r.flushed, 0);
  assert.equal(r.expired, 1, 'held bytes must not become deliverable just because they waited');
  assert.equal(queue.pendingCount(), 0);
  assert.ok(drops(notices).some(n => /expired/i.test(n.message)));
});

// ---- 7. no unsigned fallback ---------------------------------------------------------------------

test('with no signing authority, input is dropped visibly — never sent unsigned', async () => {
  const { queue, transport, notices } = build({ signer: null });
  assert.equal(await queue.capture('sudo reboot'), 'dropped');
  assert.equal(queue.pendingCount(), 0);
  assert.equal(transport.sent.length, 0);
  assert.ok(drops(notices).some(n => /signing authority/i.test(n.message)));
});

test('buffered bytes are dropped rather than flushed unsigned if authority disappears', async () => {
  const clock = makeClock();
  const transport = makeTransport(['ch-A']);
  const withSigner = build({ clock, transport });
  await withSigner.queue.capture('held');
  assert.equal(withSigner.queue.pendingCount(), 1);

  // Same buffer contents, no signer: the flush must discard, not deliver.
  const { queue, notices } = build({ clock, transport, signer: null });
  await queue.capture('held');                  // dropped at capture; proves there is no second path in
  assert.equal(queue.pendingCount(), 0);
  assert.equal(transport.sent.length, 0);
  assert.ok(drops(notices).length >= 1);
});

test('a signer that returns a frame without a signature is treated as a failure', async () => {
  const signer = { calls: [], async signInput(req) { this.calls.push(req); return { ...req }; } };
  const { queue, transport } = build({ signer });
  await queue.capture('boom');
  const res = await queue.reconnect();
  assert.equal(res.flushed, 0);
  assert.equal(transport.sent.length, 0, 'an unsigned frame must never reach the transport');
  assert.equal(queue.pendingCount(), 1);
});

test('the module exposes no raw/unsigned send path', () => {
  const calls = moduleSource.match(/transport\.\w+/g) || [];
  assert.deepEqual(
    [...new Set(calls)].sort(),
    ['transport.openChannel', 'transport.requestAttachReplay', 'transport.sendSigned'],
    'the only way out of this module is sendSigned',
  );
});

// ---- 8. no relay persistence ---------------------------------------------------------------------

test('the module never touches any storage or network API', () => {
  const banned = /localStorage|sessionStorage|indexedDB|document\.cookie|XMLHttpRequest|navigator\.|WebSocket|\bfetch\s*\(/;
  const code = moduleSource.split('\n').filter(l => !/^\s*(\/\/|\*|\/\*)/.test(l)).join('\n');
  const hit = code.match(banned);
  assert.equal(hit, null, 'plaintext must not be persistable: found ' + hit);
});

test('a full buffer+reconnect+flush cycle runs with every storage global booby-trapped', async () => {
  const trap = new Proxy({}, {
    get(_t, prop) { throw new Error('relay-visible persistence attempted: ' + String(prop)); },
    set(_t, prop) { throw new Error('relay-visible persistence attempted: ' + String(prop)); },
  });
  const sandbox = {
    localStorage: trap, sessionStorage: trap, indexedDB: trap, caches: trap,
    document: trap, fetch: () => { throw new Error('network attempted'); },
    XMLHttpRequest: function () { throw new Error('network attempted'); },
    module: { exports: {} },
  };
  sandbox.globalThis = sandbox;
  vm.createContext(sandbox);
  vm.runInContext(moduleSource, sandbox, { filename: 'input-continuity.js' });

  const clock = makeClock();
  const signer = makeSigner();
  const transport = makeTransport(['ch-A']);
  const queue = sandbox.createInputContinuity({
    now: clock.now, ttlMs: 10_000, capBytes: 4096, signer, transport,
    verifyAccept: async () => true, verifyAttachReplay: async () => true,
    notice: () => {},
  });
  await queue.capture('typed while offline');
  clock.advance(500);
  const res = await queue.reconnect();

  assert.equal(res.flushed, 1);
  assert.deepEqual(transport.sent.map(f => f.bytes), ['typed while offline']);
  assert.equal(queue.pendingBytes(), 0, 'the buffer drains to nothing; there is nowhere else it lives');
});

test('relay-side code holds no reconnect buffer', () => {
  const server = fs.readFileSync(path.join(__dirname, '..', 'server.js'), 'utf8');
  for (const marker of ['input-continuity', 'inputQueue', 'capturedAt']) {
    assert.equal(server.includes(marker), false, 'the relay must not learn about buffered input: ' + marker);
  }
});

// ---- compose closes only after sent/queued -------------------------------------------------------

// The compose body is driven for real. term.paste routes the text back through send exactly as xterm's
// onData does, and send reports the REAL disposition — including a promise from the buffer, which decides
// asynchronously whether it can hold the bytes.
function composeHarness() {
  const start = indexSource.indexOf('async function submitCompose(');
  assert.notEqual(start, -1, 'missing submitCompose');
  let depth = 0, end = -1;
  for (let i = indexSource.indexOf('{', start); i < indexSource.length; i++) {
    if (indexSource[i] === '{') depth++;
    else if (indexSource[i] === '}') { depth--; if (depth === 0) { end = i + 1; break; } }
  }
  const body = indexSource.slice(start, end);

  return function make(over = {}) {
    const ctx = {
      ws: over.ws === undefined ? null : over.ws,
      inputQueue: over.inputQueue === undefined ? null : over.inputQueue,
      pasted: [], closed: 0, flashed: [], focused: 0, raw: [],
      inputSendProbe: { sink: null },
    };
    ctx.$ = sel => sel === '#composetext' ? { value: over.text || 'run the migration' } : { close(){ ctx.closed++; } };
    ctx.term = { paste(t){ ctx.pasted.push(t); ctx.send('i', t); }, focus(){ ctx.focused++; } };
    ctx.flash = m => ctx.flashed.push(m);
    ctx.send = (t, d) => {
      let r;
      if (ctx.ws && ctx.ws.readyState === 1) r = 'sent';
      else if (t === 'i' && ctx.inputQueue) r = ctx.inputQueue.capture(d);
      else r = 'dropped';
      if (t === 'i') ctx.raw.push(d);
      if (ctx.inputSendProbe.sink) ctx.inputSendProbe.sink.push(r);
      return r;
    };
    vm.createContext(ctx);
    vm.runInContext(body + '\nthis.submitCompose = submitCompose;', ctx);
    return ctx;
  };
}

test('compose closes only after the text is sent or queued', async () => {
  const make = composeHarness();

  // Sockets down and no signer: nothing can carry the text, so it must not leave the dialog.
  const a = make();
  assert.equal(await a.submitCompose(false), 'dropped');
  assert.equal(a.closed, 0, 'compose closed on a dead socket and ate the message');
  assert.equal(a.pasted.length, 0, 'nothing may be pasted when it cannot be delivered');
  assert.equal(a.flashed.length, 1, 'the failure must be visible');

  // Signer present but the buffer REFUSES the bytes (over the 4 KB cap, or authority vanished). The old
  // code predicted 'queued' from socket state and closed the dialog on a message that never arrived.
  const b = make({ inputQueue: { capture: () => 'dropped' } });
  assert.equal(await b.submitCompose(false), 'dropped', 'a refused buffer is a drop, not a queue');
  assert.equal(b.closed, 0, 'a refused buffer must leave the dialog open with the text');
  assert.equal(b.flashed.length, 1, 'the refusal must be visible');

  // The buffer accepts: the text is held and the dialog closes.
  const c = make({ inputQueue: { capture: () => 'queued' } });
  assert.equal(await c.submitCompose(false), 'queued');
  assert.equal(c.closed, 1, 'compose must close once the text is queued');
  assert.deepEqual(c.pasted, ['run the migration']);

  // The buffer's verdict arrives as a promise; compose must await it rather than assume.
  const d = make({ inputQueue: { capture: () => Promise.resolve('queued') } });
  assert.equal(await d.submitCompose(false), 'queued');
  assert.equal(d.closed, 1);

  const e = make({ ws: { readyState: 1 } });
  assert.equal(await e.submitCompose(false), 'sent');
  assert.equal(e.closed, 1);
});

test('Send + Enter fires the Enter only when the text actually went out', async () => {
  const make = composeHarness();

  const live = make({ ws: { readyState: 1 } });
  assert.equal(await live.submitCompose(true), 'sent');
  assert.deepEqual(live.raw.slice(-1), ['\r'], 'the Enter must follow a delivered text');
  assert.deepEqual(live.pasted, ['run the migration']);

  // The text is refused. Sending the trailing CR anyway would submit whatever was already on the prompt
  // line — a stray Enter is a real side effect, not a harmless no-op.
  const refused = make({ inputQueue: { capture: () => 'dropped' } });
  assert.equal(await refused.submitCompose(true), 'dropped');
  assert.equal(refused.raw.includes('\r'), false, 'a dropped text must not fire the Enter');
  assert.equal(refused.closed, 0);
});

test('compose observes the send disposition instead of predicting it', () => {
  const body = indexSource.slice(indexSource.indexOf('async function submitCompose('));
  assert.match(body, /inputSendProbe\.sink=seen/, 'compose must collect the real dispositions');
  assert.doesNotMatch(body, /readyState===1\) ?\? ?'sent'/, 'compose must not predict from socket state');
});

test('index.html scopes buffered input to the session it was typed into', () => {
  assert.match(indexSource, /inputQueue\.capture\(d, \{ kind:'input', scope:current \}\)/,
    'buffered input must carry the session it was typed into');
  assert.match(indexSource, /inputQueue\.reconnect\(name\)/,
    'the flush must know which session just attached');
});

test('index.html burns the channel on close and opens a fresh one on open', () => {
  assert.match(indexSource, /inputQueue\.disconnect\(/, 'socket close must retire the signing channel');
  assert.match(indexSource, /inputQueue\.reconnect\(\)/, 'socket open must open+verify a fresh channel');
  // Assert CONTAINMENT in the handler, not the relative position of two whole-file indexOf hits. The
  // old form compared the FIRST occurrence of 'inputQueue.reconnect()' against 'sock.onopen', and a
  // comment above the handler mentions inputQueue.reconnect() by name — so the first hit was the
  // comment, which sits earlier, and the check failed against correct code. Containment is also the
  // stronger claim: it fails if the call moves out of onopen, which the index comparison would not.
  const onopenIdx = indexSource.indexOf('sock.onopen');
  assert.notEqual(onopenIdx, -1, 'missing the viewer socket onopen handler');
  const onopen = indexSource.slice(onopenIdx, indexSource.indexOf('sock.onmessage', onopenIdx));
  assert.match(onopen, /inputQueue\.reconnect\(name\)/,
    'the fresh channel must be opened from sock.onopen, scoped to the session that just attached');
});

test('the queue arms only when this origin actually holds a signing key', () => {
  const start = indexSource.indexOf('const inputQueue = (function()');
  assert.notEqual(start, -1, 'missing inputQueue construction');
  const body = indexSource.slice(start, indexSource.indexOf('})();', start));
  assert.match(body, /typeof auth\.signInput !== 'function'\) return null/, 'no signer => no queue, no fallback');
});
