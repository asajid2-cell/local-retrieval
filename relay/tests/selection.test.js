// Regression: desktop selection must feel like a real local terminal. Three things break it on the web:
// (1) a live TUI repaints every frame and wipes the highlight mid-drag, (2) the clipboard API refuses on a
// plain-http origin and the copy silently dies, (3) a custom right-click menu steals the one native escape
// hatch left. The phone flow (explicit Sel toggle + copy overlay) is deliberately NOT touched by any of it.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');

// Pull the real functions out of index.html and run them — this tests the shipped code, not a copy.
// (Same brace-walk as wheel-scroll.test.js, extended to load several functions into ONE context so they
// share the module-level state they mutate: autoFreeze, selectMode, autoCopyOnSelect.)
function fnSource(name){
  const start = source.indexOf('function ' + name + '(');
  assert.notEqual(start, -1, 'missing function ' + name);
  let depth = 0, end = -1;
  for(let i = source.indexOf('{', start); i < source.length; i++){
    if(source[i] === '{') depth++;
    else if(source[i] === '}'){ depth--; if(depth === 0){ end = i + 1; break; } }
  }
  assert.notEqual(end, -1, 'unbalanced braces for ' + name);
  return source.slice(start, end);
}

function load(ctx, names, prelude){
  const code = (prelude ? prelude + '\n' : '')
    + names.map(fnSource).join('\n') + '\n'
    + names.map(n => 'this.' + n + ' = ' + n + ';').join('\n');
  vm.runInNewContext(code, ctx);
  return ctx;
}

function section(start, end){
  const from = source.indexOf(start);
  const to = source.indexOf(end, from + start.length);
  assert.notEqual(from, -1, 'missing section start: ' + start);
  assert.notEqual(to, -1, 'missing section end: ' + end);
  return source.slice(from, to);
}

// ---- (1) the drag latch: a live highlight always wins ----------------------------------------------

function freezeCtx(){
  const ctx = {
    selectMode: false,
    autoFreeze: false,
    hasSel: false,          // what the stubbed xterm reports
    flushes: 0,
    copies: 0,
    autoCopyOnSelect: false,
  };
  ctx.term = { hasSelection: () => ctx.hasSel };
  ctx.flushFrozen = () => { ctx.flushes++; };
  ctx.copySelectionNow = () => { ctx.copies++; return Promise.resolve(true); };
  return load(ctx, ['termHasSelection', 'maybeUnfreezeSelection', 'selectionFreezeSync', 'autoCopyIfArmed']);
}

test('a mouse-drag selection latches the freeze and no unfreeze path can flush through it', () => {
  const ctx = freezeCtx();

  // Drag starts producing a highlight → frozen, nothing replayed yet.
  ctx.hasSel = true;
  assert.equal(ctx.selectionFreezeSync(), true, 'drag selection did not latch the freeze');
  assert.equal(ctx.autoFreeze, true, 'autoFreeze not set from onSelectionChange (mouse drag, not Sel mode)');
  assert.equal(ctx.flushes, 0);

  // Every unfreeze path (the #term mouseup, the document mouseup, the 2s watchdog) funnels through this.
  // While the highlight is alive it MUST refuse — that is what survives 30 lines of streaming output.
  assert.equal(ctx.maybeUnfreezeSelection(), false, 'unfroze while a selection was still live');
  assert.equal(ctx.maybeUnfreezeSelection(), false, 'unfroze on a repeat watchdog tick');
  assert.equal(ctx.flushes, 0, 'flushed buffered output through a live drag-selection');
  assert.equal(ctx.autoFreeze, true);

  // Selection cleared (click away) → live again, buffered output replayed exactly once.
  ctx.hasSel = false;
  assert.equal(ctx.selectionFreezeSync(), false);
  assert.equal(ctx.autoFreeze, false, 'still frozen after the selection cleared');
  assert.equal(ctx.flushes, 1, 'buffered output not replayed on clear');

  // Already live: idempotent, no double flush.
  assert.equal(ctx.maybeUnfreezeSelection(), false);
  assert.equal(ctx.flushes, 1);
});

