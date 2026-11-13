// The viewer half of the element map (OSC 21338 -> relay `E` frame -> hit boxes) was REMOVED by P1-2.
// The overlay was a second, independent pointer-to-cell mapping sitting on top of the terminal's, so a tap
// could land on a cell the box did not cover and the app saw a different event than a plain click. The
// viewer now ignores the `E` frame whole, and a tap is nothing but the app's own mouse-mode bytes.
//
// These assertions guard the removal: reinstating the overlay has to come with a new test, not a silent
// revert. They read the shipped page, so they cannot drift away from it.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const source = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');

function section(start, end) {
  const from = source.indexOf(start);
  assert.notEqual(from, -1, `missing section start: ${start}`);
  const to = source.indexOf(end, from + start.length);
  assert.notEqual(to, -1, `missing section end: ${end}`);
  return source.slice(from, to);
}

test('the viewer creates no element-map layer, nodes, or widgets chip', () => {
  assert.doesNotMatch(source, /\.emap-el/);
  assert.doesNotMatch(source, /#emap\b/);
  assert.doesNotMatch(source, /id\s*=\s*['"]emap['"]/);
  assert.doesNotMatch(source, /widgetsbtn/);
  assert.doesNotMatch(source, /\bcycleEmap\b|\bemapApply\b|\bemapBuild\b|\bemapPlace\b|\bemapTap\b/);
});

test('the stored widgets preference is retired once, and never written again', () => {
  assert.match(source, /localStorage\.removeItem\('mux_widgets'\)/);
  assert.doesNotMatch(source, /LS\.set\(\s*'mux_widgets'/);
  assert.doesNotMatch(source, /LS\.get\(\s*'mux_widgets'/);
});

test('an E control frame is ignored without being parsed', () => {
  const onControl = section('function onControl(s){', "  if(s[0]!=='d') return;");
  assert.doesNotMatch(onControl, /JSON\.parse/);
  assert.doesNotMatch(onControl, /\bemap/i);
  assert.match(onControl, /'E'/);   // the removed channel is still named, so the gap is not a mystery
});
