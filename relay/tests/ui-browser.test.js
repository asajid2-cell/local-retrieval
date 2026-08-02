const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const path = require('node:path');
const { test } = require('node:test');
const { chromium } = require('playwright');

const {
  RelayHarness,
  REPO,
  freePort,
  waitFor,
} = require('./harness');

const TEST_TIMEOUT_MS = 8000;
const SESSION_NAME = 'ui-browser-test';
const INSTALL_COMMANDS = [
  'npm install --save-dev playwright --package-lock=false',
  'npx playwright install chromium',
].join('\n');
const MOBILE_CONTEXT = {
  viewport: { width: 390, height: 700 },
  screen: { width: 390, height: 700 },
  isMobile: true,
  hasTouch: true,
  userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1',
};

let browser = null;
let browserError = null;

test.before(async () => {
  try {
    browser = await chromium.launch({ headless: true });
  } catch (error) {
    browserError = error;
    console.warn(
      `Chromium unavailable; skipping browser UI tests.\nInstall with:\n${INSTALL_COMMANDS}\nReason: ${error.message}`,
    );
  }
});

test.after(async () => {
  if (browser) await browser.close();
});

function skipWithoutChromium(t) {
  if (browser) return false;
  t.skip(`Chromium unavailable; run these commands first:\n${INSTALL_COMMANDS}\n${browserError && browserError.message}`);
  return true;
}

async function waitForQuiet(readValue, label, quietMs = 250, timeoutMs = TEST_TIMEOUT_MS) {
  let previous = Symbol('unset');
  let changedAt = Date.now();
  // await: readers may be async (page.evaluate), and an unawaited Promise is never === the last
  // one, so the value would look like it is changing forever and never settle.
  return await waitFor(async () => {
    const current = await readValue();
    if (current !== previous) {
      previous = current;
      changedAt = Date.now();
    }
    return Date.now() - changedAt >= quietMs;
  }, label, timeoutMs);
}

