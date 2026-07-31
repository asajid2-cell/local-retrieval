// Regression: a full-screen TUI on the alternate buffer (codex/claude) has no scrollback of its own, so the
// only way it scrolls its transcript is the mouse WHEEL report the terminal forwards to the PTY. The input
// path strips the mouse-move flood (a leaked DECSET 1003 mode can flood + kill a session), but it must NOT
// strip wheel reports (Cb bit 6) — dropping them is what made "can't scroll" on codex sessions.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');

// Pull the real function out of index.html and run it — this tests the shipped code, not a copy.
function extractFn(name){
  const start = source.indexOf('function ' + name + '(');
  assert.notEqual(start, -1, 'missing function ' + name);
  let depth = 0, end = -1;
  for(let i = source.indexOf('{', start); i < source.length; i++){
    if(source[i] === '{') depth++;
    else if(source[i] === '}'){ depth--; if(depth === 0){ end = i + 1; break; } }
  }
  assert.notEqual(end, -1, 'unbalanced braces for ' + name);
  const ctx = {};
  vm.runInNewContext(source.slice(start, end) + '\nthis.' + name + ' = ' + name + ';', ctx);
  return ctx[name];
}

test('stripMouseReports keeps wheel reports (so alt-buffer TUIs can scroll)', () => {
  const strip = extractFn('stripMouseReports');

  // Wheel up (64) / down (65) MUST survive — this is the fix.
  assert.equal(strip('\x1b[<64;100;30M'), '\x1b[<64;100;30M', 'wheel-up dropped');
  assert.equal(strip('\x1b[<65;100;30M'), '\x1b[<65;100;30M', 'wheel-down dropped');
  // Wheel with a modifier still has bit 6 set (ctrl+wheel-up = 64|16 = 80).
  assert.equal(strip('\x1b[<80;5;5M'), '\x1b[<80;5;5M', 'modified wheel dropped');
});

test('stripMouseReports still kills the move/click flood the guard exists for', () => {
  const strip = extractFn('stripMouseReports');

  assert.equal(strip('\x1b[<35;219;53M'), '', 'motion report (Cb 35) not stripped');   // 32|3, the 1003 flood
  assert.equal(strip('\x1b[<0;10;10M'), '', 'plain click not stripped');
  assert.equal(strip('\x1b[<32;10;10M'), '', 'drag (motion+button) not stripped');
  assert.equal(strip('\x1b[M\x20\x21\x22'), '', 'X10 mouse not stripped');
  assert.equal(strip('\x1b[555;79;1M'), '', 'urxvt/1015 flood not stripped');
});

test('stripMouseReports preserves wheel even interleaved in a move flood, and leaves real input alone', () => {
  const strip = extractFn('stripMouseReports');

  assert.equal(
    strip('\x1b[<35;1;1M\x1b[<64;1;1M\x1b[<35;2;2M'),
    '\x1b[<64;1;1M',
    'wheel not preserved when surrounded by motion reports',
  );
  assert.equal(strip('ls -la\r'), 'ls -la\r', 'plain keystrokes altered');
  assert.equal(strip('\x1b[A'), '\x1b[A', 'arrow-up key altered');   // must not touch non-mouse CSIs
});

// ─── mouse-report guard v2: discrete click/release forwarding + leak detector ─────────────────────
// The three tests above are the contract wheel scrolling rests on; everything below adds the click
// path WITHOUT moving them. The rule the v2 guard encodes: motion is flood and dies forever, wheel is
// proven and passes forever, press/release passes only while an app has demonstrably taken the mouse.

const DECSET = '\x1b[?1000h\x1b[?1006h';   // what a TUI emits when it starts tracking
const DECRST = '\x1b[?1000l\x1b[?1006l';

test('stripMouseReports forwards discrete press/release ONLY while tracking is active', () => {
  const strip = extractFn('stripMouseReports');

  // Tracking active → the click and its release reach the app (this is click-to-position/focus).
  assert.equal(strip('\x1b[<0;10;10M', true), '\x1b[<0;10;10M', 'press dropped while tracking');
  assert.equal(strip('\x1b[<0;10;10m', true), '\x1b[<0;10;10m', 'release dropped while tracking');
  assert.equal(strip('\x1b[<2;4;7M', true), '\x1b[<2;4;7M', 'right-press dropped while tracking');
  assert.equal(strip('\x1b[<16;4;7M', true), '\x1b[<16;4;7M', 'ctrl-click dropped while tracking');

  // Not tracking → identical bytes are denied. Omitting the argument must deny too, so no caller can
  // reopen the leaked-mode flood by simply forgetting to prove tracking.
  assert.equal(strip('\x1b[<0;10;10M', false), '', 'press forwarded with tracking off');
  assert.equal(strip('\x1b[<0;10;10m', false), '', 'release forwarded with tracking off');
  assert.equal(strip('\x1b[<0;10;10M'), '', 'press forwarded when trackingActive omitted (must deny)');
});

