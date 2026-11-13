'use strict';
// A key the user presses must reach the app byte-for-byte. Esc and Alt+<letter> are the ones that go
// missing in practice: they are the only ordinary terminal input that is a bare ESC or an ESC prefix, so a
// handler that "swallows Escape", a focus that never lands in the terminal, or a rewrite of the input
// string all cost them while ordinary characters keep working. Reported live: "buttons dont pass through,
// esc or alt p etc dont really pass through".
//
// This drives a real browser on a loopback relay with a fake host and reads the 'i' frames the relay put on
// the wire - not the page's intent, the bytes that were sent.
const test = require('node:test');
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const path = require('node:path');

const { RelayHarness, REPO, freePort, launchBrowser, waitFor, sleep } = require('./harness');

const TEST_TIMEOUT_MS = 10000;
const SESSION = 'keys-probe';
const INSTALL = 'npm install --save-dev playwright --package-lock=false\nnpx playwright install chromium';

let browser = null;
let browserError = null;
test.before(async () => {
  try { browser = await launchBrowser(); }
  catch (error) { browserError = error; console.warn(`Chromium unavailable; skipping.\n${INSTALL}\n${error.message}`); }
});
test.after(async () => { console.error('CLEANUP browser'); if (browser) await browser.close(); });

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

function teedRow(name) {
  return {
    name, alive: true, created: Date.now(), cols: 100, rows: 30,
    hasCommand: false, shellOnly: true, ready: true, kind: 'local-tab', owner: true,
    localFirst: true, localViewers: 1, sessionId: '', generationId: 'gen-keys', streamBytes: 0, aliases: [],
    gridOwner: 'local', gridOwnerLabel: '', gridEpoch: 7,
  };
}

// Every byte the relay put on the host wire for this session, decoded. Input frames are base64 'd'.
function inputBytes(host) {
  return host.messages.filter(m => m.t === 'i')
    .map(m => Buffer.from(m.d || '', 'base64').toString('latin1')).join('');
}

test('Esc, Alt+<letter>, Ctrl chords and plain characters all reach the app as their own bytes', async t => {
  if (!browser) return t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`);
  const harness = new RelayHarness();
  harness.start = () => startBrowserRelay(harness);
  let context = null;
  try {
    await harness.start();
    const host = await harness.connectHost([teedRow(SESSION)]);
    t.after(() => host.close());
    context = await browser.newContext({ viewport: { width: 1100, height: 760 } });
    context.setDefaultTimeout(TEST_TIMEOUT_MS);
    const page = await context.newPage();
    await page.goto(`http://127.0.0.1:${harness.port}/?s=${SESSION}`, { waitUntil: 'domcontentloaded' });
    await page.locator('#term').waitFor({ state: 'visible' });
    await waitFor(() => host.messages.some(m => m.t === 'sb' && m.s === SESSION), 'scrollback request', TEST_TIMEOUT_MS);
    host.sendScrollback(SESSION, 'keys probe\n');
    await waitFor(async () => {
      const text = await page.locator('#status').innerText();
      return text.includes(SESSION) && text.includes('live') ? true : null;
    }, 'live terminal status', TEST_TIMEOUT_MS);

    // Focus the way a user does: click the terminal.
    const box = await page.locator('#term').boundingBox();
    await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
    await sleep(200);

    const cases = [
      ['Escape', () => page.keyboard.press('Escape'), '\x1b'],
      ['Alt+p', () => page.keyboard.press('Alt+p'), '\x1bp'],
      ['Alt+b', () => page.keyboard.press('Alt+b'), '\x1bb'],
      ['Ctrl+L', () => page.keyboard.press('Control+l'), '\x0c'],
      ['plain k', () => page.keyboard.press('k'), 'k'],
      ['Shift+Tab', () => page.keyboard.press('Shift+Tab'), '\x1b[Z'],
    ];
    const seen = {};
    for (const [label, send, want] of cases) {
      host.messages.length = 0;
      await send();
      await sleep(200);
      const got = inputBytes(host);
      seen[label] = got;
      assert.equal(got, want, `${label} sent ${JSON.stringify(got)} to the app, want ${JSON.stringify(want)} (all: ${JSON.stringify(seen)})`);
    }
    // A second Escape after other keys must still be a bare ESC - a "one Escape per session" latch is a
    // plausible way for this to half-work, and would look exactly like the live report.
    host.messages.length = 0;
    await page.keyboard.press('Escape');
    await sleep(200);
    assert.equal(inputBytes(host), '\x1b', 'the SECOND Escape did not reach the app');
  } catch (e) {
    console.error(`RELAY STDOUT:\n${(harness.stdout || '').slice(-2000)}`);
    console.error(`RELAY STDERR:\n${(harness.stderr || '').slice(-2000)}`);
    throw e;
  } finally {
    if (context) await context.close();
    await harness.stop();
  }
});
