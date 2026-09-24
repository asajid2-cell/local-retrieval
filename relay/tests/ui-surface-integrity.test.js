// THE SURFACE STAYS REACHABLE.
//
// The report is "the bottom nav bar or random areas are black or do not render" and "sometimes we cannot
// scroll, select or interact at all, only type" - healed only by toggling fullscreen or refreshing.
//
// Measured in a real browser over the whole transition matrix (boot, view switches, the copy overlay,
// zen, font, keybar, dialogs, rotate, keyboard shrink, and a full-screen TUI in the alternate buffer with
// mouse tracking), the GEOMETRY is correct in every one of those states. So the defect is not a layout
// rule that can be fixed once - it is a state the browser reaches that nothing checks for. These tests
// therefore assert the PROPERTY (the surface is reachable, the nav is on screen) rather than any
// particular CSS, and then prove the integrity watch REPAIRS a stale state instead of only noticing it.
//
// Self-contained on purpose: it owns its relay glue so it does not depend on ui-browser.test.js, whose
// helpers are not exported and whose context is fixed to one viewport.
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const { test } = require('node:test');

const { RelayHarness, REPO, freePort, launchBrowser, waitFor, sleep } = require('./harness');

const TEST_TIMEOUT_MS = 10000;
const SESSION_NAME = 'surface-test';
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
  try { browser = await launchBrowser(); } catch (error) { browserError = error; }
});

test.after(async () => { if (browser) await browser.close(); });

function skipWithoutChromium(t) {
  if (browser) return false;
  t.skip(`Chromium unavailable: ${browserError && browserError.message}`);
  return true;
}

async function startBrowserRelay(harness) {
  // The real browser sends its page origin on the websocket handshake, so the port-specific allowlist
  // has to be in place before the process is spawned.
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
      MUX_TEST_PERSIST_FAULT_FILE: require('node:path').join(harness.tmp, '.persist-fault.json'),
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
  await waitFor(() => harness.stdout.includes(`multiplex-app on 127.0.0.1:${harness.port}`), 'relay start', TEST_TIMEOUT_MS);
}