test('motion reports are stripped unconditionally — even with tracking active', () => {
  const strip = extractFn('stripMouseReports');

  // The historic flood. No value of trackingActive may let these through.
  for(const active of [true, false, undefined]){
    assert.equal(strip('\x1b[<35;219;53M', active), '', `1003 motion survived (active=${active})`);
    assert.equal(strip('\x1b[<32;10;10M', active), '', `drag survived (active=${active})`);
    assert.equal(strip('\x1b[<43;1;1M', active), '', `motion+ctrl survived (active=${active})`);
    assert.equal(strip('\x1b[M\x20\x21\x22', active), '', `X10 survived (active=${active})`);
    assert.equal(strip('\x1b[555;79;1M', active), '', `urxvt/1015 survived (active=${active})`);
    // …and wheel still passes regardless, which is the r.3-child-10 ground this leaf must not disturb.
    assert.equal(strip('\x1b[<64;1;1M', active), '\x1b[<64;1;1M', `wheel dropped (active=${active})`);
  }

  // Mixed frame: click kept, motion around it gone, wheel untouched.
  assert.equal(
    strip('\x1b[<35;1;1M\x1b[<0;5;5M\x1b[<64;1;1M\x1b[<35;2;2M', true),
    '\x1b[<0;5;5M\x1b[<64;1;1M',
    'mixed motion/click/wheel frame not filtered correctly',
  );
});

test('isClickReport matches exactly the discrete press/release class', () => {
  const isClick = extractFn('isClickReport');

  assert.equal(isClick('\x1b[<0;10;10M'), true, 'press not recognised');
  assert.equal(isClick('\x1b[<0;10;10m'), true, 'release not recognised');
  assert.equal(isClick('\x1b[<64;1;1M'), false, 'wheel must keep its own path');
  assert.equal(isClick('\x1b[<35;1;1M'), false, 'motion must never be click');
  assert.equal(isClick('\x1b[<0;1;1Mls'), false, 'must not match a report with trailing input');
  assert.equal(isClick('ls -la\r'), false, 'plain keystrokes matched as click');
  assert.equal(isClick(''), false, 'empty matched as click');
});

test('mouse guard tracks DECSET/DECRST out of session output, across chunk splits', () => {
  const guard = extractFn('createMouseGuard')();

  assert.equal(guard.inputAllowed(), false, 'forwarding allowed before any app took the mouse');

  guard.ingestOutput(DECSET, 1000);
  assert.equal(guard.inputAllowed(), true, 'DECSET 1000 did not enable forwarding');

  guard.ingestOutput(DECRST, 1100);
  assert.equal(guard.inputAllowed(), false, 'DECRST 1000 did not disable forwarding');

  // Split mid-sequence: the 64-byte carried tail must stitch it back together (muxctl semantics).
  guard.ingestOutput('\x1b[?10', 1200);
  assert.equal(guard.inputAllowed(), false, 'half a DECSET already counted as tracking');
  guard.ingestOutput('02h', 1201);
  assert.equal(guard.inputAllowed(), true, 'DECSET split across two frames was not stitched');

  // 1006 alone is the SGR-encoding flag, not a tracking mode.
  const g2 = extractFn('createMouseGuard')();
  g2.ingestOutput('\x1b[?1006h', 1000);
  assert.equal(g2.inputAllowed(), false, '1006 alone counted as tracking');

  // Re-scanning the carried tail must not double-count: mode state is a set, so this stays idempotent.
  const g3 = extractFn('createMouseGuard')();
  g3.ingestOutput('\x1b[?1002h', 1000);
  g3.ingestOutput('x', 1001);
  g3.ingestOutput('y', 1002);
  assert.equal(g3.inputAllowed(), true, 'tail re-scan lost the mode');
  g3.ingestOutput('\x1b[?1002l', 1003);
  assert.equal(g3.inputAllowed(), false, 'tail re-scan resurrected a disabled mode');
});

