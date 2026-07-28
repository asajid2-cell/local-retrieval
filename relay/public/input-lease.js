(function installMuxInputLease(global) {
  'use strict';

  // Client mirror of the muxd-owned write lease.
  //
  // The rule this file enforces: nothing changes what the user sees or is allowed to do unless a
  // muxd signature was verified for it. The relay is a transport. It can drop a notice, replay an
  // old one, or invent one wholesale — none of those move this state machine, because an
  // unverified frame is discarded and a verified frame with a non-advancing epoch is discarded too.
  //
  // Injected, never imported: `verifyNotice` (muxd identity) and `signSteal` (this viewer's
  // principal key). Both live in principal-auth.js. A missing or throwing signer sends NOTHING —
  // the takeover button fails visibly rather than issuing an unsigned frame.

  var LEASE_KINDS = ['lease.state', 'lease.held', 'lease.granted', 'lease.released'];

  function isLeaseKind(kind) {
    for (var i = 0; i < LEASE_KINDS.length; i++) if (LEASE_KINDS[i] === kind) return true;
    return false;
  }

  function newIntentId() {
    if (global.crypto && global.crypto.randomUUID) return 'steal-' + global.crypto.randomUUID().replace(/-/g, '');
    return 'steal-' + Math.random().toString(36).slice(2) + Math.random().toString(36).slice(2);
  }

  function shortLabel(principalId) {
    var id = String(principalId || '');
    if (!id) return 'another device';
    return id.length <= 18 ? id : id.slice(0, 8) + '…' + id.slice(-6);
  }

  function createInputLease(options) {
    var opts = options || {};
    var session = String(opts.session || '');
    var selfPrincipalId = String(opts.selfPrincipalId || '');
    var selfChannelId = String(opts.selfChannelId || '');
    var verifyNotice = typeof opts.verifyNotice === 'function' ? opts.verifyNotice : null;
    var signSteal = typeof opts.signSteal === 'function' ? opts.signSteal : null;
    var send = typeof opts.send === 'function' ? opts.send : null;
    var onState = typeof opts.onState === 'function' ? opts.onState : null;

    var state = {
      leaseEpoch: 0,
      holderPrincipalId: '',
      holderChannelId: '',
      heldByOther: false,
      heldBySelf: false,
      readOnly: false,
      lastReason: '',
      verified: false,
    };
    var counters = { accepted: 0, unverified: 0, stale: 0, ignoredRelayHints: 0, stealsSent: 0, stealsRefused: 0 };
    var pendingSteal = false;

    function snapshot() {
      return {
        session: session,
        leaseEpoch: state.leaseEpoch,
        holderPrincipalId: state.holderPrincipalId,
        holderChannelId: state.holderChannelId,
        heldByOther: state.heldByOther,
        heldBySelf: state.heldBySelf,
        readOnly: state.readOnly,
        lastReason: state.lastReason,
        verified: state.verified,
        takeoverPending: pendingSteal,
      };
    }

    function publish() { if (onState) { try { onState(snapshot()); } catch (e) {} } }

    // The one door into this state machine. `raw` is whatever arrived on the socket; it is trusted
    // only after verifyNotice returns a muxd-signed body. No verifier configured = trust nothing.
    function ingestNotice(raw) {
      if (!verifyNotice) { counters.unverified++; return { applied: false, reason: 'no-verifier' }; }
      var verdict;
      try { verdict = verifyNotice(raw); } catch (e) { verdict = null; }
      if (!verdict || verdict.trusted !== true || !verdict.body || typeof verdict.body !== 'object') {
        counters.unverified++;
        return { applied: false, reason: 'unverified' };
      }
      var body = verdict.body;
      if (!isLeaseKind(String(body.kind || ''))) { counters.unverified++; return { applied: false, reason: 'not-lease' }; }
      if (session && String(body.session || '') !== session) {
        counters.unverified++;
        return { applied: false, reason: 'wrong-session' };
      }
      var epoch = Number(body.leaseEpoch);
      if (!isFinite(epoch) || epoch <= 0) { counters.unverified++; return { applied: false, reason: 'bad-epoch' }; }
      // Epoch is the replay fence. A relay that re-sends yesterday's genuinely signed "you are
      // read-only" cannot pin this viewer, and it cannot rewind a takeover it disliked.
      if (epoch < state.leaseEpoch) { counters.stale++; return { applied: false, reason: 'stale-epoch' }; }
      if (epoch === state.leaseEpoch && state.verified && String(body.kind) !== 'lease.held') {
        counters.stale++;
        return { applied: false, reason: 'duplicate-epoch' };
      }

      state.leaseEpoch = epoch;
      state.holderPrincipalId = String(body.holderPrincipalId || '');
      state.holderChannelId = String(body.holderChannelId || '');
      state.heldBySelf = !!state.holderChannelId && state.holderChannelId === selfChannelId;
      state.heldByOther = !!state.holderPrincipalId && !state.heldBySelf;
      state.readOnly = state.heldByOther;
      state.lastReason = String(body.reason || body.kind || '');
      state.verified = true;
      if (state.heldBySelf || !state.heldByOther) pendingSteal = false;
      counters.accepted++;
      publish();
      return { applied: true, reason: state.lastReason, state: snapshot() };
    }

    // Anything the relay says about the holder on its own authority. Kept as a named entry point
    // precisely so the ignoring is testable: it records and returns, it never touches `state`.
    function applyRelayHint(hint) {
      counters.ignoredRelayHints++;
      return { applied: false, reason: 'relay-is-not-an-authority', hint: hint };
    }

    function canType() { return !state.readOnly; }

    function banner() {
      if (!state.readOnly) return { visible: false, text: '', action: '', pending: false };
      return {
        visible: true,
        text: 'Read-only — ' + shortLabel(state.holderPrincipalId) + ' is typing',
        action: pendingSteal ? 'Taking over…' : 'Take over',
        pending: pendingSteal,
      };
    }

    // One tap. Builds the intent, hands it to the principal signer, forwards only what came back.
    function requestTakeover() {
      if (!signSteal) { counters.stealsRefused++; return { sent: false, reason: 'no-signer' }; }
      if (!send) { counters.stealsRefused++; return { sent: false, reason: 'no-transport' }; }
      var intent = {
        kind: 'lease.steal',
        session: session,
        // The epoch this viewer believes it is superseding — muxd fences on it, so a steal built
        // from a stale banner loses instead of silently clobbering a newer holder.
        leaseEpoch: state.leaseEpoch,
        principalId: selfPrincipalId,
        channelId: selfChannelId,
        intentId: newIntentId(),
      };
      var signed;
      try { signed = signSteal(intent); } catch (e) {
        counters.stealsRefused++;
        return { sent: false, reason: 'signer-failed' };
      }
      if (typeof signed !== 'string' || !signed) { counters.stealsRefused++; return { sent: false, reason: 'unsigned' }; }
      pendingSteal = true;
      counters.stealsSent++;
      try { send('L' + signed); } catch (e) {
        pendingSteal = false;
        counters.stealsSent--;
        counters.stealsRefused++;
        return { sent: false, reason: 'send-failed' };
      }
      publish();
      return { sent: true, intentId: intent.intentId, signed: signed };
    }

    // Every input path asks this first. It is advisory in the same direction the lease is: it can
    // only ever stop a doomed write early, never authorize one — muxd re-decides regardless.
    function guardOutgoingInput() {
      if (state.readOnly) return { allowed: false, reason: 'lease-held' };
      return { allowed: true, reason: '' };
    }

    return {
      ingestNotice: ingestNotice,
      applyRelayHint: applyRelayHint,
      requestTakeover: requestTakeover,
      guardOutgoingInput: guardOutgoingInput,
      canType: canType,
      banner: banner,
      state: snapshot,
      counters: function () { var c = {}; for (var k in counters) c[k] = counters[k]; return c; },
    };
  }

  global.createInputLease = createInputLease;
  global.MUX_LEASE_KINDS = LEASE_KINDS.slice();
  if (typeof module === 'object' && module && module.exports) {
    module.exports = { createInputLease: createInputLease, LEASE_KINDS: LEASE_KINDS.slice() };
  }
})(typeof globalThis !== 'undefined' ? globalThis : this);
