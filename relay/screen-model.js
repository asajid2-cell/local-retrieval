'use strict';
// A real screen model for hosted sessions, kept on the relay.
//
// Reattach used to send CLEAR_SCREEN plus a byte replay of muxd's ring — up to 800KB — and the viewer's
// xterm re-parsed every byte to rebuild a screen that is only a few kilobytes of cells. The relay already
// receives that same byte stream on the host link, so it can keep the SCREEN instead of the LOG: one
// headless xterm per session, fed the identical bytes, serialized on attach into a compact VT snapshot.
//
// The candidate is not guessed at. muxd/spike/vt-fidelity measured it against xterm-headless itself as
// oracle: pyte was NO-GO (99.35% cell fidelity, 0.37MB/s), xterm-headless was GO (100% fidelity at 71MB/s
// with native reflow). So the model IS xterm-headless plus its SerializeAddon — the same code the browser
// runs, which is what makes its screen agree with the viewer's by construction.
//
// It is a CACHE, never a source of truth: it must be fed every byte in order at the exact geometry the
// viewer will render at, or its screen drifts from the stream. When it is not trustworthy — the geometry
// it was sized at no longer matches, it was just resized and is mid-reflow, or the parser could not keep
// up — `snapshot()` returns null and the caller falls back to muxd's byte replay, which is always correct.

let Terminal = null;
let SerializeAddon = null;
let loadError = null;
try {
  // Runtime dependencies: the model runs on the relay's reattach path in production, so these two are
  // declared under `dependencies` (a `--omit=dev` install must still provide them - they used to be
  // devDependencies and a production install left the model silently inert). The guard stays anyway: an
  // install that is missing them keeps the byte-replay path, and `available()` / /api/health's `screenModel`
  // report which path a deployment is on. The model is an accelerator, not a gate.
  ({ Terminal } = require('@xterm/headless'));
  ({ SerializeAddon } = require('@xterm/addon-serialize'));
} catch (error) {
  loadError = error;
}

function available() { return !!(Terminal && SerializeAddon); }

// How much scrollback the snapshot carries. Bounded on purpose: the screen is 24 rows, so the useful
// part of a reattach is the history above it, and a slice keeps the payload compact while still far
// smaller than the byte log.
const SNAPSHOT_SCROLLBACK = 500;

const MODELS = new Map();     // name -> { term, ser, cols, rows, bytes, idleAt }

function dispose(name) {
  const model = MODELS.get(name);
  if (!model) return;
  try { model.ser.dispose(); } catch {}
  try { model.term.dispose(); } catch {}
  MODELS.delete(name);
}

function modelFor(name, cols, rows) {
  if (!available()) return null;
  if (!(cols > 1 && rows > 1)) return null;
  let model = MODELS.get(name);
  if (model && (model.cols !== cols || model.rows !== rows)) {
    // Geometry changed: the cells xterm holds are laid out for the old grid and a serialize would emit a
    // screen nobody is rendering. Rebuild rather than resize, so the first snapshot after a resize is the
    // stream re-read at the new size, not a lossy reflow of the old one.
    dispose(name);
    model = null;
  }
  if (!model) {
    let term;
    try {
      term = new Terminal({ cols, rows, allowProposedApi: true, scrollback: 1000 });
    } catch (error) {
      if (!loadError) loadError = error;
      return null;
    }
    const ser = new SerializeAddon();
    term.loadAddon(ser);
    model = { term, ser, cols, rows, bytes: 0, idleAt: 0 };
    MODELS.set(name, model);
  }
  return model;
}

// Feed the model the exact bytes a viewer will receive. The caller must have already filtered to the
// per-viewer stream; here we take the session's shared stream, which is what every viewer is sent.
function feed(name, cols, rows, buf) {
  const model = modelFor(name, cols, rows);
  if (!model) return false;
  try {
    model.term.write(buf);
    model.bytes += buf.length;
    model.idleAt = Date.now();
    return true;
  } catch (error) {
    dispose(name);            // a wedged parser must not be trusted again; fall back to byte replay
    return false;
  }
}

// The serialized screen, or null when the model cannot be trusted for a fresh viewer. `cols`/`rows` are
// the geometry the viewer will render at; a model at any other size is refused (the caller rebuilds by
// replaying bytes at that size instead).
function snapshot(name, cols, rows) {
  const model = MODELS.get(name);
  if (!model || model.cols !== cols || model.rows !== rows) return null;
  if (model.bytes === 0) return null;
  let out;
  try {
    // Serialize a bounded slice of scrollback, not the whole thing. The screen itself is 24 rows; the
    // history behind it is what a person scrolls up to. 500 lines is ~30KB - well under muxd's 800KB
    // replay - and past that the marginal history costs more than the replay path it is replacing.
    out = model.ser.serialize({ scrollback: SNAPSHOT_SCROLLBACK });
  } catch (error) {
    dispose(name);
    return null;
  }
  // xterm-headless parses on a microtask, so a snapshot taken in the same tick as the last feed can be
  // empty even though bytes were handed over. An empty screen after the caller's CLEAR is a BLACK
  // terminal, so refuse it and let the byte replay (always correct) paint instead.
  return out && out.length ? out : null;
}

function noteScrollback(name, cols, rows, bytes) {
  // muxd also replays bytes for a fresh attach; seeding the model from that same reply keeps it warm
  // without a separate feed pass, so the FIRST reattach after a boot has a model ready to serve.
  return feed(name, cols, rows, bytes);
}

function stats() {
  return { sessions: MODELS.size, available: available(), error: loadError ? String(loadError.message || loadError) : '' };
}

module.exports = { available, feed, snapshot, noteScrollback, dispose, stats };
