#!/usr/bin/env node
// s5_oracle.mjs — S5 Part B: ingest two raw ConPTY streams through the SAME @xterm/headless oracle
// and report whether the final visible screens are identical.
//
// The oracle config is copied from muxd/spike/vt-fidelity/harness.mjs: windowsPty conpty at build
// 26200 and reflowCursorLine, because that is the config the relay terminal runs under. Reading with a
// different config would compare two different emulators and prove nothing.
//
// Input: JSON on stdin {direct: <b64>, teed: <b64>} plus --cols/--rows.
// Output: JSON {identical, diffLines, direct:{screen,altScreen,cursor}, teed:{...}}.

import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = join(HERE, '..', '..', '..');
const require = createRequire(join(ROOT, 'relay', 'package.json'));
const { Terminal } = require('@xterm/headless');

const SCROLLBACK = 5000;

function argOf(flag, dflt) {
  const i = process.argv.indexOf(flag);
  return i >= 0 ? process.argv[i + 1] : dflt;
}
const COLS = parseInt(argOf('--cols', '140'), 10);
const ROWS = parseInt(argOf('--rows', '40'), 10);

function newTerm() {
  return new Terminal({
    cols: COLS, rows: ROWS,
    allowProposedApi: true,
    scrollback: SCROLLBACK,
    windowsPty: { backend: 'conpty', buildNumber: 26200 },
    reflowCursorLine: true,
  });
}

function readScreen(term) {
  const b = term.buffer.active;
  const top = b.baseY;
  const lines = [];
  for (let i = 0; i < ROWS; i++) {
    const line = b.getLine(top + i);
    lines.push((line ? line.translateToString(false) : '').padEnd(term.cols).slice(0, term.cols));
  }
  return { screen: lines, cursor: { x: b.cursorX, y: b.cursorY }, altScreen: b.type === 'alternate' };
}

// One bulk write, then one completion callback: chunking would measure the event loop, and the parse
// result is the same either way.
function ingest(bytes) {
  return new Promise((res) => {
    const t = newTerm();
    t.write(bytes, () => res(readScreen(t)));
  });
}

let input = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', (c) => { input += c; });
process.stdin.on('end', async () => {
  const { direct, teed } = JSON.parse(input);
  const d = await ingest(Buffer.from(direct, 'base64'));
  const t = await ingest(Buffer.from(teed, 'base64'));
  const diff = [];
  for (let i = 0; i < ROWS; i++) {
    if (d.screen[i] !== t.screen[i]) diff.push({ row: i, direct: d.screen[i], teed: t.screen[i] });
  }
  const identical = diff.length === 0 && d.altScreen === t.altScreen
    && d.cursor.x === t.cursor.x && d.cursor.y === t.cursor.y;
  process.stdout.write(JSON.stringify({
    identical, diffLines: diff.length, diff: diff.slice(0, 10),
    direct: { altScreen: d.altScreen, cursor: d.cursor },
    teed: { altScreen: t.altScreen, cursor: t.cursor },
  }));
});
