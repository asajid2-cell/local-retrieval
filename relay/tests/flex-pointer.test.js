// I-1: under the 'flex' scale, a native mouse report must name the same cell the pointer is over.
//
// Two coordinate phases live in this page and only one of them is scale-blind.
//
//   * THE PAGE'S OWN HIT TEST - bufferCellAt() - takes the .xterm-screen RECT and divides it by the
//     VISUAL cell size (rect.width/cols). Under a CSS scale that rect is the scaled box, so this phase
//     is already exact at every scale: the armed-pane click, the owner wheel report, selection and the
//     force-select gesture all land on the cell under the pointer. It must NOT be pre-divided by the
//     fix; doing so would move them.
//   * XTERM'S NATIVE PROTOCOL - getMouseReportCoords() / getCoordsRelativeToElement() - reads the same
//     scaled rect but divides by the UNSCALED CSS cell size (renderService cell.width/height). At 1.12
//     a report lands a few columns to the right of the pointer; at 0.8 it falls behind it.
//
// The fix (public/index.html, "FLEX POINTER MAPPING") is a capture-phase listener on #term, registered
// AFTER the page's own gesture handlers (same element, same phase, so they still read the true viewport
// point), which rewrites the event's clientX/clientY into CSS pixels of the untransformed grid. xterm
// then subtracts the rect and divides by the cell size exactly as it does at scale 1, so the report for
// a given cell is byte-identical at every scale. It returns early - leaving the event object exactly as
// the browser made it - when both scales are exactly 1, and it acts only on targets inside #term.
//
// WHERE THE NATIVE COORDINATES ARE OBSERVED. Motion and button-drag reports are stripped from the wire
// unconditionally by the page's flood guard (stripMouseReports: Cb bit 5 is dropped forever), so a
// "move report reaches the host" assertion would be false by construction in this product. The native
// coordinate decision is therefore read at xterm's own report seam (coreMouseService.triggerMouseEvent,
// which is where the cell is chosen), and the WIRE assertion uses the one native report class the guard
// forwards - the wheel (Cb bit 6) - comparing its bytes across scales.
//
// The regression reproduces the red direction by DROPPING the fix: at k=1.12 the control's cell (10,5)
// is reported as (11,5), so the comparisons below fail with the report shifted one column. I-14 (sub-1.0
// flex instability) is marked: 0.8 is exercised because the same divisor error runs the other way
// there, and the fix must be symmetric.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { test } = require('node:test');

const { RelayHarness, freePort, launchBrowser, waitFor, sleep } = require('./harness');

const source = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');

const TEST_TIMEOUT_MS = 8000;
const SESSION = 'flex-pointer';
const INSTALL = 'npm install --save-dev playwright --package-lock=false\nnpx playwright install chromium';
const ARMED = '\x1b[?1000h\x1b[?1002h\x1b[?1006h\x1b[?1003h';   // tracking modes an armed TUI enables

// The cell every measurement points at. Kept away from the edges: xterm clamps reports to the grid and
// its protocol guards do not represent the final row/column, neither of which this test is about.
const COL = 10, ROW = 5;
const SGR_COL = COL + 1, SGR_ROW = ROW + 1;

