'use strict';
// Two-viewer proof that the write lease is muxd's and only muxd's.
//
// Everything under test is the real code: relay/lease-conduit.js is the actual conduit the server
// requires, and relay/public/input-lease.js is the actual browser module. Only muxd and the sockets
// are faked, and the fake muxd signs with a real Ed25519 key — so "verified" here means verified.
//
// Zero dependencies on purpose: this worktree has no relay/node_modules, and a lease test that
// cannot run is not a verifier.

const test = require('node:test');
const assert = require('node:assert');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const { createLeaseConduit, RELAY_MINT_KEYS } = require('../lease-conduit');
const { createInputLease } = require('../public/input-lease');

const SESSION = 'work';

// Stable key order so a signature covers the same bytes on both sides.
function canon(value) {
  return JSON.stringify(value, (k, v) =>
    v && typeof v === 'object' && !Array.isArray(v)
      ? Object.fromEntries(Object.keys(v).sort().map(kk => [kk, v[kk]]))
      : v);
}

function newPrincipal(id) {
  const { publicKey, privateKey } = crypto.generateKeyPairSync('ed25519');
  return {
    principalId: id,
    keyId: id + '-k1',
    publicKey,
    sign(body) {
      const sig = crypto.sign(null, Buffer.from(canon(body)), privateKey).toString('base64');
      return JSON.stringify({ ...body, auth: { principalId: id, keyId: id + '-k1', signature: sig } });
    },
  };
}

// ---------------------------------------------------------------------------- fake host (the PTY)

function createFakeHost() {
  const writes = [];
  return {
    writes,
    write(principalId, data, epoch) { writes.push({ principalId, data, epoch }); },
    // No interleaving means: the writer only ever changes across a lease epoch bump. Two principals
    // alternating inside one epoch is exactly the corruption the lease exists to prevent.
    interleavings() {
      const bad = [];
      for (let i = 1; i < writes.length; i++) {
        const prev = writes[i - 1], cur = writes[i];
        if (prev.principalId !== cur.principalId && cur.epoch <= prev.epoch) bad.push([prev, cur]);
      }
      return bad;
    },
  };
}

// ---------------------------------------------------------------------------- fake muxd (authority)

