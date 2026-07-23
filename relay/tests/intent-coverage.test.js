const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const REPO = path.join(__dirname, '..');

function read(rel) {
  return fs.readFileSync(path.join(REPO, ...rel.split('/')), 'utf8');
}

// Static scanner: for every `fetch(` occurrence, take the following ~400 chars as
// the call text and flag it MUTATING when that text carries an explicit
// method:'POST'|'PUT'|'PATCH'|'DELETE'. A mutating raw fetch is EXEMPT when any of
// the 3 source lines preceding the fetch carries a `raw-fetch-allowlist:` marker.
// Returns the surviving (line, snippet) list — every entry is a policy violation.
function rawMutatingFetches(source) {
  const lines = source.split('\n');
  const survivors = [];
  const NEEDLE = 'fetch(';
  const MUTATING = /method\s*:\s*['"](POST|PUT|PATCH|DELETE)['"]/;
  for (let idx = source.indexOf(NEEDLE); idx !== -1; idx = source.indexOf(NEEDLE, idx + 1)) {
    const callText = source.slice(idx, idx + 400);
    if (!MUTATING.test(callText)) continue;
    // 1-based line of this fetch(
    const lineNo = source.slice(0, idx).split('\n').length;
    // 3 source lines preceding the fetch (lines lineNo-1 .. lineNo-3, 1-based)
    let exempt = false;
    for (let back = 1; back <= 3; back++) {
      const prev = lines[lineNo - 1 - back];
      if (prev !== undefined && prev.includes('raw-fetch-allowlist:')) { exempt = true; break; }
    }
    if (exempt) continue;
    survivors.push({ line: lineNo, snippet: callText.slice(0, 80) });
  }
  return survivors;
}

// ===========================================================================
// r.1.6.2 — static no-raw-mutating-fetch allowlist scan
// (later siblings append integration blocks below this delimited section)
// ===========================================================================

test('r.1.6.2 static: no un-allowlisted raw mutating fetch in browser sources', () => {
  for (const rel of ['public/index.html', 'public/projects.html']) {
    const survivors = rawMutatingFetches(read(rel));
    assert.deepEqual(survivors, [], `${rel} has un-allowlisted raw mutating fetch: ${JSON.stringify(survivors)}`);
  }
});

test('r.1.6.2 static: NEGATIVE CONTROL — scanner is not vacuously passing', () => {
  const raw = "await fetch(base+'/api/sessions/x',{method:'DELETE'});";
  assert.equal(rawMutatingFetches(raw).length, 1, 'bare raw mutating fetch must be flagged');
  const allowlisted = "// raw-fetch-allowlist: test\n" + raw;
  assert.equal(rawMutatingFetches(allowlisted).length, 0, 'allowlist marker within 3 lines must exempt');
});

test('r.1.6.2 static: index.html allowlist markers guard the raw-byte upload POSTs', () => {
  const index = read('public/index.html');
  const idxLines = index.split('\n');
  const markers = [];
  idxLines.forEach((l, i) => { if (l.includes('raw-fetch-allowlist:')) markers.push(i + 1); });
  assert.equal(markers.length, 2, `index.html must hold exactly 2 raw-fetch-allowlist markers, got ${markers}`);
  for (const m of markers) {
    let near = false;
    for (let d = -3; d <= 3; d++) {
      const l = idxLines[m - 1 + d];
      if (l && l.includes("fetch(base+'/api/upload?")) { near = true; break; }
    }
    assert.ok(near, `allowlist marker at line ${m} must sit within 3 lines of a raw-byte upload POST`);
  }
  const projMarkers = read('public/projects.html').split('\n').filter(l => l.includes('raw-fetch-allowlist:'));
  assert.equal(projMarkers.length, 0, 'projects.html must hold zero allowlist markers');
});

test('r.1.6.2 static: no XMLHttpRequest / sendBeacon escape hatches', () => {
  for (const rel of ['public/index.html', 'public/projects.html']) {
    const src = read(rel);
    assert.ok(!src.includes('XMLHttpRequest'), `${rel} must not use XMLHttpRequest`);
    assert.ok(!src.includes('sendBeacon'), `${rel} must not use sendBeacon`);
  }
});

test('r.1.6.2 static: both sources load intent-journal.js', () => {
  for (const rel of ['public/index.html', 'public/projects.html']) {
    assert.ok(read(rel).includes('<script src="intent-journal.js"></script>'), `${rel} must load intent-journal.js`);
  }
});

test('r.1.6.2 static: index.html routes residue mutations through sendIntent with the right verbs', () => {
  const index = read('public/index.html');
  const has = (re) => assert.ok(re.test(index), `index.html missing ${re}`);
  const count = (re) => (index.match(re) || []).length;
  has(/sendIntent\('POST',[^\n]*\/autoheal/);
  assert.equal(count(/sendIntent\('DELETE',[^\n]*\/api\/sessions\//g), 2, 'two DELETE /api/sessions/ sendIntent routes');
  has(/sendIntent\('PATCH',[^\n]*\/api\/sessions\//);
  has(/sendIntent\('PATCH',[^\n]*\/api\/uploads\//);
  has(/sendIntent\('DELETE',[^\n]*\/api\/uploads\//);
});
