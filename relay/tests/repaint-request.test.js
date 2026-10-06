// R6: a paint fault the CLIENT cannot repair must ask the APP to redraw, not just repaint the local copy.
//
// A local heal (recoverTerminalPaint / term.refresh) re-renders whatever the client's buffer already holds.
// That is enough for a dropped DOM frame, but it is useless the moment the buffer ITSELF is wrong: a screen
// corrupted inside the client, or an app frame drawn for another size. The app's own frame is the authority
// and only the app can rebuild it, so the paint-fault detector (both the WebGL and the DOM path) and the
// toolbar's Repaint button all take the SAME wire path:
//
//   client ws 'R'  ->  relay (rate-limited)  ->  muxd repaint_viewer
//        -> FOCUS_IN for a main-screen ?1004 app, the size wiggle for an alt TUI
//
// Red direction (HEAD): healDetectedPaintFault sent 'R' only for the alt-screen stale-width class and
// healWebglBlank never sent it at all, so a blanked WebGL surface and a DOM surface that lost rows healed
// locally and left the app frame untouched - the host saw no redraw. And there was no Repaint button.
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const { test } = require('node:test');

const { RelayHarness, REPO, freePort, launchBrowser, waitFor, sleep } = require('./harness');

const source = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');

const TEST_TIMEOUT_MS = 8000;
const SESSION = 'repaint';
const INSTALL_COMMANDS = [
  'npm install --save-dev playwright --package-lock=false',
  'npx playwright install chromium',
].join('\n');

const LINES = Array.from({ length: 24 }, (_, i) =>
  `REPAINT authoritative line ${String(i + 1).padStart(2, '0')} alpha beta gamma delta epsilon zeta`);
const AUTHORITATIVE = '\x1b[2J\x1b[H' + LINES.join('\r\n') + '\r\n';

let browser = null;
let browserError = null;

test.before(async () => {
  try { browser = await launchBrowser(); }
  catch (error) { browserError = error; console.warn(`Chromium unavailable; skipping repaint-request tests.\n${INSTALL_COMMANDS}\n${error.message}`); }
});

test.after(async () => { if (browser) await browser.close(); });

function skipWithoutChromium(t) {
  if (browser) return false;
  t.skip(`Chromium unavailable; run these commands first:\n${INSTALL_COMMANDS}\n${browserError && browserError.message}`);
  return true;
}

// ---- source gate: the detector and the button share one server-repaint entry point -------------------
function fnBody(name) {
  const m = source.match(new RegExp('function ' + name + '\\s*\\([^)]*\\)\\s*\\{[\\s\\S]*?\\n\\}'));
  assert.ok(m, `could not find ${name} in the shipped page`);
  return m[0];
}