function createFakeMuxd(host) {
  const muxdKeys = crypto.generateKeyPairSync('ed25519');
  const principals = new Map();                       // principalId -> publicKey (authorized set)
  const lease = { epoch: 0, principalId: '', channelId: '' };
  const rejects = [];
  let emit = () => {};

  function verifyClient(raw) {
    let frame;
    try { frame = typeof raw === 'string' ? JSON.parse(raw) : { ...raw }; } catch { return null; }
    if (!frame || !frame.auth) return null;
    delete frame.t;
    delete frame.s;
    const pub = principals.get(frame.auth.principalId);
    if (!pub) return null;                            // unauthorized principal: not a lease question
    const { auth, ...body } = frame;
    let ok = false;
    try {
      ok = crypto.verify(null, Buffer.from(canon(body)), pub, Buffer.from(auth.signature, 'base64'));
    } catch { ok = false; }
    return ok ? { body, auth } : null;
  }

  function notice(kind, reason) {
    const body = {
      kind,
      session: SESSION,
      leaseEpoch: lease.epoch,
      holderPrincipalId: lease.principalId,
      holderChannelId: lease.channelId,
      reason,
    };
    const sig = crypto.sign(null, Buffer.from(canon(body)), muxdKeys.privateKey).toString('base64');
    return JSON.stringify({ ...body, muxdSig: sig });
  }

  return {
    publicKey: muxdKeys.publicKey,
    rejects,
    lease,
    register(p) { principals.set(p.principalId, p.publicKey); },
    onEmit(fn) { emit = fn; },

    // The single entry point the relay can reach. Frames arrive exactly as the client signed them.
    handle(msg) {
      if (msg.t === 'lease') {
        const v = verifyClient(msg.f);
        if (!v) { rejects.push({ why: 'bad-signature', t: msg.t }); return; }
        if (v.body.kind === 'lease.steal') {
          // Fence on the epoch the stealer claims to supersede: a steal built from a stale banner
          // loses rather than clobbering a holder it never saw.
          if (Number(v.body.leaseEpoch) !== lease.epoch) {
            rejects.push({ why: 'stale-steal', claimed: v.body.leaseEpoch, actual: lease.epoch });
            emit(notice('lease.held', 'steal-rejected-stale-epoch'));
            return;
          }
          lease.epoch += 1;
          lease.principalId = v.auth.principalId;
          lease.channelId = String(v.body.channelId || '');
          emit(notice('lease.granted', 'stolen'));
          return;
        }
        if (v.body.kind === 'lease.release' && v.auth.principalId === lease.principalId) {
          lease.epoch += 1; lease.principalId = ''; lease.channelId = '';
          emit(notice('lease.released', 'released'));
        }
        return;
      }

      if (msg.t === 'i') {
        const v = verifyClient(msg);
        if (!v) { rejects.push({ why: 'bad-signature', t: msg.t }); return; }
        // The epoch the client signed is the epoch it *observed*. On acquisition that is the
        // free-lease epoch, which the grant then supersedes — so the comparison happens first.
        const claimed = Number(v.body.leaseEpoch);
        if (!lease.principalId) {                     // free lease: first authorized writer takes it
          if (claimed !== lease.epoch) {
            rejects.push({ why: 'old-epoch', claimed, actual: lease.epoch });
            emit(notice('lease.held', 'input-rejected-old-epoch'));
            return;
          }
          lease.epoch += 1;
          lease.principalId = v.auth.principalId;
          lease.channelId = String(v.body.channelId || '');
          emit(notice('lease.granted', 'acquired'));
          host.write(v.auth.principalId, String(v.body.data || ''), lease.epoch);
          return;
        }
        if (v.auth.principalId !== lease.principalId) {
          rejects.push({ why: 'not-holder', who: v.auth.principalId, holder: lease.principalId });
          emit(notice('lease.held', 'input-rejected-not-holder'));
          return;
        }
        if (claimed !== lease.epoch) {
          rejects.push({ why: 'old-epoch', claimed, actual: lease.epoch });
          emit(notice('lease.held', 'input-rejected-old-epoch'));
          return;
        }
        host.write(v.auth.principalId, String(v.body.data || ''), lease.epoch);
      }
    },
  };
}

// ---------------------------------------------------------------------------- fake relay + viewers

function createFakeRelay(muxd) {
  const conduit = createLeaseConduit();               // THE REAL CONDUIT
  const viewers = new Map();
  muxd.onEmit(raw => {
    const cached = conduit.cacheLeaseNotice(SESSION, raw);
    if (cached) for (const v of viewers.values()) v.deliver('L' + cached);
  });
  return {
    conduit,
    attach(id, viewer) {
      viewers.set(id, viewer);
      const pending = conduit.cachedNotice(SESSION);  // late viewer gets muxd's last word, verbatim
      if (pending) viewer.deliver('L' + pending);
    },
    fromViewer(text) {
      const raw = text.slice(1);
      const out = text[0] === 'L'
        ? conduit.forwardSignedLeaseOp(SESSION, raw)
        : (conduit.forwardSignedInput(SESSION, raw) || conduit.forwardDeliberateSend(SESSION, raw));
      if (out) muxd.handle(out);
      return out;
    },
    // A hostile relay speaking on its own authority.
    forge(text) { for (const v of viewers.values()) v.deliver('L' + text); },
  };
}

