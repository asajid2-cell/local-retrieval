// Lever 5: predictive echo (mosh-style local typeahead).
//
// On WAN the felt gap between a keystroke and its echo is ~86ms p50, so a fast typist watches the
// cursor trail the fingers. The fix draws the typed char locally, dim, the instant it is sent, and
// peels it off as the server's real echo lands. The prediction is PIXELS ONLY - it is never written
// into xterm's buffer - so this suite pins the four properties that make that safe, on the real
// shipped page driven by a real browser:
//   1. a printable keystroke shows a pending prediction before any echo arrives;
//   2. the server's echo CONFIRMS it and the prediction clears;
//   3. a diverging echo (a control/newline the server sent instead) CANCELS it whole;
//   4. it stays off on the alternate screen, where an app owns every cell and a guess is wrong.
//
// The browser leg is the honest one: the whole point of the lever is what a person sees while typing,
// and that lives in the page, not in the relay. If Chromium is unavailable the suite skips loudly.
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const path = require('node:path');
const { test } = require('node:test');

const { RelayHarness, REPO, freePort, launchBrowser, waitFor } = require('./harness');

const TEST_TIMEOUT_MS = 8000;
const SESSION_NAME = 'predict';
const INSTALL_COMMANDS = [
  'npm install --save-dev playwright --package-lock=false',
  'npx playwright install chromium',
].join('\n');

let browser = null;
let browserError = null;

test.before(async () => {
  try {
    browser = await launchBrowser();
  } catch (error) {
    browserError = error;
    console.warn(`Chromium unavailable; skipping predictive-echo tests.\n${INSTALL_COMMANDS}\nReason: ${error.message}`);
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

async function withBrowserRelay(session, callback) {
  const harness = new RelayHarness();
  let host = null, context = null;
  try {
    harness.start = () => startBrowserRelay(harness);
    await harness.start();
    host = await harness.connectHost([session]);
    context = await browser.newContext({ viewport: { width: 1000, height: 700 } });
    context.setDefaultTimeout(TEST_TIMEOUT_MS);
    context.setDefaultNavigationTimeout(TEST_TIMEOUT_MS);
    const page = await context.newPage();
    await page.goto(`http://127.0.0.1:${harness.port}/`);
    // Attach the one session. `connect` is what a tab click calls, so this is the real attach path.
    await page.waitForFunction(() => window._sessions && window._sessions.some(s => s.name === 'predict'));
    await page.evaluate(() => connect('predict'));
    // The terminal is up once its textarea exists. We wait on that and not `.xterm-rows`, because the
    // WebGL renderer (the shipped default) draws into a canvas and builds no row elements at all.
    await page.waitForFunction(() => !!document.querySelector('#term .xterm-helper-textarea'));
    await new Promise(r => setTimeout(r, 300));
    await callback({ harness, host, page });
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
}

// The pending prediction, as a person sees it: the element is shown and its text is what we typed.
async function readPrediction(page) {
  return await page.evaluate(() => {
    const el = document.querySelector('#predict');
    return { shown: el.classList.contains('show'), text: el.textContent, opacity: getComputedStyle(el).opacity };
  });
}

test('a keystroke shows a dim prediction before its echo arrives, and the echo clears it', async t => {
  if (skipWithoutChromium(t)) return;
  await withBrowserRelay({ name: SESSION_NAME, alive: true, shellOnly: true, cols: 80, rows: 24 }, async ({ host, page }) => {
    // Type "hi" through the REAL onData path. No echo is delivered yet, so both chars must be pending.
    await page.evaluate(() => { for (const ch of ['h', 'i']) send('i', ch); });
    const pending = await readPrediction(page);
    assert.equal(pending.shown, true, 'a typed char must show a prediction before any echo');
    assert.equal(pending.text, 'hi', `the prediction must be exactly what was typed: ${JSON.stringify(pending.text)}`);
    assert.ok(Number(pending.opacity) < 0.9, 'the prediction must be dimmed, not full-strength');

    // The server echoes "hi". The prediction must clear entirely.
    host.sendOutput(SESSION_NAME, 'hi');
    await waitFor(async () => (await readPrediction(page)).text === '', 'prediction cleared on echo', 3000);
    assert.equal((await readPrediction(page)).shown, false, 'a fully confirmed prediction must hide');
  });
});

test('the echo peels the confirmed prefix and leaves the unconfirmed tail', async t => {
  if (skipWithoutChromium(t)) return;
  await withBrowserRelay({ name: SESSION_NAME, alive: true, shellOnly: true, cols: 80, rows: 24 }, async ({ host, page }) => {
    await page.evaluate(() => { for (const ch of ['a', 'b', 'c']) send('i', ch); });
    assert.equal((await readPrediction(page)).text, 'abc');
    // Only "ab" comes back so far: the confirmed pair goes, "c" stays pending.
    host.sendOutput(SESSION_NAME, 'ab');
    await waitFor(async () => (await readPrediction(page)).text === 'c', 'partial confirm', 3000);
    assert.equal((await readPrediction(page)).shown, true, 'an unconfirmed tail must stay visible');
  });
});

test('a diverging echo cancels the prediction whole', async t => {
  if (skipWithoutChromium(t)) return;
  await withBrowserRelay({ name: SESSION_NAME, alive: true, shellOnly: true, cols: 80, rows: 24 }, async ({ host, page }) => {
    await page.evaluate(() => { for (const ch of ['x', 'y']) send('i', ch); });
    assert.equal((await readPrediction(page)).text, 'xy');
    // The server answered with a newline (a shell prompt, an error) - not the echo. The guess is stale.
    host.sendOutput(SESSION_NAME, '\r\n');
    await waitFor(async () => (await readPrediction(page)).shown === false, 'prediction cancelled', 3000);
  });
});

test('the prediction stays off on the alternate screen', async t => {
  if (skipWithoutChromium(t)) return;
  await withBrowserRelay({ name: SESSION_NAME, alive: true, shellOnly: true, cols: 80, rows: 24 }, async ({ host, page }) => {
    // Enter the app's alternate screen, then type. Nothing may be predicted.
    host.sendOutput(SESSION_NAME, '\x1b[?1049h');
    await waitFor(async () => await page.evaluate(() => { try { return term.buffer.active.type === 'alternate'; } catch (e) { return false; } }), 'alt screen entered', 3000);
    await page.evaluate(() => send('i', 'z'));
    await new Promise(r => setTimeout(r, 120));
    assert.equal((await readPrediction(page)).shown, false, 'no prediction may be drawn on the alternate screen');
  });
});
