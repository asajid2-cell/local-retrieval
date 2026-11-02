'use strict';
// Relay-side consumer for OSC 21338 (the terminal element map).
//
// Contract: docs/terminal-element-map.md. The emitter (the gateway renderer) writes
//   ESC ] 21338 ; <base64url UTF-8 JSON> ESC \
// and a relayed stream may carry the 8-bit form 0x9D "21338;" ... 0x9C. Nothing else in the
// stream belongs to us, so everything that is not a legal introducer is passed through
// byte-for-byte. A browser viewer must never receive the sequence itself: the relay strips it
// here and forwards the decoded map separately.

const INTRODUCER_ESC = Buffer.from([0x1b, 0x5d, 0x32, 0x31, 0x33, 0x33, 0x38, 0x3b]); // ESC ] 21338 ;
const INTRODUCER_C1 = Buffer.from([0x9d, 0x32, 0x31, 0x33, 0x33, 0x38, 0x3b]);        // 0x9D 21338 ;

const BEL = 0x07;
const CAN = 0x18;
const SUB = 0x1a;
const ESC = 0x1b;
const BACKSLASH = 0x5c;
const C1_INTRO = 0x9d;
const C1_ST = 0x9c;

const MAX_CARRY_BYTES = 65536;   // ELEMENT_MAP_MAX_CARRY_BYTES
const MAX_PAYLOAD_BYTES = 32768; // ELEMENT_MAP_MAX_PAYLOAD_BYTES (emitter rule; not enforced here)
const EMPTY = Buffer.alloc(0);
const B64URL = /^[A-Za-z0-9_-]+={0,2}$/;

function createStripState() {
  return { carry: EMPTY, dropped: 0 };
}

// Returns the introducer's length when a complete legal introducer starts at `at`, 0 when the
// bytes from `at` are a non-empty proper prefix of one (hold them), and -1 when this is not an
// introducer at all (the byte is text). The two forms differ in their first byte, so only one
// template can match.
function matchIntroducer(buf, at) {
  const template = buf[at] === ESC ? INTRODUCER_ESC : buf[at] === C1_INTRO ? INTRODUCER_C1 : null;
  if (!template) return -1;
  const avail = Math.min(template.length, buf.length - at);
  for (let p = 0; p < avail; p++) if (buf[at + p] !== template[p]) return -1;
  return avail === template.length ? template.length : 0;
}

// First legal introducer at or after `from`; used when a carry overflows and scanning resumes
// inside the same chunk. Returns the index, or the index of a trailing partial introducer, or -1.
function findIntroducer(buf, from) {
  for (let at = from; at < buf.length; at++) {
    if (buf[at] !== ESC && buf[at] !== C1_INTRO) continue;
    const m = matchIntroducer(buf, at);
    if (m === 0) return at;
    if (m > 0) return at;
  }
  return -1;
}

function isInt(n) { return typeof n === 'number' && Number.isInteger(n); }

function isAction(a) {
  if (!a || typeof a !== 'object' || Array.isArray(a)) return false;
  switch (a.t) {
    case 'ptr': return isInt(a.x) && isInt(a.y) && (a.b === 0 || a.b === 1 || a.b === 2);
    case 'key': return typeof a.d === 'string' && B64URL.test(a.d);
    case 'focus':
    case 'none': return true;
    default: return false;
  }
}

function isElement(el) {
  if (!el || typeof el !== 'object' || Array.isArray(el)) return false;
  if (typeof el.id !== 'string') return false;
  if (typeof el.role !== 'string') return false;
  if (!Array.isArray(el.r) || el.r.length !== 4 || !el.r.every(isInt)) return false;
  if (el.label !== undefined && typeof el.label !== 'string') return false;
  if (el.act !== undefined && !isAction(el.act)) return false;
  return true;
}

function isFrame(o) {
  if (!o || typeof o !== 'object' || Array.isArray(o)) return false;
  if (o.v !== 1) return false;
  if (!isInt(o.gen) || o.gen < 0) return false;
  if (!isInt(o.cols) || o.cols < 0) return false;
  if (!isInt(o.rows) || o.rows < 0) return false;
  if (!Array.isArray(o.els)) return false;
  return o.els.every(isElement);
}

// A body is valid base64url of a UTF-8 JSON object that conforms. Anything else is dropped - the
// sequence is ours by command number regardless of whether its body makes sense.
function decodeFrame(body) {
  const text = body.toString('latin1');
  if (!B64URL.test(text)) return null;
  let json;
  try { json = Buffer.from(text, 'base64url').toString('utf8'); } catch { return null; }
  let obj;
  try { obj = JSON.parse(json); } catch { return null; }
  return isFrame(obj) ? obj : null;
}

