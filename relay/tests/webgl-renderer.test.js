// Lever 7: the WebGL renderer, with the DOM renderer as the floor.
//
// The vendored xterm shipped as a DOM-only build: it paints every frame by laying out a div per row, and
// that is the slow path a phone feels when a full-screen app redraws the whole grid. The WebGL addon draws
// the same screen into one canvas. Two properties have to hold, and both live in the page, so both are
// checked on the real shipped page in a real browser:
//   1. with the addon present, the terminal actually uses it (a WebGL canvas exists and xterm reports it);
//   2. `?mux_render=dom` forces the DOM renderer back and the terminal still paints - the fallback is real,
//      not a claim.
// The second matters as much as the first: a renderer swap that cannot be turned off is a support problem
// the moment it misbehaves on one device.
//
// WebGL needs a GPU context. Headless Chromium provides one (WebGL 2.0 via ANGLE/SwiftShader), so this
// runs headless like the rest of the browser suites. If Chromium is unavailable the suite skips loudly.
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const { test } = require('node:test');

const { RelayHarness, REPO, freePort, launchBrowser, waitFor } = require('./harness');

const TEST_TIMEOUT_MS = 8000;
const SESSION_NAME = 'render';
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
    console.warn(`Chromium unavailable; skipping WebGL renderer tests.\n${INSTALL_COMMANDS}\nReason: ${error.message}`);
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

// Attach the one session on the shipped page at the requested renderer, paint a couple of lines, and hand
// the page back. `renderMode` is 'webgl' (default) or 'dom' (the forced fallback).
async function withBrowserRelay(session, renderMode, callback) {
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
    const query = renderMode ? `?mux_render=${renderMode}` : '';
    await page.goto(`http://127.0.0.1:${harness.port}/${query}`);
    await page.waitForFunction(() => window._sessions && window._sessions.some(s => s.name === 'render'));
    await page.evaluate(() => connect('render'));
    await page.waitForFunction(() => !!document.querySelector('.xterm'));
    host.sendOutput(SESSION_NAME, 'renderer probe line one\r\nrenderer probe line two\r\n');
    await new Promise(r => setTimeout(r, 500));
    await callback({ harness, host, page });
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
}

// What the page is actually rendering with: the flag it set, the canvases it built, and whether the text
// it was sent is on screen in a canvas-backed renderer.
async function readRenderer(page) {
  return await page.evaluate(() => {
    const host = document.querySelector('#term');
    const canvases = [...host.querySelectorAll('canvas')];
    const types = canvases.map(c => {
      try { return c.getContext('webgl2') ? 'webgl2' : (c.getContext('webgl') ? 'webgl' : (c.getContext('2d') ? '2d' : 'none')); }
      catch (e) { return 'err'; }
    });
    return {
      rendererKind: (typeof rendererKind !== 'undefined') ? rendererKind : '?',
      addonLoaded: !!(window.WebglAddon && window.WebglAddon.WebglAddon),
      canvasTypes: types,
      rows: [...host.querySelectorAll('.xterm-rows > div')].length,
    };
  });
}

test('the shipped page renders the terminal through the WebGL addon', async t => {
  if (skipWithoutChromium(t)) return;
  await withBrowserRelay({ name: SESSION_NAME, alive: true, shellOnly: true, cols: 80, rows: 24 }, null, async ({ page }) => {
    const r = await readRenderer(page);
    assert.equal(r.addonLoaded, true, 'the vendored WebGL addon must be on the page');
    assert.equal(r.rendererKind, 'webgl', 'the page must be using the WebGL renderer by default');
    assert.ok(r.canvasTypes.includes('webgl2') || r.canvasTypes.includes('webgl'),
      `a WebGL canvas must back the terminal, saw ${JSON.stringify(r.canvasTypes)}`);
  });
});

test('the fallback renderer still paints when WebGL is forced off', async t => {
  if (skipWithoutChromium(t)) return;
  await withBrowserRelay({ name: SESSION_NAME, alive: true, shellOnly: true, cols: 80, rows: 24 }, 'dom', async ({ page }) => {
    const r = await readRenderer(page);
    assert.equal(r.rendererKind, 'dom', '?mux_render=dom must force the DOM renderer');
    assert.ok(!r.canvasTypes.includes('webgl2') && !r.canvasTypes.includes('webgl'),
      `no WebGL canvas may exist in DOM mode, saw ${JSON.stringify(r.canvasTypes)}`);
    // The DOM renderer produces the row elements the paint-integrity stack reads, and the text is on them.
    assert.ok(r.rows > 0, 'the DOM renderer must produce the row elements');
    const painted = await page.evaluate(() => {
      const rows = [...document.querySelectorAll('#term .xterm-rows > div')].map(r => r.textContent || '');
      return rows.some(t => t.includes('renderer probe line one'));
    });
    assert.equal(painted, true, 'the terminal text must actually be painted in DOM mode');
  });
});

test('the paint-integrity stack stands down under WebGL instead of crying blank', async t => {
  if (skipWithoutChromium(t)) return;
  // Under WebGL there are no `.xterm-rows` divs, so the row-based integrity checks must report "cannot
  // judge" (null) - not "blank" (true), which would repaint forever and fight the renderer.
  await withBrowserRelay({ name: SESSION_NAME, alive: true, shellOnly: true, cols: 80, rows: 24 }, null, async ({ page }) => {
    const verdict = await page.evaluate(() => {
      const host = document.querySelector('#term');
      const rect = host.getBoundingClientRect();
      const out = { kind: rendererKind };
      try { out.haveVisibleText = domRowsHaveVisibleText(host, rect); } catch (e) { out.haveVisibleText = 'THREW'; }
      try { out.lostRows = domRowsLosingBufferText(host, rect); } catch (e) { out.lostRows = 'THREW'; }
      try { out.looksBlank = terminalPaintLooksBlank(); } catch (e) { out.looksBlank = 'THREW'; }
      return out;
    });
    assert.equal(verdict.kind, 'webgl', 'this test is about the WebGL path');
    assert.equal(verdict.haveVisibleText, null, 'the DOM-row check must not claim a blank WebGL surface');
    assert.equal(verdict.lostRows, null, 'the lost-row check must not judge a WebGL surface');
    assert.equal(verdict.looksBlank, false, 'the terminal must never be reported blank while WebGL is painting it');
  });
});
