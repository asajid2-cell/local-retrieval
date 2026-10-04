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

// The transition matrix above proves the surface is REACHABLE in a full-screen TUI; it does not prove the
// session is USABLE there. With mouse tracking on, xterm hands every button event to the app, so the three
// things the report names have to be driven for real: a selection (Shift-drag on a desktop, the Sel toggle
// on a phone), a scroll that reaches the app, and the bottom bar.
test('a full-screen TUI can still be scrolled, selected and driven from the nav', async t => {
  if (skipWithoutChromium(t)) return;
  const session = { name: SESSION_NAME, alive: true, shellOnly: true, sessionId: 'tui-sid', generationId: 'tui-generation' };
  await withSurfacePage(session, async ({ harness, host, page }) => {
    await page.goto(`http://127.0.0.1:${harness.port}/`);
    await waitFor(() => page.evaluate(() => typeof connect === 'function'), 'page boot');
    await page.evaluate(name => connect(name), SESSION_NAME);
    await sleep(400);

    host.sendOutput(SESSION_NAME, '\x1b[?1049h\x1b[?1002h\x1b[?1006h\x1b[2J\x1b[H' +
      Array.from({ length: 24 }, (_, i) => `TUI line ${i + 1}`).join('\r\n'));
    await sleep(500);
    assert.equal(await page.evaluate(() => window.__muxMouseGuard.stats().tracking), true,
      'the app must own the mouse for this test to mean anything');

    // Everything the browser actually sent the session, in order.
    const forwarded = () => host.messages
      .filter(m => m.t === 'i' && m.s === SESSION_NAME)
      .map(m => Buffer.from(m.d, 'base64').toString('utf8')).join('');
    const centre = await page.evaluate(() => {
      const r = document.querySelector('#term').getBoundingClientRect();
      return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) };
    });
    const selection = () => page.evaluate(() => ({ has: term.hasSelection(), text: term.getSelection() || '' }));
    const drag = async () => {
      await page.mouse.move(centre.x - 60, centre.y - 20);
      await page.mouse.down();
      await page.mouse.move(centre.x + 60, centre.y + 20, { steps: 6 });
      await page.mouse.up();
      await sleep(200);
    };
    const reset = () => page.evaluate(() => {
      if (selectMode) toggleSelect();
      autoFreeze = false;
      try { term.clearSelection(); } catch (e) {}
      flushFrozen();
    });

    // 1. Shift-drag: the desktop escape hatch. The highlight must survive the mouseup.
    const beforeShift = forwarded().length;
    await page.keyboard.down('Shift');
    await drag();
    await page.keyboard.up('Shift');
    const shifted = await selection();
    assert.ok(shifted.has, `Shift-drag in a mouse-tracking TUI must leave a selection (got ${JSON.stringify(shifted)})`);
    assert.ok(shifted.text.trim().length > 0, 'Shift-drag must select real text, not an empty range');
    // ...and the gesture must not have been reported to the app as a click: it was a selection, not a click.
    assert.equal(/\x1b\[<[0-9]+;/.test(forwarded().slice(beforeShift)), false,
      'a selection gesture must not be forwarded to the app as a mouse report');
    await reset();

    // 2. The phone's Sel toggle: the same promise, with no modifier to hold.
    await page.evaluate(() => toggleSelect());
    await drag();
    const toggled = await selection();
    assert.ok(toggled.has, `the Sel toggle must make a drag select in a mouse-tracking TUI (got ${JSON.stringify(toggled)})`);
    assert.ok(toggled.text.trim().length > 0, 'the Sel toggle must select real text');
    await reset();

    // 3. A plain drag is NOT a selection: the app asked for the mouse and must still get it. This is the
    // guard against "fixing" selection by switching mouse reporting off for everything.
    const beforePlain = forwarded().length;
    await drag();
    assert.ok(/^\x1b\[<[0-9]+;[0-9]+;[0-9]+[Mm]/m.test(forwarded().slice(beforePlain)),
      'a plain drag in a mouse-tracking TUI must still be forwarded to the app');
    assert.equal((await selection()).has, false, 'a plain drag must not steal the gesture from the app');

    // 4. The wheel must reach the app. The alternate buffer has no scrollback of its own, so the app owns
    // scrolling: what has to leave the page is the wheel mouse REPORT (SGR button 64/65), not a local
    // scroll and not an arrow key. This is the "no scrolling" half of the report.
    const beforeWheel = forwarded().length;
    await page.mouse.move(centre.x, centre.y);
    await page.mouse.wheel(0, -600);
    await sleep(250);
    const wheeled = forwarded().slice(beforeWheel);
    assert.ok(/\x1b\[<6[45];[0-9]+;[0-9]+[Mm]/.test(wheeled),
      `the wheel must reach the app as a wheel report in a full-screen session (got ${JSON.stringify(wheeled)})`);

    // 5. The bottom bar must be pressable while the app owns the mouse.
    await page.getByRole('button', { name: 'Sessions', exact: true }).click();
    await sleep(350);
    assert.ok(await page.evaluate(() => document.querySelector('#app').classList.contains('mobile-view-sessions')),
      'the bottom nav must drive the views in a full-screen session');
    await page.getByRole('button', { name: 'Terminal', exact: true }).click();
    await sleep(350);

    // 6. A leak-tripped guard must not take selection down with it. When the session echoes one of our own
    // reports straight back the guard stops forwarding (proof nothing consumed it) and clears its mode
    // mirror so the next DECSET reads as a real re-arm - but xterm's mouse reporting is untouched, so its
    // selection service stays disabled while the wheel keeps working. That divergence is "scroll works,
    // selection won't at all", and the forced-selection gesture has to survive it: it is gated on xterm's
    // state, not on our forwarding policy.
    const beforeLeak = forwarded().length;
    await drag();
    const echoed = forwarded().slice(beforeLeak);
    assert.ok(/\x1b\[<[0-9]+;[0-9]+;[0-9]+[Mm]/.test(echoed), 'an armed guard must forward a plain drag');
    host.sendOutput(SESSION_NAME, echoed);
    await waitFor(async () => await page.evaluate(() => window.__muxMouseGuard.stats().blocked), 'leak detector tripped');
    assert.equal(await page.evaluate(() => window.__muxMouseGuard.stats().mouseActive), true,
      'xterm must still own the mouse after the guard trips, or this test proves nothing');
    await page.keyboard.down('Shift');
    await drag();
    await page.keyboard.up('Shift');
    const afterLeak = await selection();
    assert.ok(afterLeak.has, `selection must survive a leak-tripped guard (got ${JSON.stringify(afterLeak)})`);
    await reset();

    assert.equal(await page.evaluate(() => window.__muxLastIntegrityHeal || null), null,
      'a usable full-screen session must never be healed by the integrity watch');
  });
});

// The watch judged only geometry and the terminal's centre, so a bar that was perfectly placed but covered
// by something invisible read as healthy - and the user was left to toggle fullscreen. It must now judge
// whether a pointer can actually REACH the controls.
test('the integrity watch notices a bottom bar a pointer can no longer reach', async t => {
  if (skipWithoutChromium(t)) return;
  const session = { name: SESSION_NAME, alive: true, shellOnly: true, sessionId: 'nav-sid', generationId: 'nav-generation' };
  await withSurfacePage(session, async ({ harness, page }) => {
    await page.goto(`http://127.0.0.1:${harness.port}/`);
    await waitFor(() => page.evaluate(() => typeof connect === 'function'), 'page boot');
    await page.evaluate(name => connect(name), SESSION_NAME);
    await sleep(400);

    // The bar keeps its exact geometry; an invisible layer takes the pointer. Nothing about the layout is
    // wrong, which is precisely why the geometry-only check called this healthy.
    await page.evaluate(() => {
      window.__muxLastIntegrityHeal = null;
      const cover = document.createElement('div');
      cover.id = 'pointer-cover';
      cover.style.cssText = 'position:fixed;left:0;right:0;bottom:0;height:80px;z-index:9;background:transparent';
      document.body.appendChild(cover);
    });
    await waitFor(async () => await page.evaluate(() => window.__muxLastIntegrityHeal && window.__muxLastIntegrityHeal.fault), 'integrity fault recorded');
    assert.equal(await page.evaluate(() => window.__muxLastIntegrityHeal.fault), 'nav-not-hit',
      'an unreachable bottom bar must be a fault, not a healthy surface');

    // ...and once the pointer can reach it again the watch must go quiet, or it would repaint under the
    // user's fingers forever.
    await page.evaluate(() => { window.__muxLastIntegrityHeal = null; document.querySelector('#pointer-cover').remove(); });
    await sleep(3200);
    assert.equal(await page.evaluate(() => window.__muxLastIntegrityHeal || null), null,
      'a reachable bottom bar must not be healed');
    assert.ok(await page.evaluate(() => {
      const nav = document.querySelector('#mobileNav');
      return [...nav.querySelectorAll('button,a')].every(b => {
        const r = b.getBoundingClientRect();
        const h = document.elementFromPoint(Math.round(r.left + r.width / 2), Math.round(r.top + r.height / 2));
        return h && (h === b || b.contains(h));
      });
    }), 'every nav control must be the pointer target at its own centre');
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

test('the surface puts back rows the renderer dropped, with no transition to react to', async t => {
  if (skipWithoutChromium(t)) return;
  const session = { name: SESSION_NAME, alive: true, shellOnly: true, sessionId: 'lostrow-sid', generationId: 'lostrow-generation' };
  await withSurfacePage(session, async ({ harness, host, page }) => {
    // Force the DOM renderer: this test drops `.xterm-rows` elements to prove the row-loss watchdog puts
    // them back, and rows only EXIST under the DOM renderer. Under the shipped WebGL renderer the rows
    // live in a canvas and the watchdog correctly stands down, so there is nothing here to drop or repair.
    await page.goto(`http://127.0.0.1:${harness.port}/?mux_render=dom`);
    await waitFor(() => page.evaluate(() => typeof connect === 'function'), 'page boot');
    await page.evaluate(name => connect(name), SESSION_NAME);
    await sleep(400);

    // A full-screen TUI frame. The lines stay comfortably inside the grid: a line that fills it exactly
    // wraps one character onto the next row, and a single-character row is deliberately below the
    // detector's "too little to judge" floor, so a frame like that cannot carry this test.
    host.sendOutput(SESSION_NAME, '\x1b[?1049h\x1b[2J\x1b[H' +
      Array.from({ length: 60 }, (_, i) => `[row ${String(i + 1).padStart(2, '0')}] ` + 'x'.repeat(20)).join('\r\n'));
    await sleep(700);

    // A healthy surface must read as healthy. If this ever reports loss on a good screen the watchdog
    // repaints and re-seats the viewport every 2s, which is worse than the defect it was meant to fix.
    const healthy = await page.evaluate(() => terminalPaintLostRows());
    assert.ok(healthy, 'the surface must be judgeable at all');
    assert.equal(healthy.lost, 0, `a healthy surface must report no lost rows (got ${JSON.stringify(healthy)})`);

    // Drop rows the way the report shows them: the row elements stay, their text is gone. No transition, no
    // output, nothing the transition-driven heals could react to - so only a watchdog that actually looks
    // can find this. Blank and read in ONE synchronous task: a separate evaluate would let a pending
    // term.refresh repaint some rows in between and the red state would be half gone before it is asserted.
    const red = await page.evaluate(() => {
      const host = document.querySelector('#term'), hr = host.getBoundingClientRect();
      // Same floor the detector uses: a row with almost nothing on it is not judged, so it is not a
      // candidate for this test either.
      const rows = [...host.querySelectorAll('.xterm-rows > div')].filter(r => {
        const b = r.getBoundingClientRect();
        return r.textContent.trim().length >= 4 && b.bottom > hr.top + 2 && b.top < hr.bottom - 2;
      });
      const targets = rows.slice(2, 8);
      for (const r of targets) r.textContent = '';
      return { dropped: targets.length, seen: terminalPaintLostRows() };
    });
    assert.ok(red.dropped >= 4, `the frame must have left enough rows to drop (got ${red.dropped})`);
    assert.ok(red.seen && red.seen.lost >= red.dropped, `every dropped row must be visible to the detector (got ${JSON.stringify(red)})`);

    await waitFor(async () => {
      const m = await page.evaluate(() => window.__muxLastLostRows);
      return !!(m && m.lost >= 4);
    }, 'row loss recorded by the watch', 6000);
    // ...and the rows come back from the BUFFER, with nobody touching the page.
    await waitFor(async () => {
      const left = await page.evaluate(() => terminalPaintLostRows());
      return !!left && left.lost === 0;
    }, 'dropped rows repainted from the buffer', 6000);
    const rec = await page.evaluate(() => window.__muxLastPaintRecovery);
    assert.match(String(rec && rec.reason), /lost-rows/, `the repaint must come from the row-loss path (got ${JSON.stringify(rec)})`);
  });
});

test('the surface notices a frame drawn for a narrower grid than it has', async t => {
  if (skipWithoutChromium(t)) return;
  const session = { name: SESSION_NAME, alive: true, shellOnly: true, sessionId: 'stalewidth-sid', generationId: 'stalewidth-generation' };
  await withSurfacePage(session, async ({ harness, host, page }) => {
    // The stale-width detector compares row text against the grid, and row text is a DOM-renderer read.
    // WebGL has no rows, so the detector stands down there and this check has nothing to measure.
    await page.goto(`http://127.0.0.1:${harness.port}/?mux_render=dom`);
    await waitFor(() => page.evaluate(() => typeof connect === 'function'), 'page boot');
    await page.evaluate(name => connect(name), SESSION_NAME);
    await sleep(600);

    // Frames are built inside the page so they always use the LIVE grid; a cols read out here can be stale
    // by the time the relay's first 'd' frame lands, and a frame that misses the grid width by accident
    // would test nothing. Autowrap is off so an over-wide line truncates instead of wrapping a stray
    // character onto the next row.
    const built = await page.evaluate(() => {
      const cols = term.cols, rows = term.rows;
      const lines = (ch, n) => Array.from({ length: rows }, () => ch.repeat(Math.max(8, n))).join('\r\n');
      return {
        grid: { cols, rows },
        fills: '\x1b[?1049h\x1b[?7l\x1b[2J\x1b[H' + lines('x', cols - 1),
        narrow: '\x1b[2J\x1b[H' + lines('y', cols - 31),
        // Ragged: half the rows fill the grid, so the shortfall is not unanimous and this is NOT stale.
        ragged: '\x1b[2J\x1b[H' + Array.from({ length: rows }, (_, i) => (i % 2 ? 'z'.repeat(cols - 31) : 'w'.repeat(cols - 1))).join('\r\n'),
      };
    });
    assert.ok(built.grid.cols >= 40, `the grid must be wide enough to draw a narrow frame (got ${built.grid.cols})`);

    // A full-screen app that fills its grid is the healthy shape and must never be reported - a false
    // positive here re-fits and repaints the surface every couple of seconds on a perfectly good screen.
    host.sendOutput(SESSION_NAME, built.fills);
    await sleep(700);
    const healthy = await page.evaluate(() => staleFrameWidth());
    assert.equal(healthy, null, `a frame that fills the grid is not stale (got ${JSON.stringify(healthy)})`);

    // Ragged widths are ordinary content, not a stale frame: the near-unanimous shortfall is what separates
    // "the app drew for the wrong width" from "the app left some rows short".
    host.sendOutput(SESSION_NAME, built.ragged);
    await sleep(700);
    const ragged = await page.evaluate(() => staleFrameWidth());
    assert.equal(ragged, null, `a ragged frame is not stale (got ${JSON.stringify(ragged)})`);

    // The same app, now drawing 30 columns narrower than the grid: every row stops short of term.cols, which
    // is the "black to the right" in the reports. The buffer and the DOM agree, so the row-loss check is
    // blind to this by construction - which is exactly why the surface used to need a fullscreen toggle.
    host.sendOutput(SESSION_NAME, built.narrow);
    await sleep(700);
    const seen = await page.evaluate(() => ({ stale: staleFrameWidth(), dom: terminalPaintLostRows() }));
    assert.ok(seen.stale, 'a frame drawn for a narrower grid must be reported');
    assert.ok(seen.stale.maxW < seen.stale.cols - 3, `the drawn width must fall short of the grid (got ${JSON.stringify(seen.stale)})`);
    assert.ok(seen.stale.short / seen.stale.judged >= 0.9, `the shortfall must be near-unanimous (got ${JSON.stringify(seen.stale)})`);
    assert.ok(!seen.dom || seen.dom.lost === 0, `the row-loss check is blind to this class (got ${JSON.stringify(seen.dom)})`);

    // A shell is NOT the alternate buffer and its rows are ragged, so the same short rows must stay quiet.
    await page.evaluate(() => { term.write('\x1b[?1049l'); });
    host.sendOutput(SESSION_NAME, '\x1b[2J\x1b[H' + Array.from({ length: built.grid.rows }, () => 'q'.repeat(built.grid.cols - 31)).join('\r\n'));
    await sleep(700);
    const shell = await page.evaluate(() => staleFrameWidth());
    assert.equal(shell, null, `a normal-buffer shell is not a stale full-screen frame (got ${JSON.stringify(shell)})`);

    // The watch must request an authoritative frame, not just repaint its own buffer or report
    // unchanged viewport dimensions, which do not reach the app that drew the stale frame.
    await page.evaluate(() => { term.write('\x1b[?1049h'); });
    host.sendOutput(SESSION_NAME, built.narrow);
    await waitFor(async () => {
      const m = await page.evaluate(() => window.__muxLastStaleWidth);
      return !!(m && m.maxW < m.cols - 3);
    }, 'stale width recorded by the watch', 6000);
    const rec = await page.evaluate(() => window.__muxLastPaintRecovery);
    assert.match(String(rec && rec.reason), /stale-width/, `the heal must come from the stale-width path (got ${JSON.stringify(rec)})`);
    await host.waitFor(m => m.t === 'redraw' && m.s === SESSION_NAME, 'stale frame redraw request');
    const before = host.messages.filter(m => m.t === 'resize').length;
    host.sendOutput(SESSION_NAME, built.fills);
    await waitFor(async () => page.evaluate(() => staleFrameWidth() === null), 'authoritative frame restored');
    assert.equal(host.messages.filter(m => m.t === 'resize').length, before,
      'redraw must repair the frame without resizing the shared PTY');
  });
});
