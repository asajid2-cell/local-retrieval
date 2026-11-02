// The third sizing mode for a teed PC tab: 'flex'. It keeps the width fill of 'fill' and squashes the
// rendered screen vertically so every row is on screen without side margins. xterm's own lineHeight
// option refuses values below 1, so the squeeze is a CSS scale on .xterm-screen - which also leaves every
// hit test intact, because this page derives cells from the screen RECT (bufferCellAt, emapPlace).
//
// The arithmetic is lifted out of the shipped page and run here, so the only non-obvious decision - how
// far to squash - is checked without a renderer.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');

function section(start, end) {
  const from = source.indexOf(start);
  assert.notEqual(from, -1, `missing section start: ${start}`);
  const to = source.indexOf(end, from + start.length);
  assert.notEqual(to, -1, `missing section end: ${end}`);
  return source.slice(from, to);
}

function harness(store) {
  const s = Object.assign({}, store);
  const sandbox = { LS: { get: (k, d) => (k in s ? s[k] : d), set: (k, v) => { s[k] = v; } } };
  vm.createContext(sandbox);
  vm.runInContext(
    section('let tabFitMode =', '// Cell metrics only change after the renderer repaints') +
    '\nthis.flexScaleTarget=flexScaleTarget;this.flexStretchTarget=flexStretchTarget;this.mode=()=>tabFitMode;' +
    'this.floor=FLEX_SCALE_FLOOR;this.maxStretch=FLEX_STRETCH_MAX;',
    sandbox,
    { filename: 'index.html#flex-fit' },
  );
  return { sandbox, store: s };
}

const near = (a, b, eps) => Math.abs(a - b) <= (eps === undefined ? 1e-6 : eps);

test('a grid that already fits is not squashed at all', () => {
  const h = harness();
  // 40 rows of 20 px in 899 px: the analytic answer is above 1, so the cap holds the scale at 1.
  assert.equal(h.sandbox.flexScaleTarget(900, 40, 20), 1);
  assert.equal(h.sandbox.flexScaleTarget(899, 40, 20), 1);
});

test('the scale is exactly what puts the last row inside the pane', () => {
  const h = harness();
  // 815 px of pane over 40 rows of 24 px cells = 0.8479. One pixel is reserved for xterm's per-cell
  // floor(), which is why the numerator is availH-1 rather than availH.
  const want = h.sandbox.flexScaleTarget(815, 40, 24);
  assert.ok(near(want, 814 / 960, 1e-9), `got ${want}`);
});

test('the target depends only on the pane and the unpainted grid, never on a previous scale', () => {
  const h = harness();
  // cellMetrics() reports layout metrics, which a transform does not change, so re-measuring mid-squash
  // yields the same answer: the fit converges instead of chasing its own transform.
  const a = h.sandbox.flexScaleTarget(815, 40, 24);
  const b = h.sandbox.flexScaleTarget(815, 40, 24);
  assert.equal(a, b);
});

test('below the legibility floor it settles at the floor instead of squashing further', () => {
  const h = harness();
  // 40 rows of 24 px would need 0.31 to fit in 300 px; the floor wins and the mode scrolls like 'fill'.
  assert.equal(h.sandbox.flexScaleTarget(300, 40, 24), h.sandbox.floor);
  assert.ok(h.sandbox.floor > 0.6 && h.sandbox.floor <= 1);
});

test('invalid measurements leave the screen unscaled', () => {
  const h = harness();
  for (const args of [[0, 40, 24], [815, 0, 24], [815, 40, 0], [-5, -1, -1]]) {
    assert.equal(h.sandbox.flexScaleTarget(...args), 1, `args ${args}`);
  }
});

test('the horizontal stretch closes the whole-pixel font gap, and never squeezes', () => {
  const h = harness();
  // font 21 paints an 11 px cell on 120 columns = 1320 px inside a 1409 px content box.
  const sx = h.sandbox.flexStretchTarget(1409, 120, 11);
  assert.ok(near(sx, 1409 / 1320, 1e-9), `got ${sx}`);
  // A grid at or wider than the pane is left alone; 'flex' never shrinks horizontally.
  assert.equal(h.sandbox.flexStretchTarget(1320, 120, 11), 1);
  assert.equal(h.sandbox.flexStretchTarget(1300, 120, 11), 1);
  // A pathological gap is capped so glyphs stay recognisable.
  assert.equal(h.sandbox.flexStretchTarget(4000, 120, 11), h.sandbox.maxStretch);
  for (const args of [[0, 120, 11], [1409, 0, 11], [1409, 120, 0]]) {
    assert.equal(h.sandbox.flexStretchTarget(...args), 1, `args ${args}`);
  }
});

test('the mode is remembered per device and an unknown stored value falls back to fill', () => {
  assert.equal(harness().sandbox.mode(), 'fill');
  assert.equal(harness({ mux_tab_fit: 'flex' }).sandbox.mode(), 'flex');
  assert.equal(harness({ mux_tab_fit: 'fit' }).sandbox.mode(), 'fit');
  assert.equal(harness({ mux_tab_fit: 'nonsense' }).sandbox.mode(), 'fill');
});

test('the chip cycles fill -> fit -> flex -> fill and names all three', () => {
  const cycle = section("tabFitMode = (tabFitMode==='fill')", '\n');
  assert.match(cycle, /'fill'\) \? 'fit'/);
  assert.match(cycle, /'fit'\) \? 'flex'/);
  assert.match(cycle, /: 'fill'/);

  const chip = section('function sizeButtonPresentation()', 'function updateSizeBtn()');
  assert.match(chip, /🖥 fills width ·/);
  assert.match(chip, /🖥 fits rows ·/);
  assert.match(chip, /🖥 flex fit ·/);
});

test('flex takes the width estimate, like fill, and transforms only the rendered screen', () => {
  const fit = section('function fitTabOwnedGrid()', 'function applyPan()');
  // The font choice itself must not treat flex as the row-fitting mode: flex spans the width.
  assert.match(fit, /const fill = tabFitMode !== 'fit';/);
  assert.match(fit, /const sx = tabFitMode==='flex' \? flexStretchTarget\(availW, term\.cols, cell\.w\) : 1;/);
  assert.match(fit, /const sy = tabFitMode==='flex' \? flexScaleTarget\(availH, term\.rows, cell\.h\) : 1;/);
  assert.match(fit, /applyFlexTransform\(sx, sy\)/);
  // 'fill' and 'fit' are untouched by the transform, and a grid we no longer own is cleared.
  assert.match(fit, /if\(tabFitMode!=='flex'\) applyFlexTransform\(1, 1\);/);
  assert.match(fit, /applyFlexTransform\(1, 1\);\n    if\(appliedFont!==fontSize\)/);

  const scale = section('function applyFlexTransform(', '\n}\n');
  assert.match(scale, /scale\('/);
  assert.match(scale, /querySelector\('\.xterm-screen'\)/);
  assert.match(scale, /classList\.add\('stretch'\)/);
  assert.doesNotMatch(scale, /lineHeight/, 'lineHeight below 1 throws in this xterm build');
});

test('the row-overflow test measures the squashed height, or the scrollbar never leaves', () => {
  const align = section('function alignTerminalSurface()', '\n}\n');
  assert.match(align, /const rowScale = \(owned && tabFitMode==='flex'\) \? tabFlexSy : 1;/);
  assert.match(align, /term\.rows\*cellMetrics\(\)\.h\*rowScale > visibleH\+1/);
  assert.match(align, /const gridH=\(term\.rows\|\|0\)\*cellMetrics\(\)\.h\*rowScale;/);
});
