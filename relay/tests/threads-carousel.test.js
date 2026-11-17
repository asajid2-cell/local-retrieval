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

// A ROW THAT MENTIONS A LEAF IS NOT A LEAF BAR. Measured live on tab-2470 (2026-11-16): a message listing
// session ids - `(fd94f334…), "Leaf 3: tv" (…), "web; deep5" (…)` - parsed as a bar whose single "leaf"
// was the whole sentence, so the bar drew one chip naming a paragraph and tapping it clicked a
// conversation cell. The rule is structural: both arrows on the row, nothing after the right one but an
// optional count, and every item shaped like a leaf item rather than a sentence.
test('prose that mentions a leaf is never a leaf bar', () => {
  const T = loadThreads();
  const prose = [
    '  (fcb8721c-f3d4-4a2d-bf6e-e2ffceabbbae), "Claude Code" (fd2d35ed-fd2b-49a3-b203-1d2289e6b3a1), "Leaf 3: tv" (fd94f334-df5f-4a64-af42-29065eada08f),',
    '  The Leaf 3: tv session is fine, and Leaf 4 too.',
    '  Leaf 12: soak   ran for 40m and finished clean',
    '  ‹ 22 name › means the second leaf in the carousel',
  ];
  for (const text of prose) {
    for (const cols of [209, undefined]) {
      const lb = T.inspectScreen([{ i: 40, text }], 'normal', cols).chrome.leafBar;
    assert.equal(lb, null, `cols=${cols} must not read a bar out of ${JSON.stringify(text)}`);
    }
  }
  // The shapes that ARE bars keep parsing, including a one-item carousel and the spaced/pipe forms.
  const ok = (text, cols, expected) => {
    const lb = T.inspectScreen([{ i: 40, text }], 'normal', cols).chrome.leafBar;
    assert.ok(lb, `must parse: ${JSON.stringify(text)}`);
    assert.deepEqual(Array.from(lb.leaves, l => l.label), expected);
  };
  ok('  ‹ Leaf 4: lattike      41 webbrow           › All 2', 209, ['Leaf 4: lattike', '41 webbrow']);
  ok('  ‹ sndo                 › All 1', 209, ['sndo']);
  ok('  < Leaf 1: alpha  |  Leaf 2: beta  |  Leaf 3: gamma >   All 3', undefined,
    ['Leaf 1: alpha', 'Leaf 2: beta', 'Leaf 3: gamma']);
  ok('  ‹ Leaf 3: tv           23 htv-lanes         24 speed-policy      › All 3', undefined,
    ['Leaf 3: tv', '23 htv-lanes', '24 speed-policy']);
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

// A TEED LOCAL TAB NEVER DELIVERS THE APP'S DECSET. muxd reads that console as a cell grid, so the app's
// "report the mouse to me" goes to the console and never appears in a byte stream the guard can scan. Live
// proof (tab-2470, 2026-11-16): guard tracking:false, wheel dead, and the gateway's carousel plainly on
// screen. Every leaf tap was refused with "This app has not armed pointer input" - the chips were drawn,
// so the control looked present and did nothing.
//
// The carousel row itself is the evidence that beats the missing DECSET: only an interactive app paints
// `‹ item item › All N`, and it is the same row whose cells the chips name. A cell that came from a
// parsed carousel of this session's own screen must therefore be clickable with no arm.
test('a leaf tap works on a pane whose app never delivered a DECSET, because the carousel proves it', async t => {
  if (!browser) { t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`); return; }
  const harness = new RelayHarness();
  let host = null, context = null;
  try {
    harness.fixedPort = await freePort();
    harness.env.ALLOWED_WS_ORIGINS = `http://127.0.0.1:${harness.fixedPort}`;
    await harness.start();
    host = await harness.connectHost([{
      name: 'teed', alive: true, created: Date.now(), cols: 120, rows: 40,
      owner: 'muxtee', gridOwner: 'local', hasCommand: false, shellOnly: true, ready: true,
    }]);
    context = await browser.newContext({ viewport: { width: 900, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true });
    context.setDefaultTimeout(15000);
    const page = await context.newPage();
    page.on('pageerror', e => console.log('PAGE ERROR:', e.message));
    await page.goto(`http://127.0.0.1:${harness.port}/?s=teed`, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => window._sessions && window._sessions.length > 0);
    await page.evaluate(() => connect('teed'));
    await page.waitForFunction(() => !!document.querySelector('#term .xterm-helper-textarea'));
    await page.waitForTimeout(400);
    await page.evaluate(() => setMobileView('threads'));
    await page.waitForTimeout(300);

    const geo = await page.evaluate(() => ({ rows: term.rows, cols: term.cols }));
    const W = Math.min(20, Math.max(8, geo.cols - 14));
    const items = ['Leaf 51: NEOGATE', '22 GatewayHead', '86 hydrogen'].map(s => (s + ' '.repeat(W)).slice(0, W));
    const barText = '  \u2039 ' + items.join(' ') + ' \u203a All 3';
    // Deliberately NO ?1000h / ?1006h: this pane behaves like a teed console.
    host.sendOutput('teed', '\x1b[2J\x1b[H' + `\x1b[${geo.rows};1H` + barText.slice(0, geo.cols));
    await page.waitForTimeout(700);

    const guard = await page.evaluate(() => ({ takesMouse: appTakesMouse(), tracking: window.__muxMouseGuard.stats() }));
    assert.equal(guard.takesMouse, false, `this fixture must not be armed, guard=${JSON.stringify(guard.tracking)}`);

    const mark = host.messages.length;
    await page.locator('#threadnav .tchip.leaf').nth(1).click();
    await page.waitForTimeout(500);
    const sent = host.messages.slice(mark)
      .filter(m => m && m.t === 'i')
      .map(m => Buffer.from(String(m.d || ''), 'base64').toString('utf8')).join('');
    const m = /\x1b\[<0;(\d+);(\d+)M\x1b\[<0;(\d+);(\d+)m/.exec(sent);
    assert.ok(m, `a proven carousel cell must still send its click; got ${JSON.stringify(sent)}`);
    const open = barText.indexOf('\u2039');
    const secondCol = open + 2 + (W + 1);
    const gotCol = Number(m[1]) - 1;
    assert.ok(gotCol >= secondCol && gotCol < secondCol + W,
      `click column ${gotCol} must fall inside the second item [${secondCol}, ${secondCol + W})`);
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
});

// THE PAGE AND ITS SEGMENTER ARE ONE BUILD. threads.js decides which cell a chip taps, so a page that
// kept an older parser after a deploy drew chips whose targets were not the leaves they named - the
// "it switches the wrong item" report. Two things make that impossible: the tag asks for
// threads.js?v=<build>, and leaf chips are refused outright unless the copy that arrived declares the
// same build the page does.
test('a segmenter from another build may not draw leaf chips', () => {
  const fs = require('node:fs');
  const path = require('node:path');
  const page = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');
  const seg = fs.readFileSync(path.join(__dirname, '..', 'public', 'threads.js'), 'utf8');
  const pageBuild = (/window\.__muxBuild\s*=\s*'([^']+)'/.exec(page) || [])[1];
  const segBuild = (/var BUILD = '([^']+)'/.exec(seg) || [])[1];
  assert.ok(pageBuild, 'the page must declare a build stamp');
  assert.ok(segBuild, 'the segmenter must declare a build stamp');
  assert.equal(segBuild, pageBuild,
    'the page and threads.js must ship as the same build; bump both when either changes');
  assert.ok(/threads\.js\?v=/.test(page),
    'threads.js must be requested under the build name, so a cached copy cannot outlive a deploy');
  assert.ok(/segmenterMatches\(\)/.test(page),
    'the page must verify the segmenter it loaded before trusting its leaf parse');
  assert.ok(/BUILD: BUILD/.test(seg) && /carousel: true/.test(seg),
    'the segmenter must advertise its build and its carousel capability');
});
