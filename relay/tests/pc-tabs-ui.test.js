// §7.3 UI: a teed PC tab (kind 'local-tab', mirrored by muxtee) is a LOCAL terminal, not a mux
// session. Three client facts carry that distinction and none of them may drift back:
//
//   * the tab strip badges it PC-TAB (index.html),
//   * the size chip reads "follows PC tab · C×R" and refuses a pin, because the grid IS the PC's
//     physical screen (§7.1 - a pin would resize the tab under the person sitting at it),
//   * the Projects master list grows a "PC tabs" group that hides plain shells unless the user expands.
//
// Source-grep assertions, in the style of projects-mirror-ui.test.js: the client is one inline script
// and there is no DOM harness for projects.html, so the contract is asserted at the source.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const indexSource = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');
const projectsSource = fs.readFileSync(path.join(__dirname, '..', 'public', 'projects.html'), 'utf8');

test('§7.3: the tab strip badges a teed PC tab', () => {
  assert.match(indexSource, /s\.localTab\?'<span class="pcchip localtab"/);
  assert.match(indexSource, /\.tab \.pcchip\.localtab\s*\{/);
});

test('§7.3: the size chip says "follows PC tab" and locks the pin', () => {
  // The label and the lock come from the same branch, so pinning cannot be offered on a tab-owned grid.
  assert.match(indexSource, /sizeTabOwned/);
  assert.match(indexSource, /follows PC tab/);
  assert.match(indexSource, /sizeTabOwned && !sizePinned/);
  // cycleSize must refuse the pin, not just paint a different label.
  assert.match(indexSource, /function cycleSize\(\)\{\s*if\(sizeTabOwned && !sizePinned\)/);
  assert.match(indexSource, /cannot be pinned/);
});

test('§7.3: the Projects master list grows a PC tabs group', () => {
  assert.match(projectsSource, /function pcTabs\(\)/);
  assert.match(projectsSource, /function pcTabHasAgent\(/);
  assert.match(projectsSource, /function openPcTabs\(/);
  assert.match(projectsSource, /'PC tabs'/);
});

test('§7.3: plain shells are hidden until the group is expanded', () => {
  // pcTabHidden is the plain-shell rule; the visible count and the expand toggle both use it.
  assert.match(projectsSource, /function pcTabHidden\(s\)\{ return !pcTabHasAgent\(s\); \}/);
  assert.match(projectsSource, /showAllPcTabs/);
  assert.match(projectsSource, /Hide plain shells/);
  assert.match(projectsSource, /Show '\+hidden\+' plain shell/);
});

test('§7.3: PC-tab membership is structural, so the group appears without a manual refresh', () => {
  // A tab joining or leaving changes the whole list shape, so it belongs in structSig - the same path a
  // new chat takes. Without this the group never appears until the user reloads.
  assert.match(projectsSource, /tabSig=\(sess\|\|sessions\|\|\[\]\)\.filter\(s=>s&&s\.localTab\)/);
  assert.match(projectsSource, /structSig\(data, sessions\)/);
  assert.match(projectsSource, /structSig\(nd\|\|data, nsess\|\|sessions\)/);
});
