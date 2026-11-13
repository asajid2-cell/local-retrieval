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
// it was sized at no longer matches, it was rebuilt and has not been seeded from the session's start, or
// the parser could not keep up — `snapshot()` returns null and the caller serves an explicitly defined
// banner state instead. The raw byte log is never a viewer's initial paint: a suffix that can begin
// mid-escape-sequence is not a screen.

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

// How much scrollback the snapshot carries, and how much the model is allowed to HOLD.
//
// These are the bound on what a viewer can read back at all: the phone's conversation view renders the
// whole buffer, so a viewer painted from a 500-line snapshot simply has no more history to show. That was
// the "it stops pretty early - it doesn't let us view more than a couple of events back" report, and it
// was also an inconsistency: a COLD attach replays up to HOST_SB_BYTES (800 KB) of muxd's ring, which at
// 209 columns is ~3800 lines, so the same session showed several times more history depending on which
// path happened to paint it.
//
// The bound that actually matters is the PAYLOAD, not the line count: a snapshot must cost about what the
// replay it replaces costs, or a warm attach becomes the slow one. So the line budget is generous and a
// byte budget trims it - at a narrow phone grid the full slice fits easily, and at 209 columns it stops
// at roughly the same byte total the replay would have spent.
const SNAPSHOT_SCROLLBACK = Math.max(50, Number(process.env.MUX_SNAPSHOT_SCROLLBACK) || 2000);
const MODEL_SCROLLBACK = Math.max(SNAPSHOT_SCROLLBACK, Number(process.env.MUX_MODEL_SCROLLBACK) || 10000);
// A small fraction of the 800 KB replay it replaces, which is what keeps a warm attach the cheap path.
// Measured: a serialized line costs ~16 bytes at 80 columns and ~190 at 209, so this is ~8000 lines on a
// narrow grid (under the line cap) and ~650 on a wide one. The line cap alone is NOT enough - at 80 columns
// 2000 lines of plain text serialize to about the same bytes as the log that produced them, and then a
// warm attach costs MORE than the replay it exists to beat.
const SNAPSHOT_MAX_BYTES = Math.max(16 * 1024, Number(process.env.MUX_SNAPSHOT_MAX_BYTES) || 128 * 1024);

const MODELS = new Map();     // name -> { term, ser, cols, rows, bytes, idleAt, cold, gen }
let _modelGen = 0;

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
      term = new Terminal({ cols, rows, allowProposedApi: true, scrollback: MODEL_SCROLLBACK });
    } catch (error) {
      if (!loadError) loadError = error;
      return null;
    }
    const ser = new SerializeAddon();
    term.loadAddon(ser);
    // COLD: the model holds only the bytes seen since it was created (or rebuilt), which begin in the
    // middle of the session's stream. Its screen is therefore a suffix, not a state, and it must not be
    // served until seed() has fed it muxd's ring from the beginning. Only seed() clears this.
    model = { term, ser, cols, rows, bytes: 0, idleAt: 0, cold: true, gen: ++_modelGen };
    MODELS.set(name, model);
  }
  return model;
}

// Feed the model the exact bytes a viewer will receive. The caller must have already filtered to the
// per-viewer stream; here we take the session's shared stream, which is what every viewer is sent.
// `done` is optional: the hot fan-out path passes none, so it costs nothing there. When given, it fires
// once the parser has consumed `buf` - the point at which a snapshot of it is safe to take.
function feed(name, cols, rows, buf, done) {
  const model = modelFor(name, cols, rows);
  if (!model) return false;
  try {
    if (typeof done === 'function') model.term.write(buf, done);
    else model.term.write(buf);
    model.bytes += buf.length;
    model.idleAt = Date.now();
    return true;
  } catch (error) {
    dispose(name);            // a wedged parser must not be trusted again; fall back to byte replay
    return false;
  }
}

