'use strict';
// The phone thread view, driven in a real browser on a phone viewport against a loopback relay.
//
// The point of this view is that it is a RENDER of the session's own bytes: the relay sends terminal output
// exactly as it always has, and the page groups those rows into messages. So the test feeds the relay real
// terminal output (an agent-shaped screen) and asserts what the phone shows, that the terminal view still
// works, and that the composer reaches the session through the same input path - not a side channel.
const test = require('node:test');
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const path = require('node:path');
const { RelayHarness, REPO, freePort, launchBrowser, waitFor, sleep } = require('./harness');

const TEST_TIMEOUT_MS = 15000;
const SESSION = 'threads-probe';
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
      MUX_HOST_SB_WAIT_MS: '400',
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
    localFirst: true, localViewers: 1, sessionId: '', generationId: 'gen-threads', streamBytes: 0, aliases: [],
    gridOwner: 'local', gridOwnerLabel: '', gridEpoch: 7,
  };
}

// An agent-shaped screen. Each line is terminal output, exactly as the pty would emit it: the glyphs are the
// app's own, and the long line WRAPS at the relay's 100-column width, which the page must glue back together.
const SCREEN = [
  '',
  '⏺ Paired and drove the set, and found the real watch failure.',
  '',
  '  What the set did at 22:37-22:39:',
  '',
  '  Then playback died, in this order:',
  '  1. Cold start: encoder silent 44.6 s.',
  '  2. The manifest long-poll hit its stall deadline.',
  '',
  '✻ Cooked for 46m 39s',
  '❯ ',
].join('\r\n');

async function openPhone(harness) {
  const context = await browser.newContext({
    viewport: { width: 390, height: 844 }, deviceScaleFactor: 3,
    isMobile: true, hasTouch: true,
    userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1',
  });
  context.setDefaultTimeout(TEST_TIMEOUT_MS);
  const page = await context.newPage();
  await page.goto(`http://127.0.0.1:${harness.port}/?s=${SESSION}`, { waitUntil: 'domcontentloaded' });
  await page.locator('#term').waitFor({ state: 'visible' });
  return { context, page };
}

async function boot(t) {
  const harness = new RelayHarness();
  harness.start = () => startBrowserRelay(harness);
  await harness.start();
  const host = await harness.connectHost([teedRow(SESSION)]);
  t.after(() => host.close());
  // The scrollback request is caused by a VIEWER attaching, so the page must open first.
  const { context, page } = await openPhone(harness);
  t.after(async () => { await context.close(); await harness.stop(); });
  await waitFor(() => host.messages.some(m => m.t === 'sb' && m.s === SESSION), 'scrollback request', TEST_TIMEOUT_MS);
  host.sendScrollback(SESSION, SCREEN);
  await waitFor(async () => {
    const text = await page.locator('#status').innerText();
    return text.includes(SESSION) && text.includes('live') ? true : null;
  }, 'live terminal status', TEST_TIMEOUT_MS);
  return { harness, host, page };
}

test('the phone shows the same session as a conversation, and the nav reaches it', async t => {
  if (!browser) return t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`);
  const { page } = await boot(t);

  // The view is reachable from the phone nav, and it is the nav that switches, not a URL.
  await page.locator('[data-mobile-view="threads"]').click();
  await page.locator('#threads').waitFor({ state: 'visible' });
  assert.equal(await page.locator('#composer').isVisible(), true, 'the composer appears with the view');

  await waitFor(async () => (await page.locator('#threads .th-msg').count()) > 0, 'thread messages painted', TEST_TIMEOUT_MS);
  const msgs = await page.evaluate(() => [...document.querySelectorAll('#threads .th-msg')]
    .map(m => ({ role: [...m.classList].find(c => c !== 'th-msg'), who: m.querySelector('.th-who').textContent, text: m.querySelector('.th-body').textContent })));
  assert.ok(msgs.length >= 2, `an agent screen is several messages, got ${JSON.stringify(msgs.map(m => m.role))}`);
  assert.ok(msgs.some(m => m.role === 'assistant' && /Paired and drove the set/.test(m.text)), 'the assistant turn is a message');
  assert.ok(msgs.some(m => m.role === 'status' && /Cooked for/.test(m.text)), 'the run summary is its own message');
  // A bare `❯` row is the empty prompt line above the composer, not a message: showing it as an empty
  // bubble reads as a broken turn. The glyph-stripped body is what decides, and the tests above prove the
  // role mapping via classify() and the threads.test.js fixture, where the user turn has real text.
  assert.ok(!msgs.some(m => m.role === 'user' && m.text.trim() === ''),
    'a prompt row with nothing typed must not become an empty bubble');
  // Body lines belong to the message above them, never as standalone messages.
  assert.ok(!msgs.some(m => /^What the set did/.test(m.text.trim())), 'a body line did not become its own message');
  assert.ok(msgs.some(m => /What the set did/.test(m.text)), 'and it IS inside the message that owns it');
});

test('switching back to the terminal keeps the real grid, untouched', async t => {
  if (!browser) return t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`);
  const { page } = await boot(t);
  await page.locator('[data-mobile-view="threads"]').click();
  await page.locator('#threads').waitFor({ state: 'visible' });
  await sleep(300);
  await page.locator('[data-mobile-view="terminal"]').click();
  await page.locator('#term').waitFor({ state: 'visible' });
  await sleep(300);
  // The thread view is a render over the buffer; it must never have resized or reflowed the terminal.
  const st = await page.evaluate(() => ({ cols: term.cols, rows: term.rows, type: term.buffer.active.type, text: termVisibleText() }));
  assert.equal(st.type, 'normal', 'the terminal is on its normal screen');
  assert.ok(st.cols >= 20 && st.rows >= 8, `the grid is still a real grid: ${st.cols}x${st.rows}`);
  assert.match(st.text, /Paired and drove the set/, 'the terminal still holds the session output');
});

test('the composer sends through the session input path, byte for byte', async t => {
  if (!browser) return t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`);
  const { host, page } = await boot(t);
  await page.locator('[data-mobile-view="threads"]').click();
  await page.locator('#threads').waitFor({ state: 'visible' });
  host.messages.length = 0;
  await page.locator('#threadinput').fill('please check the logs');
  await page.locator('#sendthread').click();
  await waitFor(() => host.messages.some(m => m.t === 'i'), 'input frame from the composer', TEST_TIMEOUT_MS);
  await sleep(300);
  const sent = host.messages.filter(m => m.t === 'i').map(m => Buffer.from(m.d || '', 'base64').toString('latin1')).join('');
  assert.equal(sent, 'please check the logs\r',
    `the composer must deliver the text then exactly one CR, got ${JSON.stringify(sent)}`);
});