// ---- source gate: phase, scope and the identity short-circuit ----------------------------------------
test('the pointer rewrite is capture-phase on #term, scoped, and inert at scale 1', () => {
  const at = source.indexOf('// ---- FLEX POINTER MAPPING');
  assert.notEqual(at, -1, 'the flex pointer mapping block is gone from the shipped page');
  const tail = "for(const type of POINTER_XY) host.addEventListener(type, unscalePointer, true);";
  const end = source.indexOf(tail, at);
  assert.notEqual(end, -1, 'the flex pointer mapping block no longer ends with its listener loop');
  const block = source.slice(at, end + tail.length);

  // Registered after the page's own press handler, on the same node and phase: the page's hit test runs
  // first and reads the true viewport point. If this ordering flips, bufferCellAt's exact phase breaks.
  const pagePress = source.indexOf("host.addEventListener('mousedown', e=>{");
  assert.ok(pagePress !== -1 && pagePress < at,
    'the rewrite must be registered after the page\'s own gesture handlers');

  // Identity at scale 1: the block must not touch an unscaled event.
  assert.match(block, /\(sx===1 && sy===1\) \? null/,
    'the scale probe no longer reports "unscaled" for exactly 1, so an unscaled pane would be rewritten');
  assert.match(block, /if\(!g\) return;/,
    'an unscaled pane no longer short-circuits before the event is touched');

  // #term targets only.
  assert.match(block, /h\.contains\(e\.target\)/,
    'the rewrite no longer checks that the target is inside #term');

  // Each axis divided by its OWN scale ('flex' applies scale(sx,sy); the axes differ).
  assert.match(block, /\(e\.clientX - g\.left\)\/g\.sx/,
    'clientX is not divided by its own scale');
  assert.match(block, /\(e\.clientY - g\.top\s*\)\/g\.sy/,
    'clientY is not divided by its own scale');

  for (const type of ['mousedown', 'mousemove', 'mouseup']) {
    assert.ok(block.includes(`'${type}'`), `the rewrite is not registered for ${type} - xterm reports on all three`);
  }
  assert.match(block, /'wheel'\]/, 'the rewrite is not registered for wheel, which xterm also reports with the pointer cell');
  // The page's own wheel router must stay a capture listener registered BEFORE the rewrite: it composes
  // the owner wheel report from the raw point and is the reason the rewrite cannot run first.
  const wheelRouter = source.indexOf("$('#term').addEventListener('wheel'");
  assert.ok(wheelRouter !== -1 && wheelRouter < at,
    'the page\'s own wheel router must be registered before the rewrite');
  const wheelEnd = source.indexOf('capture:true}', wheelRouter);
  assert.ok(wheelEnd !== -1, 'the page\'s own wheel router is no longer a capture listener');
  assert.match(block, /Object\.defineProperty\(e,'clientX'/,
    'the event coordinate is no longer shadowed with an own property, so xterm reads the browser value');
});

// ---- the real behaviour, on a loopback relay with a fake host ---------------------------------------

let browser = null;
let browserError = null;

test.before(async () => {
  try { browser = await launchBrowser(); }
  catch (error) {
    browserError = error;
    console.warn(`Chromium unavailable; skipping the live half.\n${INSTALL}\n${error.message}`);
  }
});

test.after(async () => { if (browser) await browser.close(); });

async function installProbes(page) {
  await page.evaluate(() => {
    // The native cell choice, recorded before the report is encoded or the page's guard sees it.
    const ms = term._core.coreMouseService;
    window.__nativeReports = [];
    const orig = ms.triggerMouseEvent.bind(ms);
    ms.triggerMouseEvent = e => {
      window.__nativeReports.push({ col: e.col, row: e.row, button: e.button, action: e.action });
      return orig(e);
    };
    // A probe registered AFTER the rewrite: it sees whatever the shipped listener left on the event.
    window.__pointerProbe = [];
    document.querySelector('#term').addEventListener('mousemove', e => {
      window.__pointerProbe.push({ x: e.clientX, y: e.clientY });
    }, true);
  });
}

// Point at the VISUAL centre of cell (col,row) - the point a user pointing at that cell produces. Read
// from the live rect, so a transform applied just before moves the point exactly as the browser would.
async function nativeMove(page, col, row) {
  return page.evaluate(({ col, row }) => {
    const scr = document.querySelector('#term .xterm-screen');
    const r = scr.getBoundingClientRect();
    const cols = term.cols | 0, rows = term.rows | 0;
    const x = r.left + r.width * (col + 0.5) / cols;
    const y = r.top + r.height * (row + 0.5) / rows;
    window.__nativeReports.length = 0;
    window.__pointerProbe.length = 0;
    term._core.coreMouseService._lastEvent = null;     // xterm dedupes identical reports; this is a new probe
    const ev = new MouseEvent('mousemove', {
      bubbles: true, cancelable: true, clientX: x, clientY: y, buttons: 0, view: window,
    });
    // MouseEvent's clientX/clientY are longs: the constructor truncates the fractional centre. The event
    // that really reaches the listener is what the identity check compares against.
    const dispatched = { x: ev.clientX, y: ev.clientY };
    scr.dispatchEvent(ev);
    return { dispatched, reports: window.__nativeReports.slice(), probe: window.__pointerProbe.slice() };
  }, { col, row });
}

async function wheelAt(page, host, col, row) {
  host.messages.length = 0;
  await page.evaluate(({ col, row }) => {
    const scr = document.querySelector('#term .xterm-screen');
    const r = scr.getBoundingClientRect();
    const cols = term.cols | 0, rows = term.rows | 0;
    const x = r.left + r.width * (col + 0.5) / cols;
    const y = r.top + r.height * (row + 0.5) / rows;
    term._core.coreMouseService._lastEvent = null;
    // Untrusted on purpose: the page's own wheel router returns early for isTrusted=false, so this
    // reaches xterm's native wheel path - the report class the wire actually carries.
    scr.dispatchEvent(new WheelEvent('wheel', {
      bubbles: true, cancelable: true, deltaY: -120, deltaMode: 0, clientX: x, clientY: y, view: window,
    }));
  }, { col, row });
  await sleep(150);
  return host.messages
    .filter(m => m.t === 'i')
    .map(m => Buffer.from(m.d, 'base64').toString('latin1'))
    .filter(s => s.startsWith('\x1b[<'));
}