test('guard accepts binary output frames (Uint8Array) the same as strings', () => {
  const guard = extractFn('createMouseGuard')();
  const bytes = s => Uint8Array.from(Array.from(s, c => c.charCodeAt(0)));

  guard.ingestOutput(bytes(DECSET), 1000);
  assert.equal(guard.inputAllowed(), true, 'DECSET in a binary frame was not seen');
  guard.ingestOutput(bytes(DECRST), 1100);
  assert.equal(guard.inputAllowed(), false, 'DECRST in a binary frame was not seen');
});

test('leak detector: an echoed report blocks forwarding, and only a fresh DECSET re-arms it', () => {
  const guard = extractFn('createMouseGuard')();
  const CLICK = '\x1b[<0;10;10M';

  guard.ingestOutput(DECSET, 1000);
  assert.equal(guard.inputAllowed(), true, 'not armed after DECSET');

  // We forward a click and register it. The session echoes those exact bytes straight back — proof that
  // nothing is consuming the reports and the flood is starting.
  guard.noteForwarded(CLICK, 1000);
  const r = guard.ingestOutput('prompt$ ' + CLICK, 1050);

  assert.equal(r.leaked, true, 'echoed report not detected as a leak');
  assert.equal(guard.inputAllowed(), false, 'still forwarding after a detected leak');
  assert.equal(guard.stats().leaks, 1, 'leak not counted');
  assert.equal(guard.stats().pending, 0, 'pending reports not cleared on leak');
  // The caller sends DECRST on r.leaked; the guard mirrors that by clearing its own mode map.
  // Array.from: stats() builds its array inside the vm context, so it has a different Array.prototype
  // and deepStrictEqual would reject it on prototype identity alone.
  assert.deepEqual(Array.from(guard.stats().modes), [], 'mode mirror not cleared to match the DECRST we send');

  // Re-arm requires a genuine off→on DECSET — nothing else unblocks us.
  guard.ingestOutput('lots of ordinary output\r\n', 1200);
  assert.equal(guard.inputAllowed(), false, 'ordinary output re-armed the guard');
  guard.ingestOutput(DECRST, 1300);
  assert.equal(guard.inputAllowed(), false, 'a DECRST re-armed the guard');

  guard.ingestOutput(DECSET, 1400);
  assert.equal(guard.inputAllowed(), true, 'a fresh DECSET did not re-arm the guard');
  assert.equal(guard.stats().blocked, false, 'still blocked after re-arm');
});

test('leak detector: no false positives outside the echo window, on wheel, or on unrelated output', () => {
  const guard = extractFn('createMouseGuard')();
  const CLICK = '\x1b[<0;10;10M';
  guard.ingestOutput(DECSET, 1000);

  // Same bytes, but long after we forwarded them → stale, not a leak.
  guard.noteForwarded(CLICK, 1000);
  assert.equal(guard.ingestOutput(CLICK, 1000 + 60000).leaked, false, 'stale echo counted as a leak');
  assert.equal(guard.inputAllowed(), true, 'stale echo blocked forwarding');

  // An app REDRAWING after a click must not look like an echo.
  guard.noteForwarded(CLICK, 2000);
  assert.equal(guard.ingestOutput('\x1b[2J\x1b[H redraw after click', 2010).leaked, false,
    'normal redraw counted as a leak');
  assert.equal(guard.inputAllowed(), true, 'normal redraw blocked forwarding');

  // Wheel is deliberately NOT registered: its forwarding is proven ground and must never arm the
  // detector against itself, even if the app happens to echo something wheel-shaped.
  const g2 = extractFn('createMouseGuard')();
  g2.ingestOutput(DECSET, 1000);
  g2.noteForwarded('\x1b[<64;1;1M\x1b[<35;2;2M', 1000);
  assert.equal(g2.stats().pending, 0, 'wheel/motion reports were armed in the leak detector');
  assert.equal(g2.ingestOutput('\x1b[<64;1;1M', 1010).leaked, false, 'echoed wheel counted as a leak');
  assert.equal(g2.inputAllowed(), true, 'echoed wheel blocked forwarding');
});

test('leak detector: a DECSET carried in the leaked chunk cannot instantly re-arm the guard', () => {
  // On leak the guard clears its mode mirror (it is about to send DECRST). The carried 64-byte tail
  // still holds bytes from that chunk, so a DECSET inside it would be re-scanned against the cleared
  // map and read as a fresh off→on. The tail must be dropped with the modes.
  const guard = extractFn('createMouseGuard')();
  const CLICK = '\x1b[<0;10;10M';

  guard.ingestOutput(DECSET, 1000);
  guard.noteForwarded(CLICK, 1000);
  assert.equal(guard.ingestOutput(CLICK + DECSET, 1010).leaked, true, 'leak missed');
  assert.equal(guard.inputAllowed(), false, 'blocked immediately after the leak');

  guard.ingestOutput('unrelated output', 1020);   // re-scans the carried tail
  assert.equal(guard.inputAllowed(), false, 'a stale DECSET in the carried tail re-armed the guard');
});