async function withSurfacePage(session, callback) {
  const harness = new RelayHarness();
  let host = null, context = null;
  try {
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

// Read straight off the live page: the geometry the user actually sees, and the one element a pointer
// would land on at the terminal's centre.
const SURFACE_SNAPSHOT = () => {
  const q = s => document.querySelector(s);
  const vh = window.innerHeight;
  const term = q('#term'), r = term.getBoundingClientRect();
  const nav = q('#mobileNav'), nr = nav.getBoundingClientRect();
  const hit = document.elementFromPoint(Math.round(r.left + r.width / 2), Math.round(r.top + r.height / 2));
  return {
    vh,
    appH: Math.round(q('#app').getBoundingClientRect().height),
    bodyScroll: document.body.scrollHeight,
    termH: Math.round(r.height),
    termBottom: Math.round(r.bottom),
    navShown: getComputedStyle(nav).display !== 'none',
    navH: Math.round(nr.height),
    navTop: Math.round(nr.top),
    navBottom: Math.round(nr.bottom),
    hitInTerm: !!(hit && term.contains(hit)),
    appInert: q('#app').hasAttribute('inert'),
    // An overlay on top of the terminal is a legitimate reason for the centre not to hit the terminal.
    overlayOpen: (!q('#copyview').hidden) || q('#empty').style.display !== 'none'
      || [...document.querySelectorAll('dialog')].some(d => d.open),
    integrity: window.__muxLastIntegrityHeal || null,
  };
};

function assertSurfaceReachable(s, label) {
  assert.ok(Math.abs(s.appH - s.vh) <= 1, `${label}: #app must fill the viewport (appH=${s.appH} vh=${s.vh})`);
  assert.ok(s.bodyScroll <= s.vh + 2, `${label}: the page must not scroll (body=${s.bodyScroll} vh=${s.vh})`);
  // The terminal view is display:none in the sessions/fleet views, so a zero height there is correct and
  // only the states where it IS on screen carry the reachability promise.
  if (s.termH > 0) {
    assert.ok(s.termH >= 80, `${label}: the terminal must keep a usable height (${s.termH})`);
    assert.ok(s.termBottom <= s.vh + 1, `${label}: the terminal must not overflow the viewport`);
    if (!s.overlayOpen) {
      assert.ok(s.hitInTerm, `${label}: the terminal centre must be reachable by a pointer`);
      assert.equal(s.appInert, false, `${label}: the app must not be inert with no overlay open`);
    }
  }
  if (s.navShown) {
    assert.ok(s.navH >= 40, `${label}: the bottom nav must keep its height (${s.navH})`);
    assert.ok(s.navTop >= 0 && s.navBottom <= s.vh + 1, `${label}: the bottom nav must stay on screen`);
    if (s.termH > 0) assert.ok(s.termBottom <= s.navTop + 1, `${label}: the terminal must not sit under the nav`);
  }
}

test('the terminal surface stays reachable across every transition', async t => {
  if (skipWithoutChromium(t)) return;
  const session = { name: SESSION_NAME, alive: true, shellOnly: true, sessionId: 'surface-sid', generationId: 'surface-generation' };
  await withSurfacePage(session, async ({ harness, host, page }) => {
    await page.goto(`http://127.0.0.1:${harness.port}/`);
    await waitFor(() => page.evaluate(() => typeof connect === 'function'), 'page boot');
    await page.evaluate(name => connect(name), SESSION_NAME);
    await waitFor(() => host.messages.some(m => m.t === 'sb'), 'scrollback request');
    host.sendScrollback(SESSION_NAME, Array.from({ length: 60 }, (_, i) => `line ${i + 1}`).join('\r\n'));

    const settle = async (label, action) => {
      if (action) await action();
      await sleep(450);
      assertSurfaceReachable(await page.evaluate(SURFACE_SNAPSHOT), label);
    };
    const jsClick = sel => page.evaluate(s => document.querySelector(s).click(), sel);
    const view = name => page.getByRole('button', { name, exact: true }).click();

    await settle('after attach');
    for (const name of ['Sessions', 'Terminal', 'Fleet', 'Terminal', 'Keys', 'Terminal']) {
      await settle(`view: ${name}`, () => view(name));
    }
    await settle('copy overlay open', () => jsClick('#copybtn'));
    await settle('copy overlay closed', () => jsClick('#copyclose'));
    await settle('zen on', () => jsClick('#zen'));
    await settle('zen off', () => jsClick('#zen'));
    await settle('font up', () => page.evaluate(() => setFont(fontSize + 1)));
    await settle('font down', () => page.evaluate(() => setFont(fontSize - 1)));
    await settle('keybar on', () => jsClick('#kbtoggle'));
    await settle('keybar off', () => jsClick('#kbtoggle'));
    await settle('manage dialog open', () => jsClick('#managebtn'));
    await settle('manage dialog closed', () => page.evaluate(() => document.querySelector('#managedlg').close()));
    await settle('rotate landscape', () => page.setViewportSize({ width: 700, height: 390 }));
    await settle('rotate portrait', () => page.setViewportSize({ width: 390, height: 700 }));
    await settle('keyboard shrink', () => page.setViewportSize({ width: 390, height: 420 }));
    await settle('keyboard restore', () => page.setViewportSize({ width: 390, height: 700 }));

    // A full-screen TUI takes the alternate buffer and the mouse. The surface must still be reachable
    // there - this is the state the "I can only type" reports come from.
    await settle('TUI: alternate buffer + mouse tracking', () => {
      host.sendOutput(SESSION_NAME, '\x1b[?1049h\x1b[?1002h\x1b[?1006h\x1b[2J\x1b[HTUI frame\r\nprompt > ');
    });
    assert.equal(await page.evaluate(() => window.__muxMouseGuard.stats().tracking), true,
      'the guard must see the app take the mouse');
    await settle('TUI: left the alternate buffer', () => {
      host.sendOutput(SESSION_NAME, '\x1b[?1049l\x1b[?1002l\x1b[?1006l');
    });

    // Nothing in that matrix is a fault, so the watch must not have fired once. A watch that heals a
    // healthy surface is worse than none: it would repaint under the user's fingers.
    assert.equal(await page.evaluate(() => window.__muxLastIntegrityHeal || null), null,
      'a healthy surface must never be healed by the integrity watch');
  });
});

test('the integrity watch repairs a stale inert instead of only reporting it', async t => {
  if (skipWithoutChromium(t)) return;
  const session = { name: SESSION_NAME, alive: true, shellOnly: true, sessionId: 'inert-sid', generationId: 'inert-generation' };
  await withSurfacePage(session, async ({ harness, page }) => {
    await page.goto(`http://127.0.0.1:${harness.port}/`);
    await waitFor(() => page.evaluate(() => typeof connect === 'function'), 'page boot');
    await page.evaluate(name => connect(name), SESSION_NAME);
    await sleep(300);

    // The copy overlay sets #app[inert] and its close clears it. A close that took another path - and any
    // other stale setter - leaves the whole surface pointer-dead while typing still works, which is the
    // report verbatim. Inject exactly that state.
    await page.evaluate(() => {
      window.__muxLastIntegrityHeal = null;
      document.querySelector('#app').setAttribute('inert', '');
    });
    await waitFor(async () => await page.evaluate(() => window.__muxLastIntegrityHeal && window.__muxLastIntegrityHeal.fault), 'integrity fault recorded');
    assert.equal(await page.evaluate(() => window.__muxLastIntegrityHeal.fault), 'app-inert');
    await waitFor(async () => (await page.evaluate(() => document.querySelector('#app').hasAttribute('inert'))) === false, 'stale inert repaired');
  });
});
