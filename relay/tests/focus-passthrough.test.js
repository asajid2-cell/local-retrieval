// R7: focus in/out reports are the APP's when the app asked for them.
//
// When an app enables focus tracking (DECSET 1004 - codex/claude/vim do), xterm emits ESC[I on focus and
// ESC[O on blur through onData. 1710834/06e46d7 classified those as terminal-generated reports and DROPPED
// them, which is right for a shell that never asked, and wrong for an app that did: it asked, it gets them.
// The split is on the session's own ?1004: a shell receives nothing, a 1004 session receives both.
//
// The reports must still never travel the typing path — a blur report fires mid-drag, and reading it as a
// keystroke is what wiped every fresh selection (pinned by selection-focus-report.test.js). So the pass is
// asserted here, and the selection-survival is asserted there.
//
// Red direction (HEAD): the reports are dropped for every session, so the "1004 session receives both" case
// gets an empty `i` stream. The "shell receives nothing" case is the guard and passes on both.
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const { test } = require('node:test');

const { RelayHarness, REPO, freePort, launchBrowser, waitFor } = require('./harness');

const TEST_TIMEOUT_MS = 8000;
const SESSION = 'focus-pass';
const INSTALL_COMMANDS = [
  'npm install --save-dev playwright --package-lock=false',
  'npx playwright install chromium',
].join('\n');
const FOCUS_ON = '\x1b[?1004h';   // what codex/claude/vim emit to ask for focus in/out reports

let browser = null;
let browserError = null;

test.before(async () => {
  try { browser = await launchBrowser(); }
  catch (error) {
    browserError = error;
    console.warn(`Chromium unavailable; skipping focus-passthrough tests.\n${INSTALL_COMMANDS}\nReason: ${error.message}`);
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

// Attach the session, optionally arm ?1004 first, then fire a real focus + blur on xterm's helper textarea
// (the true user path xterm listens on) and hand back the focus reports that reached the host as input.
async function withSession(armFocus, callback) {
  const harness = new RelayHarness();
  let host = null, context = null;
  try {
    harness.start = () => startBrowserRelay(harness);
    await harness.start();
    host = await harness.connectHost([{ name: SESSION, alive: true, shellOnly: !armFocus, cols: 80, rows: 24 }]);
    context = await browser.newContext({ viewport: { width: 1000, height: 700 } });
    context.setDefaultTimeout(TEST_TIMEOUT_MS);
    const page = await context.newPage();
    await page.goto(`http://127.0.0.1:${harness.port}/?s=${SESSION}`);
    await page.waitForFunction(() => !!document.querySelector('.xterm'));
    await waitFor(() => host.messages.some(m => m.t === 'sb' && m.s === SESSION), 'scrollback request', TEST_TIMEOUT_MS);
    host.sendScrollback(SESSION, 'boot\n');
    await page.waitForFunction(() => term.buffer.active.length > 0);
    if (armFocus) host.sendOutput(SESSION, FOCUS_ON);   // the app asks for focus reports
    await new Promise(r => setTimeout(r, 300));
    host.messages.length = 0;                            // only the focus reports matter from here
    await page.evaluate(() => {
      const ta = term.textarea;
      ta.blur(); ta.focus();                             // ESC[I (focus)
    });
    await new Promise(r => setTimeout(r, 150));
    await page.evaluate(() => term.textarea.blur());     // ESC[O (blur)
    await new Promise(r => setTimeout(r, 250));
    const input = host.messages
      .filter(m => m.t === 'i')
      .map(m => Buffer.from(m.d, 'base64').toString('latin1'));
    await callback({ input, page });
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
}

test('a shell without ?1004 receives no focus reports', async t => {
  if (skipWithoutChromium(t)) return;
  await withSession(false, async ({ input }) => {
    const focus = input.filter(s => s === '\x1b[I' || s === '\x1b[O');
    assert.equal(focus.length, 0,
      `a shell that never enabled ?1004 must receive no focus reports, got ${JSON.stringify(focus)}`);
  });
});

test('a session with ?1004 receives both focus-in and focus-out', async t => {
  if (skipWithoutChromium(t)) return;
  await withSession(true, async ({ input }) => {
    const focus = input.filter(s => s === '\x1b[I' || s === '\x1b[O');
    assert.ok(focus.includes('\x1b[I'), `the app asked for focus reports (?1004) but focus-in was not forwarded: ${JSON.stringify(focus)}`);
    assert.ok(focus.includes('\x1b[O'), `the app asked for focus reports (?1004) but focus-out was not forwarded: ${JSON.stringify(focus)}`);
  });
});