function createViewer(id, principal, relay, muxdPublicKey) {
  const channelId = 'ch-' + id;
  const seen = [];
  const lease = createInputLease({
    session: SESSION,
    selfPrincipalId: principal.principalId,
    selfChannelId: channelId,
    verifyNotice(raw) {
      let frame;
      try { frame = JSON.parse(raw); } catch { return { trusted: false }; }
      if (!frame || typeof frame.muxdSig !== 'string') return { trusted: false };
      const { muxdSig, ...body } = frame;
      let ok = false;
      try {
        ok = crypto.verify(null, Buffer.from(canon(body)), muxdPublicKey, Buffer.from(muxdSig, 'base64'));
      } catch { ok = false; }
      return ok ? { trusted: true, body } : { trusted: false };
    },
    signSteal(intent) { return principal.sign(intent); },
    send(text) { relay.fromViewer(text); },
    onState(s) { seen.push(s); },
  });

  return {
    id, channelId, principal, lease, seen,
    deliver(text) { if (text[0] === 'L') lease.ingestNotice(text.slice(1)); },
    // Exactly what index.html does on a keystroke: ask the guard, then sign, then send.
    type(data, opts) {
      const o = opts || {};
      if (!o.force && !lease.guardOutgoingInput().allowed) return { sent: false, reason: 'lease-held' };
      const epoch = o.epoch != null ? o.epoch : lease.state().leaseEpoch;
      const raw = principal.sign({
        kind: 'input.raw', session: SESSION, data, channelId, leaseEpoch: epoch,
      });
      relay.fromViewer('I' + raw);
      return { sent: true, raw };
    },
    // A frame signed now, delivered later. This is the lag case, not a synthetic one.
    composeInput(data, epoch) {
      return principal.sign({ kind: 'input.raw', session: SESSION, data, channelId, leaseEpoch: epoch });
    },
  };
}

function bootstrap() {
  const host = createFakeHost();
  const muxd = createFakeMuxd(host);
  const relay = createFakeRelay(muxd);
  const pa = newPrincipal('alice'), pb = newPrincipal('bob');
  muxd.register(pa); muxd.register(pb);
  const a = createViewer('A', pa, relay, muxd.publicKey);
  const b = createViewer('B', pb, relay, muxd.publicKey);
  relay.attach('A', a);
  relay.attach('B', b);
  return { host, muxd, relay, a, b };
}

// ---------------------------------------------------------------------------- the two-viewer story

test('A acquires, B sees a signed read-only banner, B signed-steals, A is fenced out', () => {
  const { host, muxd, relay, a, b } = bootstrap();

  // --- A acquires by typing first.
  assert.equal(a.type('ls\r').sent, true);
  assert.equal(muxd.lease.principalId, 'alice');
  assert.equal(muxd.lease.epoch, 1);
  assert.equal(a.lease.state().heldBySelf, true);
  assert.equal(a.lease.canType(), true);

  // --- B sees the lease held, and believes it only because muxd signed it.
  const bs = b.lease.state();
  assert.equal(bs.verified, true);
  assert.equal(bs.heldByOther, true);
  assert.equal(bs.readOnly, true);
  assert.equal(bs.holderPrincipalId, 'alice');
  assert.equal(b.lease.canType(), false);

  const banner = b.lease.banner();
  assert.equal(banner.visible, true);
  assert.match(banner.text, /Read-only/);
  assert.equal(banner.action, 'Take over');

  // B's keystrokes stop at its own guard, and would be refused by muxd regardless.
  assert.deepEqual(b.type('rm -rf /\r'), { sent: false, reason: 'lease-held' });
  assert.equal(host.writes.length, 1);

  // --- A composes input NOW, at epoch 1. It will arrive after the steal.
  const delayed = a.composeInput('echo late\r', 1);

  // --- B takes over with one tap. The relay never mints this; B's key does.
  const takeover = b.lease.requestTakeover();
  assert.equal(takeover.sent, true);
  assert.equal(muxd.lease.principalId, 'bob');
  assert.equal(muxd.lease.epoch, 2);
  assert.equal(b.lease.state().heldBySelf, true);
  assert.equal(b.lease.canType(), true);

  // A flipped to read-only from muxd's signed notice, not from anything the relay decided.
  const as = a.lease.state();
  assert.equal(as.readOnly, true);
  assert.equal(as.holderPrincipalId, 'bob');
  assert.equal(as.leaseEpoch, 2);
  assert.equal(a.lease.banner().visible, true);

  // --- The delayed epoch-1 frame lands. muxd rejects it; the PTY never sees it.
  relay.fromViewer('I' + delayed);
  const fenced = muxd.rejects.filter(r => r.why === 'old-epoch' || r.why === 'not-holder');
  assert.ok(fenced.length >= 1, 'muxd must reject the stale-epoch write, not the relay');
  assert.ok(!host.writes.some(w => w.data === 'echo late\r'), 'stale input reached the PTY');

  // --- B writes cleanly under epoch 2.
  assert.equal(b.type('whoami\r').sent, true);
  assert.equal(host.writes.length, 2);
  assert.deepEqual(host.writes.map(w => w.principalId), ['alice', 'bob']);
  assert.deepEqual(host.interleavings(), []);
});

