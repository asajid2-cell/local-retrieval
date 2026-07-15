const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

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
