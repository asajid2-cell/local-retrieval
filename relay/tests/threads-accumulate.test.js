'use strict';
// The conversation view must remember what it has seen.
//
// Measured live 2026-11-13 on all six teed tabs: every viewer's xterm buffer held `bufLen === rows` -
// ZERO lines of scrollback. An agent TUI repaints in place with absolute cursor addressing (494 CUP
// sequences, no scroll region, no newline scrolling in muxd's ring), and replaying that whole 240 KB ring
// into a headless xterm yields the same 0 lines. So the history is not in the byte stream at all: no
// replay depth, snapshot size or scrollback setting can recover it, and the mirror is faithful precisely
// because the app never emits it.
//
// That is the "it stops pretty early - it doesn't let us view more than a couple of events back" report.
// The only place it can be answered is the render: accumulate the turns as the screen shows them, and
// render the accumulation. These cases drive `accumulateThreads` directly, which is the rule under test.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const SRC = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');

function fnSrc(name) {
  const i = SRC.indexOf('function ' + name);
  assert.ok(i >= 0, `function ${name} not found`);
  let depth = 0, started = false, j = i;
  for (; j < SRC.length; j++) {
    const c = SRC[j];
    if (c === '{') { depth++; started = true; }
    else if (c === '}') { depth--; if (started && depth === 0) { j++; break; } }
  }
  return SRC.slice(i, j);
}

function harness() {
  const sandbox = {
    current: 'tab-test',
    threadAccum: {},
    THREADS_KEEP: 400,
    THREADS_KEEP_CHARS: 400000,
    window: {},
  };
  // The real grouping is used, so what is asserted is what the phone renders. threads.js installs itself
  // on `window`, which a browser has and node does not - so it is loaded against the sandbox's window
  // instead of required into this module's (empty) global.
  sandbox.window = sandbox;
  vm.createContext(sandbox);
  const threadsSrc = fs.readFileSync(path.join(__dirname, '..', 'public', 'threads.js'), 'utf8');
  vm.runInContext(threadsSrc, sandbox);
  assert.ok(sandbox.MuxThreads, 'threads.js installs MuxThreads on the window it is given');
  vm.createContext(sandbox);
  vm.runInContext(
    fnSrc('threadsKey') + '\n'
    + fnSrc('accumulateThreads')
    + '\n;this.accumulate = accumulateThreads;',
    sandbox);
  return sandbox;
}

const msg = (role, text) => ({ role, text, lead: '', kind: 'text' });

test('a turn that scrolls off the screen stays in the conversation', () => {
  const s = harness();
  s.accumulate([msg('user', 'first question'), msg('assistant', 'first answer')]);
  // The app repaints; only the newest turn is on screen now.
  const grown = s.accumulate([msg('assistant', 'second answer')]);
  const texts = grown.map(m => m.text);
  assert.ok(texts.includes('first question'), 'the earliest turn is still there');
  assert.ok(texts.includes('first answer'));
  assert.ok(texts.includes('second answer'), 'and the new one was appended');
  assert.equal(texts.filter(t => t === 'first answer').length, 1, 'nothing is duplicated');
});

test('a streaming reply updates in place instead of appearing twice', () => {
  const s = harness();
  s.accumulate([msg('assistant', 'Working on it')]);
  const mid = s.accumulate([msg('assistant', 'Working on it\n\nHalf done')]);
  assert.equal(mid.filter(m => /Working on it/.test(m.text)).length, 1,
    'a longer body for the SAME turn replaces the shorter one');
  assert.match(mid.find(m => /Working on it/.test(m.text)).text, /Half done/);

  const done = s.accumulate([msg('assistant', 'Working on it\n\nHalf done\n\nFinished')]);
  assert.equal(done.filter(m => /Working on it/.test(m.text)).length, 1);
  assert.match(done.find(m => /Working on it/.test(m.text)).text, /Finished/);
});

test('a screen that still holds the last few turns does not duplicate them', () => {
  const s = harness();
  s.accumulate([msg('user', 'one'), msg('assistant', 'two')]);
  // The next screen still shows both, plus a new turn - the ordinary case where the screen has not
  // scrolled the older turns off yet.
  const out = s.accumulate([msg('user', 'one'), msg('assistant', 'two'), msg('assistant', 'three')]);
  assert.deepEqual(Array.from(out, m => m.text), ['one', 'two', 'three'],
    'the overlap is recognised, so the list is not one copy per repaint');
});

test('two different turns that open the same way are not merged', () => {
  const s = harness();
  s.accumulate([msg('assistant', 'Fix 2 - the sound every frame'), msg('user', 'next')]);
  // A later turn with the same opening line is a DIFFERENT turn: the alignment is positional, so it is
  // appended rather than rewriting history.
  const out = s.accumulate([msg('assistant', 'Fix 2 - the sound every frame'), msg('user', 'again')]);
  assert.equal(out.filter(m => /Fix 2/.test(m.text)).length, 2,
    'matching on the opening alone would erase one of two real turns');
});

test('the accumulation is bounded, and drops the OLDEST turns first', () => {
  const s = harness();
  s.THREADS_KEEP = 5;
  for (let i = 0; i < 12; i++) s.accumulate([msg('assistant', 'turn ' + i)]);
  const out = s.accumulate([msg('assistant', 'turn 12')]);
  assert.ok(out.length <= 5, `the list stays bounded, got ${out.length}`);
  assert.ok(out.some(m => /turn 12/.test(m.text)), 'the newest turn is kept');
  assert.ok(!out.some(m => /turn 0$/.test(m.text)), 'the oldest turns are the ones evicted');
});

test('the character budget bounds a wall of output too', () => {
  const s = harness();
  s.THREADS_KEEP_CHARS = 1000;
  for (let i = 0; i < 10; i++) s.accumulate([msg('tool', 'x'.repeat(400) + ' ' + i)]);
  const out = s.accumulate([msg('assistant', 'newest')]);
  const total = out.reduce((n, m) => n + m.text.length, 0);
  assert.ok(total <= 1400, `the record stays inside its budget, got ${total} chars`);
  assert.ok(out.some(m => m.text === 'newest'), 'and the newest turn still arrives');
});

test('the record is per session, and a switch starts a new one', () => {
  const s = harness();
  s.accumulate([msg('assistant', 'only in A')]);
  s.current = 'tab-other';
  const b = s.accumulate([msg('assistant', 'only in B')]);
  assert.deepEqual(Array.from(b, m => m.text), ['only in B'], 'a different session does not inherit the record');
  // And switching back finds the first session's record still intact.
  s.current = 'tab-test';
  const a = s.accumulate([msg('assistant', 'only in A')]);
  assert.deepEqual(Array.from(a, m => m.text), ['only in A']);
});
