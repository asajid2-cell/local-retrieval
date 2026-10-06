// Regression: the wheel must reach the APP whenever the app has TAKEN THE MOUSE, on ANY buffer, and must
// scroll the mirror locally otherwise - for EVERY session, regardless of who owns it.
//
// Two wrong gates are pinned here, and they failed in opposite directions:
//   1. OWNERSHIP (isVisibleOwner()). A session created from the web is owner:false, so an owner gate killed
//      the wheel for every browser-launched app, and a plain owner shell on the normal buffer sent SGR
//      reports (`\x1b[<64;..M`) into the shell's stdin, where it echoed them as garbage while the mirror's
//      own scrollback sat unused ("some sessions just don't scroll").
//   2. BUFFER (appOwnsScreen(), the alternate screen only). A real terminal sends wheel reports whenever
//      ?1000/1002/1003 is set, on either buffer. The CC gateway paints its transcript IN PLACE in its own
//      ScrollBox on the NORMAL buffer, so its history lives in the app and xterm's base stays at the top:
//      an alternate-only gate made the wheel walk an ~8-row stub ("top - 8 up") while the real history was
//      unreachable. This is the reversal recorded in docs/scroll-parity.json (web/normal/tracking=true/*).
//
// The fix: the wheel is the app's whenever it has demonstrably taken the mouse (appTakesMouse()); otherwise
// the host's scrollback scrolls locally and nothing is forwarded. A bare shell never armed the mouse, so it
// keeps native scrollback - and SHIFT+wheel is the deliberate escape back to the local scrollback. Neither
// the owner flag nor the buffer is consulted. This file pins the source gates (cheap, always run) and the
// real behaviour on a live relay: a bare shell still scrolls locally, and a tracked NORMAL buffer gets the
// report.
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

test('sendOwnerWheel refuses unless the app has taken the mouse — buffer and ownership are not consulted', () => {
  const at = source.indexOf('function sendOwnerWheel(');
  assert.notEqual(at, -1, 'sendOwnerWheel() is gone');
  const end = source.indexOf('\n}', at);
  const body = source.slice(at, end);
  assert.match(body, /appTakesMouse\(\)/, 'sendOwnerWheel() no longer checks whether the app took the mouse');
  // The guard must be mouse INTENT alone. Not the buffer: an alternate-only gate made the CC gateway's
  // normal-buffer transcript unreachable (the recorded reversal). Not ownership: a web-created session is
  // owner:false, so an owner gate killed the wheel for every browser-launched app.
  assert.match(body, /if\(!appTakesMouse\(\)\s*\|\|\s*!px\)/,
    'the owner wheel guard must be the negation of appTakesMouse() alone');
  assert.doesNotMatch(body, /appOwnsScreen/, 'sendOwnerWheel() still consults the buffer type');
  assert.doesNotMatch(body, /isVisibleOwner/,
    'sendOwnerWheel() consults isVisibleOwner() again - a web-created (owner:false) app would lose the wheel');
});

