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