test('the phone Sel toggle still owns its own unfreeze (drag latch never touches selectMode)', () => {
  const ctx = freezeCtx();
  ctx.selectMode = true;
  ctx.autoFreeze = true;
  ctx.hasSel = false;
  assert.equal(ctx.maybeUnfreezeSelection(), false, 'drag latch unfroze a phone Sel-mode session');
  assert.equal(ctx.flushes, 0);
  ctx.hasSel = true;
  ctx.autoFreeze = false;
  ctx.selectionFreezeSync();
  assert.equal(ctx.autoFreeze, false, 'selectMode must not be shadowed by autoFreeze');
});

test('auto-copy fires on release, only when a selection actually exists', () => {
  const ctx = freezeCtx();
  ctx.autoCopyOnSelect = false; ctx.hasSel = true;
  assert.equal(ctx.autoCopyIfArmed(), false, 'copied with the toggle off');
  ctx.autoCopyOnSelect = true; ctx.hasSel = false;
  assert.equal(ctx.autoCopyIfArmed(), false, 'copied an empty selection');
  assert.equal(ctx.copies, 0);
  ctx.hasSel = true;
  assert.equal(ctx.autoCopyIfArmed(), true);
  assert.equal(ctx.copies, 1);
});

test('xterm selection events and the freeze watchdogs are wired to the single latch', () => {
  assert.match(source, /term\.onSelectionChange\(selectionFreezeSync\)/,
    'onSelectionChange must go through selectionFreezeSync so mouse drags latch like Sel mode');
  assert.match(source, /document\.addEventListener\('mouseup', \(\)=>\{ setTimeout\(maybeUnfreezeSelection, 0\)/,
    'the lost-mouseup safety valve must respect the drag latch');
  const valve = section('// FREEZE SAFETY-VALVE', 'function reloadTerminal');
  assert.doesNotMatch(valve, /term\.hasSelection\(\)\)\)\{ autoFreeze=false/,
    'the watchdog must not re-implement the unfreeze condition — it drifts from the latch');
});

// ---- (2) copy: selection → clipboard, overlay fallback when the clipboard refuses ------------------

function copyCtx(writeText){
  const ctx = { wrote: [], flashes: [], opened: 0, selection: 'line one\nline two' };
  ctx.term = { getSelection: () => ctx.selection };
  ctx.navigator = { clipboard: writeText ? { writeText: t => { ctx.wrote.push(t); return writeText(t); } } : undefined };
  ctx.flash = m => { ctx.flashes.push(m); };
  ctx.openCopyView = () => { ctx.opened++; };
  return load(ctx, ['copySelectionNow']);
}

test('copy routes the terminal selection to the clipboard writer verbatim', async () => {
  const ctx = copyCtx(() => Promise.resolve());
  assert.equal(await ctx.copySelectionNow(), true);
  assert.deepEqual(ctx.wrote, ['line one\nline two'], 'selection not handed to the clipboard verbatim');
  assert.equal(ctx.opened, 0, 'overlay opened on a successful copy');
  assert.match(ctx.flashes.join(' '), /Copied selection/);
});

test('a rejected clipboard write falls back to the copy overlay instead of dead-ending', async () => {
  const ctx = copyCtx(() => Promise.reject(new Error('NotAllowedError: write permission denied')));
  assert.equal(await ctx.copySelectionNow(), false);
  assert.deepEqual(ctx.wrote, ['line one\nline two']);
  assert.equal(ctx.opened, 1, 'clipboard rejected and no overlay fallback — the copy is simply lost');
  assert.match(ctx.flashes.join(' '), /Ctrl\/⌘\+C/, 'user not told how to finish the copy by hand');
});

test('no clipboard API at all still reaches the overlay fallback', async () => {
  const ctx = copyCtx(null);   // navigator.clipboard undefined (plain-http origin)
  assert.equal(await ctx.copySelectionNow(), false);
  assert.equal(ctx.opened, 1);
});

test('an empty selection copies nothing and opens nothing', async () => {
  const ctx = copyCtx(() => Promise.resolve());
  ctx.selection = '';
  assert.equal(await ctx.copySelectionNow(), false);
  assert.deepEqual(ctx.wrote, []);
  assert.equal(ctx.opened, 0);
});

test('Ctrl/Cmd+Shift+C is bound to the selection copy on both platforms', () => {
  const keys = section('// ---- desktop keyboard shortcuts', "if(e.key==='F11')");
  assert.match(keys, /_isC && \(e\.ctrlKey\|\|e\.metaKey\) && e\.shiftKey\)\{ copySelectionNow\(\)/,
    'Cmd+Shift+C (macOS) must copy the same as Ctrl+Shift+C');
});

// ---- (3) the auto-copy toggle round-trips the real LS shim -----------------------------------------

test('auto-copy-on-select persists through the real LS/localStorage shim', () => {
  const lsLine = source.split('\n').find(l => l.trim().startsWith('const LS = {'));
  assert.ok(lsLine, 'missing the LS localStorage shim');

  const store = new Map();
  const ctx = {
    localStorage: {
      getItem: k => (store.has(k) ? store.get(k) : null),
      setItem: (k, v) => { store.set(k, String(v)); },
    },
  };
  load(ctx, ['getAutoCopyOnSelect', 'setAutoCopyOnSelect'], lsLine);

  assert.equal(ctx.getAutoCopyOnSelect(), false, 'auto-copy must default to off');
  assert.equal(ctx.setAutoCopyOnSelect(true), true);
  assert.equal(store.get('mux_autocopy'), 'true', 'toggle not written through the LS JSON shim');
  assert.equal(ctx.getAutoCopyOnSelect(), true, 'toggle did not survive a reload');
  assert.equal(ctx.autoCopyOnSelect, true, 'in-memory flag out of sync with storage');

  ctx.setAutoCopyOnSelect(false);
  assert.equal(store.get('mux_autocopy'), 'false');
  assert.equal(ctx.getAutoCopyOnSelect(), false);
  assert.equal(ctx.autoCopyOnSelect, false);

  // A corrupt/absent value must read as off, not throw (the LS shim swallows the parse error).
  store.set('mux_autocopy', '{not json');
  assert.equal(ctx.getAutoCopyOnSelect(), false);

  // The toggle needs a real control, or it is unreachable.
  assert.match(source, /setAutoCopyOnSelect\(acbox\.checked\)/, 'no UI control wired to the toggle');
  assert.match(source, /acbox\.checked\s*=\s*getAutoCopyOnSelect\(\)/, 'control does not reflect the stored value');
});

// ---- (4) the native right-click menu stays native over the terminal --------------------------------

test('nothing preventDefaults contextmenu over #term, so right-click Copy still works on a selection', () => {
  const desktop = section('// ---- DESKTOP text selection', '// ---- IMAGE PASTE');
  assert.doesNotMatch(desktop, /contextmenu[^\n]*preventDefault/,
    'a custom right-click menu over #term kills the last copy escape hatch when the clipboard API is blocked');
  assert.match(desktop, /no contextmenu handler here, on purpose/,
    'the deliberate absence must stay documented or it gets "fixed" back in');

  // Whole-file sweep: the ONLY element allowed to claim contextmenu is the session tab button (pin gesture).
  const targets = [...source.matchAll(/([\w$]+|\$\('[^']+'\))\.addEventListener\('contextmenu'/g)].map(m => m[1]);
  assert.deepEqual(targets, ['sb'],
    'contextmenu may only be claimed by the session tab (sb); found: ' + JSON.stringify(targets));
  assert.doesNotMatch(source, /oncontextmenu/, 'inline oncontextmenu handler added');
});

// ---- the phone flow is untouched ------------------------------------------------------------------

test('phone select/freeze/copy-overlay flow is unchanged', () => {
  assert.match(source, /function toggleSelect\(\)\{[\s\S]*selectMode=!selectMode/, 'Sel toggle gone');
  assert.match(source, /\$\('#copybtn'\)\.onclick = openCopyView/, 'copy overlay button unwired');
  assert.match(source, /\$\('#copyall'\)\.onclick/, 'copy-all fallback gone');
  const overlay = section('function openCopyView', 'function closeCopyView');
  assert.match(overlay, /setAttribute\('inert'/, 'copy overlay no longer modal');
});

test('the build stamp was bumped for this deploy', () => {
  const m = source.match(/window\.__muxBuild='([^']+)'/);
  assert.ok(m, 'missing window.__muxBuild');
  assert.notEqual(m[1], '2026-07-12-mobile-ime-viewport-hardening', '__muxBuild not bumped');
});
