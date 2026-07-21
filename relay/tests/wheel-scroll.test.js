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
