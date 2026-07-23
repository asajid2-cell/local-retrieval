// Executable assertion for the `web` half of docs/scroll-parity.json. The Python suite
// (muxd/tests/test_local_scroll_forwarding.py) owns the muxctl cells and the file's structure; this file owns
// the claim that the web cells describe the code that actually ships in relay/public/index.html.
//
// Deliberate asymmetry, so the next reader does not "fix" it: stripMouseReports is a NAMED function, so it is
// pulled out of index.html and RUN — the wheel-report cells are proven by execution. The capture-phase wheel
// router is an anonymous `$('#term').addEventListener('wheel', e=>{...})`; it cannot be lifted out and called
// (it closes over term/$/cellMetrics and needs a live DOM), so its cells are proven against its SOURCE
// branches instead. Both read the shipped file — neither tests a copy.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const HTML_PATH = 'relay/public/index.html';
const source = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');
const HTML_LINE_COUNT = source.split('\n').length;
const vectors = JSON.parse(fs.readFileSync(path.join(__dirname, '..', '..', 'docs', 'scroll-parity.json'), 'utf8'));
const web = vectors.cells.filter(c => c.surface === 'web');

const key = c => `${c.screen}/tracking=${c.tracking}/1007=${c.alt_scroll_1007}`;

// Match the block of `{...}` that starts at or after `from`, returning the index just past its close.
function matchBraces(from, what){
  const open = source.indexOf('{', from);
  assert.notEqual(open, -1, 'no block found for ' + what);
  let depth = 0;
  for(let i = open; i < source.length; i++){
    if(source[i] === '{') depth++;
    else if(source[i] === '}'){ depth--; if(depth === 0) return i + 1; }
  }
  assert.fail('unbalanced braces for ' + what);
}

// Pull the real function out of index.html and run it — this tests the shipped code, not a copy.
function extractFn(name){
  const start = source.indexOf('function ' + name + '(');
  assert.notEqual(start, -1, 'missing function ' + name);
  const ctx = {};
  vm.runInNewContext(source.slice(start, matchBraces(start, name)) + '\nthis.' + name + ' = ' + name + ';', ctx);
  return ctx[name];
}

// The router is anonymous — extract its SOURCE (body + the listener options that follow it).
const ROUTER_ANCHOR = "$('#term').addEventListener('wheel'";
function extractWheelRouterSource(){
  const start = source.indexOf(ROUTER_ANCHOR);
  assert.notEqual(start, -1, 'missing capture-phase wheel router in ' + HTML_PATH);
  assert.equal(source.indexOf(ROUTER_ANCHOR, start + 1), -1, 'more than one #term wheel router — anchor is ambiguous');
  const bodyEnd = matchBraces(start, 'wheel router');
  const lineEnd = source.indexOf('\n', bodyEnd);
  return source.slice(start, lineEnd === -1 ? bodyEnd : lineEnd);
}

test('web surface covers all 8 terminal states exactly once, each naming one known mechanism', () => {
  assert.equal(web.length, 8, 'expected 8 web cells');
  const expected = [];
  for(const screen of ['normal', 'alt'])
    for(const tracking of [false, true])
      for(const alt_scroll_1007 of [false, true])
        expected.push(key({screen, tracking, alt_scroll_1007}));

  const seen = web.map(key);
  assert.equal(new Set(seen).size, 8, 'duplicate web cells: ' + seen.join(', '));
  assert.deepEqual([...seen].sort(), expected.sort(), 'web cells do not cover the state axes exactly once');

  // exactly-one-mechanism: one named mechanism per cell, and it must be defined in the file.
  for(const c of web){
    assert.equal(typeof c.mechanism, 'string', key(c) + ' has no mechanism');
    assert.ok(
      Object.prototype.hasOwnProperty.call(vectors.mechanisms, c.mechanism),
      key(c) + ' names unknown mechanism ' + c.mechanism,
    );
  }
});

test('tracking wins on the alternate screen, and the real stripMouseReports delivers the notch untouched', () => {
  for(const c of web.filter(c => c.tracking === true && c.screen === 'alt')){
    assert.equal(c.mechanism, 'wheel_report', key(c) + ' must be wheel_report when the app tracks the mouse');
  }

  // The claim behind those cells: the app really receives the notch, and ONLY the notch.
  const strip = extractFn('stripMouseReports');
  assert.equal(strip('\x1b[<64;10;5M'), '\x1b[<64;10;5M', 'wheel-up report dropped');
  assert.equal(strip('\x1b[<65;10;5M'), '\x1b[<65;10;5M', 'wheel-down report dropped');
  assert.equal(strip('\x1b[<80;5;5M'), '\x1b[<80;5;5M', 'modified wheel (64|16) dropped');

  assert.equal(strip('\x1b[<0;10;5M'), '', 'plain click reached the app');
  assert.equal(strip('\x1b[<35;10;5M'), '', 'motion report (the 1003 flood) reached the app');
  assert.equal(strip('\x1b[M\x20\x21\x22'), '', 'X10 mouse report reached the app');
  assert.equal(strip('\x1b[555;79;1M'), '', 'urxvt/1015 report reached the app');
});

