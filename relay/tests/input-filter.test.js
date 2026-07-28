// Regression: the input filter used to freeze the UI thread.
//
// takeTerminalReport matched the OSC report with /^\x1b\][0-9;]+;[^\x07\x1b]*(?:\x07|\x1b\\)/, and
// stripTerminalGeneratedReports called it once per character on a fresh s.slice(i). On 'ESC ]' followed by a
// long unterminated [0-9;] run, `[0-9;]+;` backtracks over every semicolon and `[^\x07\x1b]*` re-scans the
// tail each time — superquadratic. Measured on the shipped function: 20k chars -> 201ms, 40k -> 789ms,
// 80k -> 4.9s. A paste-sized chunk froze the tab for minutes, and this runs on EVERY keystroke and paste
// (term.onData -> stripTerminalGeneratedReports).
//
// The pathological input finishing inside this file's test timeout IS the regression gate: with the old
// implementation the 200k-semicolon case below does not complete in any tolerable time.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');

// Pull the real functions out of index.html and run them — this tests the shipped code, not a copy.
// (stripTerminalGeneratedReports calls takeTerminalReport, so they share one context.)
function extractFns(names){
  const ctx = {};
  for(const name of names){
    const start = source.indexOf('function ' + name + '(');
    assert.notEqual(start, -1, 'missing function ' + name);
    let depth = 0, end = -1;
    for(let i = source.indexOf('{', start); i < source.length; i++){
      if(source[i] === '{') depth++;
      else if(source[i] === '}'){ depth--; if(depth === 0){ end = i + 1; break; } }
    }
    assert.notEqual(end, -1, 'unbalanced braces for ' + name);
    vm.runInNewContext(source.slice(start, end) + '\nthis.' + name + ' = ' + name + ';', ctx);
  }
  return ctx;
}

const { takeTerminalReport, stripTerminalGeneratedReports } = extractFns(
  ['takeTerminalReport', 'stripTerminalGeneratedReports'],
);
// The result object is born inside the vm realm, so copy it into this one — deepStrictEqual compares
// prototypes and a cross-realm {} is not a local {}.
const strip = s => { const r = stripTerminalGeneratedReports(s); return { out:r.out, dropped:r.dropped }; };

