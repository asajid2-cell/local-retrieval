// R4: a frozen terminal has to say it is frozen.
//
// autoFreeze/selectMode FREEZE the terminal on purpose — a live repaint would wipe a highlight mid-drag — but
// the freeze was invisible: output silently buffered into _frozenBuf and the tab read as stuck. The only way
// back was to know that the "Sel" toggle, the ↓ pill, or a stray click resumes it. This pins the visible half:
// a frozen terminal shows a "paused — click to resume" affordance, clicking it goes live and replays whatever
// buffered, and a plain click that makes no selection does NOT flash the overlay (the debounce).
//
// The red direction is structural — HEAD has no #paused element at all, so the "is it visible" assertion
// cannot be satisfied. Everything past that (the resume click, the buffered-output replay, the no-flicker
// case) is driven through real CDP input on the shipped page.
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const { test } = require('node:test');

const { RelayHarness, REPO, freePort, launchBrowser, waitFor } = require('./harness');

const TEST_TIMEOUT_MS = 8000;
const SESSION_NAME = 'paused';
const INSTALL_COMMANDS = [
  'npm install --save-dev playwright --package-lock=false',
  'npx playwright install chromium',
].join('\n');
const REVEAL_WAIT_MS = 1000;   // 250ms poll + 350ms debounce; leave generous headroom under load

let browser = null;
let browserError = null;

test.before(async () => {
  try {
    browser = await launchBrowser();
  } catch (error) {
    browserError = error;
    console.warn(`Chromium unavailable; skipping paused-overlay tests.\n${INSTALL_COMMANDS}\nReason: ${error.message}`);
  }
});

test.after(async () => { if (browser) await browser.close(); });

function skipWithoutChromium(t) {
  if (browser) return false;
  t.skip(`Chromium unavailable; run these commands first:\n${INSTALL_COMMANDS}\n${browserError && browserError.message}`);
  return true;
}

async function startBrowserRelay(harness) {
  harness.port = await freePort();
  harness.env.ALLOWED_WS_ORIGINS = `http://127.0.0.1:${harness.port}`;
  harness.proc = childProcess.spawn(process.execPath, ['server.js'], {
    cwd: REPO,
    env: {
      ...process.env,
      PORT: String(harness.port),
      MUX_HOST_TOKEN: 'test-token',
      MUX_TEST_MODE: '1',
      MUX_TEST_FIXTURE: '1',
      MUX_BIND_HOST: '127.0.0.1',
      MUX_AUTOHEAL: '0',
      MUX_STATE_DIR: harness.tmp,
      MUX_HOST_SB_WAIT_MS: '40',
      HLAUTH_BASE: 'http://127.0.0.1:1',
      ...harness.env,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  harness.stdout = '';
  harness.stderr = '';
  harness.proc.stdout.on('data', d => { harness.stdout += d.toString(); });
  harness.proc.stderr.on('data', d => { harness.stderr += d.toString(); });
  await waitFor(() => harness.stdout.includes(`multiplex-app on 127.0.0.1:${harness.port}`), 'browser relay start', 5000);
}

// Attach the one fixture session on the shipped page and hand the page back live.
async function withBrowserRelay(callback) {
  const harness = new RelayHarness();
  let host = null, context = null;
  try {
    harness.start = () => startBrowserRelay(harness);
    await harness.start();
    host = await harness.connectHost([{ name: SESSION_NAME, alive: true, shellOnly: true, cols: 80, rows: 24 }]);
    context = await browser.newContext({ viewport: { width: 1000, height: 700 } });
    context.setDefaultTimeout(TEST_TIMEOUT_MS);
    context.setDefaultNavigationTimeout(TEST_TIMEOUT_MS);
    const page = await context.newPage();
    await page.goto(`http://127.0.0.1:${harness.port}/`);
    await page.waitForFunction(() => window._sessions && window._sessions.some(s => s.name === 'paused'));
    await page.evaluate(() => connect('paused'));
    await page.waitForFunction(() => !!document.querySelector('.xterm'));
    host.sendOutput(SESSION_NAME, 'paused probe line\r\n');
    await new Promise(r => setTimeout(r, 400));
    await callback({ harness, host, page });
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
}

// The overlay's state as the page sees it. A missing element reads as "not visible", which is exactly the
// HEAD state - so this helper is honest on both sides of the change.
async function pausedOverlay(page) {
  return await page.evaluate(() => {
    const el = document.querySelector('#paused');
    if (!el) return { exists: false, visible: false, text: '' };
    return { exists: true, visible: !el.hidden, text: (el.textContent || '').trim() };
  });
}

async function bufferText(page) {
  return await page.evaluate(() => {
    const b = term.buffer.active; let out = '';
    for (let i = 0; i < b.length; i++) { const l = b.getLine(i); if (l) out += l.translateToString(true) + '\n'; }
    return out;
  });
}

test('a frozen terminal shows a visible paused affordance', async t => {
  if (skipWithoutChromium(t)) return;
  await withBrowserRelay(async ({ page }) => {
    await page.evaluate(() => { toggleSelect(); });               // the Sel toggle: a real, persistent freeze
    await new Promise(r => setTimeout(r, REVEAL_WAIT_MS));
    const ov = await pausedOverlay(page);
    assert.equal(ov.exists, true, 'a frozen terminal must carry a #paused affordance');
    assert.equal(ov.visible, true, 'the affordance must be visible while the terminal is frozen');
    assert.match(ov.text, /paused/i, 'the affordance must say the terminal is paused');
    assert.match(ov.text, /resume/i, 'the affordance must say how to resume');
  });
});

test('clicking the paused affordance resumes the terminal and replays buffered output', async t => {
  if (skipWithoutChromium(t)) return;
  await withBrowserRelay(async ({ host, page }) => {
    await page.evaluate(() => { toggleSelect(); });
    await page.waitForSelector('#paused', { state: 'visible' });
    // Output that arrives while frozen must BUFFER, not paint - that is the whole point of the freeze.
    host.sendOutput(SESSION_NAME, 'BUFFERED_WHILE_PAUSED_MARKER\r\n');
    await new Promise(r => setTimeout(r, 300));
    assert.ok(!(await bufferText(page)).includes('BUFFERED_WHILE_PAUSED_MARKER'),
      'output arriving while frozen must be buffered, not written to the grid');
    await page.click('#paused');                                   // a real CDP click on the overlay
    await page.waitForFunction(() => !document.querySelector('#paused') || document.querySelector('#paused').hidden);
    const frozen = await page.evaluate(() => isFrozen());
    assert.equal(frozen, false, 'clicking the affordance must leave the terminal live');
    await page.waitForFunction(() => {
      const b = term.buffer.active; for (let i = 0; i < b.length; i++) { const l = b.getLine(i); if (l && l.translateToString(true).includes('BUFFERED_WHILE_PAUSED_MARKER')) return true; }
      return false;
    }, null, { timeout: 3000 });
  });
});

test('a plain click that selects nothing does not flash the overlay', async t => {
  if (skipWithoutChromium(t)) return;
  await withBrowserRelay(async ({ page }) => {
    const box = await page.locator('#term').boundingBox();
    await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);   // real CDP click, no drag
    await new Promise(r => setTimeout(r, REVEAL_WAIT_MS));
    const ov = await pausedOverlay(page);
    assert.equal(ov.visible, false, 'a click that latches autoFreeze for one frame must not raise the overlay');
    const frozen = await page.evaluate(() => isFrozen());
    assert.equal(frozen, false, 'a click with no selection must leave the terminal live');
  });
});
