// Option is the Mac's Alt key, and in a TERMINAL it must arrive at the app as META - the ESC prefix on
// the key - never as the third-level-shift glyph the Mac keyboard composes. Left at xterm's default
// (macOptionIsMeta:false), a Mac browser turns Option+P into the literal 'π' (Option+O 'ø', Option+T
// '†') and hands the app the glyph instead of the chord. The app inside a session runs on the WINDOWS
// host (Claude Code / Gateway), reads Alt from the ESC prefix, and only maps the composed glyph back to
// a chord when ITS OWN platform is macOS - which it never is here - so the shortcut never fires and the
// glyph is inserted. The client therefore sets macOptionIsMeta:true, and these tests pin the wire bytes
// the relay forwards to the host.
//
// xterm's third-level-shift test is Mac-only (`isMac && !macOptionIsMeta && altKey && ...`), so the flag
// is INERT on Windows/Linux: the control test below proves a Windows client's Alt+key is unchanged. The
// client also reports its platform to xterm from navigator.platform, which is what these tests override
// to stand in a Mac; the Mac-only composition itself happens in the OS, so on this Windows box a
// pre-fix Mac client simply emits NOTHING for Option+key rather than 'π' - the same ceded keystroke, a
// different symptom, and both are the bug this file pins. The composed glyph is asserted absent either
// way, so the test cannot pass by the wrong route.
//
// The keybar path is the other half: a phone taps the sticky Alt button and then a char, which never
// goes through xterm's keydown at all (term.onData -> applyModsToChar prepends ESC). It already sent
// ESC+key before this fix, and is pinned here so a future change to the modifier machinery cannot
// silently regress it.
const test = require('node:test');
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const path = require('node:path');

const { RelayHarness, REPO, freePort, launchBrowser, waitFor, sleep } = require('./harness');

const COLS = 80;
const ROWS = 24;
const TEST_TIMEOUT_MS = 8000;
const INSTALL = 'npm install --save-dev playwright --package-lock=false\nnpx playwright install chromium';

const APP_LOG = '\x1b[?1049h\x1b[?1000h\x1b[?1006hREADY\r\nVISIBLE FOOTER\r\n';
// The glyphs a Mac keyboard composes for the three chords under test - they must never reach the host.
const COMPOSED = /[\u03c0\u00f8\u2020]/;

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

function hostedSession(name) {
  return {
    name, alive: true, created: Date.now(), lastOut: Date.now(), cols: COLS, rows: ROWS,
    hasCommand: true, shellOnly: false, ready: true, owner: false, kind: 'command',
    sessionId: name + '-session', aliases: [],
  };
}

// Open a viewer whose browser REPORTS `platform` to xterm (navigator.platform), boot it to live, and
// return it. The keybar is switched on so the sticky-modifier path is reachable on a desktop viewport.
async function bootViewer(harness, host, session, platform) {
  const context = await browser.newContext({ viewport: { width: 1000, height: 700 } });
  context.setDefaultTimeout(TEST_TIMEOUT_MS);
  // Runs before any page script, so xterm's module-load-time platform read sees this value.
  await context.addInitScript(p => {
    try { Object.defineProperty(navigator, 'platform', { get: () => p, configurable: true }); } catch (e) {}
  }, platform);
  await context.addInitScript(() => { try { localStorage.setItem('mux_keybar', 'true'); } catch {} });

  const page = await context.newPage();
  await page.goto(`http://127.0.0.1:${harness.port}/?s=${session}`, { waitUntil: 'domcontentloaded' });
  await page.locator('#term').waitFor({ state: 'visible' });
  const sb = await host.waitFor(m => m.t === 'sb' && m.s === session, 'scrollback request');
  host.sendScrollback(session, APP_LOG, sb);
  await waitFor(async () => {
    const text = await page.locator('#status').innerText();
    return text.includes(session) && text.includes('live') ? true : null;
  }, 'live terminal status', TEST_TIMEOUT_MS);

  const box = await page.locator('#term').boundingBox();
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
  await sleep(150);
  return { context, page };
}

// The bytes the relay forwarded to the host for the input seam, decoded from the base64 JSON `i` frame.
function hostInputs(host) {
  return host.messages
    .filter(m => m.t === 'i')
    .map(m => Buffer.from(m.d || '', 'base64').toString('latin1'));
}

async function withClient(t, platform, session, fn) {
  if (!browser) return t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`);
  const harness = new RelayHarness();
  harness.start = () => startBrowserRelay(harness);
  let host = null, context = null;
  try {
    await harness.start();
    host = await harness.connectHost([hostedSession(session)]);
    const viewer = await bootViewer(harness, host, session, platform);
    context = viewer.context;
    await fn(viewer.page, host);
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
}

// ---- the fix: a macOS client must send Option+key as META ------------------------------------------

test('a macOS client sends Option+P/O/T as ESC+key, never the composed glyph', async t => {
  await withClient(t, 'MacIntel', 'opt-mac', async (page, host) => {
    assert.equal(await page.evaluate(() => navigator.platform), 'MacIntel', 'the client must report macOS to xterm');
    for (const [combo, want] of [['Alt+p', '\x1bp'], ['Alt+o', '\x1bo'], ['Alt+t', '\x1bt']]) {
      host.messages.length = 0;
      await page.evaluate(() => term.focus());
      await page.keyboard.press(combo);
      await sleep(200);
      const got = hostInputs(host);
      assert.deepEqual(got, [want],
        `${combo} on a macOS client must reach the host as ${JSON.stringify(want)} (got ${JSON.stringify(got)})`);
      assert.equal(got.some(s => COMPOSED.test(s)), false,
        `${combo} must not compose a glyph (got ${JSON.stringify(got)})`);
    }
  });
});

// ---- control: a Windows client's Alt+key is unchanged by the Mac-only flag -------------------------

test('a Windows client still sends Alt+P/O/T as ESC+key', async t => {
  await withClient(t, 'Win32', 'opt-win', async (page, host) => {
    assert.equal(await page.evaluate(() => navigator.platform), 'Win32');
    for (const [combo, want] of [['Alt+p', '\x1bp'], ['Alt+o', '\x1bo'], ['Alt+t', '\x1bt']]) {
      host.messages.length = 0;
      await page.evaluate(() => term.focus());
      await page.keyboard.press(combo);
      await sleep(200);
      assert.deepEqual(hostInputs(host), [want], `${combo} on a Windows client must be unchanged`);
    }
  });
});

// ---- the keybar sticky-Alt path (the phone route), and that the mod clears after one char ----------

test('the keybar sticky Alt applies ESC to the next char and clears after it', async t => {
  await withClient(t, 'MacIntel', 'opt-keybar', async (page, host) => {
    // Tap the keybar Alt, then type 'p' - the sticky modifier must prefix ESC and then reset.
    host.messages.length = 0;
    await page.locator('.key.mod-alt').first().click();
    await sleep(120);
    await page.keyboard.press('p');
    await sleep(200);
    assert.deepEqual(hostInputs(host), ['\x1bp'], 'keybar Alt then p must send ESC+p');
    assert.equal(await page.locator('.key.mod-alt').first().evaluate(el => el.classList.contains('active')), false,
      'the sticky Alt must clear once it has been applied');

    // The very next char is plain - the modifier did not stick across keystrokes.
    host.messages.length = 0;
    await page.keyboard.press('q');
    await sleep(200);
    assert.deepEqual(hostInputs(host), ['q'], 'the char after a sticky combo must be unmodified');
  });
});
