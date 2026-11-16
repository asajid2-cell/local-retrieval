'use strict';
// The gateway's leaf carousel, as it really is.
//
// Read from the gateway itself (LeafWorkspaceDialog.tsx / LeafCarousel):
//   Box paddingX={2} gap={1}:  ‹  [item W][item W]...  ›  All N
//   W = min(20, max(8, columns - 14))
//   item = "<number> <name>" + (state ? " · working|approval|failed" : "")
//   the CURRENT leaf is drawn inverse.
//
// The old parser split on 2+ spaces, so an item that filled its 20-column box was separated from its
// neighbour by a single gap and the two merged into one chip - a tap then landed on the wrong leaf. These
// tests pin the fixed-width parse, the exact tap column, and the inverse-video active leaf.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { RelayHarness, freePort, launchBrowser } = require('./harness');

const INSTALL = 'npm install --save-dev playwright --package-lock=false\nnpx playwright install chromium';

function loadThreads() {
  const src = fs.readFileSync(path.join(__dirname, '..', 'public', 'threads.js'), 'utf8');
  const sandbox = {}; sandbox.window = sandbox;
  vm.createContext(sandbox);
  vm.runInContext(src, sandbox);
  return sandbox.MuxThreads;
}

const COLS = 209, W = Math.min(20, Math.max(8, COLS - 14));
const pad = s => (s + ' '.repeat(W)).slice(0, W);
function bar(items, all = items.length) {
  // paddingX 2, then ‹, a gap, the items each W wide separated by one gap column, then › and "All N".
  return '  \u2039 ' + items.map(pad).join(' ') + ' \u203a All ' + all;
}

test('a carousel row yields one leaf per fixed-width item, with exact columns', () => {
  const T = loadThreads();
  const row = { i: 50, text: bar(['Leaf 51: NEOGATE', '22 GatewayHead', '86 hydrogen', '85 chron-soak']) };
  const res = T.inspectScreen([row], 'normal', COLS);
  const lb = res.chrome.leafBar;
  assert.ok(lb, 'the carousel must parse as a leaf bar');
  assert.deepEqual(Array.from(lb.leaves, l => l.label),
    ['Leaf 51: NEOGATE', '22 GatewayHead', '86 hydrogen', '85 chron-soak']);
  const open = row.text.indexOf('\u2039');
  assert.equal(lb.leaves[0].col, open + 2, 'the first item starts one gap column after the arrow');
  for (let i = 1; i < lb.leaves.length; i++) {
    assert.equal(lb.leaves[i].col, lb.leaves[i - 1].col + W + 1,
      `item ${i} must start exactly W+1 columns after item ${i - 1}`);
    // The column must point at the label in the raw row - that is what a tap uses.
    assert.equal(row.text.slice(lb.leaves[i].col, lb.leaves[i].col + lb.leaves[i].label.length), lb.leaves[i].label);
  }
  assert.equal(lb.leaves[1].number, 22, 'the leading number is kept separate from the name');
  assert.equal(lb.leaves[1].name, 'GatewayHead');
  assert.equal(lb.arrows.prev.col, open, 'the left arrow cell is reported');
  assert.equal(lb.arrows.next.col, row.text.lastIndexOf('\u203a'), 'and the right arrow cell');
});

test('the active leaf is the one drawn inverse, not the first label that looks like a leaf', () => {
  const T = loadThreads();
  const row = { i: 50, text: bar(['Leaf 51: NEOGATE', '22 GatewayHead', '86 hydrogen']) };
  const open = row.text.indexOf('\u2039');
  const secondStart = open + 2 + W + 1;
  row.inv = [[secondStart, secondStart + W]];      // item 1 is reverse-video
  const lb = T.inspectScreen([row], 'normal', COLS).chrome.leafBar;
  assert.deepEqual(Array.from(lb.leaves, l => l.active), [false, true, false]);
  // Without spans nothing is guessed: a guessed highlight renders every chip as "you are here".
  const noSpans = T.inspectScreen([{ i: 50, text: row.text }], 'normal', COLS).chrome.leafBar;
  assert.deepEqual(Array.from(noSpans.leaves, l => l.active), [false, false, false]);
});

