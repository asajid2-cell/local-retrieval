// Input continuity across ws reconnects — the reconnect buffer, living in the TRUSTED CLIENT ORIGIN.
//
// Why here and not at the relay: a keystroke that could not be sent has to wait somewhere. If the relay
// holds it, the relay holds plaintext input and (worse) needs authority to replay it later. So the buffer
// lives in the origin that already owns the signing key, and it holds ONLY plaintext bytes — never a
// signature, never a key, never anything durable. Nothing in this file touches localStorage,
// sessionStorage, indexedDB, cookies, fetch or XHR; the buffer dies with the tab, which is the point.
//
// A signature is bound to (channel, sequence). A dropped socket kills the channel, so a signature made for
// the old channel is worthless on the new one — buffering signed frames would be buffering garbage. We
// therefore buffer the plaintext and RE-SIGN on the fresh channel, carrying the ORIGINAL capturedAt/
// expiresAt across. Reconnecting is not allowed to make stale input fresh: bytes you typed 40 seconds ago
// into a dead socket expire on their own clock and are dropped where you can see it, because silently
// replaying them into a live shell minutes later is how you run a command you no longer meant to run.
(function installMuxInputContinuity(global) {
  'use strict';

  const DEFAULT_CAP_BYTES = 4096;
  const DEFAULT_TTL_MS = 15000;

  // UTF-8 length without Buffer/TextEncoder so the cap means the same thing in the browser and in tests.
  function utf8Len(str) {
    let n = 0;
    for (let i = 0; i < str.length; i++) {
      const c = str.charCodeAt(i);
      if (c < 0x80) n += 1;
      else if (c < 0x800) n += 2;
      else if (c >= 0xd800 && c <= 0xdbff && i + 1 < str.length) { n += 4; i++; }
      else n += 3;
    }
    return n;
  }

  // A buffered item belongs to the session it was typed into. The relay forwards a signed frame to whichever
  // session the socket is attached to, so without this the bytes you typed into session A would flush into
  // session B the moment you switched — a wrong-session insertion. Scope is a plain session name (not a
  // secret); the empty string means "unscoped", which is what the single-session tests use.
  function normalizeScope(scope) {
    return scope === undefined || scope === null ? '' : String(scope);
  }

  function rejectionKind(err) {
    const raw = err && (err.reason || err.code || err.message) || '';
    const text = String(raw).toLowerCase();
    if (text.includes('seq')) return 'sequence';
    if (text.includes('lease')) return 'lease';
    return '';
  }

  function createInputContinuity(options) {
    const opts = options || {};
    const now = typeof opts.now === 'function' ? opts.now : () => Date.now();
    const ttlMs = Number.isFinite(opts.ttlMs) && opts.ttlMs > 0 ? opts.ttlMs : DEFAULT_TTL_MS;
    const capBytes = Number.isFinite(opts.capBytes) && opts.capBytes > 0 ? opts.capBytes : DEFAULT_CAP_BYTES;
    const notice = typeof opts.notice === 'function' ? opts.notice : () => {};
    const signer = opts.signer || null;
    const transport = opts.transport || null;
    const verifyAccept = typeof opts.verifyAccept === 'function' ? opts.verifyAccept : null;
    const verifyAttachReplay = typeof opts.verifyAttachReplay === 'function' ? opts.verifyAttachReplay : null;

    let channel = null;              // { id, seq, live } — seq is per-channel and only ever moves forward
    let lastChannelId = null;
    const retired = new Set();       // every channel id we have ever opened; never opened or signed for twice
    let buffer = [];                 // [{ bytes, capturedAt, expiresAt, size }] — PLAINTEXT ONLY, in-memory only
    let bufferedBytes = 0;
    let halted = false;              // a sequence/lease rejection stops the flush until the next reconnect

    const stats = { queued: 0, sent: 0, droppedExpired: 0, droppedCap: 0, droppedUnsignable: 0, evicted: 0 };

    function say(kind, message, detail) {
      notice({ kind, message, detail: detail || null });
    }

    function canSign() {
      return !!(signer && typeof signer.signInput === 'function');
    }

    function evictForRoom(size, at) {
      // Expiry is evaluated LAZILY, at flush. Reaping it here too would let a later keystroke silently
      // absorb an earlier one's expiry — the reconnect that drops it is the moment the user needs told.
      // The one exception is cap pressure: bytes that can no longer be delivered lose their slot first.
      if (bufferedBytes + size > capBytes) pruneExpired(at);
      // Oldest-first: the bytes you typed longest ago are the ones you are least likely to still mean.
      while (buffer.length && bufferedBytes + size > capBytes) {
        const gone = buffer.shift();
        bufferedBytes -= gone.size;
        stats.evicted += 1;
        say('drop', 'Buffered input dropped — reconnect buffer full', { bytes: gone.size, capBytes });
      }
      return bufferedBytes + size <= capBytes;
    }

    function pruneExpired(at) {
      if (!buffer.length) return;
      const keep = [];
      for (const item of buffer) {
        if (item.expiresAt <= at) {
          bufferedBytes -= item.size;
          stats.droppedExpired += 1;
          say('drop', 'Buffered input expired and was NOT sent', {
            bytes: item.size, capturedAt: item.capturedAt, expiresAt: item.expiresAt,
          });
        } else keep.push(item);
      }
      buffer = keep;
    }

    async function signAndSend(item) {
      if (!channel || !channel.live) throw new Error('no live channel');
      const seq = channel.seq + 1;
      // Re-signed for the NEW channel, but the clock fields are the ones from capture time. Refreshing them
      // here is exactly the "reconnect launders stale input" bug this module exists to prevent.
      const frame = await signer.signInput({
        channelId: channel.id,
        seq,
        bytes: item.bytes,
        capturedAt: item.capturedAt,
        expiresAt: item.expiresAt,
      });
      if (!frame || !frame.signature) throw new Error('signer returned an unsigned frame');
      channel.seq = seq;             // only advances once a signature exists; a seq is never handed out twice
      await transport.sendSigned(frame);
      stats.sent += 1;
      return frame;
    }

    function enqueue(bytes, at, scope) {
      const size = utf8Len(bytes);
      if (size > capBytes) {
        stats.droppedCap += 1;
        say('drop', 'Input too large for the reconnect buffer — NOT sent', { bytes: size, capBytes });
        return 'dropped';
      }
      evictForRoom(size, at);
      buffer.push({ bytes, capturedAt: at, expiresAt: at + ttlMs, size, scope: normalizeScope(scope) });
      bufferedBytes += size;
      stats.queued += 1;
      return 'queued';
    }

    return {
      /**
       * One input frame from the terminal. Returns 'sent' | 'queued' | 'dropped'.
       * `kind` other than 'input' is NEVER buffered — a resize or heartbeat replayed minutes late is noise,
       * and the sender will emit a current one on reattach anyway.
       */
      async capture(bytes, meta) {
        const kind = (meta && meta.kind) || 'input';
        const scope = normalizeScope(meta && meta.scope);
        if (typeof bytes !== 'string' || bytes === '') return 'dropped';
        const at = now();

        if (kind !== 'input') {
          if (!channel || !channel.live || halted || !canSign()) return 'dropped';
          try {
            await signAndSend({ bytes, capturedAt: at, expiresAt: at + ttlMs, size: utf8Len(bytes) });
            return 'sent';
          } catch (err) { return 'dropped'; }
        }

        if (!canSign()) {
          // No signing authority in this origin means there is no honest way to deliver these bytes. We drop
          // them visibly rather than fall back to an unsigned send that muxd would (correctly) refuse.
          stats.droppedUnsignable += 1;
          say('drop', 'Input dropped — no signing authority in this origin', null);
          return 'dropped';
        }

        if (channel && channel.live && !halted) {
          try {
            await signAndSend({ bytes, capturedAt: at, expiresAt: at + ttlMs, size: utf8Len(bytes) });
            return 'sent';
          } catch (err) {
            const kindOfRejection = rejectionKind(err);
            if (kindOfRejection) {
              halted = true;
              channel.live = false;
              say('halt', 'Input stopped — ' + kindOfRejection + ' rejected by the host', { reason: kindOfRejection });
            }
            return enqueue(bytes, at, scope);
          }
        }
        return enqueue(bytes, at, scope);
      },

      /** The socket went away: the channel is dead and its id is burned. Buffered plaintext survives. */
      disconnect(reason) {
        if (channel) {
          retired.add(channel.id);
          lastChannelId = channel.id;
          channel = null;
        }
        halted = false;
        if (reason) say('info', 'Disconnected — holding input in this browser', { reason: String(reason) });
      },

      /**
       * Fresh channel, verified accept, verified attach replay, then flush. Any failure short-circuits
       * BEFORE the flush: unverified means unattached, and we do not write into a session we cannot vouch for.
       */
      async reconnect(scope) {
        if (channel) this.disconnect('reconnect');
        if (!transport || typeof transport.openChannel !== 'function') throw new Error('no transport');
        if (!verifyAccept || !verifyAttachReplay) throw new Error('no verifier');

        const opened = await transport.openChannel();
        const channelId = opened && opened.channelId;
        if (!channelId) throw new Error('channel.open returned no channel id');
        if (retired.has(channelId)) {
          say('error', 'Refused a reused channel id on reconnect', { channelId });
          throw new Error('channel id reuse refused: ' + channelId);
        }

        const acceptOk = await verifyAccept(opened.accept, { channelId });
        if (!acceptOk) {
          retired.add(channelId);
          say('error', 'channel.accept failed verification — not attaching', { channelId });
          throw new Error('channel.accept verification failed');
        }

        const replay = await transport.requestAttachReplay({ channelId });
        const replayOk = await verifyAttachReplay(replay, { channelId });
        if (!replayOk) {
          retired.add(channelId);
          say('error', 'attach replay failed verification — buffered input held, not flushed', { channelId });
          throw new Error('attach replay verification failed');
        }

        channel = { id: channelId, seq: 0, live: true };
        retired.add(channelId);
        halted = false;
        const flushed = await this.flush(scope);
        return { channelId, replay, ...flushed };
      },

      /**
       * In capture order, still-fresh only, stopping dead on a sequence/lease rejection.
       * `scope` is the session now attached: input captured for a DIFFERENT session is held, never delivered
       * into this one. Order is preserved, so a head item for another session stops the drain rather than
       * letting later items overtake it.
       */
      async flush(scope) {
        const want = normalizeScope(scope);
        const result = { flushed: 0, expired: 0, halted: false, remaining: 0, held: 0 };
        if (!channel || !channel.live) { result.remaining = buffer.length; return result; }
        if (!canSign()) {
          stats.droppedUnsignable += buffer.length;
          say('drop', 'Buffered input dropped — no signing authority in this origin', { count: buffer.length });
          buffer = []; bufferedBytes = 0;
          return result;
        }

        const before = stats.droppedExpired;
        pruneExpired(now());
        result.expired += stats.droppedExpired - before;

        while (buffer.length) {
          const item = buffer[0];
          if (item.scope !== want) {
            // These bytes belong to a session this socket is not attached to. Delivering them here would be
            // a wrong-session insertion, so they stay put (and expire on their own clock if you never return).
            result.held = buffer.reduce((n, i) => n + (i.scope !== want ? 1 : 0), 0);
            result.remaining = buffer.length;
            say('info', 'Buffered input held — it was typed into another session', { held: result.held, scope: want });
            return result;
          }
          if (item.expiresAt <= now()) {   // a slow flush can age out later items mid-drain
            buffer.shift();
            bufferedBytes -= item.size;
            stats.droppedExpired += 1;
            result.expired += 1;
            say('drop', 'Buffered input expired and was NOT sent', {
              bytes: item.size, capturedAt: item.capturedAt, expiresAt: item.expiresAt,
            });
            continue;
          }
          try {
            await signAndSend(item);
          } catch (err) {
            const kindOfRejection = rejectionKind(err);
            if (kindOfRejection) {
              halted = true;
              channel.live = false;
              result.halted = true;
              say('halt', 'Flush stopped — ' + kindOfRejection + ' rejected by the host; input still held', {
                reason: kindOfRejection, remaining: buffer.length,
              });
            } else {
              say('error', 'Flush stopped — ' + (err && err.message ? err.message : 'send failed'), null);
            }
            result.remaining = buffer.length;
            return result;
          }
          buffer.shift();
          bufferedBytes -= item.size;
          result.flushed += 1;
        }
        result.remaining = 0;
        return result;
      },

      /**
       * The chat was renamed: same session, new name. Retarget held bytes so an unrelated rename does not
       * strand the user's input until it expires. Returns how many items moved.
       */
      rescope(from, to) {
        const a = normalizeScope(from), b = normalizeScope(to);
        if (a === b) return 0;
        let moved = 0;
        for (const item of buffer) if (item.scope === a) { item.scope = b; moved += 1; }
        return moved;
      },

      pendingBytes() { return bufferedBytes; },
      pendingCount() { return buffer.length; },
      /** Plaintext copy for the UI only (an "unsent input" affordance). Never leaves this origin. */
      peek() { return buffer.map(i => ({ bytes: i.bytes, capturedAt: i.capturedAt, expiresAt: i.expiresAt, scope: i.scope })); },
      channelId() { return channel ? channel.id : null; },
      sequence() { return channel ? channel.seq : 0; },
      isLive() { return !!(channel && channel.live && !halted); },
      retiredChannelIds() { return Array.from(retired); },
      stats() { return { ...stats, bufferedBytes, pending: buffer.length }; },
      capBytes,
      ttlMs,
    };
  }

  const api = { createInputContinuity, DEFAULT_CAP_BYTES, DEFAULT_TTL_MS, utf8Len };
  global.createInputContinuity = createInputContinuity;
  global.muxInputContinuity = api;
  if (typeof module === 'object' && module && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
