// Regression: the wheel must reach the APP when the app owns the SCREEN (alternate buffer) and has taken
// the mouse, and must scroll the mirror locally otherwise - for EVERY session, regardless of who owns it.
//
// The owner wheel path used to forward a wheel record to the app whenever the session was the visible
// owner (isVisibleOwner()), with no regard for WHICH screen was up or whether the app had ever asked for
// the mouse. On the single most common owner session there is - a plain shell the local console owns,
// sitting on the normal buffer with no mouse tracking - that sent SGR mouse reports (`\x1b[<64;..M`)
// straight into the shell's stdin, where the shell echoed them back as garbage, while the mirror's own
// scrollback sat unused. The user saw exactly that: "some sessions just don't scroll". A non-owner shell
// was never affected (its branch scrolls xterm's scrollback locally), which is why the failure looked
// arbitrary - it tracked ownership, not the session kind.
//
// The fix: the wheel is the app's ONLY when the app owns the SCREEN (the alternate buffer) AND has
// demonstrably taken the mouse. Otherwise the host's scrollback scrolls locally and nothing is forwarded.
// The owner flag must NOT be consulted at all: a session created from the web is owner:false, so an owner
// gate here ALSO killed the wheel for every browser-launched full-screen app - an alt-screen app that had
// armed the mouse got no wheel, and fell through to a viewport that has no scrollback. That is the dead
// wheel. The same trap, and the same correction, is already recorded for the shift gesture in
// shift-select.test.js. This file pins both halves: the source gate (cheap, always runs) and the real
// behaviour on a live owner shell (the proof).
const test = require('node:test');
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const {
  RelayHarness,
  REPO,
  freePort,
  launchBrowser,
  waitFor,
  sleep,
} = require('./harness');

const source = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');

// ---- source gate: the owner wheel path must consult the SCREEN and the app's mouse intent -------------

