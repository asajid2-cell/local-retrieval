// Regression: a plain click on an APP pane must reach the pty whether or not the app ARMED mouse
// reporting, and must still never reach a bare shell's stdin.
//
// The bug (Ahmed, 2026-10-03): "shift drag forces terminal selection but it instantly goes away when let
// go because the click event wins — we cannot actually copy anything. and in the case when selection wins
// then we can never click even with shift. it's an either/or." The owner-press mousedown handler was
// gated on appTakesMouse() — mouseGuard.inputAllowed(), true only once the app ARMS the mouse. On a
// session the app had not armed, the handler returned early AND xterm's own native reporting was off
// too, so the click forwarded NOTHING. Armed -> click works but native selection is suppressed (hold
// Shift to select); unarmed -> selection works but the click is dead. One winner, never both.
//
// The gate is now the SCREEN the app owns (appOwnsScreen()), not whether it happened to arm. A bare
// shell sits on the normal buffer, so it stays protected; an app pane (alt screen) gets its click in
// EITHER arm state, so selection (Shift) and clicking coexist. This file pins the source gate and both
// halves of the behaviour, on a loopback relay with a fake host.
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

// ---- source gate: the click handler must gate on the SCREEN, not the mouse being armed --------------

// The owner-press handler is the one that builds an SGR report. Pull just that listener out of the
// shipped file (it is anonymous, so it is asserted against its source, not executed).
function ownerPressMousedownSource() {
  const call = source.indexOf('ownerPress={button, cell, session:current}');
  assert.notEqual(call, -1, 'no mousedown handler sets ownerPress — the click forwarding is gone');
  const start = source.lastIndexOf("addEventListener('mousedown'", call);
  assert.notEqual(start, -1, 'the owner-press report is no longer reached from a mousedown listener');
  const end = source.indexOf('}, true);', call);
  assert.notEqual(end, -1, 'could not find the end of the owner-press mousedown listener');
  // Strip comment lines: the gate is asserted against CODE. A comment explaining the old gate names it,
  // and must not be mistaken for the gate itself.
  return source.slice(start, end).split('\n').filter(l => !/^\s*\/\//.test(l)).join('\n');
}

test('the click mousedown gate is the screen the app owns, not whether the app armed the mouse', () => {
  const handler = ownerPressMousedownSource();
  assert.match(handler, /appOwnsScreen\(\)/,
    'the click gate no longer checks which screen is up — a bare shell could take a report on its stdin');
  assert.doesNotMatch(handler, /appTakesMouse|inputAllowed|mouseActive/,
    'the click gate depends on the app ARMED the mouse again — an unarmed app pane would lose every click');
  assert.doesNotMatch(handler, /isVisibleOwner/,
    'the click gate depends on the session being the visible owner again — a web-created pane would lose clicks');
});

// ---- the real behaviour, on a loopback relay with a fake host ---------------------------------------

const TEST_TIMEOUT_MS = 8000;
const SESSION = 'click-gate-shell';
const INSTALL = 'npm install --save-dev playwright --package-lock=false\nnpx playwright install chromium';
const ARMED = '\x1b[?1000h\x1b[?1002h\x1b[?1003h\x1b[?1006h';   // what a TUI emits when it takes the mouse
const ALT = '\x1b[?1049h';                                        // what a TUI emits when it owns the screen

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

const LINES = Array.from({ length: 24 }, (_, i) =>
  `CLICKGATE line ${String(i + 1).padStart(2, '0')} alpha beta gamma delta epsilon zeta eta theta`);

async function bootPage(harness, host) {
  const context = await browser.newContext({ viewport: { width: 1000, height: 700 } });
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
  host.sendOutput(SESSION, '\x1b[2J\x1b[H' + LINES.join('\r\n') + '\r\n');
  await sleep(300);
  return { context, page };
}

// Click the center of the terminal; return the input frames the page sent to the pty as DECODED bytes.
// The fixture carries input bodies base64-encoded on the host wire (the relay sends `{t:'i',d:<b64>}`).
async function clickCenter(page, host) {
  const box = await page.locator('#term').boundingBox();
  host.messages.length = 0;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.up();
  await sleep(150);
  return host.messages.filter(m => m.t === 'i').map(m => Buffer.from(m.d, 'base64').toString('latin1'));
}

test('a click on an ARMED app pane forwards press+release; a click on a bare shell forwards nothing', async t => {
  if (!browser) return t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`);

  const harness = new RelayHarness();
  harness.start = () => startBrowserRelay(harness);
  let host = null, context = null;
  try {
    await harness.start();
    host = await harness.connectHost([{
      name: SESSION, alive: true, created: Date.now(), cols: 80, rows: 24,
      hasCommand: false, shellOnly: false, ready: true, owner: false, sessionId: 'click-gate-session',
    }]);
    const booted = await bootPage(harness, host);
    context = booted.context; const page = booted.page;

    // 1) BARE SHELL (normal buffer, no mouse tracking): a click must NOT reach its stdin.
    const shellFrames = await clickCenter(page, host);
    assert.equal(shellFrames.length, 0,
      `a click on a normal-buffer shell leaked to its stdin: ${JSON.stringify(shellFrames)}`);

    // 2) ARMED APP PANE (alt screen + mouse modes): a click must forward press+release.
    host.sendOutput(SESSION, ALT + ARMED);
    await sleep(250);
    const armedFrames = await clickCenter(page, host);
    assert.equal(armedFrames.length, 2,
      `a click on an armed app pane did not forward exactly press+release: ${JSON.stringify(armedFrames)}`);
    assert.ok(armedFrames.some(d => /M$/.test(d)) && armedFrames.some(d => /m$/.test(d)),
      `the forwarded report is not an SGR press+release pair: ${JSON.stringify(armedFrames)}`);

    // 3) UNARMED APP PANE (alt screen, app never armed): the exact regression — the click must STILL forward.
    host.sendOutput(SESSION, '\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l');
    await sleep(250);
    const unarmedFrames = await clickCenter(page, host);
    assert.equal(unarmedFrames.length, 2,
      `a click on an UNARMED app pane forwarded nothing (the either/or regression): ${JSON.stringify(unarmedFrames)}`);
    assert.ok(unarmedFrames.some(d => /M$/.test(d)) && unarmedFrames.some(d => /m$/.test(d)),
      `the forwarded report is not an SGR press+release pair: ${JSON.stringify(unarmedFrames)}`);
  } finally {
    if (context) await context.close();
    await harness.stop();
  }
});