async function setScale(page, css) {
  return page.evaluate(css => {
    const scr = document.querySelector('#term .xterm-screen');
    scr.style.transition = 'none';
    scr.style.transformOrigin = '0 0';
    scr.style.transform = css;
    const r = scr.getBoundingClientRect();
    return { sx: r.width / scr.offsetWidth, sy: r.height / scr.offsetHeight };
  }, css);
}

// The page's armed-pane click: mousedown is swallowed and recorded, mouseup forwards press+release
// built from bufferCellAt. Both leave through sendMouseInput, the page's own phase.
async function clickAt(page, host, col, row) {
  host.messages.length = 0;
  await page.evaluate(({ col, row }) => {
    const scr = document.querySelector('#term .xterm-screen');
    const r = scr.getBoundingClientRect();
    const cols = term.cols | 0, rows = term.rows | 0;
    const x = r.left + r.width * (col + 0.5) / cols;
    const y = r.top + r.height * (row + 0.5) / rows;
    const mk = type => new MouseEvent(type, {
      bubbles: true, cancelable: true, clientX: x, clientY: y, button: 0, buttons: 0, view: window,
    });
    scr.dispatchEvent(mk('mousedown'));
    scr.dispatchEvent(mk('mouseup'));
  }, { col, row });
  await sleep(150);
  return host.messages
    .filter(m => m.t === 'i')
    .map(m => Buffer.from(m.d, 'base64').toString('latin1'))
    .filter(s => s.startsWith('\x1b[<'));
}

