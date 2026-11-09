const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');

function section(start, end) {
  const from = source.indexOf(start);
  const to = source.indexOf(end, from + start.length);
  assert.notEqual(from, -1, `missing section start: ${start}`);
  assert.notEqual(to, -1, `missing section end: ${end}`);
  return source.slice(from, to);
}

test('terminal dimensions are never measured or reported while the surface is hidden', () => {
  const measure = section('function runViewportMeasure', 'function scheduleViewportFit');
  assert.match(measure, /!terminalSurfaceVisible\(\)/);
  assert.ok(
    measure.indexOf('!terminalSurfaceVisible()') < measure.indexOf('fit.proposeDimensions()'),
    'visibility must be checked before FitAddon reads geometry'
  );
  assert.match(measure, /dim\.cols<10 \|\| dim\.rows<3/);
  assert.match(measure, /pendingViewportCount<2/);
});

test('pan mode does not override xterm viewport or screen sizing internals', () => {
  assert.match(source, /#term\.pan \.xterm \{ width:max-content !important; min-width:100%; \}/);
  assert.doesNotMatch(source, /#term\.pan \.xterm-viewport/);
  assert.doesNotMatch(source, /#term\.pan \.xterm-screen/);
});

test('alternate-buffer wheel forwarding cannot recurse through the capture handler', () => {
  const wheel = section("$('#term').addEventListener('wheel'", 'function setFont');
  assert.match(wheel, /if\(!e\.isTrusted\) return/);
  assert.match(wheel, /if\(xt && xt\.contains\(e\.target\)\) return/);
  assert.ok(
    wheel.indexOf('if(!e.isTrusted) return') < wheel.indexOf('dispatchEvent(new WheelEvent'),
    'synthetic events must be rejected before forwarding'
  );
});

// Pull the scroll-affordance seam straight out of the shipped page and run it against a fake terminal,
// so the test exercises the real code rather than a copy that can drift away from it.
function loadScrollAffordance(fakeTerm, els) {
  const code = section('function scrollPositionLabel', 'term.onScroll(');
  const sandbox = {
    term: fakeTerm,
    $: sel => els[sel] || null,
    winSize: null,
    flash: () => {},
    // applyScrollAffordance consults appTakesMouse() (332e4b2, "Send wheel to a tracked normal buffer");
    // a bare shell never armed the mouse, so the stub is the unarmed case these assertions are written for.
    appTakesMouse: () => false,
    scrollPositionLabel: null,
    applyScrollAffordance: null,
  };
  vm.createContext(sandbox);
  vm.runInContext(code + '\nthis.scrollPositionLabel=scrollPositionLabel; this.applyScrollAffordance=applyScrollAffordance;', sandbox);
  return sandbox;
}

function fakeEl() {
  const classes = new Set();
  return {
    hidden: false,
    textContent: '',
    title: '',
    addEventListener: () => {},
    querySelector: () => null,
    classList: {
      add: c => classes.add(c),
      remove: c => classes.delete(c),
      contains: c => classes.has(c),
      toggle: (c, force) => (force === undefined ? (classes.has(c) ? classes.delete(c) : classes.add(c))
        : force ? classes.add(c) : classes.delete(c)),
    },
  };
}

test('the normal buffer gets a real, visible scrollbar on the xterm viewport', () => {
  const base = section('#term .xterm-viewport {', '}');
  assert.match(base, /scrollbar-width:\s*thin/, 'the Firefox/standards scrollbar must be thin, not hidden');
  assert.doesNotMatch(base, /display:\s*none/);
  assert.doesNotMatch(base, /scrollbar-width:\s*none/);

  const webkit = section('#term .xterm-viewport::-webkit-scrollbar {', '}');
  assert.doesNotMatch(webkit, /display:\s*none/, 'a hidden webkit scrollbar defeats the whole point');
  assert.match(webkit, /width:\s*(?!0)\d/, 'the webkit scrollbar needs a non-zero width');
  // ...and only for a real pointer: a styled ::-webkit-scrollbar on iOS pins a permanent bar that eats
  // viewport width and fights the touch pan handling #term already does.
  assert.match(source, /@media \(hover:hover\) and \(pointer:fine\) \{\s*\n\s*#term \.xterm-viewport::-webkit-scrollbar \{/);

  // themed, not the browser default grey
  const thumb = section('#term .xterm-viewport::-webkit-scrollbar-thumb {', '}');
  assert.match(thumb, /var\(--/, 'the thumb must use theme variables');

  // The alternate screen suppresses only the dead vertical direction. Horizontal panning belongs
  // to the outer #term.pan host and must remain visible.
  assert.match(source, /#term\.altbuf \.xterm-viewport \{[^}]*overflow-y:\s*hidden/);
  assert.match(source, /#term\.altbuf \.xterm-viewport::-webkit-scrollbar \{[^}]*width:\s*0/);
  assert.doesNotMatch(source, /#term\.altbuf \.xterm-viewport::-webkit-scrollbar \{[^}]*height:\s*0/);
  assert.match(source, /#term\.pan \{[^}]*overflow-x:\s*auto[^}]*scrollbar-width:\s*thin/);
  assert.match(source, /#term\.pan::-webkit-scrollbar \{[^}]*height:\s*(?!0)\d/);
});

test('alternate buffer separates app-owned vertical scroll from horizontal pan', () => {
  const host = fakeEl(), chip = fakeEl(), txt = fakeEl();
  const els = { '#term': host, '#scrollstate': chip, '#scrolltext': txt };
  const buffer = { active: { type: 'normal', viewportY: 40, baseY: 100 } };
  const loaded = loadScrollAffordance({ buffer, rows: 30, cols: 120 }, els);
  const { applyScrollAffordance } = loaded;

  buffer.active.type = 'alternate';
  assert.equal(applyScrollAffordance(), 'app scroll');
  assert.ok(host.classList.contains('altbuf'), 'the scrollbar-hiding class must be on #term');
  assert.ok(chip.classList.contains('app'));
  assert.equal(txt.textContent, 'app scroll');
  assert.equal(chip.hidden, false, 'the chip is the whole replacement affordance; it must be visible');

  host.classList.add('pan');
  loaded.winSize = { cols: 120, rows: 30 };
  assert.equal(applyScrollAffordance(), 'app scroll · pan 120 cols');
  assert.equal(txt.textContent, 'app scroll · pan 120 cols');
  assert.match(chip.title, /horizontal scrollbar/i);

  buffer.active.type = 'normal';
  host.classList.remove('pan');
  applyScrollAffordance();
  assert.equal(host.classList.contains('altbuf'), false, 'leaving the alt screen must restore the scrollbar');
  assert.equal(chip.classList.contains('app'), false);
  assert.equal(txt.textContent, '40% · 60 up');
  assert.equal(chip.hidden, false);

  // pinned live: nothing to report, the jump pill already owns that state
  buffer.active.viewportY = 100;
  applyScrollAffordance();
  assert.equal(chip.hidden, true);

  // onRender fires every frame, so an unchanged state must not touch the DOM at all
  txt.textContent = 'SENTINEL';
  assert.equal(applyScrollAffordance(), 'live · bottom', 'the label is still reported when memoised');
  assert.equal(txt.textContent, 'SENTINEL', 'unchanged scroll state must do zero DOM writes');
});

test('width mismatch activates a discoverable horizontal pan host', () => {
  const host = fakeEl();
  const code = section('function applyPan()', 'let surfaceAlignRaf');
  const sandbox = {
    winSize: { cols: 140, rows: 30 },
    viewportFit: { cols: 80, rows: 30 },
    sizeTabOwned: false, sizePinned: false,
    $: sel => sel === '#term' ? host : null,
    getComputedStyle: () => ({ paddingLeft: '0px', paddingRight: '0px' }),
    scheduleTerminalSurfaceAlign: () => {},
    applyPan: null,
  };
  vm.createContext(sandbox);
  vm.runInContext(code + '\nthis.applyPan=applyPan;', sandbox);

  sandbox.applyPan();
  assert.ok(host.classList.contains('pan'));
  assert.match(host.title, /horizontal scrollbar/i);

  sandbox.viewportFit = { cols: 140, rows: 30 };
  sandbox.applyPan();
  assert.equal(host.classList.contains('pan'), false);
  assert.equal(host.title, '');
});

test('size chip names auto, own pin, and another device exactly', () => {
  const host = fakeEl();
  const code = section('function sizeButtonPresentation()', 'let _altScrollAt');
  const sandbox = {
    winSize: { cols: 100, rows: 30 },
    sizePinned: false,
    sizeMine: false,
    sizeLocal: false,
    sizeTabOwned: false,
    sizePinLabel: '',
    tabFitMode: 'fill',
    _gridBusy: null,
    $: sel => sel === '#term' ? host : null,
    sizeButtonPresentation: null,
    updateSizeBtn: null,
  };
  vm.createContext(sandbox);
  vm.runInContext(code + '\nthis.sizeButtonPresentation=sizeButtonPresentation;', sandbox);

  assert.equal(sandbox.sizeButtonPresentation().text, 'auto · 100×30');
  sandbox.sizeLocal = true;
  assert.match(sandbox.sizeButtonPresentation().title, /following the attached PC terminal/i);
  sandbox.sizeLocal = false;
  sandbox.sizePinned = true;
  sandbox.sizeMine = true;
  assert.equal(sandbox.sizeButtonPresentation().text, '📌 this device · 100×30');
  sandbox.sizeMine = false;
  sandbox.sizePinLabel = 'Phone';
  assert.equal(sandbox.sizeButtonPresentation().text, '📌 Phone · 100×30');

  host.classList.add('pan');
  const mirrored = sandbox.sizeButtonPresentation();
  assert.match(mirrored.title, /mirroring Phone/i);
  assert.match(mirrored.title, /tap to pin/i);
  assert.match(mirrored.title, /horizontal scrollbar/i);

  // §7.3: a teed PC tab's grid is the physical screen - the chip says so and the pin is locked off.
  // Its tap is not a pin though: it switches how THIS device renders the fixed grid.
  sandbox.sizePinned = false; sandbox.sizePinLabel = ''; host.classList.remove('pan');
  sandbox.sizeTabOwned = true;
  const tabOwned = sandbox.sizeButtonPresentation();
  assert.match(tabOwned.text, /fills width/);
  assert.match(tabOwned.text, /100×30/);
  assert.match(tabOwned.title, /cannot be pinned/i);
  assert.match(tabOwned.title, /scrolls vertically/i);
  assert.match(tabOwned.title, /fit every row instead/i);
  sandbox.tabFitMode = 'fit';
  const tabFit = sandbox.sizeButtonPresentation();
  assert.match(tabFit.text, /fits rows/);
  assert.match(tabFit.title, /every row/i);
  // The old third mode scaled the screen non-uniformly; it is gone (GRID-OWNERSHIP-CONTRACT §6). The
  // second tap from 'fit' takes this device's grid instead, which is a request, not a transform.
  assert.match(tabFit.title, /take this screen's grid instead/i);
  sandbox.sizeTabOwned = false; sandbox.tabFitMode = 'fill';

  assert.match(source, /<button type="button" class="meta" id="statusmeta"/);
  assert.match(source, /\$\('#statusmeta'\)\.addEventListener\('click', cycleSize\)/);
});

test('scroll position label reports top, middle and bottom honestly', () => {
  const { scrollPositionLabel } = loadScrollAffordance({ buffer: { active: {} }, rows: 30 }, {});
  assert.equal(scrollPositionLabel(100, 100, 30), 'live · bottom');
  assert.equal(scrollPositionLabel(0, 100, 30), 'top · 100 up');
  assert.equal(scrollPositionLabel(50, 100, 30), '50% · 50 up');
  assert.equal(scrollPositionLabel(75, 100, 30), '75% · 25 up');
  assert.equal(scrollPositionLabel(0, 0, 30), 'no scrollback');
  // out-of-range geometry must never produce a nonsense readout
  assert.equal(scrollPositionLabel(500, 100, 30), 'live · bottom');
  assert.equal(scrollPositionLabel(-5, 100, 30), 'top · 100 up');
});

test('scroll-affordance hygiene preserves touch pan, quiet readout, and cached layout', () => {
  const viewport = section('#term .xterm-viewport {', '}');
  assert.doesNotMatch(viewport, /overscroll-behavior/);
  // #term is also the .rowpan pan host, so containment on the unconditional inner-viewport rule
  // would swallow the touch drag that must chain out to the host scroll.
  assert.match(source, /#term:not\(\.rowpan\) \.xterm-viewport \{[^}]*overscroll-behavior:\s*contain/);

  const chip = section('<button type="button" id="scrollstate"', '>');
  assert.doesNotMatch(chip, /aria-hidden/);
  assert.doesNotMatch(chip, /aria-live/);
  assert.match(source, /\$\('#scrollstate'\)\.addEventListener\('click', explainScrollAffordance\)/);

  const stateZ = section('#scrollstate {', '}').match(/z-index:\s*(\d+)/);
  const emptyZ = section('#empty {', '}').match(/z-index:\s*(\d+)/);
  assert.ok(stateZ);
  assert.ok(emptyZ);
  assert.ok(Number(stateZ[1]) < Number(emptyZ[1]));
  // Both are children of <main id="term">, so the chip must paint under the start-screen overlay.

  const aff = section('function scrollPositionLabel', 'term.onScroll(');
  assert.ok((aff.match(/\$\('#scrollstate'\)/g) || []).length <= 1);
  // applyScrollAffordance is wired to term.onRender, so it runs once per rendered frame and the
  // lookup must be resolved once and cached.
});

test('tab switches drain old parser work and reject stale write callbacks', () => {
  const writes = section('function clearTermWriteQueue', '// FREEZE SAFETY-VALVE');
  assert.match(writes, /item\.epoch !== termWriteEpoch/);
  const connect = section('function afterTerminalParserDrain', "document.addEventListener('visibilitychange'");
  assert.match(connect, /const generation=\+\+attachGeneration/);
  assert.ok(
    connect.indexOf('afterTerminalParserDrain(generation') < connect.indexOf('const sock = ws = new WebSocket'),
    'the new socket must open only after the prior parser generation drains'
  );
});

// The connect-time geometry is what sizes the shared PTY before our first 'v' report lands. On a fresh
// attach viewportFit is still null (the settle measure has not run) and term was just reset to xterm's
// 80x24 default, so seeding the ws URL from term.cols/rows opened EVERY attach at 80x24 and dragged the
// shared PTY through it (197x47 -> 80x24 -> 197x47 on the wire, verified) until the real measure landed.
// The URL must measure synchronously - the same call the settle path uses - so the first size the relay
// sees is already the real one.
test('a fresh attach seeds the connect-time geometry from a real measure, never xterm\'s 80x24 default', () => {
  const connect = section('const proto = location.protocol', 'sock.binaryType');
  assert.match(connect, /fit\.proposeDimensions\(\)/,
    'the ws URL must measure the real viewport synchronously - a fresh attach has no viewportFit and term is a reset 80x24');
  assert.match(connect, /seed/, 'the measured geometry must be carried as a seed for the ws URL');
  assert.ok(
    connect.indexOf('fit.proposeDimensions()') < connect.indexOf('||term.cols'),
    'the real measure must be tried before falling back to the reset terminal geometry'
  );
  // And the guard mirrors runViewportMeasure's: a nonsense propose (hidden/zero) still falls back.
  assert.match(connect, /d\.cols>=10 && d\.rows>=3/);
});

// SCOPE, measured: this is a source-level guard ON PURPOSE. The defect it protects against is a frame the
// browser dropped while the surface was display:none or covered by an overlay — headless chromium never
// drops one, so a browser test cannot see the bug it prevents. What a browser test CAN see (and what the
// reason string exists for) is that the heal is reachable: switching away from the terminal view and back
// must leave window.__muxLastSurfaceHeal.reason === 'mobile-view-terminal'.
// The transition matrix is covered for real by tests/ui-surface-integrity.test.js, which drives a browser
// through every one of those states and asserts the surface stays reachable. This source-level guard
// covers the OTHER half - the states nobody can enumerate, which is why they felt random: the watch must
// exist, must be driven by a timer, and must repair the one fault whose repair is not a repaint.
test('an unenumerable surface fault is watched for and repaired, not just reported', () => {
  const fault = section('function terminalSurfaceIntegrityFault', 'let integrityHealAt');
  // Every fault class the user described, named so a future edit cannot quietly drop one.
  for (const cls of ['term-collapsed', 'term-overflow', 'nav-collapsed', 'nav-offscreen', 'term-under-nav', 'app-inert', 'term-not-hit', 'term-covered']) {
    assert.match(fault, new RegExp(cls), `the fault test must cover ${cls}`);
  }
  // A hidden view, an open overlay and an unattached session are legitimate, not faults: healing then
  // would fight the transition in progress.
  assert.match(fault, /if\(!current\) return ''/);
  assert.match(fault, /if\(document\.hidden\) return ''/);
  assert.match(fault, /terminalSurfaceVisible\(\)/);
  assert.match(fault, /dialog\[open\]/);
  assert.match(fault, /#copyview/);

  const watch = section('function watchTerminalSurface', 'function cancelViewportRestore');
  assert.match(watch, /terminalSurfaceIntegrityFault\(\)/, 'the watch must consult the fault test');
  assert.match(watch, /healTerminalSurface\('integrity:'\+fault\)/, 'a fault must trigger the same heal the transitions use');
  assert.match(watch, /removeAttribute\('inert'\)/, 'a stale inert needs a repair, not a repaint');
  assert.match(watch, /__muxLastIntegrityHeal/, 'the last integrity heal must be observable from the page');
  assert.match(source, /setInterval\(watchTerminalSurface, \d+\)/, 'the watch must run on a timer - its faults have no event');
});

test('every transition that can strand a stale frame repaints, not only a grid change', () => {
  const heal = section('function healTerminalSurface', 'function cancelViewportRestore');
  assert.match(heal, /scheduleViewportFit\(0\)/, 'the grid must be re-measured, not just repainted');
  assert.match(heal, /schedulePaintHeal\(/, 'the rows must actually be repainted');
  assert.match(heal, /__muxLastSurfaceHeal/, 'the last heal must be observable from the page');

  const sites = {
    'mobile view switch back to the terminal': section('function setMobileView', 'function syncMobileView'),
    'zen / fullscreen': section('function toggleZen', "$('#zen').onclick"),
    'leaving fullscreen by any route': section("document.addEventListener('fullscreenchange'", '// ---- desktop keyboard shortcuts'),
  };
  for (const [name, text] of Object.entries(sites)) {
    assert.match(text, /healTerminalSurface\(/, `${name} must heal the surface`);
  }
  // A redundant re-set of the SAME view (the boot sync, a media-query refresh) has no stale frame to
  // answer for, so the heal is gated on the terminal having actually been away.
  assert.match(
    sites['mobile view switch back to the terminal'],
    /const heal = wasTerminalHidden/,
    'only a real view change may heal — the boot sync must not'
  );

  // The one-line transitions move the surface without changing the grid, so nothing that reacts to a
  // grid change would run: the overlay that covered the terminal, the font change, the keybar toggle.
  for (const name of ['closeCopyView', 'setFont', 'applyKeybarPref']) {
    assert.match(
      source,
      new RegExp(`function ${name}\\b[\\s\\S]{0,400}?healTerminalSurface\\(`),
      `${name} must heal the surface`
    );
  }

  // And the frame that used to be a silent no-op: a 'd' re-asserting the size we already have. The heal
  // must sit OUTSIDE the grid-change branch, or that frame repaints nothing.
  const control = section('function onControl', '// TAP the size chip');
  const gridChangeLine = control.split('\n').find(line => line.includes('if(term.cols!==m.cols'));
  assert.ok(gridChangeLine, 'onControl must still resize the grid when it changes');
  assert.doesNotMatch(gridChangeLine, /schedulePaintHeal/, 'the heal must not be trapped inside the grid-change branch');
  assert.ok(
    control.indexOf('schedulePaintHeal(20)') > control.indexOf(gridChangeLine),
    'a re-asserted size frame must still repaint'
  );
});

// LEVER 3 — an output burst must not repaint the whole viewport after every echo.
//
// schedulePaintHeal runs verifyTerminalPaint AND two full term.refresh passes over every row. On the DOM
// renderer that rebuilds the whole viewport ~40ms after every completed write burst — exactly when the
// next keystroke is arriving while typing fast. The write already painted its own rows, so that heal was
// repainting a correct surface. The fix: the output path verifies (a cheap read-only check) and only pays
// the two refreshes when the check finds a REAL fault; state transitions keep the unconditional heal.
test('the output path verifies without repainting; only a detected fault heals', () => {
  // The verify-only helper exists and does NOT refresh unconditionally.
  const verify = section('function schedulePaintVerify', 'function healTerminalSurface');
  assert.match(verify, /verifyTerminalPaint/, 'the post-write path must still DETECT a lost surface');
  assert.doesNotMatch(verify, /healTerminalPaint\(\)/,
    'the post-write path must not refresh unconditionally — that is the per-echo repaint');

  // The two write-burst sites use the verify-only path, not the full heal.
  const pump = section('function pumpTermWrite', "addEventListener('mouseup'");
  const healCalls = (pump.match(/schedulePaintHeal\(/g) || []).length;
  assert.equal(healCalls, 0, `the write pump still schedules ${healCalls} unconditional heal(s)`);
  assert.match(pump, /schedulePaintVerify\(/, 'the write pump must verify its paint');

  // And the heal the write path DOES keep is real: verifyTerminalPaint heals only on a detected fault.
  const verifyFn = section('function verifyTerminalPaint', 'function schedulePaintVerify');
  assert.match(verifyFn, /healDetectedPaintFault/,
    'verifyTerminalPaint must still heal when it finds a fault — detection is not weakened');
  // A blank or lost-rows surface still reaches the heal.
  assert.match(verifyFn, /terminalPaintLooksBlank\(\)/, 'blank detection removed');
  assert.match(verifyFn, /terminalPaintLostRows\(\)/, 'lost-rows detection removed');
});

test('state transitions still repaint unconditionally — only the write path changed', () => {
  // The autonomous watchdog, the visibility/focus/pageshow returns, the control re-assert and the
  // surface-transition heal must all keep the full repaint: those answer a surface that lost custody
  // of its pixels, where a refresh (not just a verify) is the repair.
  assert.match(section('function healTerminalSurface', 'function cancelViewportRestore'), /schedulePaintHeal\(/);
  assert.match(source, /visibilitychange[\s\S]{0,120}schedulePaintHeal\(80\)/, 'visibility return must still heal');
  assert.match(source, /addEventListener\('pageshow'[\s\S]{0,80}schedulePaintHeal\(80\)/, 'pageshow must still heal');
  // The watch interval still verifies autonomously (the backstop the write path now relies on).
  assert.match(source, /setInterval\(\(\)=>\{[\s\S]{0,700}verifyTerminalPaint\('watchdog'\)/, 'the watchdog verify must remain');
});

// The first measurement after a socket opens can be a transient - fonts and layout have not settled yet
// (measured on attach: 48 rows for two frames, then the real 47). Reporting it resizes the shared PTY,
// which re-arms the Gateway inline mouse mid-settle with an origin read from the client's cursor while it
// is still at the bottom-left; the bottom bar then stays dead until a later resize. So a measurement must
// be HELD for a settle window before it is reported, while a deliberate user resize still goes out.
//
// The seam is pulled straight out of the shipped page and driven with a scripted geometry and clock, so
// the test exercises the real settle logic rather than a copy that can drift from it.
function loadViewportMeasure(getDims, clock) {
  const code = section('let viewportTimer=0, layoutRaf=0, layoutGeneration=0;', 'function doFit()');
  const reports = [], scheduled = [];
  // The section declares its own `let layoutGeneration=0` (a lexical binding, not a global property), so
  // the measure is driven with that same value; the generation guard is exercised but always fresh here.
  // `terminalSurfaceVisible` is a real function declaration in the section, so it is not stubbed — it is
  // given a visible host so the shipped visibility gate passes for real.
  const host = { isConnected: true, offsetParent: {}, getBoundingClientRect: () => ({ width: 400, height: 300 }) };
  const sandbox = {
    fit: { proposeDimensions: () => getDims() },
    scheduleViewportFit: () => {},
    ws: { readyState: 1 },
    current: 's',
    viewportFit: null,
    document: { visibilityState: 'visible' },
    actFlag: () => false,
    send: (t, d) => { if (t === 'v') reports.push(JSON.parse(d)); },
    applyPan: () => {}, updateMeta: () => {}, updateSizeBtn: () => {}, applyScrollAffordance: () => {},
    fitTabOwnedGrid: () => {},
    $: () => host,
    getComputedStyle: () => ({ display: 'block', visibility: 'visible' }),
    setTimeout: () => 0, clearTimeout: () => {}, requestAnimationFrame: () => 0, cancelAnimationFrame: () => {},
    Date: { now: () => clock.t },
    runViewportMeasure: null,
  };
  vm.createContext(sandbox);
  vm.runInContext(code + '\nthis.runViewportMeasure=runViewportMeasure;', sandbox);
  // scheduleViewportFit just records the requested delay; the test advances the clock and re-measures.
  sandbox.scheduleViewportFit = d => scheduled.push(d);
  return { reports, scheduled, measure: () => sandbox.runViewportMeasure(0) };
}

test('an attach-time transient measurement never reaches the wire; only the settled size is reported', () => {
  const clock = { t: 1000 };
  let cur = { cols: 197, rows: 48 };
  const { reports, measure } = loadViewportMeasure(() => cur, clock);
  measure();                                              // 48 first seen
  clock.t += 16; measure();                               // 48 again - the transient held two frames
  clock.t += 16; cur = { cols: 197, rows: 47 }; measure();// 47 - geometry changed, hold again
  clock.t += 60; measure();                               // 47 stable, but only held 60ms
  assert.equal(reports.length, 0, 'nothing may be reported before the geometry settles');
  clock.t += 100; measure();                              // 47 held 160ms >= the settle window
  assert.equal(reports.length, 1, 'one settle must produce exactly one report');
  assert.equal(reports[0].cols, 197);
  assert.equal(reports[0].rows, 47, 'the reported size is the settled one, never the transient 48');
});

test('a deliberate user resize is still reported once, within one settle window', () => {
  const clock = { t: 5000 };
  let cur = { cols: 197, rows: 47 };
  const { reports, measure } = loadViewportMeasure(() => cur, clock);
  measure(); clock.t += 20; measure(); clock.t += 20; measure(); clock.t += 160; measure();
  assert.equal(reports.length, 1, 'the initial size settles to a single report');
  assert.equal(reports[0].rows, 47);
  clock.t += 10; cur = { cols: 197, rows: 60 }; measure(); // the user resizes
  assert.equal(reports.length, 1, 'the new size waits out the settle window before it is sent');
  clock.t += 150; measure();
  assert.equal(reports.length, 2, 'the user resize still goes out, exactly once');
  assert.equal(reports[1].rows, 60, 'and it carries the new size');
});

// The r1 reject burst: a fresh attach opened at 197x48 and the settled grid was 197x47, so the shared PTY
// took a real SIGWINCH hop on every attach and a probe in flight was answered at the pre-resize geometry.
// The connect measurement was never wrong - the status strip's size chip (#statusmeta) was empty until the
// first 'd' decision filled it, and an empty button has no line box, so the strip grew ~4px at that moment
// and shrank #term by exactly one row. Keeping a line box in the empty state makes the strip's height
// independent of whether the chip has text yet, so the connect-time size equals the settled size.
test('the status size chip keeps its line box while empty, so the strip never reflows when a size lands', () => {
  assert.ok(source.includes('#status #statusmeta:empty::before'), 'the empty size chip must keep a line box');
  assert.ok(/#status #statusmeta:empty::before \{[^}]*content:"/.test(source), 'and that line box must come from content');
});

test('the connect seed is the settled size and is marked reported, so one attach makes one size decision', () => {
  const connect = section('const proto = location.protocol', 'sock.onerror = () => {};');
  // seed is the held settled fit (re-attach) or a measured dim - never xterm's post-reset 80x24 default
  assert.match(connect, /let seed = viewportFit;/);
  assert.match(connect, /proposeDimensions\(\)/);
  // the size already in the URL is recorded as reported for THIS socket, so the settle measure's identical
  // 'v' is suppressed and the relay never recomputes a second, identical decision
  assert.match(connect, /lastViewportReport=seed\.cols\+'x'\+seed\.rows; lastViewportReportSocket=sock;/);
  assert.match(source, /lastViewportReport!==key \|\| lastViewportReportSocket!==ws/);
});

// A tab-owned grid is the PC terminal's physical screen: its cols x rows are fixed and the relay never
// resizes it for this viewer. The shipped fit scales the WHOLE grid into the pane on both axes - one font
// size, so the cell aspect ratio is untouched - and boxes both the pan and the row-pan classes from what
// is actually painted, so nothing scrolls at 1x. The numbers used here were measured live on this
// machine: 209x51; font 13 paints a 7x15 cell (1463x765, fits a 1827x792 pane), font 14 an 8x16 cell
// (1672x816, 24px too tall), font 16 an 8x19 cell (1672x969, the version that made the wrapper scroll).
function loadTabFit(opts){
  const code = section('function cellMetrics', 'function applyPan');
  const classes = new Set();
  let rafCount = 0;
  const host = {
    clientWidth: opts.hostW, clientHeight: opts.hostH,
    classList: {
      toggle(n,on){ if(on) classes.add(n); else classes.delete(n); },
      add(n){ classes.add(n); }, remove(n){ classes.delete(n); }, contains(n){ return classes.has(n); },
    },
  };
  if (opts.term && opts.term.options) opts.term.options.fontSize = opts.preference;
  const sandbox = {
    fontSize: opts.preference,
    sizeTabOwned: opts.tabOwned !== false,
    sizePinned: !!opts.pinned,
    term: opts.term,
    LS: {
      get: (k, d) => (k === 'mux_tab_font' && opts.tabScale !== undefined ? opts.tabScale
                    : k === 'mux_tab_fit' && opts.mode !== undefined ? opts.mode : d),
      set(){},
    },
    $: sel => sel === '#term' ? host : null,
    getComputedStyle: () => ({ paddingLeft:'0px', paddingTop:'0px', paddingRight:'0px', paddingBottom:'0px' }),
    healTerminalSurface(){}, doFit(){}, applyPan(){}, scheduleTerminalSurfaceAlign(){},
    requestAnimationFrame(){ return ++rafCount; },   // tests drive fit() directly; the frame is counted
  };
  vm.createContext(sandbox);
  vm.runInContext(code + '\nthis.fitTabOwnedGrid=fitTabOwnedGrid; this.appliedFont=()=>appliedFont;', sandbox);
  return { fit: sandbox.fitTabOwnedGrid, applied: sandbox.appliedFont, classes: () => Array.from(classes).sort(), raf: () => rafCount, sandbox };
}
function gridTerm(cols,rows,cellW,cellH){
  return { cols, rows, options:{ fontSize:14 }, _core:{ _renderService:{ dimensions:{ css:{ cell:{ width:cellW, height:cellH } } } } } };
}
function setCell(term,cellW,cellH){
  const cell=term._core._renderService.dimensions.css.cell;
  cell.width=cellW; cell.height=cellH;
}

test('fit mode sizes the whole grid into the pane on BOTH axes, so nothing scrolls at 1x', () => {
  // 7.7x17 at 14 is 0.55 x 1.214 cells per px of font. The pane allows floor(1842/114.95)=16.02 by width
  // but only floor(792/61.93)=12.79 by height, so 12 is the largest font that shows all 51 rows.
  const term = gridTerm(209,51,7.7,17);
  const fit = loadTabFit({ term, preference:14, mode:'fit', hostW:1842, hostH:792 });
  fit.fit();
  assert.equal(term.options.fontSize, 12, 'the shorter axis binds, exactly as a terminal folds a screen');
  assert.equal(fit.applied(), 12);
  assert.deepEqual(fit.classes(), ['tabfit'], 'the fitted grid is centred, not pinned to the left edge');
});

test('fill mode spends the width and lets the rows overflow, which the row pan lives with', () => {
  // The same grid in fill mode: 1842/114.95 = 16.02 floors to 16, and the 51 rows that paint at 16 are
  // taller than the pane on purpose - alignTerminalSurface pins the last row and scrolls the rest.
  const term = gridTerm(209,51,7.7,17);
  const fit = loadTabFit({ term, preference:14, mode:'fill', hostW:1842, hostH:792 });
  fit.fit();
  assert.equal(term.options.fontSize, 16, 'fill mode takes the width estimate and ignores the height');
  assert.equal(fit.applied(), 16);
  assert.deepEqual(fit.classes(), ['tabfit']);
});

test('fill mode never treats a tall grid as overflow, so it does not step itself back down', () => {
  // 16px paints 1672x867 into a 1672x792 pane: exactly as wide as the pane and 75px too tall. In fit mode
  // that is an overflow and the font steps down; in fill mode only the width can withdraw a size, because
  // the row pan is what shows the rest.
  const term = gridTerm(209,51,8,19);
  const fit = loadTabFit({ term, preference:16, mode:'fill', hostW:1672, hostH:792 });
  fit.fit(); fit.fit();
  assert.equal(term.options.fontSize, 16, 'a height overflow is the row pan\'s business, not a reason to shrink');
});

test('the overflow latch survives a scrollbar-sized pane change', () => {
  // Pan and rowpan are toggled by this fit, and either one moves clientWidth/Height by a scrollbar. If
  // that counted as "the pane changed" the latch would clear on every step and fill would bounce back to
  // the size that just overflowed (seen live: 17 with a horizontal pan bar).
  const term = gridTerm(209,51,8,19);
  const fit = loadTabFit({ term, preference:16, mode:'fill', hostW:1827, hostH:792 });
  fit.fit();                                   // 16 -> the estimate says 17.48, tries 17
  assert.equal(term.options.fontSize, 17);
  setCell(term, 9, 19);                        // 17 re-measures: 1881px, wider than the pane
  fit.fit();                                   // overflow: latch 17 and step back
  assert.equal(term.options.fontSize, 16);
  setCell(term, 8, 19);
  fit.sandbox.$('#term').clientWidth -= 15;    // rowpan's vertical scrollbar appears
  fit.fit();
  assert.equal(term.options.fontSize, 16, 'a 15px shrink must not re-open the search for 17');
});

test('fit mode bottoms out at the readable floor in a squat pane', () => {
  // 400 / 61.93 = 6.46 in a 400px-tall pane, floored to 6 and then clamped to the 8px readable floor.
  const term = gridTerm(209,51,7.7,17);
  loadTabFit({ term, preference:14, mode:'fit', hostW:1842, hostH:400 }).fit();
  assert.equal(term.options.fontSize, 8, 'height binds and the fit bottoms out at the readable floor');
});

test('a grid that already fits both axes is a fixed point, so two sizes cannot chase each other', () => {
  // The live 13px geometry: a 7x15 cell paints 1463x765 into an 1827x792 pane. Both ratios (16.2, 13.5)
  // floor at the current 13, so the fit must leave the size alone instead of nudging it.
  const term = gridTerm(209,51,7,15);
  const fit = loadTabFit({ term, preference:13, mode:'fit', hostW:1827, hostH:792 });
  fit.fit();
  assert.equal(term.options.fontSize, 13, 'a grid already inside both edges is left alone');
});

test('quantised cell metrics cannot make two fonts chase each other', () => {
  // Measured live: 16px paints an 8x19 cell, so 51 rows are 969px in a 792px pane. The fit steps down to
  // 13; while the renderer still reports a stale smaller cell the estimate points back at 16, which is
  // latched because it was the size that overflowed. Without the latch the fit would flip 13<->16 and drag
  // the pan classes with it.
  const term = gridTerm(209,51,8,19);
  const fit = loadTabFit({ term, preference:16, mode:'fit', hostW:1827, hostH:792 });
  fit.fit();
  assert.equal(term.options.fontSize, 13, 'an overflowing size steps down to one that fits');
  setCell(term, 7, 12);                       // stale metric, still painted from the larger size
  fit.fit();
  assert.equal(term.options.fontSize, 13, 'an estimate that points at the latched size is refused');
  setCell(term, 7, 15);                       // the renderer re-measures at the size actually in use
  fit.fit(); fit.fit();
  assert.equal(term.options.fontSize, 13, 'and it settles: the fitted size is a fixed point');
  assert.equal(fit.applied(), 13);
});

test('a font change schedules a next-frame re-fit, so the size settles on measured metrics', () => {
  // Cell metrics only update after the repaint, so the first estimate is read at the old font. Live, that
  // left fit mode at 12 after a reload; the re-run at the re-measured 6x14 allows 13.31 and climbs to 13,
  // which is the size that actually paints inside the pane.
  const term = gridTerm(209,51,7.7,17);
  const fit = loadTabFit({ term, preference:14, mode:'fit', hostW:1842, hostH:792 });
  fit.fit();
  assert.equal(term.options.fontSize, 12, 'the first step is computed from the metrics at 14');
  assert.ok(fit.raf() >= 1, 'the step that changed the font must schedule the re-measure');
  setCell(term, 6, 14);                        // the renderer re-measures at 12 (measured live)
  fit.fit();                                   // the follow-up frame
  assert.equal(term.options.fontSize, 13, 'the re-run corrects the size that was never measured');
  setCell(term, 7, 15);                        // and at 13 (also measured live) it is a fixed point
  fit.fit();
  assert.equal(term.options.fontSize, 13, 'the corrected size is stable');
});

test('A+/A- on a tab-owned grid zooms the fit fractionally instead of overwriting it', () => {
  // Zoom is a multiplier on the fitted size, applied at 0.1px resolution so every press is visible; a
  // rounded result would snap back to the size already in use and look like the zoom never applied.
  const term = gridTerm(209,51,7.7,17);
  const fit = loadTabFit({ term, preference:14, mode:'fit', hostW:1842, hostH:792, tabScale:1.25 });
  fit.fit();
  assert.equal(term.options.fontSize, 16, '12.789 x 1.25 = 15.99 - the zoom rides on top of the fit');
  assert.equal(fit.applied(), 16);
});

test('leaving a tab-owned session restores the stored preference and drops the fit class', () => {
  const term = gridTerm(209,51,7.7,17);
  const fit = loadTabFit({ term, preference:14, mode:'fit', hostW:1842, hostH:792 });
  fit.fit();
  assert.equal(term.options.fontSize, 12);
  fit.sandbox.sizeTabOwned = false;
  fit.fit();
  assert.equal(term.options.fontSize, 14, 'the preference is restored, never overwritten by a derived size');
  assert.deepEqual(fit.classes(), []);
});

test('the fit runs on every size frame and every re-measure, not only on a grid change', () => {
  assert.match(section('function onControl', 'function cycleSize'), /fitTabOwnedGrid\(\)/);
  assert.match(section('function reportViewport', 'function runViewportMeasure'), /fitTabOwnedGrid\(\)/);
  assert.match(source, /#term\.tabfit:not\(\.pan\) \.xterm \{ width:max-content !important; margin-left:auto; margin-right:auto; \}/);
});
