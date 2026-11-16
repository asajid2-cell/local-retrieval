'use strict';
// The phone terminal's RENDER BOUNDARY.
//
// The report: "our bottom bar thing cuts off the gateway leafs - the leafs need to go above the relay UI."
// The relay's own chrome is a grid row, so it cannot overlay the terminal; what was happening is that the
// xterm pane overflowed its own box, so the app's bottom rows (its status line and its leaf bar) fell past
// the pane's bottom edge where overflow:hidden ate them. These tests pin the geometry, not a screenshot.
//
// The cause, measured: #quickkeys was a CHILD of <main id="term">, and every measurement of the pane -
// FitAddon's proposal, nativeDims, fitTabOwnedGrid, alignTerminalSurface - read clientHeight that included
// the 42px strip while the xterm itself started 46px down. It is now a grid row of its own.
const test = require('node:test');
const assert = require('node:assert/strict');
const { RelayHarness, freePort, launchBrowser } = require('./harness');

const INSTALL = 'npm install --save-dev playwright --package-lock=false\nnpx playwright install chromium';

let browser = null, browserError = null;
test.before(async () => {
  try { browser = await launchBrowser(); } catch (e) { browserError = e; }
});
test.after(async () => { if (browser) await browser.close(); });

async function withPhone(t, opts, fn) {
  if (!browser) { t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`); return; }
  const harness = new RelayHarness();
  let host = null, context = null;
  try {
    harness.fixedPort = await freePort();
    harness.env.ALLOWED_WS_ORIGINS = `http://127.0.0.1:${harness.fixedPort}`;
    await harness.start();
    host = await harness.connectHost([{
      name: 'phone', alive: true, created: Date.now(), cols: opts.cols, rows: opts.rows,
      owner: 'muxtee', gridOwner: 'local', hasCommand: false, shellOnly: true, ready: true,
    }]);
    context = await browser.newContext({
      viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, isMobile: true, hasTouch: true, locale: 'en-US',
    });
    context.setDefaultTimeout(15000);
    const page = await context.newPage();
    page.on('pageerror', e => console.log('PAGE ERROR:', e.message));
    await page.goto(`http://127.0.0.1:${harness.port}/?s=phone`, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => window._sessions && window._sessions.some(s => s.name === 'phone'));
    await page.evaluate(() => connect('phone'));
    await page.waitForFunction(() => !!document.querySelector('#term .xterm-helper-textarea'));
    await page.waitForTimeout(500);
    await page.evaluate(() => setMobileView('terminal'));
    await page.waitForTimeout(400);
    await paintLeafScreen(page, host);
    await fn({ host, page });
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
}

// Paint a full screen whose LAST two rows are the app's status line and its leaf bar, exactly where a real
// TUI draws them. Painted at the rows the PAGE actually holds: the grid is decided from the viewer's
// viewport, so a hard-coded row count lands outside it and the screen is simply blank.
async function paintLeafScreen(page, host) {
  const geo = await page.evaluate(() => ({ rows: term.rows, cols: term.cols }));
  let out = '\x1b[2J\x1b[H';
  for (let r = 1; r <= geo.rows; r++) {
    let text = '';
    if (r === geo.rows - 1) text = '  cd: work   bypass permissions on - usage: 37% left';
    else if (r === geo.rows) text = '  < Leaf 1: alpha  |  Leaf 2: beta >   All 2';
    else if (r > 1) text = '  row ' + r;
    out += `\x1b[${r};1H${String(text).slice(0, geo.cols)}`;
  }
  host.sendOutput('phone', out);
  await page.waitForTimeout(500);
}

async function geometry(page) {
  return await page.evaluate(() => {
    const box = el => { if (!el) return null; const b = el.getBoundingClientRect(); return { top: Math.round(b.top), bottom: Math.round(b.bottom), h: Math.round(b.height) }; };
    const termEl = document.querySelector('#term');
    const scr = termEl.querySelector('.xterm-screen');
    const rowsEls = [...termEl.querySelectorAll('.xterm-rows > div')];
    const last = rowsEls.length ? rowsEls[rowsEls.length - 1] : null;
    return {
      term: box(termEl), screen: box(scr), lastRow: box(last),
      quickkeys: box(document.querySelector('#quickkeys')),
      status: box(document.querySelector('#status')), nav: box(document.querySelector('#mobileNav')),
      classes: termEl.className,
      overflowY: getComputedStyle(termEl).overflowY,
      scrollTop: Math.round(termEl.scrollTop),
      scrollHeight: termEl.scrollHeight, clientHeight: termEl.clientHeight,
      lastRowText: last ? last.textContent.trim() : '',
      quickkeysInTerm: !!termEl.querySelector('#quickkeys'),
    };
  });
}

test('the pane contains the whole painted grid, leaf bar included', async t => {
  await withPhone(t, { cols: 100, rows: 30 }, async ({ page }) => {
    const g = await geometry(page);
    assert.ok(g.screen.bottom <= g.term.bottom + 1,
      `the grid must end inside the pane: screenBottom=${g.screen.bottom} termBottom=${g.term.bottom}`);
    if (g.lastRow) {
      assert.ok(g.lastRow.bottom <= g.term.bottom + 1,
        `the app's last row must be visible: lastRow=${JSON.stringify(g.lastRow)} term=${JSON.stringify(g.term)}`);
    }
    // The row divs are renderer-dependent; the BUFFER is not. The leaf bar must be the last line of the
    // live screen.
    const tail = await page.evaluate(() => {
      const b = term.buffer.active;
      const ln = b.getLine((b.baseY | 0) + term.rows - 1);
      return ln ? ln.translateToString(true).trim() : '';
    });
    assert.match(tail, /Leaf 1: alpha/, `the leaf bar must be the last line of the screen: ${JSON.stringify(tail)}`);
  });
});

test('the quick-keys strip is its own row, above the pane boundary, not inside it', async t => {
  await withPhone(t, { cols: 100, rows: 30 }, async ({ page }) => {
    const g = await geometry(page);
    assert.equal(g.quickkeysInTerm, false, '#quickkeys must not be a child of #term');
    assert.ok(g.quickkeys && g.quickkeys.top >= g.term.bottom - 1,
      `quick keys must sit below the pane: qkTop=${g.quickkeys && g.quickkeys.top} termBottom=${g.term.bottom}`);
    assert.ok(g.status.top >= g.quickkeys.bottom - 1, 'the status strip follows the quick keys');
    assert.ok(g.nav.top >= g.status.bottom - 1, 'the nav follows the status strip');
  });
});

test('hiding the relay bottom bar gives the rows to the pane, not to a clipped grid', async t => {
  await withPhone(t, { cols: 100, rows: 30 }, async ({ page }) => {
    const before = await geometry(page);
    await page.evaluate(() => document.querySelector('#navtoggle').click());
    await page.waitForTimeout(500);
    const after = await geometry(page);
    assert.ok(after.term.h > before.term.h, 'the pane must get taller when the bar is hidden');
    assert.ok(after.screen.bottom <= after.term.bottom + 1,
      `the grid must still end inside the pane: screenBottom=${after.screen.bottom} termBottom=${after.term.bottom}`);
    if (after.lastRow) assert.ok(after.lastRow.bottom <= after.term.bottom + 1, 'the leaf bar must stay visible');
  });
});

test('a grid taller than the pane row-pans so the leaf bar stays above the relay chrome', async t => {
  await withPhone(t, { cols: 100, rows: 80 }, async ({ page }) => {
    const g = await geometry(page);
    assert.match(g.classes, /\browpan\b/, 'a painting taller than the pane must enable row pan');
    assert.equal(g.overflowY, 'auto', 'row pan is the scroll container');
    assert.ok(g.scrollHeight > g.clientHeight, 'the painted grid must be taller than the pane');
    assert.ok(g.scrollHeight - g.scrollTop - g.clientHeight <= 2,
      `the view must be anchored to the bottom: scrollTop=${g.scrollTop} scrollHeight=${g.scrollHeight} clientHeight=${g.clientHeight}`);
    if (g.lastRow) {
      assert.ok(g.lastRow.bottom <= g.term.bottom + 1,
        `the leaf bar must be scrolled into view: lastRowBottom=${g.lastRow.bottom} termBottom=${g.term.bottom}`);
      assert.ok(g.lastRow.bottom >= g.term.top, 'and it must not be scrolled past the top');
      assert.match(g.lastRowText, /Leaf 1: alpha/);
    }
  });
});

test('the quick-keys strip is not shown on views that have no terminal', async t => {
  await withPhone(t, { cols: 100, rows: 30 }, async ({ page }) => {
    for (const view of ['threads', 'sessions', 'fleet', 'keys']) {
      await page.evaluate(v => setMobileView(v), view);
      await page.waitForTimeout(300);
      const shown = await page.evaluate(() => getComputedStyle(document.querySelector('#quickkeys')).display);
      assert.equal(shown, 'none', `#quickkeys must be hidden on the ${view} view`);
    }
  });
});