test('sendOwnerWheel refuses unless the app owns the alternate screen and has taken the mouse', () => {
  const at = source.indexOf('function sendOwnerWheel(');
  assert.notEqual(at, -1, 'sendOwnerWheel() is gone');
  const end = source.indexOf('\n}', at);
  const body = source.slice(at, end);
  assert.match(body, /appOwnsScreen\(\)/, 'sendOwnerWheel() no longer checks which screen is up');
  assert.match(body, /appTakesMouse\(\)/, 'sendOwnerWheel() no longer checks whether the app took the mouse');
  // The guard must be the screen+intent pair, and must NOT consult ownership: a web-created session is
  // owner:false, so an owner gate here killed the wheel for every browser-launched full-screen app.
  assert.match(body, /if\(!appOwnsScreen\(\)\s*\|\|\s*!appTakesMouse\(\)/,
    'the owner wheel guard must be the negation of appOwnsScreen() && appTakesMouse()');
  assert.doesNotMatch(body, /isVisibleOwner/,
    'sendOwnerWheel() consults isVisibleOwner() again - a web-created (owner:false) app would lose the wheel');
});

test('the wheel router only hands the app a record when it owns the screen and the mouse', () => {
  const start = source.indexOf("$('#term').addEventListener('wheel'");
  assert.notEqual(start, -1, 'the capture-phase wheel router is gone');
  const end = source.indexOf('}, {passive:false, capture:true});', start);
  assert.notEqual(end, -1, 'could not find the end of the wheel router');
  const router = source.slice(start, end);
  // The app branch must require BOTH, so a normal-buffer session falls through to the local scrollback
  // branch instead of forwarding a report - and must NOT require ownership (see the header).
  assert.match(router, /if\(appOwnsScreen\(\)\s*&&\s*appTakesMouse\(\)\)/,
    'the router app branch is no longer gated on appOwnsScreen() && appTakesMouse()');
  assert.doesNotMatch(router, /isVisibleOwner/,
    'the wheel router consults isVisibleOwner() again - a web-created (owner:false) app would lose the wheel');
});

// ---- the real behaviour: an owner shell must scroll, and nothing may reach its stdin ------------------

const TEST_TIMEOUT_MS = 8000;
const SESSION = 'owner-wheel-shell';
const INSTALL = 'npm install --save-dev playwright --package-lock=false\nnpx playwright install chromium';

let browser = null;
let browserError = null;

test.before(async () => {
  try { browser = await launchBrowser(); }
  catch (error) { browserError = error; console.warn(`Chromium unavailable; skipping the live half.\n${INSTALL}\n${error.message}`); }
});

test.after(async () => { if (browser) await browser.close(); });

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
      MUX_TEST_PERSIST_FAULT_FILE: path.join(harness.tmp, '.persist-fault.json'),
      MUX_HOST_SB_WAIT_MS: '40',
      MUX_COMMAND_LEASE_MS: '1000',
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

const LINES = Array.from({ length: 60 }, (_, i) =>
  `OWNERWHEEL line ${String(i + 1).padStart(3, '0')} alpha beta gamma delta epsilon zeta eta theta iota`);

test('an owner shell scrolls its own scrollback and the wheel never reaches the PTY', async t => {
  if (!browser) return t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`);

  const harness = new RelayHarness();
  harness.start = () => startBrowserRelay(harness);
  let host = null, context = null;
  try {
    await harness.start();
    // owner:true is the whole point - a shell the local console owns, the case the old gate broke.
    host = await harness.connectHost([{
      name: SESSION, alive: true, created: Date.now(), cols: 80, rows: 24,
      hasCommand: false, shellOnly: true, ready: true, owner: true, sessionId: 'owner-wheel-session',
    }]);
    context = await browser.newContext({ viewport: { width: 1000, height: 700 } });
    context.setDefaultTimeout(TEST_TIMEOUT_MS);
    const page = await context.newPage();

    await page.goto(`http://127.0.0.1:${harness.port}/?s=${SESSION}`, { waitUntil: 'domcontentloaded' });
    await page.locator('#term').waitFor({ state: 'visible' });
    await waitFor(() => host.messages.some(m => m.t === 'sb' && m.s === SESSION), 'scrollback request', TEST_TIMEOUT_MS);
    host.sendScrollback(SESSION, 'boot\n');
    await waitFor(async () => {
      const text = await page.locator('#status').innerText();
      return text.includes(SESSION) && text.includes('live') ? true : null;
    }, 'live terminal status', TEST_TIMEOUT_MS);

    // A bare normal-buffer shell: no alt screen, no mouse tracking - exactly a plain owner bash session.
    host.sendOutput(SESSION, '\x1b[2J\x1b[H' + LINES.join('\r\n') + '\r\n');
    await sleep(500);

    const before = await page.evaluate(() => {
      const b = term.buffer.active;
      return { viewportY: b.viewportY, baseY: b.baseY, type: b.type };
    });
    assert.equal(before.type, 'normal', 'this test needs the normal buffer');
    assert.ok(before.baseY > 10, `the shell needs real scrollback to scroll (baseY=${before.baseY})`);

    const box = await page.locator('#term').boundingBox();
    const cx = box.x + box.width / 2, cy = box.y + box.height / 2;

    host.messages.length = 0;
    await page.mouse.move(cx, cy);
    for (let i = 0; i < 3; i++) { await page.mouse.wheel(0, -220); await sleep(50); }
    await sleep(200);

    const after = await page.evaluate(() => {
      const b = term.buffer.active;
      return { viewportY: b.viewportY, baseY: b.baseY };
    });
    assert.ok(after.viewportY < before.viewportY,
      `an owner shell must scroll its scrollback on the wheel (viewportY ${before.viewportY} -> ${after.viewportY}) - the exact regression`);

    // The wheel is a scroll, not input: nothing may have been written to the shell's stdin.
    const leaked = host.messages.filter(m => m.t === 'i')
      .map(m => Buffer.from(m.d || '', 'base64').toString('latin1'));
    assert.equal(leaked.length, 0,
      `the wheel leaked input frames to the owner shell's PTY: ${JSON.stringify(leaked)}`);
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
});