async function startBrowserRelay(harness) {
  // The real browser sends its page origin on the websocket handshake. The stock helper starts
  // before it knows the free port, so this preserves its lifecycle with a port-specific allowlist.
  harness.port = await freePort();
  harness.env.ALLOWED_WS_ORIGINS = `http://127.0.0.1:${harness.port}`;
  harness.proc = childProcess.spawn(process.execPath, ['server.js'], {
    cwd: REPO,
    env: {
      ...process.env,
      PORT: String(harness.port),
      MUX_HOST_TOKEN: 'test-token',
      MUX_TEST_MODE: '1',
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
  harness.proc.stdout.on('data', data => { harness.stdout += data.toString(); });
  harness.proc.stderr.on('data', data => { harness.stderr += data.toString(); });
  await waitFor(
    () => harness.stdout.includes(`multiplex-app on 0.0.0.0:${harness.port}`),
    'browser relay start',
    5000,
  );
}

async function withBrowserRelay(session, callback) {
  const harness = new RelayHarness();
  let host = null;
  let context = null;
  try {
    // Keep the public RelayHarness shape used by the existing suite while injecting the dynamic
    // browser-origin setup before its real process is spawned.
    harness.start = () => startBrowserRelay(harness);
    await harness.start();
    host = await harness.connectHost([session]);
    context = await browser.newContext(MOBILE_CONTEXT);
    context.setDefaultTimeout(TEST_TIMEOUT_MS);
    context.setDefaultNavigationTimeout(TEST_TIMEOUT_MS);
    const page = await context.newPage();
    await callback({ harness, host, page });
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
}

async function withSessionPage(options, callback) {
  const session = {
    name: SESSION_NAME,
    alive: true,
    created: Date.now(),
    cols: options.cols || 80,
    rows: options.rows || 24,
    localViewers: options.localViewers || 0,
    hasCommand: false,
    shellOnly: true,
    ready: true,
  };
  await withBrowserRelay(session, async resources => {
    const { harness, host, page } = resources;
    // Count the viewport reports the CLIENT sends. The storm this suite exists to catch lives here,
    // client -> relay, and never reaches the host: the relay only forwards a `resize` when the
    // NEGOTIATED grid changes, and a scroll does not change cols/rows, so counting host frames
    // reports zero whether the bug is present or not.
    await page.addInitScript(() => {
      window.__vReports = 0;
      const send = WebSocket.prototype.send;
      WebSocket.prototype.send = function (data) {
        try { if (typeof data === 'string' && data[0] === 'v') window.__vReports += 1; } catch (e) {}
        return send.call(this, data);
      };
    });
    await page.goto(`http://127.0.0.1:${harness.port}/?s=${SESSION_NAME}`, {
      waitUntil: 'domcontentloaded',
    });
    await page.locator('#term').waitFor({ state: 'visible' });
    await waitFor(
      () => host.messages.some(message => message.t === 'sb' && message.s === SESSION_NAME),
      'terminal scrollback request',
      TEST_TIMEOUT_MS,
    );
    host.sendScrollback(SESSION_NAME, 'browser harness output\n');
    await waitFor(async () => {
      const text = await page.locator('#status').innerText();
      return text.includes(SESSION_NAME) && text.includes('live') ? true : null;
    }, 'live terminal status', TEST_TIMEOUT_MS);
    await callback(resources);
  });
}

// SCOPE, measured rather than assumed: this does NOT reproduce the production freeze. That bug
// needed a real mobile visual viewport whose offsetTop shifts under the keyboard, and headless
// chromium reports offsetTop 0 and never shifts, so the document never grows and the loop never
// starts — verified by restoring the exact regression (marginTop write + a visualViewport 'scroll'
// listener) and watching this test stay green. The guard for THAT regression is the source-level
// assertion in mobile-input.test.js, which does go red. What this test genuinely covers is the
// adjacent guarantee: ordinary scrolling must not push viewport reports at the server.
test('mobile page scrolling does not resize the terminal in a report storm', async t => {
  if (skipWithoutChromium(t)) return;

  await withSessionPage({}, async ({ host, page }) => {
    await waitFor(
      () => host.messages.some(message => message.t === 'resize'),
      'initial terminal size report',
      TEST_TIMEOUT_MS,
    );
    await waitForQuiet(
      () => host.messages.filter(message => message.t === 'resize').length,
      'initial terminal size reports settle',
    );
    host.messages.length = 0;
    await page.evaluate(() => { window.__vReports = 0; });

    await page.mouse.move(8, 8);
    for (let i = 0; i < 8; i += 1) await page.mouse.wheel(0, 160);
    await page.evaluate(() => {
      window.scrollTo(0, 1);
      if (window.visualViewport) {
        for (let i = 0; i < 8; i += 1) {
          window.visualViewport.dispatchEvent(new Event('scroll'));
        }
      }
    });
    await waitForQuiet(
      () => page.evaluate(() => window.__vReports),
      'post-scroll viewport reports settle',
    );

    const vReports = await page.evaluate(() => window.__vReports);
    const resizeReports = host.messages.filter(message => message.t === 'resize');
    // Scrolling changes nothing about the grid, so a correct client re-measures at most once and
    // reports nothing. The freeze was this number climbing with every scroll event, each one
    // re-measuring and pushing the PTY to resize.
    assert.ok(
      vReports <= 1,
      `The terminal stays responsive while the page is scrolled: scrolling sent ${vReports} viewport reports to the server (a resize storm is what froze the terminal mid-keystroke).`,
    );
    assert.ok(
      resizeReports.length <= 1,
      `The PTY is not resized by scrolling: expected at most one host resize, got ${resizeReports.length}.`,
    );
  });
});

test('the first keybar tap sends a key and keeps the terminal focused', async t => {
  if (skipWithoutChromium(t)) return;

  await withSessionPage({ localViewers: 1 }, async ({ host, page }) => {
    await page.locator('[data-mobile-view="keys"]').click();
    const enterButton = page.locator('#keybar button.core').nth(3);
    await waitFor(
      () => enterButton.isVisible(),
      'visible terminal keybar',
      TEST_TIMEOUT_MS,
    );

    const terminalInput = page.locator('#term .xterm-helper-textarea');
    await terminalInput.focus();
    assert.equal(
      await page.evaluate(() => {
        const input = document.querySelector('#term .xterm-helper-textarea');
        return document.activeElement === input;
      }),
      true,
      'The terminal input is focused before the keybar tap so the user can continue typing.',
    );

    host.messages.length = 0;
    await enterButton.click();
    const inputFrame = await waitFor(
      () => host.messages.find(message => message.t === 'i' && message.s === SESSION_NAME),
      'first keybar tap action',
      TEST_TIMEOUT_MS,
    );
    assert.equal(
      Buffer.from(inputFrame.d || '', 'base64').toString('utf8'),
      '\r',
      'The first keybar tap sends Enter to the terminal instead of requiring a second tap.',
    );
    assert.equal(
      await page.evaluate(() => {
        const input = document.querySelector('#term .xterm-helper-textarea');
        return document.activeElement === input;
      }),
      true,
      'The terminal keeps focus after the first keybar tap so the next keystroke still reaches it.',
    );
  });
});

test('the horizontal pan affordance moves a terminal wider than the mobile viewport', async t => {
  if (skipWithoutChromium(t)) return;

  await withSessionPage({ cols: 120, rows: 40, localViewers: 1 }, async ({ page }) => {
    const before = await waitFor(async () => {
      const state = await page.evaluate(() => {
        const term = document.querySelector('#term');
        const affordance = document.querySelector('#scrollstate');
        return {
          className: term && term.className,
          scrollLeft: term && term.scrollLeft,
          scrollWidth: term && term.scrollWidth,
          clientWidth: term && term.clientWidth,
          affordanceHidden: !affordance || affordance.hidden,
          affordanceText: affordance ? affordance.textContent : '',
        };
      });
      return state.className.includes('pan')
        && state.scrollWidth > state.clientWidth
        && !state.affordanceHidden
        && state.affordanceText.includes('pan')
        ? state
        : null;
    }, 'horizontal pan affordance', TEST_TIMEOUT_MS);
    assert.ok(
      before.scrollWidth > before.clientWidth,
      'The terminal shows a reachable horizontal pan affordance when shared columns exceed the mobile viewport.',
    );

    const terminal = page.locator('#term');
    const touch = x => ({
      identifier: 1,
      clientX: x,
      clientY: 300,
      pageX: x,
      pageY: 300,
      screenX: x,
      screenY: 300,
    });
    await terminal.dispatchEvent('touchstart', {
      touches: [touch(300)],
      bubbles: true,
      cancelable: true,
    });
    await terminal.dispatchEvent('touchmove', {
      touches: [touch(100)],
      bubbles: true,
      cancelable: true,
    });
    await terminal.dispatchEvent('touchend', {
      touches: [],
      bubbles: true,
      cancelable: true,
    });

    const after = await waitFor(async () => {
      const scrollLeft = await page.locator('#term').evaluate(element => element.scrollLeft);
      return scrollLeft > before.scrollLeft ? scrollLeft : null;
    }, 'horizontal pan movement', TEST_TIMEOUT_MS);
    assert.ok(
      after > before.scrollLeft,
      'Dragging the pan affordance moves the terminal view so the user can reach columns outside the phone viewport.',
    );
  });
});

test('the chats page shows PC archive unavailable when discovery is unreachable', async t => {
  if (skipWithoutChromium(t)) return;

  await withBrowserRelay({
    name: SESSION_NAME,
    alive: true,
    created: Date.now(),
    cols: 80,
    rows: 24,
    hasCommand: false,
    shellOnly: true,
    ready: true,
  }, async ({ harness, page }) => {
    await page.route('**/multiplex/pc/api/discovery/**', route => route.abort('failed'));
    await page.goto(`http://127.0.0.1:${harness.port}/chats.html`, {
      waitUntil: 'domcontentloaded',
    });
    await waitFor(async () => {
      const status = await page.locator('#status').innerText();
      const state = await page.locator('#state').innerText();
      return status.includes('PC archive unavailable') && state.includes('PC archive unavailable')
        ? true
        : null;
    }, 'PC archive unavailable state', TEST_TIMEOUT_MS);

    const statusText = await page.locator('#status').innerText();
    const state = page.locator('#state');
    assert.equal(
      await state.isVisible(),
      true,
      'The chats page keeps an explicit unavailable state visible instead of leaving the user at a spinner.',
    );
    assert.match(
      statusText,
      /PC archive unavailable/,
      'The chats status tells the user that the PC archive is unavailable.',
    );
    assert.doesNotMatch(
      statusText,
      /Connecting|Loading/,
      'The chats status stops presenting an infinite loading state when the PC archive cannot be reached.',
    );
  });
});
