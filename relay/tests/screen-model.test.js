// Lever 6: the relay's screen model, driven end to end.
//
// Reattach used to ship CLEAR_SCREEN + up to 800KB of muxd's byte ring for the viewer's xterm to
// re-parse into a screen that is only a few KB of cells. The relay now keeps that screen instead: one
// headless xterm per session, fed the same bytes, serialized on attach. The claim to prove is not
// "fewer bytes" on its own — it is that the few bytes RENDER THE SAME SCREEN. So this suite does the
// honest round trip: feed a stream to the model, take its snapshot, replay the snapshot into a fresh
// headless terminal, and require the resulting screen to equal the one a full byte replay produces.
//
// The candidate was already chosen by measurement, not taste: muxd/spike/vt-fidelity scored pyte NO-GO
// (99.35% cells, 0.37MB/s) and xterm-headless GO (100% cells, 71MB/s). This suite holds xterm-headless
// to the part that matters here — a snapshot that reconstitutes the screen.
const test = require('node:test');
const assert = require('node:assert/strict');
const { WebSocket } = require('ws');
const { once } = require('node:events');

const screenModel = require('../screen-model');
const { Terminal } = require('@xterm/headless');
const { SerializeAddon } = require('@xterm/addon-serialize');
const { RelayHarness, waitFor, waitForWsText } = require('./harness');

const COLS = 80;
const ROWS = 24;

// Render a byte stream through a fresh headless terminal and return its visible rows as text. This is
// the viewer's own rendering path, so comparing two of these compares what a person would see.
async function render(bytes) {
  const term = new Terminal({ cols: COLS, rows: ROWS, allowProposedApi: true, scrollback: 1000 });
  await new Promise(resolve => term.write(bytes, resolve));
  const rows = [];
  const buf = term.buffer.active;
  for (let y = 0; y < ROWS; y++) {
    const line = buf.getLine(buf.baseY + y);
    rows.push(line ? line.translateToString(true) : '');
  }
  const cursor = { x: term.buffer.active.cursorX, y: term.buffer.active.cursorY };
  term.dispose();
  return { rows, cursor };
}

const ESC = '\x1b';

// xterm-headless parses asynchronously (its write loop schedules on a timer); the relay's feeds and
// snapshots land many turns apart, but a synchronous test must yield for the model to finish before it
// will serialize. A macrotask hop past the parser's own timer is what settles it.
const flushTurn = () => new Promise(resolve => setTimeout(resolve, 20));

test('a snapshot of a fed stream re-renders the same screen as the full byte replay', async () => {
  assert.equal(screenModel.available(), true, 'xterm-headless must be installed for this lever to run');
  // A stream with the things a real screen has: plain lines, colour, a cursor move, a partial line.
  const stream =
    `${ESC}[32mgreen line${ESC}[0m\r\n` +
    `plain line two\r\n` +
    `${ESC}[1;31mBOLD RED${ESC}[0m\r\n` +
    `cursor will sit here: ` +
    `no-newline-tail`;
  const bytes = Buffer.from(stream, 'utf8');

  const full = await render(bytes);                         // what the OLD path (byte replay) shows
  screenModel.feed('sm-render', COLS, ROWS, bytes);
  await flushTurn();
  const snap = screenModel.snapshot('sm-render', COLS, ROWS);
  assert.ok(snap, 'a fed, correctly-sized model must produce a snapshot');
  const fromSnap = await render(Buffer.from(snap, 'utf8')); // what the NEW path shows

  assert.deepEqual(fromSnap.rows, full.rows, 'the serialized screen must render identically to the replay');
  assert.deepEqual(fromSnap.cursor, full.cursor, 'the cursor must land in the same cell');
  screenModel.dispose('sm-render');
});

test('the snapshot is a small screen, not a copy of the byte log', async () => {
  // 4000 lines of scrollback: the byte log is large, the screen it leaves is 24 rows.
  let stream = '';
  for (let i = 0; i < 4000; i++) stream += `${ESC}[36mline ${String(i).padStart(5, '0')}${ESC}[0m\r\n`;
  stream += `the only line that stays on screen`;
  const bytes = Buffer.from(stream, 'utf8');

  screenModel.feed('sm-size', COLS, ROWS, bytes);
  await flushTurn();
  const snap = screenModel.snapshot('sm-size', COLS, ROWS);
  assert.ok(snap);
  // The whole point: the snapshot is a bounded screen plus a bounded slice of history (~30KB at 500
  // lines), so it is orders of magnitude under the log even when the log is 4000 lines deep.
  assert.ok(Buffer.byteLength(snap) < 64 * 1024,
    `a bounded snapshot must stay under 64KB, got ${Buffer.byteLength(snap)} bytes`);
  assert.ok(Buffer.byteLength(snap) < bytes.length / 10,
    `the snapshot (${Buffer.byteLength(snap)}) must be far under the byte log (${bytes.length})`);

  // And it still shows the right final line.
  const model = await render(Buffer.from(snap, 'utf8'));
  assert.ok(model.rows.some(r => r.includes('the only line that stays on screen')),
    'the snapshot must contain the current screen content');
  screenModel.dispose('sm-size');
});

