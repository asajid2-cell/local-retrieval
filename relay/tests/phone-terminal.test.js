'use strict';
// A phone must not be handed an 8-pixel terminal to read.
//
// Live (tab-26f4, 2026-11-12, 390x844 viewport): the pane was 390 px wide, the grid 209 columns, and the
// fit search settled at font 8 to make the width "fit" - so the painted screen was 906 px inside a 390 px
// pane (2.3x too wide, clipped) at a size nobody can read. The chip still said "fills width - 209x51".
//
// There are only two honest answers for a mirror whose grid is far wider than the phone:
//   (a) shrink until it fits - unreadable, and the pane is still clipped because the cell has a floor; or
//   (b) say plainly that this is a wide terminal and offer the conversation view, which is the surface
//       designed for a narrow screen.
// Silently handing the user (a) while the chip claims "fills width" is the defect.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const SRC = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');

function fnSrc(name, src = SRC) {
  const i = src.indexOf('function ' + name);
  assert.ok(i >= 0, `function ${name} not found`);
  let depth = 0, started = false, j = i;
  for (; j < src.length; j++) {
    const c = src[j];
    if (c === '{') { depth++; started = true; }
    else if (c === '}') { depth--; if (started && depth === 0) { j++; break; } }
  }
  return src.slice(i, j);
}

// The rule under test, extracted so it can be exercised without a browser. It answers the one question
// that matters on a narrow screen: is this grid readable at a usable font, or is it a wide mirror that
// needs the other surface?
test('the phone decides "readable" from the font it would need, not from "it fits"', () => {
  const helper = fnSrc('phoneGridVerdict');
  const sandbox = {};
  vm.createContext(sandbox);
  vm.runInContext(helper + '\n;this.verdict = phoneGridVerdict;', sandbox);
  const v = sandbox.verdict;

  // A 390px pane, 209 columns, ~7.7px per cell at font 14 -> needs ~4.4px per cell => font ~8.
  const wide = v({ paneW: 390, cols: 209, cellW: 7.7, font: 14 });
  assert.equal(wide.readable, false, '209 columns in 390px is not readable at a usable font');
  assert.ok(wide.fitFont < 10, `it would need font ${wide.fitFont}, which is unreadable`);
  assert.equal(wide.hint, 'threads', 'and the phone is pointed at the surface built for it');

  // The same pane with a phone-sized grid IS readable.
  const ok = v({ paneW: 390, cols: 46, cellW: 7.7, font: 14 });
  assert.equal(ok.readable, true, '46 columns at font 14 fits a phone');

  // A wide pane with a grid it can actually hold. (209 columns needs ~1610 px at font 14, so a 1024 px
  // window is NOT enough - which is why the desktop paints at its own size rather than fitting.)
  const widePane = v({ paneW: 2100, cols: 209, cellW: 7.7, font: 14 });
  assert.equal(widePane.readable, true, 'a pane wide enough for the grid is fine');
});

// The chip is what the user is told. Live it read "fills width - 209x51" over an 8px clipped grid: true
// of the fit, false of what could be read. In the unreadable state it must name the state and the fix.
test('the size chip never claims a fit it could not make readable', () => {
  const helper = fnSrc('sizeButtonPresentation');
  const sandbox = {
    winSize: { cols: 209, rows: 51 },
    _gridBusy: null,
    tabFitPaneMode: 'unreadable',
    sizeTabOwned: true, sizePinned: false, sizeMine: false, sizeLocal: true, sizePinLabel: '',
    tabFitMode: 'fill',
    term: { cols: 209 },
    $: () => null,
  };
  vm.createContext(sandbox);
  vm.runInContext(helper + '\n;this.present = sizeButtonPresentation;', sandbox);
  const unreadable = sandbox.present();
  assert.ok(!/fills width/.test(unreadable.text),
    `the chip must not claim a readable fit it does not have (got "${unreadable.text}")`);
  assert.match(unreadable.text, /too wide/i, 'it names the actual state');
  assert.equal(unreadable.disabled, false, 'and the tap is still live - it is the way out');
  assert.match(unreadable.title, /resize the tab/i, 'the title says what the tap will do');

  // The same tab once it is readable keeps the ordinary vocabulary.
  sandbox.tabFitPaneMode = 'fill';
  const normal = sandbox.present();
  assert.match(normal.text, /fills width/);
});

// fill and fit are both wrong answers to an unreadable grid, so a tap in that state must not spend a round
// on a rendering change nobody can see: it must take the grid, which is the only step that changes the
// COLUMN COUNT. Pinned structurally because the ladder's branches live in the page, not in a pure helper.
test('an unreadable grid short-circuits the ladder straight to taking the grid', () => {
  const src = fnSrc('cycleSize');
  const unreadableAt = src.indexOf("tabFitPaneMode==='unreadable'");
  assert.ok(unreadableAt > 0, 'the ladder has an unreadable branch');
  assert.ok(/tabFitPaneMode==='unreadable'\)\{\s*takeGrid\(\);/.test(src.slice(unreadableAt)),
    'and it takes the grid rather than flipping fill/fit');
  // Both ladders have it: the teed-tab branch and the pane-owned one.
  assert.equal((src.match(/tabFitPaneMode==='unreadable'\)\{\s*takeGrid\(\)/g) || []).length, 2,
    'both the teed-tab and the pane-owned ladder short-circuit the same way');
});