test('the detector and the Repaint button share one server-repaint path', () => {
  assert.match(fnBody('requestServerRepaint'), /send\(\s*'R'\s*,\s*''\s*\)/,
    'requestServerRepaint must send the ws R repaint request');
  assert.match(fnBody('healDetectedPaintFault'), /requestServerRepaint\(/,
    'the DOM paint-fault heal must request a server repaint, not only a local one');
  assert.match(fnBody('healWebglBlank'), /requestServerRepaint\(/,
    'the WebGL paint-fault heal must request a server repaint too');
  assert.match(source, /id="repaint"/, 'the toolbar Repaint button must exist in the page');
  assert.match(source, /\$\('#repaint'\)\.onclick[\s\S]*?requestServerRepaint\(/,
    'the Repaint button must be wired to the same server-repaint path');
});

// ---- the real behaviour, on a loopback relay with a fake host ---------------------------------------

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
  await waitFor(() => harness.stdout.includes(`multiplex-app on 127.0.0.1:${harness.port}`), 'browser relay start', 10000);
}

async function withBrowserRelay(renderMode, callback) {
  const harness = new RelayHarness();
  let host = null, context = null;
  try {
    harness.start = () => startBrowserRelay(harness);
    await harness.start();
    host = await harness.connectHost([{ name: SESSION, alive: true, shellOnly: true, cols: 80, rows: 24 }]);
    context = await browser.newContext({ viewport: { width: 1000, height: 700 } });
    context.setDefaultTimeout(TEST_TIMEOUT_MS);
    context.setDefaultNavigationTimeout(30000);   // the box is shared; a cold page load can exceed 8s
    const page = await context.newPage();
    const query = renderMode ? `?mux_render=${renderMode}` : '';
    await page.goto(`http://127.0.0.1:${harness.port}/${query}`, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => window._sessions && window._sessions.some(s => s.name === 'repaint'));
    await page.evaluate(() => connect('repaint'));
    await page.waitForFunction(() => !!document.querySelector('.xterm'));
    await waitFor(() => host.messages.some(m => m.t === 'sb' && m.s === SESSION), 'scrollback request', TEST_TIMEOUT_MS);
    host.sendScrollback(SESSION, 'boot\n');
    await page.waitForFunction(() => term.buffer.active.length > 0);
    host.sendOutput(SESSION, AUTHORITATIVE);
    await page.waitForFunction(() => {
      const b = term.buffer.active;
      for (let y = 0; y < b.length; y++) { const l = b.getLine(y); if (l && /REPAINT authoritative line/.test(l.translateToString(true))) return true; }
      return false;
    }, null, { timeout: TEST_TIMEOUT_MS });
    await sleep(400);   // let the write burst settle so the detector judges a settled surface
    await callback({ harness, host, page });
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
}

const sawRedraw = host => host.messages.some(m => m.t === 'redraw' && m.s === SESSION);

test('a blanked WebGL surface asks the app to repaint', async t => {
  if (skipWithoutChromium(t)) return;
  await withBrowserRelay(null, async ({ host, page }) => {
    // The WebGL canvas appears shortly after load; on a loaded box a cold context can take a beat, and a
    // box that cannot hand out a GL context at all is an environment limit, not a fault in this wire.
    let haveCanvas = false;
    try {
      await page.waitForFunction(() => [...document.querySelectorAll('#term canvas')].some(c => {
        try { return !!(c.getContext('webgl2') || c.getContext('webgl')); } catch (e) { return false; }
      }), null, { timeout: TEST_TIMEOUT_MS });
      haveCanvas = true;
    } catch (e) {}
    if (!haveCanvas) return t.skip('no WebGL context available here; the WebGL wiring is pinned by the source gate and the DOM path is proven by the sibling test');
    host.messages.length = 0;
    const blankAndVerify = () => page.evaluate(() => {
      try { lastWebglBlankCheckAt = 0; } catch (e) {}       // clear the once-a-second blank-check throttle
      const canvas = [...document.querySelectorAll('#term canvas')].find(c => {
        try { return !!(c.getContext('webgl2') || c.getContext('webgl')); } catch (e) { return false; }
      });
      const gl = canvas.getContext('webgl2') || canvas.getContext('webgl');
      gl.clearColor(0, 0, 0, 1); gl.clear(gl.COLOR_BUFFER_BIT);   // blank in the same task as the verdict
      verifyTerminalPaint('test');
      return window.__muxLastRepaintRequest || null;
    });
    // The relay rate-limits the repaint request to one per 10s per session, so a request the page fired
    // during load can briefly own the window and swallow this one. Re-ask until the request lands instead
    // of racing that window - the client is genuinely asking each time.
    let last = null, landed = false;
    const deadline = Date.now() + 15000;
    while (!landed && Date.now() < deadline) {
      last = await blankAndVerify();
      landed = await waitFor(() => sawRedraw(host), 'a redraw request after a blanked WebGL surface', 2500)
        .then(() => true).catch(() => false);
    }
    assert.ok(landed, `the WebGL heal must ask the app to repaint; last client request=${JSON.stringify(last)}`);
  });
});

test('a DOM surface that lost rows asks the app to repaint', async t => {
  if (skipWithoutChromium(t)) return;
  await withBrowserRelay('dom', async ({ host, page }) => {
    host.messages.length = 0;
    const injected = await page.evaluate(() => {
      const hostEl = document.querySelector('#term');
      const rows = [...hostEl.querySelectorAll('.xterm-rows > div')];
      let cleared = 0;
      for (let i = 3; i < Math.min(rows.length, 9); i++) { rows[i].textContent = ''; cleared++; }   // drop pixels the buffer still holds
      const m = domRowsLosingBufferText(hostEl, hostEl.getBoundingClientRect());
      verifyTerminalPaint('test');
      return { cleared, lost: (m && m.lost) || 0 };
    });
    assert.ok(injected.cleared > 0, 'the DOM renderer must produce rows to clear');
    assert.ok(injected.lost > 0, 'the injected DOM loss must be seen by the lost-row check');
    await waitFor(() => sawRedraw(host), 'a redraw request after DOM row loss', TEST_TIMEOUT_MS);
  });
});

test('the Repaint button asks the app to redraw and the screen is restored without a reload', async t => {
  if (skipWithoutChromium(t)) return;
  await withBrowserRelay('dom', async ({ host, page }) => {
    // Corrupt the CLIENT screen only: term.write feeds the emulator's own buffer, never the PTY.
    await page.evaluate(() => {
      const garbage = Array.from({ length: 24 }, (_, i) => `CLIENTSIDE CORRUPTION ${i} !!!!!!`).join('\r\n');
      term.write('\x1b[2J\x1b[H' + garbage + '\r\n');
    });
    await page.waitForFunction(() => {
      const l = term.buffer.active.getLine(0);
      return !!(l && /CLIENTSIDE CORRUPTION/.test(l.translateToString(true)));
    }, null, { timeout: TEST_TIMEOUT_MS });
    assert.equal(host.messages.filter(m => m.t === 'i').length, 0,
      'corrupting the client screen must never reach the PTY as input');

    host.messages.length = 0;
    await page.locator('#repaint').click();
    await waitFor(() => sawRedraw(host), 'a redraw request from the Repaint button', TEST_TIMEOUT_MS);

    // muxd answers a repaint by making the app redraw its block; the host re-emits the authoritative frame.
    host.sendOutput(SESSION, AUTHORITATIVE);
    await page.waitForFunction(() => {
      const b = term.buffer.active; let hit = false, junk = false;
      for (let y = 0; y < b.length; y++) {
        const l = b.getLine(y); if (!l) continue; const t = l.translateToString(true);
        if (/REPAINT authoritative line/.test(t)) hit = true;
        if (/CLIENTSIDE CORRUPTION/.test(t)) junk = true;
      }
      return hit && !junk;
    }, null, { timeout: TEST_TIMEOUT_MS });
  });
});