test('guard.reset() drops all session-scoped mouse state on re-attach', () => {
  const guard = extractFn('createMouseGuard')();
  guard.ingestOutput(DECSET, 1000);
  guard.noteForwarded('\x1b[<0;1;1M', 1000);
  assert.equal(guard.inputAllowed(), true);

  guard.reset();
  assert.equal(guard.inputAllowed(), false, 'a new attach inherited the old session\'s tracking state');
  assert.equal(guard.stats().pending, 0, 'a new attach inherited pending reports');
  assert.deepEqual(Array.from(guard.stats().modes), [], 'a new attach inherited mode state');
});

// ─── wiring: the parts that cannot run standalone are asserted against the shipped source ─────────
test('the disable sequence and its wiring are what the leak detector actually sends', () => {
  const seq = /const MOUSE_DISABLE_SEQ = '([^']+)'/.exec(source);
  assert.notEqual(seq, null, 'MOUSE_DISABLE_SEQ missing');
  const decoded = seq[1].replace(/\\x1b/g, '\x1b');
  for(const mode of [1000, 1002, 1003, 1006]){
    assert.ok(decoded.includes('\x1b[?' + mode + 'l'), 'disable sequence is missing DECRST ' + mode);
  }

  // A detected leak must send that sequence, and it must leave through the signed seam.
  assert.match(source, /if\(r\.leaked\) sendMouseInput\(MOUSE_DISABLE_SEQ\)/,
    'a detected leak does not send the DECRST');
  assert.match(source, /mouseGuard\.noteForwarded\(d, Date\.now\(\)\);[\s\S]{0,200}?sendMouseInput\(d\)/,
    'forwarded clicks are not registered with the leak detector before being sent');
  assert.match(source, /stripMouseReports\(d, mouseGuard\.inputAllowed\(\)\)/,
    'the input path does not gate stripMouseReports on the tracker');
});

test('every mouse-originated write goes through the signed seam — no raw send(\'i\', …) remains', () => {
  // TRUST AMENDMENT: forwarded clicks/releases and the leak detector's DECRST are signed input.raw.
  const start = source.indexOf('function sendMouseInput(');
  assert.notEqual(start, -1, 'sendMouseInput seam missing');
  const seam = source.slice(start, source.indexOf('\nfunction ', start + 1));
  assert.match(seam, /signer\.signInputRaw/, 'seam does not use principalAuth.signInputRaw');
  assert.match(seam, /kind:\s*'input\.raw'/, 'seam does not sign as input.raw');
  // A signer that throws must send NOTHING — a failed signature may never degrade to an unsigned frame.
  assert.match(seam, /catch\(e\)\{ return false; \}/, 'a throwing signer can fall through to unsigned send');

  // The mouse path itself must never call send('i', …) directly.
  const path = source.slice(source.indexOf('function noteSessionOutput('), source.indexOf('term.onData(d =>'))
    + source.slice(source.indexOf('if(isClickReport(d)){'), source.indexOf('const filtered = stripTerminalGeneratedReports(d);'));
  assert.equal(/send\('i'/.test(path), false, 'the mouse path still calls send(\'i\', …) directly');
});

// This asserted that the stamp contained "mouse" and differed from one specific older value — i.e. it
// pinned the deploy that happened to be in flight when it was written. Every later deploy fails it by
// construction, which is noise, not signal: the next stamp bump breaks it again no matter what changed.
// What is worth holding is that the stamp EXISTS and is dated, because it is the only handle a loaded
// tab has for identifying its own build (an open tab never re-fetches the inline script after a deploy).
test('window.__muxBuild carries a dated build stamp', () => {
  const m = /window\.__muxBuild\s*=\s*'([^']+)'/.exec(source);
  assert.notEqual(m, null, '__muxBuild missing: an open tab has no way to identify its build');
  assert.match(
    m[1], /^\d{4}-\d{2}-\d{2}-\S/,
    `__muxBuild must be a dated stamp like 2026-07-27-<what-changed>, got ${JSON.stringify(m[1])}`,
  );
});