// The implementation this replaced, kept verbatim as a behavioural oracle. Only ever fed SHORT inputs —
// feeding it the pathological case is exactly what this file exists to prevent.
function takeTerminalReportOld(s){
  let m = s.match(/^\x1b\[\?[0-9;]*c/); if(m) return m[0].length;
  m = s.match(/^\x1b\[>[0-9;]*c/); if(m) return m[0].length;
  m = s.match(/^\x1b\[[0-9;]*R/); if(m) return m[0].length;
  m = s.match(/^\x1b\[[0-9;?]*n/); if(m) return m[0].length;
  m = s.match(/^\x1b\][0-9;]+;[^\x07\x1b]*(?:\x07|\x1b\\)/); if(m) return m[0].length;
  return 0;
}
function stripOld(s){
  let out = '', dropped = false;
  for(let i = 0; i < s.length;){
    const n = takeTerminalReportOld(s.slice(i));
    if(n){ dropped = true; i += n; continue; }
    out += s[i++];
  }
  return { out, dropped };
}

test('REGRESSION: an unterminated OSC run of semicolons is filtered in linear time', () => {
  // The exact shape that used to backtrack: ESC ] then a huge [0-9;] run with no BEL / ESC-backslash.
  // Measured on the old implementation, same machine: 5k=9ms, 10k=36ms, 20k=141ms, 40k=567ms (clean
  // quadratic — 4x the time per 2x the input), 200k=14221ms. The new scan is flat: ~3ms at 1,000,000.
  //
  // 400k is in here on purpose: under the old curve it is ~57s, so it blows this file's 20s timeout on its
  // own. At 200k the old code finished in 14s, which would have squeaked under the timeout — the explicit
  // budget assertion below is what makes THAT size discriminating.
  for(const n of [200000, 400000]){
    const hostile = '\x1b]' + ';'.repeat(n);
    const started = process.hrtime.bigint();
    const r = strip(hostile);
    const ms = Number(process.hrtime.bigint() - started) / 1e6;

    // Unterminated -> not a report -> passes through untouched (see the pass-through policy test below).
    assert.equal(r.out, hostile, `hostile chunk (n=${n}) was altered`);
    assert.equal(r.dropped, false, `hostile chunk (n=${n}) was reported as dropped`);
    // Generous — ~1000x the real cost, but ~7x UNDER the old implementation at the smaller size.
    assert.ok(ms < 2000, `n=${n} took ${ms.toFixed(1)}ms — backtracking is back`);
  }

  // Same shape mixed with digits, and with real text on either side, is the paste case.
  const pasted = 'echo ' + '\x1b]0' + '1;'.repeat(100000) + ' done';
  const r2 = strip(pasted);
  assert.equal(r2.out, pasted);
  assert.equal(r2.dropped, false);
});

test('takeTerminalReport measures each well-formed report exactly', () => {
  assert.equal(takeTerminalReport('\x1b[?1;2c'), 7, 'primary DA');
  assert.equal(takeTerminalReport('\x1b[?c'), 4, 'primary DA, no params');
  assert.equal(takeTerminalReport('\x1b[>0;276;0c'), 11, 'secondary DA');
  assert.equal(takeTerminalReport('\x1b[24;80R'), 8, 'cursor-position report');
  assert.equal(takeTerminalReport('\x1b[R'), 3, 'CPR, no params');
  assert.equal(takeTerminalReport('\x1b[0n'), 4, 'device status report');
  assert.equal(takeTerminalReport('\x1b[?2n'), 5, 'DSR with private param');
  assert.equal(takeTerminalReport('\x1b]11;rgb:1e1e/1e1e/1e1e\x07'), 24, 'OSC report, BEL-terminated');
  assert.equal(takeTerminalReport('\x1b]11;rgb:00/00/00\x1b\\'), 19, 'OSC report, ST-terminated');
  assert.equal(takeTerminalReport('\x1b]0;a title\x07'), 12, 'OSC title');

  // Non-reports measure 0.
  assert.equal(takeTerminalReport('\x1b[A'), 0, 'arrow-up is not a report');
  assert.equal(takeTerminalReport('\x1b[2J'), 0, 'erase-display is not a report');
  assert.equal(takeTerminalReport('\x1b[<64;1;1M'), 0, 'mouse report is not a terminal report');
  assert.equal(takeTerminalReport('ls -la\r'), 0, 'plain text');
  assert.equal(takeTerminalReport(''), 0, 'empty string');
  assert.equal(takeTerminalReport('\x1b'), 0, 'bare ESC');
  assert.equal(takeTerminalReport('\x1b]'), 0, 'bare OSC introducer');

  // The optional index argument reads a report in place, without slicing.
  assert.equal(takeTerminalReport('xx\x1b[24;80R', 2), 8, 'indexed read');
  assert.equal(takeTerminalReport('xx\x1b[24;80R', 0), 0, 'indexed read at a non-report offset');
});

test('stripTerminalGeneratedReports drops reports and leaves everything else byte-identical', () => {
  // Every report form is stripped, and `dropped` is set.
  for(const report of ['\x1b[?1;2c', '\x1b[>0;276;0c', '\x1b[24;80R', '\x1b[0n', '\x1b[?2n',
                       '\x1b]11;rgb:1e1e/1e1e/1e1e\x07', '\x1b]10;#ffffff\x1b\\']){
    assert.deepEqual(strip(report), { out:'', dropped:true }, 'not stripped: ' + JSON.stringify(report));
    assert.deepEqual(strip('a' + report + 'b'), { out:'ab', dropped:true }, 'not stripped in context');
  }

  // Plain typing and non-report escape sequences are untouched, and `dropped` stays false.
  for(const keep of ['ls -la\r', '', 'hello world', '\x1b[A', '\x1b[B', '\x1b[2J', '\x1b[1;5C',
                     '\x1bOP', '\x1b[<64;10;30M', '\x1b]11;unterminated', '\x1b', '\x1b\x1b',
                     'git commit -m "fix; the; thing"\r']){
    assert.deepEqual(strip(keep), { out:keep, dropped:false }, 'altered: ' + JSON.stringify(keep));
  }

  // Back-to-back reports, and reports wrapped in real input.
  assert.deepEqual(
    strip('\x1b[?1;2c\x1b[24;80R\x1b]11;rgb:00/00/00\x07'),
    { out:'', dropped:true },
    'back-to-back reports',
  );
  assert.deepEqual(
    strip('ec\x1b[24;80Rho hi\x1b[?1;2c\r'),
    { out:'echo hi\r', dropped:true },
    'reports interleaved with typing',
  );
  // A report must not eat the arrow key next to it.
  assert.deepEqual(strip('\x1b[24;80R\x1b[A'), { out:'\x1b[A', dropped:true }, 'arrow key eaten');
});

// POLICY (documented choice, asserted below): an OSC whose terminator has not arrived yet is NOT a report,
// so it passes through UNCHANGED. This is what the old implementation did, and it is the safe direction on
// the INPUT path — this filter only exists to swallow reports the terminal generated in reply to a query,
// and a chunk that isn't provably one of those is user data. Swallowing it (or buffering it across chunks)
// would silently eat real pasted text; passing it through at worst forwards bytes the PTY already tolerates.
// It also means a split report can reach the PTY in halves, which is accepted: reports are emitted by xterm
// as one onData chunk, and the alternative costs a stateful buffer on the hottest input path.
test('an unterminated OSC at the end of a chunk passes through unchanged', () => {
  for(const partial of ['\x1b]11;rgb:1e1e/1e1e', '\x1b]0;a title', '\x1b]11;', '\x1b];', '\x1b]0',
                        '\x1b]11;text\x1b', '\x1b]11;text\x1b[']){
    assert.deepEqual(strip(partial), { out:partial, dropped:false },
      'unterminated OSC not passed through: ' + JSON.stringify(partial));
  }
  // ...and the halves still round-trip to the original bytes on the wire.
  const whole = '\x1b]11;rgb:1e1e/1e1e/1e1e\x07';
  const a = strip(whole.slice(0, 10)), b = strip(whole.slice(10));
  assert.equal(a.out + b.out, whole, 'split report lost bytes');
});

test('behaviour is byte-identical to the regex implementation it replaced', () => {
  const CORPUS = ['\x1b[?1;2c', '\x1b[>0;276;0c', '\x1b[24;80R', '\x1b[0n', '\x1b[?2n', '\x1b[?1;2n',
    '\x1b]11;rgb:1e1e/1e1e/1e1e\x07', '\x1b]10;#fff\x1b\\', '\x1b]0;t\x07', '\x1b]11;a\x1b[', '\x1b];7\x07',
    '\x1b];;\x07', '\x1b]1;;\x07', '\x1b]12;\x07', '\x1b]\x07', '\x1b]1;\x1b\\', '\x1b]1;2;x\x07',
    '\x1b[A', '\x1b[2J', '\x1b[<64;1;1M', '\x1b[c', '\x1b[>c', '\x1b[?c', '\x1b[n', '\x1b[;n', '\x1b[?R',
    '\x1b', '\x1b\\', '\x1bOP', '\x1b[', '\x1b]', ']', 'a', ';', '0', '\x07', '\r', ' ', 'ls -la'];

  // Every corpus atom, alone and as a pair — the pairs cover "a report abutting the next thing".
  for(const a of CORPUS){
    assert.equal(takeTerminalReport(a), takeTerminalReportOld(a), 'takeTerminalReport: ' + JSON.stringify(a));
    assert.deepEqual(strip(a), stripOld(a), 'strip: ' + JSON.stringify(a));
    for(const b of CORPUS){
      assert.deepEqual(strip(a + b), stripOld(a + b), 'strip pair: ' + JSON.stringify(a + b));
    }
  }

  // Deterministic fuzz over the alphabet the reports are built from (no Math.random — this must reproduce).
  const ALPHA = ['\x1b', '[', ']', '?', '>', ';', '0', '1', 'c', 'R', 'n', '\x07', '\\', 'x', ' '];
  let seed = 0x2f6e2b1;
  const rnd = () => ((seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff);
  for(let trial = 0; trial < 4000; trial++){
    let s = '';
    for(let k = 0, len = 1 + Math.floor(rnd() * 14); k < len; k++) s += ALPHA[Math.floor(rnd() * ALPHA.length)];
    assert.deepEqual(strip(s), stripOld(s), 'fuzz mismatch on ' + JSON.stringify(s));
  }
});