test('a stale steal loses instead of clobbering a newer holder', () => {
  const { muxd, relay, a, b } = bootstrap();
  a.type('one\r');
  b.lease.requestTakeover();                          // epoch -> 2, bob holds
  assert.equal(muxd.lease.principalId, 'bob');

  // A hostile relay replays B's *first* steal intent, rebuilt at the now-stale epoch 1.
  relay.fromViewer('L' + b.principal.sign({
    kind: 'lease.steal', session: SESSION, leaseEpoch: 1,
    principalId: 'bob', channelId: b.channelId, intentId: 'replay',
  }));
  assert.equal(muxd.lease.epoch, 2, 'a stale steal must not bump the epoch');
  assert.ok(muxd.rejects.some(r => r.why === 'stale-steal'));
});

// ---------------------------------------------------------------------------- the relay is not an authority

test('forged relay holder metadata is ignored by both viewers', () => {
  const { relay, a, b } = bootstrap();
  a.type('ls\r');
  const before = b.lease.state();

  // 1. Unsigned, but otherwise perfectly shaped.
  relay.forge(JSON.stringify({
    kind: 'lease.granted', session: SESSION, leaseEpoch: 99,
    holderPrincipalId: 'mallory', holderChannelId: 'ch-M', reason: 'relay says so',
  }));
  // 2. Signed with a key that is not muxd's.
  const impostor = crypto.generateKeyPairSync('ed25519');
  const body = {
    kind: 'lease.granted', session: SESSION, leaseEpoch: 100,
    holderPrincipalId: 'mallory', holderChannelId: 'ch-M', reason: 'impostor',
  };
  relay.forge(JSON.stringify({
    ...body,
    muxdSig: crypto.sign(null, Buffer.from(canon(body)), impostor.privateKey).toString('base64'),
  }));
  // 3. Genuinely signed, but replayed from before the grant (epoch fence).
  relay.forge(JSON.stringify({ kind: 'lease.released', session: SESSION, leaseEpoch: 0 }));

  for (const v of [a, b]) {
    const s = v.lease.state();
    assert.equal(s.holderPrincipalId, 'alice', v.id + ' adopted a forged holder');
    assert.ok(s.leaseEpoch < 99, v.id + ' adopted a forged epoch');
  }
  assert.equal(b.lease.state().readOnly, before.readOnly);
  assert.ok(b.lease.counters().unverified >= 2);

  // The explicit hint door records and refuses; it never reaches the state machine.
  const hint = b.lease.applyRelayHint({ holderPrincipalId: 'mallory' });
  assert.equal(hint.applied, false);
  assert.equal(hint.reason, 'relay-is-not-an-authority');
  assert.equal(b.lease.state().holderPrincipalId, 'alice');
  assert.equal(b.lease.counters().ignoredRelayHints, 1);
});

test('the conduit refuses to carry anything a client did not sign', () => {
  const conduit = createLeaseConduit();
  const unsigned = JSON.stringify({ kind: 'input.raw', session: SESSION, data: 'x', leaseEpoch: 1 });
  assert.equal(conduit.forwardSignedInput(SESSION, unsigned), null);
  assert.equal(conduit.forwardSignedLeaseOp(SESSION, JSON.stringify({ kind: 'lease.steal', session: SESSION })), null);
  assert.equal(conduit.forwardSignedInput(SESSION, 'not json'), null);

  // A relay trying to attach its own authority to an otherwise valid frame is refused whole,
  // not quietly sanitized — sanitizing would edit bytes a client signed.
  for (const key of RELAY_MINT_KEYS) {
    const frame = {
      kind: 'input.raw', session: SESSION, data: 'x', leaseEpoch: 1,
      auth: { principalId: 'alice', keyId: 'k', signature: 'sig' },
    };
    frame[key] = 'relay-minted';
    assert.equal(conduit.forwardSignedInput(SESSION, JSON.stringify(frame)), null, key + ' was carried');
  }

  // The conduit never invents a "lease is free" hint for a session muxd said nothing about.
  assert.equal(conduit.cachedNotice('never-seen'), null);
  assert.equal(conduit.cacheLeaseNotice(SESSION, JSON.stringify({ kind: 'not.a.lease' })), null);
});