function stripElementMap(chunk, state) {
  if (!state || typeof state !== 'object') throw new TypeError('stripElementMap needs a strip state');
  const carry = Buffer.isBuffer(state.carry) ? state.carry : EMPTY;
  const body = Buffer.isBuffer(chunk) ? chunk : Buffer.from(String(chunk == null ? '' : chunk), 'latin1');
  const input = carry.length ? Buffer.concat([carry, body], carry.length + body.length) : body;
  if (!isInt(state.dropped) || state.dropped < 0) state.dropped = 0;

  const parts = [];
  const frames = [];
  let textFrom = 0;
  let i = 0;

  const flush = end => { if (end > textFrom) parts.push(input.subarray(textFrom, end)); textFrom = end; };
  const hold = at => {
    flush(at);
    let open = input.subarray(at);
    // A carried open sequence past the cap is abandoned; scanning resumes inside the same bytes
    // at the next legal introducer, so a well-formed frame behind the garbage is not lost with it.
    while (open.length > MAX_CARRY_BYTES) {
      state.dropped += 1;
      const next = findIntroducer(open, 1);
      open = next >= 0 ? open.subarray(next) : EMPTY;
    }
    state.carry = open.length ? Buffer.from(open) : EMPTY;
  };

  while (i < input.length) {
    // TEXT: everything up to the next byte that could begin an introducer is passed through.
    let start = -1;
    for (let at = i; at < input.length; at++) {
      if (input[at] === ESC || input[at] === C1_INTRO) { start = at; break; }
    }
    if (start < 0) break;

    const m = matchIntroducer(input, start);
    if (m === 0) { i = input.length; hold(start); return { text: Buffer.concat(parts), frames }; }
    if (m < 0) { i = start + 1; continue; }   // not ours: this byte stays text, keep scanning

    // SEQUENCE: body runs to the first terminator or abort. No body byte can be a terminator
    // (base64url excludes all of them), so the boundary cannot be spoofed from inside a payload.
    let k = start + m;
    let bodyEnd = -1;
    let next = -1;
    while (k < input.length) {
      const c = input[k];
      if (c === BEL || c === C1_ST) { bodyEnd = k; next = k + 1; break; }   // terminator consumed
      if (c === CAN || c === SUB) { bodyEnd = k; next = k + 1; break; }     // abort consumed
      if (c === ESC) {
        if (k + 1 >= input.length) break;                                   // could still be ST
        if (input[k + 1] === BACKSLASH) { bodyEnd = k; next = k + 2; break; } // ST consumed
        bodyEnd = k; next = k; break;                                       // abort: ESC survives
      }
      k += 1;
    }
    if (bodyEnd < 0) { i = input.length; hold(start); return { text: Buffer.concat(parts), frames }; }

    // An abort does not suppress a body that decodes; only an undecodable body is counted.
    const decoded = decodeFrame(input.subarray(start + m, bodyEnd));
    if (decoded) frames.push(decoded); else state.dropped += 1;
    flush(start);
    textFrom = next;
    i = next;
  }

  flush(input.length);
  state.carry = EMPTY;
  return { text: Buffer.concat(parts), frames };
}

// The viewer frame: the relay's single shape for a decoded map, with every rect clipped to the
// frame's own grid. An element scrolled out is reported by coordinates, not by deletion, so an
// empty box is dropped rather than the whole map being rewritten. Duplicate ids resolve last-wins.
function clipFrame(frame) {
  if (!isFrame(frame)) return null;
  const cols = frame.cols, rows = frame.rows;
  const byId = new Map();
  for (const el of frame.els) {
    const [x, y, w, h] = el.r;
    const x0 = Math.max(0, x), y0 = Math.max(0, y);
    const x1 = Math.min(cols, x + w), y1 = Math.min(rows, y + h);
    if (x1 <= x0 || y1 <= y0) continue;
    // Last-wins includes paint order: a re-painted id belongs where it was painted last.
    byId.delete(el.id);
    byId.set(el.id, { ...el, r: [x0, y0, x1 - x0, y1 - y0] });
  }
  return { v: frame.v, gen: frame.gen, cols, rows, els: [...byId.values()] };
}

// Prefix for a map frame on the viewer socket. Deliberately one character that no terminal
// payload uses, and it is sent directly rather than through sendViewer (control frames must not
// be coalesced with terminal output).
function encodeViewerFrame(frame) {
  return 'E' + JSON.stringify(frame);
}

module.exports = {
  createStripState,
  stripElementMap,
  clipFrame,
  encodeViewerFrame,
  isFrame,
  MAX_CARRY_BYTES,
  MAX_PAYLOAD_BYTES,
};