test('the wheel router hands the app a record whenever it has taken the mouse, on either buffer', () => {
  const start = source.indexOf("$('#term').addEventListener('wheel'");
  assert.notEqual(start, -1, 'the capture-phase wheel router is gone');
  const end = source.indexOf('}, {passive:false, capture:true});', start);
  assert.notEqual(end, -1, 'could not find the end of the wheel router');
  const router = source.slice(start, end);
  // The app branch gates on mouse intent alone, so a tracked normal-buffer app gets the report; a bare
  // shell (which never armed the mouse) still falls through to the local scrollback branch.
  assert.match(router, /if\(appTakesMouse\(\)\)/,
    'the router app branch is no longer gated on appTakesMouse()');
  assert.doesNotMatch(router, /appOwnsScreen/,
    'the router app branch still consults the buffer type - a tracked normal buffer would lose the wheel');
  assert.doesNotMatch(router, /isVisibleOwner/,
    'the wheel router consults isVisibleOwner() again - a web-created (owner:false) app would lose the wheel');
  // SHIFT+wheel is the deliberate escape back to the local scrollback, checked FIRST so a tracked app
  // (a leaked DECSET 1000 from a dead TUI) can never swallow it.
  const iShift = router.indexOf('e.shiftKey');
  const iApp = router.indexOf('if(appTakesMouse())');
  assert.notEqual(iShift, -1, 'the shift+wheel local-scrollback escape is gone');
  assert.ok(iShift < iApp, 'shift+wheel must be checked before the app branch');
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

// ---- the reversal: a tracked NORMAL-buffer app gets the wheel (the CC gateway shape) ------------------
// This is the fixture regression for the field report ("top - 8 up" with the real history unreachable). It
// goes RED on the old buffer-gated router: the notch fell through to the local scrollback (viewportY moved,
// no report left the browser) instead of reaching the app. Green once the app branch gates on mouse intent.

const TRACKED = 'owner-wheel-tracked';

test('a tracked NORMAL-buffer app receives the wheel report and the local scrollback stays put', async t => {
  if (!browser) return t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`);

  const harness = new RelayHarness();
  harness.start = () => startBrowserRelay(harness);
  let host = null, context = null;
  try {
    await harness.start();
    // owner:false on purpose: a browser-launched app pane. The old owner gate would ALSO have killed this.
    host = await harness.connectHost([{
      name: TRACKED, alive: true, created: Date.now(), cols: 80, rows: 24,
      hasCommand: true, shellOnly: false, ready: true, owner: false, sessionId: 'tracked-normal-session',
    }]);
    context = await browser.newContext({ viewport: { width: 1000, height: 700 } });
    context.setDefaultTimeout(TEST_TIMEOUT_MS);
    const page = await context.newPage();

    await page.goto(`http://127.0.0.1:${harness.port}/?s=${TRACKED}`, { waitUntil: 'domcontentloaded' });
    await page.locator('#term').waitFor({ state: 'visible' });
    await waitFor(() => host.messages.some(m => m.t === 'sb' && m.s === TRACKED), 'scrollback request', TEST_TIMEOUT_MS);
    host.sendScrollback(TRACKED, 'boot\n');
    await waitFor(async () => {
      const text = await page.locator('#status').innerText();
      return text.includes(TRACKED) && text.includes('live') ? true : null;
    }, 'live terminal status', TEST_TIMEOUT_MS);

    // The app paints its transcript IN PLACE on the NORMAL buffer and arms the mouse (DECSET 1002 + SGR
    // 1006) - the CC gateway shape. It never switches to the alternate screen.
    host.sendOutput(TRACKED, '\x1b[2J\x1b[H\x1b[?1002h\x1b[?1006h' + LINES.join('\r\n') + '\r\n');
    await sleep(500);
    await waitFor(() => page.evaluate(() => !!(window.__muxMouseGuard && window.__muxMouseGuard.inputAllowed())),
      'the app armed the mouse (mouseGuard.inputAllowed())', TEST_TIMEOUT_MS);

    const before = await page.evaluate(() => {
      const b = term.buffer.active;
      return { viewportY: b.viewportY, baseY: b.baseY, type: b.type };
    });
    assert.equal(before.type, 'normal', 'this test needs the normal buffer');
    assert.ok(before.baseY > 10, `need real scrollback to prove the local buffer does NOT move (baseY=${before.baseY})`);

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
    const reports = host.messages.filter(m => m.t === 'i')
      .map(m => Buffer.from(m.d || '', 'base64').toString('latin1'));
    assert.ok(reports.some(r => /\x1b\[<64;/.test(r)),
      `a tracked normal-buffer app must receive the wheel-up report (got ${JSON.stringify(reports)})`);
    assert.equal(after.viewportY, before.viewportY,
      `the local scrollback must NOT move when the app owns the wheel (viewportY ${before.viewportY} -> ${after.viewportY})`);
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
});