test("normal screen is the host's: the router scrolls xterm's scrollback and swallows the event", () => {
  for(const c of web.filter(c => c.screen === 'normal' && c.tracking === false)){
    assert.equal(c.mechanism, 'host_scrollback', key(c) + ' must be host_scrollback on a bare normal buffer');
  }

  const router = extractWheelRouterSource();
  const iAltBranch = router.indexOf('Alternate buffer');
  assert.notEqual(iAltBranch, -1, 'wheel router lost its alternate-buffer branch');

  const iScroll = router.indexOf('term.scrollLines(');
  assert.notEqual(iScroll, -1, 'normal-buffer branch no longer calls term.scrollLines()');
  const iPrevent = router.indexOf('preventDefault()', iScroll);
  assert.notEqual(iPrevent, -1, 'normal-buffer branch no longer calls preventDefault()');
  const iStop = router.indexOf('stopPropagation()', iPrevent);
  assert.notEqual(iStop, -1, 'normal-buffer branch no longer calls stopPropagation() — the app would see the notch');
  assert.ok(iStop < iAltBranch, 'scrollLines/preventDefault/stopPropagation must be the NORMAL-buffer branch');
  assert.ok(router.indexOf('capture:true') !== -1, 'router is no longer capture-phase');
});

test('the bare-alternate divergence from muxctl is recorded and accepted', () => {
  const cell = web.find(c => c.screen === 'alt' && c.tracking === false && c.alt_scroll_1007 === false);
  assert.ok(cell, 'missing web cell alt/tracking=false/1007=false');
  assert.equal(cell.mechanism, 'none', 'a real terminal drops the notch on a bare alternate screen');

  const rec = (vectors.divergences || []).find(d => /alt\/tracking=false\/1007=false/.test(d.cell || ''));
  assert.ok(rec, 'no divergence record for alt/tracking=false/1007=false');
  assert.equal(rec.muxctl, 'page_keys', 'muxctl keeps its rate-limited page-key fallback');
  assert.equal(rec.web, 'none', 'the web defers to xterm.js and drops the notch');
  assert.equal(rec.accepted, true, 'the divergence must be an accepted decision, not a latent bug');
});

test('the capture-phase divergence on a tracked NORMAL buffer is recorded and accepted', () => {
  // tracking-wins holds wherever the app sees the wheel at all. On the web normal buffer the capture-phase
  // router deliberately takes the notch first (a leaked DECSET 1000 would otherwise freeze the scrollback
  // forever), so those cells are host_scrollback — which is only honest because it is a recorded divergence.
  for(const c of web.filter(c => c.screen === 'normal' && c.tracking === true)){
    assert.equal(c.mechanism, 'host_scrollback', key(c) + ' must be host_scrollback: the capture-phase guard wins');
  }

  const rec = (vectors.divergences || []).find(d => /^web\/normal\/tracking=true/.test(d.cell || ''));
  assert.ok(rec, 'no divergence record for web/normal/tracking=true — the exception would be silent');
  assert.equal(rec.muxctl, 'wheel_report');
  assert.equal(rec.web, 'host_scrollback');
  assert.equal(rec.accepted, true, 'the divergence must be an accepted decision, not a latent bug');
});

test('every web anchor points at real lines of ' + HTML_PATH, () => {
  for(const c of web){
    const m = /^relay\/public\/index\.html:(\d+)-(\d+)$/.exec(c.anchor || '');
    assert.ok(m, key(c) + ' has a malformed anchor: ' + JSON.stringify(c.anchor));
    const start = Number(m[1]), end = Number(m[2]);
    assert.ok(start >= 1, key(c) + ' anchor starts before line 1');
    assert.ok(start <= end, key(c) + ' anchor start ' + start + ' > end ' + end);
    assert.ok(end <= HTML_LINE_COUNT, key(c) + ' anchor end ' + end + ' past EOF (' + HTML_LINE_COUNT + ' lines)');
  }
});

test('asserted_by names this file for the web surface', () => {
  assert.deepEqual(vectors.asserted_by, {
    muxctl: 'muxd/tests/test_local_scroll_forwarding.py',
    web: 'relay/tests/scroll-parity-web.test.js',
  });
});
