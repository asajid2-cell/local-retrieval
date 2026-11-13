'use strict';
// Phone typing, driven through a REAL browser's input pipeline on a phone viewport.
//
// The report: "typing on mobile is still broken. Every word when we do a space bar it glitches and backspaces,
// it ruins all typing momentum and makes the sentences come out like this" - i.e. the space after a word
// deleted the word. The delta forwarder diffs the helper textarea against a cached baseline, so ANY event that
// leaves the field shorter than the baseline (a reset, a lost IME commit, a keyboard that re-creates the field)
// was sent as that many DEL characters.
//
// These cases drive the shapes a soft keyboard really emits and assert the BYTES THE HOST RECEIVES, because
// that is the only place the defect shows: the page's own state looks fine either way.
const test = require('node:test');
const assert = require('node:assert/strict');
const { RelayHarness, freePort, launchBrowser, waitFor } = require('./harness');

const INSTALL = 'npm install --save-dev playwright --package-lock=false\nnpx playwright install chromium';
const SESSION = 'phone';
const TEST_TIMEOUT_MS = 15000;

let browser = null, browserError = null;
test.before(async () => {
  try { browser = await launchBrowser(); } catch (e) { browserError = e; }
});
test.after(async () => { if (browser) await browser.close(); });

async function withPhone(t, fn) {
  if (!browser) { t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`); return; }
  const harness = new RelayHarness();
  let host = null, context = null;
  try {
    // The harness's own start() already sets the env the relay needs (command bridge token, fixture dirs);
    // rolling it by hand produced a relay the host socket refused.
    harness.fixedPort = await freePort();
    harness.env.ALLOWED_WS_ORIGINS = `http://127.0.0.1:${harness.fixedPort}`;
    await harness.start();
    host = await harness.connectHost([{
      name: SESSION, alive: true, created: Date.now(), cols: 200, rows: 50,
      hasCommand: false, shellOnly: true, ready: true,
    }]);
    context = await browser.newContext({
      viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, isMobile: true, hasTouch: true, locale: 'en-US',
    });
    context.setDefaultTimeout(TEST_TIMEOUT_MS);
    const page = await context.newPage();
    await page.goto(`http://127.0.0.1:${harness.port}/?s=${SESSION}`, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => window._sessions && window._sessions.some(s => s.name === 'phone'));
    await page.evaluate(() => connect('phone'));
    await page.waitForFunction(() => !!document.querySelector('#term .xterm-helper-textarea'));
    await page.waitForTimeout(400);
    const cdp = await context.newCDPSession(page);
    await (await page.$('#term .xterm-helper-textarea')).focus();
    await fn({ harness, host, page, cdp });
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
}

// Everything the host received on the input seam since `mark`, as plain text.
function inputSince(host, mark) {
  return host.messages.slice(mark)
    .filter(m => m && m.t === 'i')
    .map(m => { try { return Buffer.from(String(m.d || ''), 'base64').toString('utf8'); } catch (e) { return ''; } })
    .join('');
}

// The Android soft-keyboard path: keyCode 229 + text on keydown, which is what a real phone emits.
async function androidType(cdp, text) {
  for (const ch of text) {
    await cdp.send('Input.dispatchKeyEvent', { type: 'keyDown', windowsVirtualKeyCode: 229, nativeVirtualKeyCode: 229, key: 'Unidentified', text: ch, unmodifiedText: ch });
    await cdp.send('Input.dispatchKeyEvent', { type: 'keyUp', windowsVirtualKeyCode: 229, nativeVirtualKeyCode: 229, key: 'Unidentified' });
  }
}

test('a word and its trailing space arrive as typed, with no backspaces', async t => {
  await withPhone(t, async ({ host, page, cdp }) => {
    const mark = host.messages.length;
    await androidType(cdp, 'make the ');
    await page.waitForTimeout(250);
    const sent = inputSince(host, mark);
    assert.equal(sent, 'make the ', `the space after a word must not delete it: got ${JSON.stringify(sent)}`);
    assert.ok(!sent.includes('\x7f'), 'nothing may be deleted while the user only inserted');
  });
});

test('typing survives the session echoing each character back', async t => {
  await withPhone(t, async ({ host, page, cdp }) => {
    const mark = host.messages.length;
    for (const ch of 'make the ') {
      await cdp.send('Input.dispatchKeyEvent', { type: 'keyDown', windowsVirtualKeyCode: 229, nativeVirtualKeyCode: 229, key: 'Unidentified', text: ch, unmodifiedText: ch });
      await cdp.send('Input.dispatchKeyEvent', { type: 'keyUp', windowsVirtualKeyCode: 229, nativeVirtualKeyCode: 229, key: 'Unidentified' });
      await page.waitForTimeout(25);
      host.sendOutput(SESSION, ch);        // the shell echoes it
      await page.waitForTimeout(70);
    }
    await page.waitForTimeout(200);
    const sent = inputSince(host, mark);
    assert.equal(sent, 'make the ', `the echo must not disturb the next keystroke: got ${JSON.stringify(sent)}`);
  });
});

test('a stale baseline is re-anchored instead of deleting the word', async t => {
  await withPhone(t, async ({ host, page }) => {
    const sent = await page.evaluate(async () => {
      const h = document.querySelector('#term .xterm-helper-textarea');
      // The exact failure: the baseline holds "make " but the field was reset and now holds one space.
      _taLast = 'make ';
      const before = window.__muxInputLog ? window.__muxInputLog.length : 0;
      h.value = ' ';
      taFlushDelta(h, { inputType: 'insertText', data: ' ' });
      return { last: _taLast, value: h.value };
    });
    assert.equal(sent.value, ' ');
    await page.waitForTimeout(200);
    const got = inputSince(host, 0);
    assert.equal(got, ' ', `a reset field must not send DELs: got ${JSON.stringify(got)}`);
  });
});

test('a real backspace still sends exactly one DEL', async t => {
  await withPhone(t, async ({ host, page, cdp }) => {
    await androidType(cdp, 'ab');
    await page.waitForTimeout(200);
    const mark = host.messages.length;
    await cdp.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', windowsVirtualKeyCode: 8, nativeVirtualKeyCode: 8, key: 'Backspace', code: 'Backspace' });
    await cdp.send('Input.dispatchKeyEvent', { type: 'keyUp', windowsVirtualKeyCode: 8, nativeVirtualKeyCode: 8, key: 'Backspace', code: 'Backspace' });
    await page.waitForTimeout(250);
    assert.equal(inputSince(host, mark), '\x7f', 'one backspace is one DEL');
  });
});

test('focusing the terminal does not zoom the page on a phone', async t => {
  await withPhone(t, async ({ page }) => {
    // iOS auto-zooms a focused field under 16px; the helper textarea is xterm's hidden input.
    const font = await page.evaluate(() => parseFloat(getComputedStyle(document.querySelector('#term .xterm-helper-textarea')).fontSize));
    assert.ok(font >= 16, `the helper textarea must be at least 16px to avoid auto-zoom, got ${font}`);
    // And double-tap zoom is off, while a deliberate pinch is still allowed.
    for (const sel of ['#app', '#term']) {
      const touch = await page.evaluate(s => getComputedStyle(document.querySelector(s)).touchAction, sel);
      assert.equal(touch, 'manipulation', `${sel} must not double-tap zoom`);
    }
  });
});

test('a display-only tap leaves the keyboard closed', async t => {
  await withPhone(t, async ({ page }) => {
    await page.evaluate(() => document.activeElement && document.activeElement.blur && document.activeElement.blur());
    await page.waitForTimeout(150);
    await page.evaluate(() => { const e = document.querySelector('#statusmeta'); if (e) e.click(); });
    await page.waitForTimeout(400);
    const focused = await page.evaluate(() => !!(document.activeElement && /xterm-helper-textarea/.test(document.activeElement.className || '')));
    assert.equal(focused, false, 'tapping the size readout must not put the keyboard up');
  });
});

test('every bottom-nav destination has a tappable, on-screen target', async t => {
  await withPhone(t, async ({ page }) => {
    const nav = await page.evaluate(() => {
      const n = document.querySelector('#mobileNav');
      const nr = n.getBoundingClientRect();
      return {
        scrolls: n.scrollWidth > n.clientWidth + 1,
        items: [...n.querySelectorAll('button,a')].map(b => {
          const r = b.getBoundingClientRect();
          const el = document.elementFromPoint(Math.round(r.left + r.width / 2), Math.round(r.top + r.height / 2));
          return { label: (b.getAttribute('aria-label') || '').trim(),
            onScreen: r.left >= nr.left - 1 && r.right <= nr.right + 1,
            width: Math.round(r.width),
            hitsSelf: !!(el && (el === b || b.contains(el))) };
        }),
      };
    });
    assert.equal(nav.scrolls, false, 'the nav must not scroll: an item past the edge is an unreachable item');
    for (const it of nav.items) {
      assert.ok(it.onScreen, `${it.label} must be on screen`);
      assert.ok(it.width >= 36, `${it.label} must have a tappable target, got ${it.width}px`);
      assert.ok(it.hitsSelf, `${it.label} must be the element under its own centre`);
    }
    // The keys route specifically, because "there is no keys button" was the report.
    assert.ok(nav.items.some(i => /keys/i.test(i.label)), 'the nav has a Keys destination');
  });
});

test('quick keys send their sequences without opening the keyboard', async t => {
  await withPhone(t, async ({ host, page }) => {
    await page.evaluate(() => document.activeElement && document.activeElement.blur && document.activeElement.blur());
    const mark = host.messages.length;
    await page.evaluate(() => {
      document.querySelector('#quickkeys [data-qk="up"]').click();
      document.querySelector('#quickkeys [data-qk="enter"]').click();
    });
    await page.waitForTimeout(250);
    assert.equal(inputSince(host, mark), '\x1b[A\r', 'arrow-up then Enter, exactly the Keys view bytes');
    const focused = await page.evaluate(() => !!(document.activeElement && /xterm-helper-textarea/.test(document.activeElement.className || '')));
    assert.equal(focused, false, 'a quick key must not leave the keyboard up over the screen');
  });
});

// The bar is ~60px of a 390x844 screen and it is exactly what covers an app's own bottom menus, so the
// phone needs a way to put it away. The toggle lives in the top bar (which stays) and must actually give
// the pane the rows back - dropping the track, not just hiding the element - and it must be remembered.
test('the bottom bar can be hidden from the top bar, and the pane gets the rows', async t => {
  await withPhone(t, async ({ page }) => {
    const before = await page.evaluate(() => {
      const nav = document.querySelector('#mobileNav');
      const b = document.querySelector('#navtoggle');
      return {
        shown: !!nav && getComputedStyle(nav).display !== 'none',
        btnShown: !!b && getComputedStyle(b).display !== 'none',
        paneH: document.querySelector('#term').getBoundingClientRect().height,
        rows: term.rows,
        pressed: b && b.getAttribute('aria-pressed'),
      };
    });
    assert.equal(before.shown, true, 'the bar starts visible');
    assert.equal(before.btnShown, true, 'and the phone has the button that hides it');
    assert.equal(before.pressed, 'false');

    await page.click('#navtoggle');
    await page.waitForTimeout(400);
    const after = await page.evaluate(() => {
      const nav = document.querySelector('#mobileNav');
      const b = document.querySelector('#navtoggle');
      return {
        shown: !!nav && getComputedStyle(nav).display !== 'none',
        paneH: document.querySelector('#term').getBoundingClientRect().height,
        rows: term.rows,
        pressed: b && b.getAttribute('aria-pressed'),
        title: b && b.title,
        stored: localStorage.getItem('mux_nav_hidden'),
      };
    });
    assert.equal(after.shown, false, 'the bar is gone');
    assert.equal(after.pressed, 'true', 'and the button says so');
    assert.match(after.title, /show/i, 'the title offers the way back');
    assert.ok(after.paneH > before.paneH + 20, `the pane keeps the space: ${before.paneH} -> ${after.paneH}`);
    assert.ok(after.rows >= before.rows, `and the grid re-fits into it: ${before.rows} -> ${after.rows}`);
    assert.equal(JSON.parse(after.stored), true, 'the choice is remembered across loads');

    await page.click('#navtoggle');
    await page.waitForTimeout(400);
    const back = await page.evaluate(() => ({
      shown: getComputedStyle(document.querySelector('#mobileNav')).display !== 'none',
      paneH: document.querySelector('#term').getBoundingClientRect().height,
    }));
    assert.equal(back.shown, true, 'and tapping again brings it back');
    assert.ok(Math.abs(back.paneH - before.paneH) < 2, 'with the pane back where it started');
  });
});
