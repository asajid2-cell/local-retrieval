'use strict';
// Relay side of the muxd-authoritative write lease.
//
// muxd owns the lease. This file exists to make that structural rather than polite: it is a
// conduit with no signing key, no clock-driven expiry, and no way to construct a lease decision.
// Everything it emits toward muxd is a byte-identical copy of what a client already signed, and
// everything it caches is a verbatim muxd frame the client re-verifies for itself.
//
// The relay may lie about the banner (it can withhold or replay a notice). It cannot make muxd
// accept input from the wrong holder, and it cannot make a client believe a holder it invented,
// because a client only adopts state that carries a muxd signature it verified.

const MAX_LEASE_FRAME_BYTES = 8192;   // transport bound only; NOT an authorization check
const MAX_INPUT_FRAME_BYTES = 65536;
const MAX_CACHED_SESSIONS = 256;

// Field names that would let a lying relay ask muxd to mint authority for it. A frame carrying
// one is not sanitized (sanitizing would change bytes the client signed) — it is refused whole.
const RELAY_MINT_KEYS = ['relaySigned', 'relayHolder', 'trustRelay', 'grant', 'privateKey', 'hostToken'];

function parseFrame(raw, limit) {
  if (typeof raw !== 'string') return null;
  if (Buffer.byteLength(raw) > limit) return null;
  let parsed;
  try { parsed = JSON.parse(raw); } catch { return null; }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null;
  for (const key of RELAY_MINT_KEYS) if (Object.prototype.hasOwnProperty.call(parsed, key)) return null;
  return parsed;
}

// A frame is forwardable when it carries a client proof. The relay does not check the proof —
// it cannot, it holds no key — it only refuses to be the thing that invents one.
function hasClientProof(frame) {
  return !!(frame
    && frame.auth && typeof frame.auth === 'object'
    && typeof frame.auth.principalId === 'string' && frame.auth.principalId
    && typeof frame.auth.keyId === 'string' && frame.auth.keyId
    && typeof frame.auth.signature === 'string' && frame.auth.signature);
}

function createLeaseConduit() {
  // Advisory only. Rendering hint, never an input gate. Values are verbatim muxd frames.
  const advisory = new Map();
  const stats = { forwardedInput: 0, forwardedLease: 0, cachedNotices: 0, refused: 0 };

  // Client -> muxd. `raw` is the exact string the client signed; it leaves untouched.
  function forwardSignedInput(session, raw) {
    const frame = parseFrame(raw, MAX_INPUT_FRAME_BYTES);
    if (!frame || frame.kind !== 'input.raw' || !hasClientProof(frame)) { stats.refused++; return null; }
    stats.forwardedInput++;
    return { t: 'iw', s: session, e: raw };          // `e` is the original text, not a re-encode
  }

  // Client -> muxd. Only `lease.steal` and `lease.release`; both must already be signed.
  function forwardSignedLeaseOp(session, raw) {
    const frame = parseFrame(raw, MAX_LEASE_FRAME_BYTES);
    if (!frame) { stats.refused++; return null; }
    if (frame.kind !== 'lease.steal' && frame.kind !== 'lease.release') { stats.refused++; return null; }
    if (!hasClientProof(frame)) { stats.refused++; return null; }
    stats.forwardedLease++;
    return { t: 'lease', s: session, f: raw };
  }

  // Deliberate /send and send-when-idle ride the same rule: the lease behavior is part of what
  // the client signed, so the relay can schedule a *reminder* but never a write.
  function forwardDeliberateSend(session, raw) {
    const frame = parseFrame(raw, MAX_INPUT_FRAME_BYTES);
    if (!frame) { stats.refused++; return null; }
    if (frame.kind !== 'input.durable' && frame.kind !== 'input.deferred') { stats.refused++; return null; }
    if (!hasClientProof(frame)) { stats.refused++; return null; }
    if (typeof frame.leaseBehavior !== 'string' || !frame.leaseBehavior) { stats.refused++; return null; }
    stats.forwardedInput++;
    return { t: 'iw', s: session, e: raw };
  }

  // muxd -> relay. Cache the frame verbatim so a late-attaching viewer can verify it itself.
  function cacheLeaseNotice(session, raw) {
    if (typeof raw !== 'string' || Buffer.byteLength(raw) > MAX_LEASE_FRAME_BYTES) return null;
    const frame = parseFrame(raw, MAX_LEASE_FRAME_BYTES);
    if (!frame || String(frame.kind || '').slice(0, 6) !== 'lease.') return null;
    if (advisory.size >= MAX_CACHED_SESSIONS && !advisory.has(session)) {
      advisory.delete(advisory.keys().next().value);
    }
    advisory.set(session, raw);
    stats.cachedNotices++;
    return raw;                                      // fan this exact string out to viewers
  }

  // What a freshly attached viewer gets: the last muxd frame, unmodified. Null when the relay has
  // nothing muxd said — the relay never fabricates a "lease is free" hint to fill the gap.
  function cachedNotice(session) {
    return advisory.has(session) ? advisory.get(session) : null;
  }

  function forgetSession(session) { advisory.delete(session); }

  return {
    forwardSignedInput,
    forwardSignedLeaseOp,
    forwardDeliberateSend,
    cacheLeaseNotice,
    cachedNotice,
    forgetSession,
    stats: () => ({ ...stats }),
  };
}

module.exports = { createLeaseConduit, MAX_LEASE_FRAME_BYTES, MAX_INPUT_FRAME_BYTES, RELAY_MINT_KEYS };
