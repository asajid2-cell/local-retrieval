'use strict';
// Soak the conversation surface: the shapes a live agent session actually produces, driven at the page, not
// reasoned about. Each case is one way the phone could lose a message, lose the reader's place, or lie about
// what the screen holds.
const test = require('node:test');
const assert = require('node:assert/strict');
const { RelayHarness, freePort, launchBrowser } = require('./harness');

const INSTALL = 'npm install --save-dev playwright --package-lock=false\nnpx playwright install chromium';
const A = 'alpha';
let browser = null, browserError = null;
test.before(async () => { try { browser = await launchBrowser(); } catch (e) { browserError = e; } });
test.after(async () => { if (browser) await browser.close(); });

function paintBottom(lines, rows, cols) {
  const top = Math.max(0, rows - lines.length);
  let out = '\x1b[2J\x1b[H';
  lines.forEach((text, i) => { out += `\x1b[${top + i + 1};1H${String(text).slice(0, cols)}`; });
  return out;
}
async function screen(page, host, lines, pause = 350) {
  const geo = await page.evaluate(() => ({ rows: term.rows, cols: term.cols }));
  host.sendOutput(A, paintBottom(lines, geo.rows, geo.cols));
  await page.waitForTimeout(pause);
}

async function withPhone(t, fn) {
  if (!browser) { t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`); return; }
  const harness = new RelayHarness();
  let host = null, context = null;
  try {
    harness.fixedPort = await freePort();
    harness.env.ALLOWED_WS_ORIGINS = `http://127.0.0.1:${harness.fixedPort}`;
    await harness.start();
    host = await harness.connectHost([{
      name: A, alive: true, created: Date.now(), cols: 100, rows: 30,
      owner: 'muxtee', gridOwner: 'local', hasCommand: false, shellOnly: true, ready: true,
    }]);
    context = await browser.newContext({
      viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, isMobile: true, hasTouch: true, locale: 'en-US',
    });
    context.setDefaultTimeout(15000);
    const page = await context.newPage();
    page.on('pageerror', e => console.log('PAGE ERROR:', e.message));
    await page.goto(`http://127.0.0.1:${harness.port}/?s=${A}`, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => window._sessions && window._sessions.some(s => s.name === 'alpha'));
    await page.evaluate(() => connect('alpha'));
    await page.waitForFunction(() => !!document.querySelector('#term .xterm-helper-textarea'));
    await page.waitForTimeout(400);
    await page.evaluate(() => setMobileView('threads'));
    await page.waitForTimeout(300);
    await fn({ host, page });
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
}

const COMPOSER = ['─'.repeat(60), '❯ ', '─'.repeat(60)];

test('an open thought block survives the message below it streaming', async t => {
  await withPhone(t, async ({ host, page }) => {
    await screen(page, host, ['∴ Thinking…', '  First pass over the parser.', '⏺ Working.', ...COMPOSER]);
    await page.evaluate(() => {
      const d = document.querySelector('#threads details.th-thought');
      d.open = true; d.dataset.marker = 'kept';
    });
    // The reply grows in place: same opening line, longer body.
    await screen(page, host, ['∴ Thinking…', '  First pass over the parser.', '⏺ Working. Now reading the tokenizer.', ...COMPOSER]);
    const st = await page.evaluate(() => {
      const d = document.querySelector('#threads details.th-thought');
      return { open: d.open, marker: d.dataset.marker, body: [...document.querySelectorAll('#threads .th-body')].map(e => e.textContent) };
    });
    assert.equal(st.open, true, 'an open thought must stay open while the message below it streams');
    assert.equal(st.marker, 'kept', 'and it must be the SAME node, not a rebuilt one');
    assert.ok(st.body.some(x => /Now reading the tokenizer/.test(x)), 'the streamed text must appear');
  });
});

test('spinner ticks do not stack dead bubbles', async t => {
  await withPhone(t, async ({ host, page }) => {
    for (let i = 0; i < 6; i++) {
      await screen(page, host, ['❯ run the suite', `✻ Working… (${i + 1}s)`, ...COMPOSER], 200);
    }
    const bubbles = await page.evaluate(() => [...document.querySelectorAll('#threads .th-msg')]
      .map(e => e.textContent.replace(/\s+/g, ' ').trim()));
    const working = bubbles.filter(x => /Working…/.test(x));
    assert.ok(working.length <= 1, `a spinner is one live bubble, got ${working.length}: ${JSON.stringify(bubbles)}`);
  });
});

test('a repeated identical line is not duplicated into the conversation', async t => {
  await withPhone(t, async ({ host, page }) => {
    await screen(page, host, ['⏺ Same answer.', ...COMPOSER]);
    await screen(page, host, ['⏺ Same answer.', ...COMPOSER]);
    const n = await page.evaluate(() => [...document.querySelectorAll('#threads .th-msg .th-body')]
      .filter(e => /Same answer\./.test(e.textContent)).length);
    assert.equal(n, 1, `a repaint of the same screen must not append a second copy, got ${n}`);
  });
});

test('scrolling back is not yanked to the bottom by new output', async t => {
  await withPhone(t, async ({ host, page }) => {
    await screen(page, host, ['⏺ One.', '⏺ Two.', '⏺ Three.', ...COMPOSER]);
    await page.evaluate(() => { const h = document.querySelector('#threads'); h.scrollTop = 0; });
    await page.waitForTimeout(150);
    await screen(page, host, ['⏺ One.', '⏺ Two.', '⏺ Three.', '⏺ Four.', ...COMPOSER]);
    const y = await page.evaluate(() => document.querySelector('#threads').scrollTop);
    assert.ok(y < 40, `a reader parked at the top must stay there, got scrollTop=${y}`);
  });
});

test('the alternate screen is refused honestly, with the way out', async t => {
  await withPhone(t, async ({ page, host }) => {
    // An app switching the buffer to the alternate screen: rows are a canvas, not a conversation.
    host.sendOutput(A, '\x1b[?1049h\x1b[2J' + '  full screen app\r\n');
    await page.waitForTimeout(500);
    const alt = await page.evaluate(() => ({
      note: (document.querySelector('#threads .th-alt') || {}).textContent || '',
      button: !!document.querySelector('#threads .th-alt button'),
    }));
    assert.match(alt.note, /full-screen interface/i, 'the surface must say why it has nothing to show');
    assert.equal(alt.button, true, 'and offer the terminal');
    host.sendOutput(A, '\x1b[?1049l');
    await page.waitForTimeout(400);
  });
});

test('a long conversation stays bounded', async t => {
  await withPhone(t, async ({ host, page }) => {
    const lines = [];
    for (let i = 0; i < 25; i++) lines.push(`⏺ Answer number ${i} with a little body text.`);
    await screen(page, host, [...lines, ...COMPOSER], 600);
    const n = await page.evaluate(() => document.querySelectorAll('#threads .th-msg').length);
    assert.ok(n >= 20, `most of the conversation must render, got ${n}`);
    assert.ok(n <= 60, `and it must not multiply each repaint, got ${n}`);
  });
});
