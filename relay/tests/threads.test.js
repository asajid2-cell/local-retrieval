'use strict';
// The phone thread render is a VIEW of the same teemux bytes - no new data plane, no in-band metadata.
// It reads the buffer rows the grid-ownership work already gives us and groups them into messages using
// only the glyphs an agent itself prints. These tests pin the grouping rules against a REAL screen captured
// from a live agent tab (probe_thread_markers.cjs, tab-5bec, 2026-11-11), so a later "improvement" that
// merges two messages or splits a paragraph turns this red.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const SRC = fs.readFileSync(path.join(__dirname, '..', 'public', 'threads.js'), 'utf8');

function load() {
  const sandbox = { window: {} };
  vm.createContext(sandbox);
  vm.runInContext(SRC, sandbox, { filename: 'threads.js' });
  return sandbox.window.MuxThreads;
}

// A row as the page's buffer hands it over: text plus the wrap flag that marks a pty continuation.
const row = (text, wrapped = false, i = 0) => ({ i, text, wrapped });

// Captured from tab-5bec. In the live buffer the agent's own glyphs sit at COLUMN 0 and their body is
// indented (indent 2 for prose, 5 for tool detail) - the fixture mirrors that exactly, because the rule
// being tested is "a glyph at column 0 opens a message; an indented line is body".
const LIVE_SCREEN = [
  row('     [10/Nov/2026:22:38:53 "GET /auth/login 200', false, 1),
  row("⏺ Paired and drove the set. It's enrolled as tizen-tv-3zto4xpcmw5q1bpy, and I found the real watch failure.", false, 9),
  row('', false, 10),
  row('  What the set did at 22:37–22:39 (measured, nginx + events.log):', false, 11),
  row('', false, 12),
  row('  Then playback died, in this order:', false, 15),
  row('  1. Cold start: encoder silent 44.6 s, first segment at 50 s.', false, 16),
  row('  2. The manifest long-poll hit its 28 s stall deadline.', false, 17),
  row('  3. videoFailed() reported failed, so one report exhausts ', false, 19),
  row('the source: failover, generation++, and the capability answers 404.', true, 20),
  row('', false, 21),
  row('  Two fixes, gated and deployed:', false, 23),
  row('  - Player: reload the same manifest up to 10 times.', false, 24),
  row('', false, 26),
  row('  I need one thing from you: open the HTV app on the TV.', false, 27),
  row('✻ Cooked for 46m 39s · 3 shells still running', false, 32),
  row('⏺ Background command "Arm pairing listener" failed with exit code 1', false, 34),
  row(' ──────────────────────────────────────────────────────────────', false, 45),
  row('❯ ', false, 46),
  row(' ──────────────────────────────────────────────────────────────', false, 47),
];

test('a live agent screen segments into messages, not rows', () => {
  const T = load();
  const msgs = T.segment(LIVE_SCREEN);
  assert.ok(msgs.length > 1, 'a screen with several glyph rows is several messages');
  // The screen begins above the first glyph (a leftover tool block), which becomes an unattributed text
  // message; the FIRST GLYPH row after it opens the assistant message, and it absorbs every body line
  // until the next glyph row.
  const first = msgs.find(m => m.role === 'assistant');
  assert.ok(first, 'the ⏺ row opens an assistant message');
  assert.match(first.text, /Paired and drove the set/, 'the opening line is the message');
  assert.match(first.text, /What the set did/, 'its body belongs to it');
  assert.match(first.text, /Two fixes/, 'a later paragraph with no glyph is still the same message');
  // The separator and the empty prompt row are structure, not messages.
  assert.ok(!msgs.some(m => /^─+$/.test(m.text.trim())), 'a rule row is never a message');
});

test('a pty-wrapped continuation is glued, not treated as a new body line', () => {
  const T = load();
  const msgs = T.segment(LIVE_SCREEN);
  const joined = msgs.map(m => m.text).join('\n');
  assert.match(joined, /exhausts the source: failover/, 'the wrapped tail rejoins the line it continues');
  assert.ok(!/^\s*the source: failover/m.test(joined.split('\n').map(l => l).find(l => l.includes('the source')) || ''),
    'the continuation must not start its own paragraph');
});

test('tool results, summaries and user prompts are distinct roles', () => {
  const T = load();
  const msgs = T.segment(LIVE_SCREEN);
  assert.ok(msgs.some(m => m.lead === '✻'), 'the run summary is its own message');
  assert.equal(T.classify('❯ ').kind, 'user', 'the prompt glyph opens a user message');
  assert.equal(T.classify('⏺ text').kind, 'assistant', '⏺ opens an assistant message');
  assert.equal(T.classify('  ⎿ LAT-1').kind, 'tool', '⎿ opens a tool result');
  assert.equal(T.classify('  plain prose').kind, 'body', 'indented prose is body, never a message start');
});

test('the alternate screen is refused, because rows there are a canvas, not messages', () => {
  const T = load();
  assert.equal(T.bufferKind({ type: 'normal' }), 'normal');
  assert.equal(T.bufferKind({ type: 'alternate' }), 'alternate');
  assert.equal(T.bufferKind(null), 'normal');
});

test('a screen that starts mid-message keeps its text instead of dropping it', () => {
  const T = load();
  const msgs = T.segment([row('  orphaned body with no glyph above it', false, 0), row('  more of it', false, 1)]);
  assert.equal(msgs.length, 1);
  assert.equal(msgs[0].role, 'text');
  assert.match(msgs[0].text, /orphaned body/);
  assert.match(msgs[0].text, /more of it/);
});
