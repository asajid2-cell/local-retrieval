const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// fleet.js touches no DOM API beyond assigning innerHTML, so it loads into a bare vm context whose
// global is a plain object — no jsdom, no browser, just the module's own exports.
const file = path.join(__dirname, '..', 'public', 'fleet.js');
const source = fs.readFileSync(file, 'utf8');
const stub = {};
vm.runInNewContext(source, { globalThis: stub }, { filename: 'fleet.js' });
const MuxFleet = stub.MuxFleet;

function el() {
  return { innerHTML: '' };
}
function count(haystack, needle) {
  return haystack.split(needle).length - 1;
}

const ESC = '\x1b';

test('module loads without a DOM and exposes the render surface', () => {
  assert.equal(typeof MuxFleet, 'object');
  assert.equal(typeof MuxFleet.renderFleet, 'function');
  assert.equal(MuxFleet.SNIPPET_LINES, 2);
  assert.equal(MuxFleet.SNIPPET_BYTES, 2048);
});

test('cleanTerminalText strips CSI and OSC sequences', () => {
  assert.equal(MuxFleet.cleanTerminalText(ESC + '[31mred' + ESC + '[0m'), 'red');
  assert.equal(MuxFleet.cleanTerminalText('a' + ESC + ']0;title\x07b'), 'ab');
  assert.equal(MuxFleet.cleanTerminalText('a' + ESC + ']0;title' + ESC + '\\b'), 'ab');
  assert.equal(MuxFleet.cleanTerminalText('l1\nl2\tx'), 'l1\nl2\tx');
});

test('renderFleet emits one row per session', () => {
  const target = el();
  const n = MuxFleet.renderFleet(target, {
    sessions: [
      { name: 'alpha', state: 'green' },
      { name: 'beta', state: 'amber' },
      { name: 'gamma', state: 'red' },
    ],
  });
  assert.equal(n, 3);
  assert.equal(count(target.innerHTML, 'class="fleet-row'), 3);
});

test('state classes are whitelisted and garbage collapses to unknown', () => {
  const target = el();
  MuxFleet.renderFleet(target, {
    sessions: [
      { name: 'alpha', state: 'green' },
      { name: 'evil', state: '" onclick=x' },
    ],
  });
  assert.match(target.innerHTML, /fleet-state-green/);
  assert.match(target.innerHTML, /fleet-state-unknown/);
  assert.doesNotMatch(target.innerHTML, /" onclick=x/);
  assert.equal(target.innerHTML.indexOf('onclick'), -1);
});

test('snippet renders as escaped text inside a pre', () => {
  const target = el();
  MuxFleet.renderFleet(target, { sessions: [{ name: 'alpha', snippet: 'build finished' }] });
  assert.match(target.innerHTML, /<pre class="fleet-snippet">build finished<\/pre>/);
});

test('session names are HTML-escaped', () => {
  const target = el();
  MuxFleet.renderFleet(target, { sessions: [{ name: '<img src=x onerror=1>', state: 'green' }] });
  assert.match(target.innerHTML, /&lt;img/);
  assert.equal(target.innerHTML.indexOf('<img'), -1);
});

test('snippetText clamps to the last SNIPPET_LINES lines', () => {
  const out = MuxFleet.snippetText({ snippet: 'l1\nl2\nl3\nl4\nl5' });
  assert.equal(out, 'l4\nl5');
});

test('snippetText clamps a long single line to SNIPPET_BYTES', () => {
  const out = MuxFleet.snippetText({ snippet: 'a'.repeat(5000) });
  assert.ok(out.length <= MuxFleet.SNIPPET_BYTES, `expected <= 2048, got ${out.length}`);
});

test('snippetText strips terminal escapes before rendering', () => {
  const target = el();
  MuxFleet.renderFleet(target, { sessions: [{ name: 'alpha', snippet: ESC + '[31mALERT' + ESC + '[0m' }] });
  assert.equal(target.innerHTML.indexOf(ESC), -1);
  assert.match(target.innerHTML, /ALERT/);
});

test('ageText renders human ages', () => {
  assert.equal(MuxFleet.ageText(0), 'now');
  assert.equal(MuxFleet.ageText(30000), '30s ago');
  assert.equal(MuxFleet.ageText(300000), '5m ago');
});

test('badges appear only when their flag is set', () => {
  const heal = el();
  MuxFleet.renderFleet(heal, { sessions: [{ name: 'a', autoheal: true }] });
  assert.match(heal.innerHTML, /fleet-autoheal/);

  const degraded = el();
  MuxFleet.renderFleet(degraded, { sessions: [{ name: 'a', snippetDegraded: true }] });
  assert.match(degraded.innerHTML, /fleet-degraded/);

  const signed = el();
  MuxFleet.renderFleet(signed, { sessions: [{ name: 'a', snippetSig: 'abc' }] });
  assert.match(signed.innerHTML, /fleet-signed/);

  const plain = el();
  MuxFleet.renderFleet(plain, { sessions: [{ name: 'a', state: 'green' }] });
  assert.equal(plain.innerHTML.indexOf('fleet-badges'), -1);
});

test('empty payloads distinguish host-offline from no-sessions', () => {
  const offline = el();
  assert.equal(MuxFleet.renderFleet(offline, { sessions: [], hostUp: false }), 0);
  assert.match(offline.innerHTML, /host offline/i);

  const idle = el();
  assert.equal(MuxFleet.renderFleet(idle, { sessions: [], hostUp: true }), 0);
  assert.match(idle.innerHTML, /No sessions yet/);
});