// ---- the giant-glyph defect (phone_drive / phone_webgl_repair, tab-26f4, 2026-11-12) ------------------
//
// After the phone TOOK the grid the model was 49x41 and the render service reported a 23px DEVICE cell, but
// the pane showed ~16 enormous clipped glyphs over a correct, tiny paint of the same buffer. The DOM
// renderer on the identical grid painted 1:1 (measured pitch 7.78 px against a 7.69 px cell). Asking the
// WebGL renderer for a resize AT THE GRID IT ALREADY HELD repainted it exactly right - so the renderer's
// geometry is a cache that a grid or cell change must invalidate.
test('a grid or cell change repaints the WebGL canvas at the size the model holds', () => {
  const sync = fnSrc('syncRendererGeometry');
  assert.match(sync, /usingWebglRenderer\(\)/, 'the sync is for the WebGL renderer only');
  // The repair that was measured to work: put the canvas BACKING STORE at the render service's device
  // canvas, then redraw - the addon leaves it at CSS size after a grid change, which is the giant-glyph bug.
  assert.match(sync, /dims\.device\.canvas\.width/, 'it reads the size the service says the grid needs');
  assert.match(sync, /cv\.width\s*=\s*wantW/, 'and writes it to the canvas backing store');
  assert.match(sync, /_requestRedrawViewport/, 'a resized canvas is a cleared one, so it is redrawn at once');
  assert.ok(!/term\.resize\(/.test(sync), 'and it never changes the model itself - a repaint, not a resize');

  // Every place that changes the CELL must sync, or the stale-scale paint comes back on that path.
  const fit = fnSrc('fitTabOwnedGrid');
  const fontWrites = (fit.match(/term\.options\.fontSize\s*=/g) || []).length;
  assert.ok(fontWrites >= 4, 'the fit still sets the font on several paths');
  // The owned branch, the readable-floor branch and the step branch each sync right after. The zoom branch
  // routes through healTerminalSurface, which syncs too - so count both kinds.
  const syncs = (fit.match(/syncRendererGeometry\(\)/g) || []).length
    + (fit.match(/healTerminalSurface\(/g) || []).length;
  assert.ok(syncs >= 4, `a font change must invalidate the renderer cache (found ${syncs} invalidations for ${fontWrites} font writes)`);

  // The commit path: a grid the model actually changed, and the resize branch, both sync.
  assert.match(SRC, /const geomChanged = term\.cols!==m\.cols \|\| term\.rows!==m\.rows;/);
  const afterResize = SRC.slice(SRC.indexOf('preserveTermViewport(()=>term.resize(m.cols, m.rows))'));
  assert.match(afterResize.slice(0, 700), /scheduleRendererRepair\(\)/,
    'a model resize re-derives the renderer, and re-asserts after the renderer\'s own later resize');
  const repair = fnSrc('scheduleRendererRepair');
  assert.match(repair, /requestAnimationFrame/, 'the repair re-asserts on the next frame');
  assert.match(repair, /setTimeout/, 'and once after the settle window, when the last resize has landed');
  const heal = fnSrc('healTerminalSurface');
  assert.match(heal, /syncRendererGeometry\(\)/, 'and a surface heal carries the sync with it');
});

test('the renderer sync repairs a stale canvas, is a no-op when correct, and coalesces a burst', () => {
  const sync = fnSrc('syncRendererGeometry');
  const calls = [];
  const canvas = { width: 376, height: 697, style: {} };
  const sandbox = {
    usingWebglRenderer: () => false,
    webglAddon: { _renderer: { _canvas: canvas, handleResize: (c, r) => calls.push([c, r]), _requestRedrawViewport: () => calls.push(['redraw']) } },
    term: { cols: 49, rows: 41, rows_: 41, refresh(){}, _core: { _renderService: { dimensions: { device: { canvas: { width: 1127, height: 2091 } }, css: { canvas: { width: 376, height: 697 } } } } } },
    // The coalescing stamp lives in a page-level `let`, which an extracted function body cannot see. Seed it.
    _rendererSyncAt: 0,
    requestAnimationFrame: () => 1,
    Date: { now: () => 1000 },
  };
  vm.createContext(sandbox);
  vm.runInContext(sync + '\n;this.sync = syncRendererGeometry;', sandbox);
  assert.equal(sandbox.sync(true), false, 'nothing to sync without a WebGL renderer');
  assert.equal(calls.length, 0, 'the DOM renderer is never touched');

  sandbox.usingWebglRenderer = () => true;
  assert.equal(sandbox.sync(), true, 'a stale canvas is repaired');
  assert.equal(canvas.width, 1127, 'the backing store takes the device size the service reports');
  assert.equal(canvas.height, 2091);
  assert.equal(canvas.style.width, '376px', 'and the CSS box stays the layout size');
  assert.ok(calls.some(c => c[0] === 'redraw'), 'a resized canvas is cleared, so it is redrawn');
  assert.equal(sandbox.sync(), false, 'a second call inside the same instant is coalesced');
  // A canvas that already matches needs no repair - the sync is idempotent, not a repaint on every heal.
  assert.equal(sandbox.sync(true), false, 'an already-correct canvas is left alone');
});
