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
    $: sel => sel === '#term' ? host : null,
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
  sandbox.sizePinned = false; sandbox.sizePinLabel = ''; host.classList.remove('pan');
  sandbox.sizeTabOwned = true;
  const tabOwned = sandbox.sizeButtonPresentation();
  assert.match(tabOwned.text, /follows PC tab/);
  assert.equal(tabOwned.disabled, true, 'a tab-owned grid must not offer a pin');
  assert.match(tabOwned.title, /cannot be pinned/i);
  sandbox.sizeTabOwned = false;

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