// Seed a COLD model from muxd's ring - the only bytes that begin at the session's start. Returns:
//   'warm'    - the model already holds a complete stream; the ring is ignored (see below)
//   'seeding' - the ring is being parsed; done(ok) fires once every byte queued before it has parsed
//   'failed'  - no model (deps missing, bad geometry, parser error)
// Only a COLD model is seeded. A warm one has been fed every byte since its seed, so replaying the ring
// into it would duplicate the history it already holds. A cold-but-non-empty model is the dangerous
// case: it was rebuilt at a new geometry or recreated after its state was deleted, so its bytes start
// mid-stream. Appending the ring to that suffix is exactly the undefined screen this change removes, so
// the model is dropped and rebuilt before the ring is fed.
//
// While the ring parses, the caller holds live bytes OUT of the model (server.js's seed hold): a byte fed
// during the seed would be inside the snapshot, and a snapshot reproduces screen CELLS, not the control
// sequences that produced them - a client-addressed query (XTVERSION, DA1) would be absorbed here and
// never reach a viewer. So the snapshot taken at done() is exactly the ring, and the caller delivers the
// held bytes itself, verbatim, after it.
function seed(name, cols, rows, bytes, done) {
  let model = modelFor(name, cols, rows);
  if (!model) return 'failed';
  if (!model.cold) return 'warm';
  if (model.bytes > 0) {
    dispose(name);
    model = modelFor(name, cols, rows);
    if (!model) return 'failed';
  }
  const gen = model.gen;
  try {
    model.term.write(bytes, () => {
      // While a seed is held the ring is the only write queued behind this callback, but writes from
      // before the reply may still be parsing, so the drain marker still proves the parser has caught
      // up - the same rule the warm path relies on. The model may have been disposed (boundary reset,
      // last viewer removed) or rebuilt at another geometry while the parser worked. This seed then
      // belongs to a screen nobody will render.
      const now = MODELS.get(name);
      const live = !!now && now === model && now.gen === gen && now.cold;
      drain(name, alive => {
        const ok = live && alive;
        const cur = MODELS.get(name);
        if (ok && cur) cur.cold = false;
        if (typeof done === 'function') done(ok);
      });
    });
    model.bytes += bytes.length;
    model.idleAt = Date.now();
    return 'seeding';
  } catch (error) {
    dispose(name);
    return 'failed';
  }
}

// Call `cb(alive)` once every byte handed to the model before this call has been parsed. The marker is
// an empty write: xterm fires a write callback before the next queued write is parsed, so only a chunk
// enqueued behind the pending ones proves the parser has caught up. Any byte fed after this call is
// parsed after the marker and is therefore NOT covered - the caller must deliver that one itself.
function drain(name, cb) {
  const model = MODELS.get(name);
  if (!model) { if (typeof cb === 'function') cb(false); return; }
  try {
    model.term.write('', () => {
      if (typeof cb === 'function') cb(MODELS.get(name) === model);
    });
  } catch (error) {
    if (typeof cb === 'function') cb(false);
  }
}

// The serialized screen, or null when the model cannot be trusted for a fresh viewer. `cols`/`rows` are
// the geometry the viewer will render at; a model at any other size is refused (the caller rebuilds by
// replaying bytes at that size instead). A COLD model is refused outright: its screen is a suffix of
// the stream, and a suffix is not a state.
function snapshot(name, cols, rows) {
  const model = MODELS.get(name);
  if (!model || model.cold || model.cols !== cols || model.rows !== rows) return null;
  if (model.bytes === 0) return null;
  let out;
  try {
    // Serialize a bounded slice of scrollback, not the whole thing. The screen itself is 24 rows; the
    // history behind it is what a person scrolls up to, and the slice is chosen to carry about as much
    // history as the byte replay it replaces (see SNAPSHOT_MAX_BYTES). If the line budget overruns the
    // byte budget the slice is halved until it fits, so a very wide grid does not pay more than the
    // replay would have; the final screen rows are always kept, so the result is still a real screen.
    let lines = SNAPSHOT_SCROLLBACK;
    for (;;) {
      out = model.ser.serialize({ scrollback: lines });
      if (!out || out.length <= SNAPSHOT_MAX_BYTES || lines <= model.rows) break;
      lines = Math.max(model.rows, Math.floor(lines / 2));
    }
  } catch (error) {
    dispose(name);
    return null;
  }
  // xterm-headless parses on a microtask, so a snapshot taken in the same tick as the last feed can be
  // empty even though bytes were handed over. An empty screen after the caller's CLEAR is a BLACK
  // terminal, so refuse it and let the byte replay (always correct) paint instead.
  return out && out.length ? out : null;
}

function stats() {
  return { sessions: MODELS.size, available: available(), error: loadError ? String(loadError.message || loadError) : '' };
}

module.exports = { available, feed, seed, drain, snapshot, dispose, stats };