test('a state suffix is split off the name without changing the tap column', () => {
  const T = loadThreads();
  const row = { i: 50, text: bar(['Leaf 51: NEOGATE', '22 gw \u00b7 working']) };
  const lb = T.inspectScreen([row], 'normal', COLS).chrome.leafBar;
  assert.equal(lb.leaves[1].number, 22);
  assert.equal(lb.leaves[1].name, 'gw');
  assert.equal(lb.leaves[1].state, 'working');
});

// ---- the tap, driven at the real page ------------------------------------------------------------------
let browser = null, browserError = null;
test.before(async () => { try { browser = await launchBrowser(); } catch (e) { browserError = e; } });
test.after(async () => { if (browser) await browser.close(); });

test('a leaf chip tap sends one SGR click at that leaf\'s cell', async t => {
  if (!browser) { t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`); return; }
  const harness = new RelayHarness();
  let host = null, context = null;
  try {
    harness.fixedPort = await freePort();
    harness.env.ALLOWED_WS_ORIGINS = `http://127.0.0.1:${harness.fixedPort}`;
    await harness.start();
    host = await harness.connectHost([{
      name: 'alpha', alive: true, created: Date.now(), cols: 120, rows: 40,
      owner: 'muxtee', gridOwner: 'local', hasCommand: false, shellOnly: true, ready: true,
    }]);
    // A wide-enough phone-ish viewport that the carousel fits on one row.
    context = await browser.newContext({ viewport: { width: 900, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true });
    context.setDefaultTimeout(15000);
    const page = await context.newPage();
    page.on('pageerror', e => console.log('PAGE ERROR:', e.message));
    await page.goto(`http://127.0.0.1:${harness.port}/?s=alpha`, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => window._sessions && window._sessions.length > 0);
    await page.evaluate(() => connect('alpha'));
    await page.waitForFunction(() => !!document.querySelector('#term .xterm-helper-textarea'));
    await page.waitForTimeout(400);
    await page.evaluate(() => setMobileView('threads'));
    await page.waitForTimeout(300);

    const geo = await page.evaluate(() => ({ rows: term.rows, cols: term.cols }));
    const W = Math.min(20, Math.max(8, geo.cols - 14));
    const items = ['Leaf 51: NEOGATE', '22 GatewayHead', '86 hydrogen'].map(s => (s + ' '.repeat(W)).slice(0, W));
    const barText = '  \u2039 ' + items.join(' ') + ' \u203a All 3';
    // Arm the mouse the way a real TUI does, then paint the screen with the middle leaf inverse.
    host.sendOutput('alpha', '\x1b[?1000h\x1b[?1006h\x1b[2J\x1b[H');
    let out = '';
    for (let r = 1; r <= geo.rows; r++) out += `\x1b[${r};1H`;
    out = '\x1b[2J\x1b[H';
    out += `\x1b[${geo.rows};1H` + barText.slice(0, geo.cols);
    host.sendOutput('alpha', out);
    await page.waitForTimeout(700);

    const leaves = await page.evaluate(() => [...document.querySelectorAll('#threadnav .tchip.leaf')].map(b => b.textContent.trim()));
    assert.ok(leaves.length >= 2, `leaf chips must be one per item, got ${JSON.stringify(leaves)}`);

    const mark = host.messages.length;
    await page.locator('#threadnav .tchip.leaf').nth(2).click();
    await page.waitForTimeout(400);
    const sent = host.messages.slice(mark)
      .filter(m => m && m.t === 'i')
      .map(m => Buffer.from(String(m.d || ''), 'base64').toString('utf8')).join('');
    const m = /\x1b\[<0;(\d+);(\d+)M\x1b\[<0;(\d+);(\d+)m/.exec(sent);
    assert.ok(m, `expected an SGR click pair, got ${JSON.stringify(sent)}`);
    assert.equal(Number(m[1]), Number(m[3]), 'press and release at the same column');
    assert.equal(Number(m[2]), Number(m[4]), 'and the same row');
    // The clicked cell must be inside the THIRD item's box, not a neighbour's.
    const open = barText.indexOf('\u2039');
    const thirdCol = open + 2 + 2 * (W + 1);
    const gotCol = Number(m[1]) - 1;      // buffer is 0-based, SGR is 1-based
    assert.ok(gotCol >= thirdCol && gotCol < thirdCol + W,
      `click column ${gotCol} must fall inside the third item [${thirdCol}, ${thirdCol + W})`);
    assert.equal(await page.evaluate(() => document.querySelector('#app').className.includes('mobile-view-threads')), true,
      'a leaf tap must not navigate away from the conversation');
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
});