test('a model at the wrong geometry refuses to serialize instead of lying', async () => {
  screenModel.feed('sm-geom', 100, 30, Buffer.from('hello', 'utf8'));
  await flushTurn();
  assert.ok(screenModel.snapshot('sm-geom', 100, 30), 'the matching geometry serializes');
  assert.equal(screenModel.snapshot('sm-geom', 80, 24), null,
    'a request at a different geometry must be refused — the caller falls back to the byte replay');
  screenModel.dispose('sm-geom');
});

test('an empty or never-fed model produces no snapshot', () => {
  assert.equal(screenModel.snapshot('sm-never', COLS, ROWS), null, 'a session with no bytes has no screen to serve');
  assert.ok(screenModel.feed('sm-empty', COLS, ROWS, Buffer.alloc(0)) === true, 'feeding zero bytes is fine');
  assert.equal(screenModel.snapshot('sm-empty', COLS, ROWS), null, 'but it is still nothing to serve');
  screenModel.dispose('sm-empty');
});

test('a resize drops the stale model so the next feed rebuilds at the new geometry', async () => {
  screenModel.feed('sm-resize', 80, 24, Buffer.from('old grid\r\n', 'utf8'));
  await flushTurn();
  assert.ok(screenModel.snapshot('sm-resize', 80, 24));
  // Feed at a new size: the old cells are for the wrong grid, so the model must not serve them.
  screenModel.feed('sm-resize', 40, 10, Buffer.from('new grid', 'utf8'));
  await flushTurn();
  assert.equal(screenModel.snapshot('sm-resize', 80, 24), null, 'the old-geometry screen must not be served');
  assert.ok(screenModel.snapshot('sm-resize', 40, 10), 'the rebuilt model serves at the new geometry');
  const model = await render(Buffer.from(screenModel.snapshot('sm-resize', 40, 10), 'utf8'));
  assert.ok(model.rows[0].includes('new grid'));
  screenModel.dispose('sm-resize');
});

// The unit tests above prove the MODEL is honest. This last one is the end-to-end claim: on the real
// reattach path, the relay sends the compact snapshot instead of muxd's whole byte log. One viewer pays
// the cold log (muxd's replay); a second viewer attaching to the now-warm relay must still see the same
// screen, but the bytes the relay puts on the wire for it must be the snapshot, not the log again.
test('reattach paints a compact snapshot, not the byte log, over the real host link', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(async () => h.stop());
  const session = { name: 'sm-live', alive: true, created: 1000, lastOut: 1000, cols: COLS, rows: ROWS, hasCommand: false, shellOnly: true, ready: true, kind: 'shell', sessionId: '', aliases: [] };
  const host = await h.connectHost([session]);
  t.after(() => host.close());

  // A big log whose SCREEN is one line. 3000 colour lines fill the byte ring; the last line is what stays.
  let log = '';
  for (let i = 0; i < 3000; i++) log += `\x1b[33mlog line ${String(i).padStart(5, '0')}\x1b[0m\r\n`;
  log += 'THE_VISIBLE_SCREEN_LINE\r\n';
  const logBytes = Buffer.byteLength(log, 'utf8');
  assert.ok(logBytes > 64 * 1024, `the byte log must be large to make the point, got ${logBytes}`);

  // First attach: the log is what the FIRST viewer gets (the model is cold), and feeding it warms the model.
  const first = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=sm-live&cols=${COLS}&rows=${ROWS}`);
  await once(first, 'open');
  const sbOne = await host.waitFor(m => m.t === 'sb' && m.s === 'sm-live', 'first scrollback request');
  const firstPainted = waitForWsText(first, /THE_VISIBLE_SCREEN_LINE/, 'first viewer painted');
  host.sendScrollback('sm-live', log, sbOne);
  assert.match(await firstPainted, /THE_VISIBLE_SCREEN_LINE/);
  await new Promise(r => setTimeout(r, 80)); // let xterm-headless finish parsing before the second attach

  // Second attach while the first is still open, so the session state (and its warm model) survives.
  const second = new WebSocket(`ws://127.0.0.1:${h.port}/ws?session=sm-live&cols=${COLS}&rows=${ROWS}`);
  await once(second, 'open');
  await waitFor(
    () => host.messages.filter(m => m.t === 'sb' && m.s === 'sm-live').length === 2,
    'second scrollback request',
  );
  const reqTwo = [...host.messages].reverse().find(m => m.t === 'sb' && m.s === 'sm-live');

  let wire = 0;
  const onSecond = raw => { wire += Buffer.byteLength(raw.toString(), 'utf8'); };
  second.on('message', onSecond);
  const secondPainted = waitForWsText(second, /THE_VISIBLE_SCREEN_LINE/, 'second viewer painted from the snapshot');
  host.sendScrollback('sm-live', log, reqTwo);
  assert.match(await secondPainted, /THE_VISIBLE_SCREEN_LINE/);
  second.off('message', onSecond);

  // The screen came through, and it cost a fraction of the log. Generous bound: the snapshot carries a
  // bounded slice of history, so require well under half the log rather than a single screen's worth.
  assert.ok(wire > 0, 'the second viewer must have received bytes');
  assert.ok(wire < logBytes / 2,
    `reattach must serve the compact snapshot, not the byte log: wire=${wire}, log=${logBytes}`);
  first.close();
  second.close();
});