test('native mouse reports name the same cell at every flex scale, and the page\'s own hit test is untouched', async t => {
  if (!browser) return t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`);

  const harness = new RelayHarness();
  let host = null, context = null;
  try {
    // The page's own ws connect carries an Origin header, so the relay must allow this loopback origin.
    // The port is chosen first because the origin names it; the harness's normal start() then boots on
    // the fixed port with the fixture host token the FakeHost handshake expects.
    harness.fixedPort = await freePort();
    harness.env.ALLOWED_WS_ORIGINS = `http://127.0.0.1:${harness.fixedPort}`;
    await harness.start();
    host = await harness.connectHost([{ name: SESSION, alive: true, shellOnly: true, cols: 80, rows: 24 }]);

    context = await browser.newContext({ viewport: { width: 1000, height: 700 } });
    context.setDefaultTimeout(TEST_TIMEOUT_MS);
    context.setDefaultNavigationTimeout(30000);
    const page = await context.newPage();
    await page.goto(`http://127.0.0.1:${harness.port}/?s=${SESSION}`, { waitUntil: 'domcontentloaded' });
    await page.locator('#term').waitFor({ state: 'visible' });
    await waitFor(() => host.messages.some(m => m.t === 'sb' && m.s === SESSION), 'scrollback request', TEST_TIMEOUT_MS);
    host.sendScrollback(SESSION, 'boot\n');
    await waitFor(async () => {
      const text = await page.locator('#status').innerText();
      return text.includes(SESSION) && text.includes('live') ? true : null;
    }, 'live terminal status', TEST_TIMEOUT_MS);
    host.sendOutput(SESSION, '\x1b[2J\x1b[Hflex pointer regression frame\r\n' + ARMED);
    await sleep(300);
    await installProbes(page);

    const viewportY = await page.evaluate(() => term.buffer.active.viewportY | 0);
    assert.equal(viewportY, 0, 'the fixture must not be scrolled for the report rows to be viewport rows');

    // ---- CONTROL at scale 1, proven against the geometry: the native report names cell (COL,ROW).
    const control = await nativeMove(page, COL, ROW);
    assert.deepEqual(control.reports, [{ col: COL, row: ROW, button: 3, action: 32 }],
      `the scale-1 control does not choose the cell under the pointer: ${JSON.stringify(control.reports)}`);

    // ---- IDENTITY at scale 1: the probe registered after the rewrite sees the event untouched.
    assert.deepEqual(control.probe, [control.dispatched],
      'an unscaled pane had its event coordinates rewritten; scale 1 must be identity');

    // ---- the scaled cases. Each points at the VISUAL centre of the SAME cell on an ARMED pane; the
    // native report must choose the same cell, and the pre-divided event must be the scale-1 point.
    const cases = [
      ['1.12 (flex stretch ceiling)', 'scale(1.12)', 1.12, 1.12],
      ['1.25 (over-size flex)', 'scale(1.25)', 1.25, 1.25],
      ['0.8 (I-14: sub-1.0 flex instability)', 'scale(0.8)', 0.8, 0.8],
      ['1.12 x 1.25 (the axes differ)', 'scale(1.12, 1.25)', 1.12, 1.25],
    ];
    for (const [label, css, wantX, wantY] of cases) {
      const applied = await setScale(page, css);
      assert.ok(Math.abs(applied.sx - wantX) < 0.01 && Math.abs(applied.sy - wantY) < 0.01,
        `the ${label} transform did not take effect (sx=${applied.sx}, sy=${applied.sy})`);

      const got = await nativeMove(page, COL, ROW);
      assert.deepEqual(got.reports, control.reports,
        `at ${label} the native motion report chose a different cell than the pointer is over: ` +
        `${JSON.stringify(got.reports)} (scale 1 chooses ${JSON.stringify(control.reports)})`);
      // Tolerance is 1.5 px: the event's integer clientX is truncated before the rewrite, and the same
      // 1 px of input error becomes up to 1/scale px in the untransformed grid. The cell assertion above
      // is what is exact; this only proves the rewrite engaged, in the right direction and phase.
      assert.ok(got.probe.length === 1 &&
        Math.abs(got.probe[0].x - control.dispatched.x) < 1.5 &&
        Math.abs(got.probe[0].y - control.dispatched.y) < 1.5,
        `at ${label} the event was not mapped into the untransformed grid ` +
        `(probe=${JSON.stringify(got.probe)}, grid=${JSON.stringify(control.dispatched)})`);
    }

    // ---- WIRE: the wheel is the native report the page's flood guard forwards, so its bytes must be
    // identical at every scale as well.
    await setScale(page, 'scale(1)');
    const wheelControl = await wheelAt(page, host, COL, ROW);
    assert.ok(wheelControl.length >= 1,
      'the scale-1 control produced no wheel report at all - the fixture is not armed for the native path');
    for (const bytes of wheelControl) {
      assert.match(bytes, new RegExp(`^\\x1b\\[<\\d+;${SGR_COL};${SGR_ROW}[Mm]$`),
        `the scale-1 wheel report does not name the pointed cell: ${JSON.stringify(bytes)}`);
    }
    for (const [label, css] of cases) {
      await setScale(page, css);
      const got = await wheelAt(page, host, COL, ROW);
      assert.deepEqual(got, wheelControl,
        `at ${label} the forwarded wheel bytes moved off the cell the pointer is over: ` +
        `${JSON.stringify(got)} (scale 1 forwards ${JSON.stringify(wheelControl)})`);
    }

    // ---- PHASE: the page's own armed-pane report (bufferCellAt, dividing the VISUAL rect) is built
    // before the rewrite and must be exact at 1.12 without any help from it.
    await setScale(page, 'scale(1)');
    const clickControl = await clickAt(page, host, COL, ROW);
    assert.deepEqual(clickControl, [`\x1b[<0;${SGR_COL};${SGR_ROW}M`, `\x1b[<0;${SGR_COL};${SGR_ROW}m`],
      `the armed-pane click at scale 1 does not name the clicked cell: ${JSON.stringify(clickControl)}`);

    await setScale(page, 'scale(1.12)');
    const clickScaled = await clickAt(page, host, COL, ROW);
    assert.deepEqual(clickScaled, clickControl,
      `the armed-pane click at 1.12 moved off the clicked cell - the page's own hit test must need no ` +
      `help from the rewrite: ${JSON.stringify(clickScaled)}`);

    // ---- SCOPE: an event outside #term is never rewritten, even while the pane is scaled.
    const outside = await page.evaluate(() => {
      window.__outsideProbe = null;
      const probe = e => { window.__outsideProbe = { x: e.clientX, y: e.clientY }; };
      const ev = new MouseEvent('mousemove', {
        bubbles: true, cancelable: true, clientX: 111, clientY: 222, buttons: 0, view: window,
      });
      const made = { x: ev.clientX, y: ev.clientY };
      document.addEventListener('mousemove', probe, true);
      document.body.dispatchEvent(ev);
      document.removeEventListener('mousemove', probe, true);
      return { made, seen: window.__outsideProbe };
    });
    assert.deepEqual(outside.seen, outside.made,
      'a pointer event outside #term was rewritten while the pane was scaled');
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
});
