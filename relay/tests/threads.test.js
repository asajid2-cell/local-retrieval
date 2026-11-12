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

// ---- the terminal's own composer/status chrome is NOT conversation (live, tab-5bec, 2026-11-12) --------
//
// The screenshot showed a quarter of the phone given over to one giant bubble reading
//   "cd: C:\Users\Ahmed · session: cd ... ⏵⏵ bypass permissions on · 3 shells · usage: 37% left ..."
// That is the agent app's STATUS BAR, painted under its prompt box. The agent draws it as three indented
// rows after a separator/prompt/separator sandwich, so a segmenter that only knows "indented = body" makes
// it the tail of the last message and the phone shows a status bar as if the agent had said it.
//
// The live tail, verbatim:
//   45  ──────────────      separator
//   46  ❯                   the prompt line (this is where the user types)
//   47  ──────────────      separator
//   48    cd: C:\Users\...  status bar row 1
//   49    ⏵⏵ bypass permissions on · 3 shells ...
//   50    ‹ Leaf 3: tv ... › All 3
const LIVE_CHROME = [
  row('⏺ Those were the stale pairing listeners from earlier cleaning up.', false, 40),
  row('', false, 41),
  row('  Still waiting on the HTV app being opened on the set.', false, 42),
  row(' ──────────────────────────────────────────────────────────────', false, 45),
  row('❯ ', false, 46),
  row(' ──────────────────────────────────────────────────────────────', false, 47),
  row("  cd: C:\Users\Ahmed · session: cd 'C:\Users\Ahmed'; & cc --resume fd94f334", false, 48),
  row('  ⏵⏵ bypass permissions on · 3 shells Mistral Vibe HigVibe usage: 37% left usage 34.6k in', false, 49),
  row('  ‹ Leaf 3: tv           23 htv-lanes         24 speed-policy      › All 3', false, 50),
];

test('the terminal composer and status bar are never rendered as a message', () => {
  const T = load();
  const msgs = T.segment(LIVE_CHROME);
  const joined = msgs.map(m => m.text).join('\n');
  assert.ok(!/bypass permissions/.test(joined), 'the status bar is chrome, not something the agent said');
  assert.ok(!/cd: C:\\Users/.test(joined), 'the session line is chrome too');
  assert.ok(!/speed-policy/.test(joined), 'the keymap hint is chrome');
  // The real conversation above the composer survives.
  assert.ok(msgs.some(m => /stale pairing listeners/.test(m.text)), 'the last real message is kept');
  assert.ok(msgs.some(m => /Still waiting on the HTV app/.test(m.text)), 'its body is kept');
});

test('consecutive messages from one speaker carry one label, not one each', () => {
  // The phone showed "TAB-5BEC" above five bubbles in a row. A messaging view labels a RUN of turns from
  // the same speaker once; repeating it is noise that costs vertical space on the smallest screen.
  const T = load();
  const rows = [
    row('⏺ first thing the agent said', false, 1),
    row('⏺ second thing the agent said', false, 3),
    row('⏺ third thing the agent said', false, 5),
  ];
  const msgs = T.segment(rows);
  assert.equal(msgs.length, 3, 'they remain three messages');
  const marks = T.grouped(msgs);
  // Compare a plain string: the segmenter runs inside a vm sandbox, so its arrays carry that realm's
  // Array prototype and a strict deepEqual would fail on the prototype rather than on the values.
  assert.equal(marks.map(m => m.showWho).join(','), 'true,false,false',
    'only the first of a run is labelled');
});

// ---- the app's footer widgets are chrome too (live, tab-5bec, 2026-11-12) -----------------------------
//
// The second screenshot kept a result bubble ending in:
//     Packets: Sent = 3, Received = 3, Lost = 0 (0% loss),   Jump to
//     bottom
//     > 171 tasks (146 done, 11 in progress, 14 open)
//     (ctrl+t)
// "Jump to bottom", the task counter and its key hint are the agent app's own FOOTER, painted over the
// bottom rows. They are not output of the command, and a phone reading a ping result should not be told
// about 171 tasks. Detected structurally: each is the app's fixed widget, not a command's text.
const LIVE_FOOTER = [
  row('  Ping statistics for 192.168.1.246:', false, 90),
  row('  Packets: Sent = 3, Received = 3, Lost = 0 (0% loss),', false, 91),
  row('  Jump to bottom \u2193', false, 92),
  row('  \u25b8 171 tasks (146 done, 11 in progress, 14 open)', false, 93),
  row('  (ctrl+t)', false, 94),
];

test('the agent app footer widgets never appear as command output', () => {
  const T = load();
  const msgs = T.segment(LIVE_FOOTER);
  const joined = msgs.map(m => m.text).join('\n');
  assert.ok(!/Jump to bottom/.test(joined), 'the scroll hint is chrome');
  assert.ok(!/171 tasks/.test(joined), 'the task counter is chrome');
  assert.ok(!/\(ctrl\+t\)/.test(joined), 'the key hint is chrome');
  // The actual command output above them is kept.
  assert.ok(/Ping statistics/.test(joined), 'real output survives');
  assert.ok(/Packets: Sent/.test(joined), 'and so does the second real line');
});

test('a tool result belongs to the agent turn, so it does not re-label the speaker', () => {
  // A turn is assistant -> tool -> assistant -> tool. Labelling each bubble would print the session name
  // three times down a phone screen. The agent's whole turn is one speaker: the tool bubbles are already
  // visibly subordinate (monospace, muted), so the run is labelled once.
  const T = load();
  const rows = [
    row('\u25cf Bash(ping -n 3 host)', false, 1),
    row('  \u23bf Ping statistics', false, 2),
    row('\u25cf Bash(cat /tmp/x)', false, 3),
    row('  \u23bf contents', false, 4),
  ];
  const msgs = T.grouped(T.segment(rows));
  assert.equal(msgs.map(m => m.showWho).join(','), 'true,false,false,false',
    'one label covers the agent turn, tool results included');
});

test('text above the first recognised message is marked as context, not shown as a message', () => {
  // Live, tab-5bec: the phone rendered the agent's private REASONING as its first bubble, because the
  // reasoning's header had scrolled off and what remained was plain indented prose. At the row level that
  // is indistinguishable from a message body whose glyph scrolled away, so it must still be SHOWN (the
  // phone never shows less than the terminal) - but it must not read as something the agent said to you.
  const T = load();
  const rows = [
    row('  Now address the user message. It is gibberish.', false, 0),
    row('  I will ask plainly whether it was accidental.', false, 1),
    row('⏺ That message came through as keyboard noise - did you mean to send something?', false, 3),
  ];
  const msgs = T.grouped(T.segment(rows));
  assert.equal(msgs[0].role, 'text', 'leading unattributed rows stay unattributed');
  assert.equal(msgs[0].showWho, false, 'and are never given a speaker label');
  assert.equal(msgs[0].context, true, 'they are marked as context above the conversation');
  assert.equal(msgs[1].context, false, 'the first real message is a message');
  assert.ok(/keyboard noise/.test(msgs[1].text), 'the real message is intact');
});