test('deliberate send and send-when-idle carry signed lease behavior and cannot be relay-minted', () => {
  const { host, muxd, relay, a, b } = bootstrap();
  a.type('ls\r');

  // A holds the lease and queues a deliberate /send. It rides the same signed path.
  const durable = a.principal.sign({
    kind: 'input.durable', session: SESSION, data: 'deploy\r',
    channelId: a.channelId, leaseEpoch: 1, leaseBehavior: 'require-hold',
  });
  assert.ok(relay.fromViewer('I' + durable), 'signed deliberate send must forward');

  // Same intent with the lease behavior stripped by the relay: refused before it ever reaches muxd.
  const stripped = JSON.parse(durable);
  delete stripped.leaseBehavior;
  assert.equal(relay.conduit.forwardDeliberateSend(SESSION, JSON.stringify(stripped)), null);

  // A relay-authored send-when-idle for the *other* viewer: no signature, no forward.
  assert.equal(relay.conduit.forwardDeliberateSend(SESSION, JSON.stringify({
    kind: 'input.deferred', session: SESSION, data: 'rm -rf /\r',
    channelId: b.channelId, leaseEpoch: 1, leaseBehavior: 'send-when-idle',
  })), null);

  assert.ok(!host.writes.some(w => w.data === 'rm -rf /\r'));
  assert.deepEqual(host.interleavings(), []);
  assert.equal(muxd.lease.principalId, 'alice');
});

test('a viewer with no signer sends nothing at all', () => {
  const sent = [];
  const lease = createInputLease({
    session: SESSION, selfPrincipalId: 'nokey', selfChannelId: 'ch-N',
    verifyNotice: () => ({ trusted: false }),
    send: t => sent.push(t),
  });
  const r = lease.requestTakeover();
  assert.equal(r.sent, false);
  assert.equal(r.reason, 'no-signer');
  assert.deepEqual(sent, []);

  // No verifier configured means no state is ever adopted, however well-formed the frame looks.
  const res = lease.ingestNotice(JSON.stringify({ kind: 'lease.granted', session: SESSION, leaseEpoch: 5 }));
  assert.equal(res.applied, false);
  assert.equal(lease.state().verified, false);
});

// ---------------------------------------------------------------------------- wiring is not optional

test('server.js routes lease traffic through the conduit and mints nothing itself', () => {
  const src = fs.readFileSync(path.join(__dirname, '..', 'server.js'), 'utf8');
  assert.match(src, /require\('\.\/lease-conduit'\)/, 'server.js must use the conduit');
  assert.match(src, /createLeaseConduit\(\)/);
  assert.match(src, /leaseConduit\.cacheLeaseNotice\(/, 'muxd notices must be cached, not re-authored');
  assert.match(src, /leaseConduit\.cachedNotice\(/, 'a late viewer must get muxd\'s last word');
  assert.match(src, /leaseConduit\.forwardSignedInput\(/);
  assert.match(src, /leaseConduit\.forwardSignedLeaseOp\(/);
  assert.match(src, /leaseConduit\.forwardDeliberateSend\(/);
  assert.match(src, /s\[0\] === 'I' \|\| s\[0\] === 'L'/, 'signed viewer frames need a route');

  // The conduit itself holds no cryptography: it cannot sign, so it cannot decide.
  const conduitSrc = fs.readFileSync(path.join(__dirname, '..', 'lease-conduit.js'), 'utf8');
  assert.ok(!/require\(['"](node:)?crypto['"]\)/.test(conduitSrc), 'the conduit must hold no key');
  assert.ok(!/\.sign\(|createSign|generateKeyPair/.test(conduitSrc), 'the conduit must not sign');

  // And the browser module is actually served.
  const html = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');
  assert.match(html, /src="input-lease\.js"/);
});
