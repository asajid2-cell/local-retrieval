// The viewer half of the element map: the `E` control frame the relay sends after stripping OSC 21338,
// turned into hit boxes that sit on the cells the app actually painted, and a tap on one of those boxes
// turned back into the byte-identical SGR press/release pair the host click path already sends.
//
// The block is lifted straight out of the shipped page and run against fakes, like client-layout.test.js,
// so these assertions exercise the real code rather than a copy that can drift away from it.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');

const START = '// ---- TERMINAL ELEMENT MAP (OSC 21338)';
const END = '// ---- SIZE A PC TAB\'S SCREEN THE WAY A TERMINAL WOULD ----';

function section(start, end) {
  const from = source.indexOf(start);
  assert.notEqual(from, -1, `missing section start: ${start}`);
  const to = source.indexOf(end, from + start.length);
  assert.notEqual(to, -1, `missing section end: ${end}`);
  return source.slice(from, to);
}

function fakeNode(tag) {
  const node = {
    tagName: tag, className: '', title: '', tabIndex: 0, type: '',
    style: {}, dataset: {}, attrs: {}, children: [], parent: null,
    // Setting textContent removes every child, exactly as the DOM does - that is how emapClear() works.
    set textContent(v) { node.children.length = 0; node._text = v; },
    get textContent() { return node._text || ''; },
    appendChild(c) { c.parent = node; node.children.push(c); return c; },
    remove() {
      const p = node.parent;
      if (!p) return;
      const i = p.children.indexOf(node);
      if (i >= 0) p.children.splice(i, 1);
      node.parent = null;
    },
    setAttribute(k, v) { node.attrs[k] = v; },
  };
  const set = new Set();
  node.classList = {
    add: c => { set.add(c); },
    remove: c => { set.delete(c); },
    contains: c => set.has(c),
    toggle(c, on) {
      if (on === undefined) { if (set.has(c)) set.delete(c); else set.add(c); return set.has(c); }
      if (on) set.add(c); else set.delete(c);
      return set.has(c);
    },
  };
  return node;
}

// hostRect/screenRect are what getBoundingClientRect reports; everything the placement maths uses
// (cell size, origin, scroll compensation) comes from those two rectangles and nothing else.
function harness(opts) {
  const o = opts || {};
  const screenRect = o.screenRect || { left: 8, top: 4, width: 800, height: 528 };
  const hostRect = o.hostRect || { left: 0, top: 0, width: 816, height: 536 };
  const screen = { getBoundingClientRect: () => ({ ...screenRect }) };
  const host = fakeNode('div');
  host.scrollLeft = o.scrollLeft || 0;
  host.scrollTop = o.scrollTop || 0;
  host.getBoundingClientRect = () => ({ ...hostRect });
  host.querySelector = sel => (sel === '.xterm-screen' ? screen : null);

  const store = Object.assign({}, o.store);
  const sent = [];
  const forwarded = [];
  const flashes = [];

  const term = {
    cols: o.cols === undefined ? 80 : o.cols,
    rows: o.rows === undefined ? 24 : o.rows,
    buffer: { active: { type: o.bufferType || 'alternate', viewportY: o.viewportY || 0 } },
    focus: () => { term.focused = true; },
  };

  const sandbox = {
    term,
    current: 'session-a',
    findSession: () => (o.noApp ? { sessionId: null, shellOnly: true } : { sessionId: 's1', shellOnly: false }),
    document: { createElement: tag => fakeNode(tag) },
    $: sel => (sel === '#term' ? host : null),
    LS: { get: (k, d) => (k in store ? store[k] : d), set: (k, v) => { store[k] = v; } },
    sendMouseInput: d => { sent.push(d); return true; },
    mouseGuard: { noteForwarded: (text, now) => forwarded.push({ text, now }) },
    flash: m => { flashes.push(m); },
    console,
  };
  vm.createContext(sandbox);
  const code = section(START, END) +
    '\nthis.emapApply=emapApply;this.emapBuild=emapBuild;this.emapPlace=emapPlace;' +
    'this.emapClear=emapClear;this.emapTap=emapTap;this.cycleEmap=cycleEmap;' +
    'this.emapActionable=emapActionable;this.emapState=()=>emap;this.emapLayerNode=()=>emapLayer;' +
    'this.emapNodesMap=()=>emapNodes;this.isShown=()=>emapShown;';
  vm.runInContext(code, sandbox, { filename: 'index.html#element-map' });

  return { sandbox, host, term, sent, forwarded, flashes, store };
}

const frame = els => ({ v: 1, gen: 1, cols: 80, rows: 24, els });

test('a map from a version this viewer does not know is dropped whole', () => {
  const h = harness({ store: { mux_widgets: '1' } });
  h.sandbox.emapApply({ v: 2, gen: 1, cols: 80, rows: 24, els: [{ id: 'a', role: 'box', r: [0, 0, 4, 1], act: { t: 'ptr', x: 0, y: 0, b: 0 } }] });
  assert.equal(h.sandbox.emapState(), null);
  assert.equal((h.sandbox.emapLayerNode() || { children: [] }).children.length, 0);
});

test('dedupe is on gen alone: the 10 s heartbeat repaints nothing', () => {
  const h = harness({ store: { mux_widgets: '1' } });
  const el = { id: 'a', role: 'box', r: [0, 0, 4, 1], act: { t: 'ptr', x: 0, y: 0, b: 0 } };
  h.sandbox.emapApply(frame([el]));
  const layer = h.sandbox.emapLayerNode();
  const node = layer.children[0];
  // A heartbeat is byte-identical, so it carries the same gen with the same elements.
  h.sandbox.emapApply(frame([el]));
  assert.equal(h.sandbox.emapLayerNode(), layer);
  assert.equal(h.sandbox.emapLayerNode().children[0], node, 'the same node object must be reused');
});

test('a new gen rebuilds and prunes ids the app no longer paints', () => {
  const h = harness({ store: { mux_widgets: '1' } });
  const a = { id: 'a', role: 'box', r: [0, 0, 4, 1], act: { t: 'ptr', x: 0, y: 0, b: 0 } };
  const b = { id: 'b', role: 'link', r: [0, 2, 6, 1], act: { t: 'ptr', x: 0, y: 2, b: 0 } };
  h.sandbox.emapApply(frame([a, b]));
  assert.equal(h.sandbox.emapLayerNode().children.length, 2);
  h.sandbox.emapApply({ v: 1, gen: 2, cols: 80, rows: 24, els: [b] });
  const children = h.sandbox.emapLayerNode().children;
  assert.equal(children.length, 1);
  assert.equal(children[0].dataset.id, 'b');
  assert.equal(h.sandbox.emapNodesMap().size, 1);
});

test('display-only elements get no box: nothing may swallow a press that does nothing', () => {
  const h = harness({ store: { mux_widgets: '1' } });
  h.sandbox.emapApply(frame([
    { id: 't', role: 'text', r: [0, 0, 12, 1], label: 'Just words' },
    { id: 'n', role: 'box', r: [0, 1, 12, 1], act: { t: 'none' } },
    { id: 'k', role: 'raw', r: [0, 2, 12, 1], act: { t: 'key', d: 'YQ' } },
  ]));
  assert.equal(h.sandbox.emapLayerNode().children.length, 0);
  assert.equal(h.sandbox.emapActionable({ act: { t: 'none' } }), false);
  assert.equal(h.sandbox.emapActionable({}), false);
  assert.equal(h.sandbox.emapActionable({ act: { t: 'focus' } }), true);
  assert.equal(h.sandbox.emapActionable({ act: { t: 'ptr', x: 1, y: 1 } }), true);
});

test('a box lands on the cell rect the app published', () => {
  // 800x528 over 80x24 -> a 10x22 cell. Screen sits at (8,4) inside #term, which has no scroll.
  const h = harness({ store: { mux_widgets: '1' } });
  h.sandbox.emapApply(frame([{ id: 'a', role: 'box', r: [2, 3, 10, 1], act: { t: 'ptr', x: 6, y: 3, b: 0 } }]));
  const n = h.sandbox.emapLayerNode().children[0];
  assert.equal(n.style.transform, 'translate(28px, 70px)');   // 8+2*10 , 4+3*22
  assert.equal(n.style.width, '100px');
  assert.equal(n.style.height, '22px');
  assert.equal(n.style.display, '');
});

test('#term scroll is compensated once, not twice', () => {
  const h = harness({ store: { mux_widgets: '1' }, scrollLeft: 30, scrollTop: 7 });
  h.sandbox.emapApply(frame([{ id: 'a', role: 'box', r: [2, 3, 10, 1], act: { t: 'ptr', x: 6, y: 3, b: 0 } }]));
  const n = h.sandbox.emapLayerNode().children[0];
  assert.equal(n.style.transform, 'translate(58px, 77px)');   // 8+30+20 , 4+7+66
});

test('no box is placed unless the frame geometry matches the terminal in front of it', () => {
  const h = harness({ store: { mux_widgets: '1' }, cols: 100, rows: 30 });
  h.sandbox.emapApply(frame([{ id: 'a', role: 'box', r: [2, 3, 10, 1], act: { t: 'ptr', x: 6, y: 3, b: 0 } }]));
  assert.equal(h.sandbox.emapLayerNode().style.display, 'none');
});

test('no box is placed over the normal buffer: the map rows are app rows, not screen rows', () => {
  const h = harness({ store: { mux_widgets: '1' }, bufferType: 'normal' });
  h.sandbox.emapApply(frame([{ id: 'a', role: 'box', r: [2, 3, 10, 1], act: { t: 'ptr', x: 6, y: 3, b: 0 } }]));
  assert.equal(h.sandbox.emapLayerNode().style.display, 'none');
});

test('the widgets chip is off by default and hides the whole layer', () => {
  const h = harness();
  h.sandbox.emapApply(frame([{ id: 'a', role: 'box', r: [2, 3, 10, 1], act: { t: 'ptr', x: 6, y: 3, b: 0 } }]));
  assert.equal(h.sandbox.isShown(), false);
  assert.equal(h.sandbox.emapLayerNode().style.display, 'none');
  h.sandbox.cycleEmap();
  assert.equal(h.store.mux_widgets, '1');
  assert.equal(h.sandbox.emapLayerNode().style.display, '');
});

test('a tap sends the same press/release pair the host click path sends', () => {
  const h = harness({ store: { mux_widgets: '1' } });
  // r = [2,3,10,2] -> centre cell (2+5, 3+1) = (7,4) 0-based -> SGR reports 8,5 1-based.
  h.sandbox.emapApply(frame([{ id: 'a', role: 'box', r: [2, 3, 10, 2], act: { t: 'ptr', x: 7, y: 4, b: 0 } }]));
  h.sandbox.emapTap('a');
  assert.deepEqual(h.sent, ['\x1b[<0;8;5M', '\x1b[<0;8;5m']);
  assert.equal(h.forwarded.length, 1, 'the pair must be marked as already forwarded, or it echoes');
  assert.equal(h.forwarded[0].text, '\x1b[<0;8;5M\x1b[<0;8;5m');
  assert.equal(typeof h.forwarded[0].now, 'number');
});

test('the press cell is the one the app published, not a centre this viewer invents', () => {
  // Measured on a live 120x30 map: a footer control at r=[2,29,120,1] publishes ptr(62,29), while the
  // even-width centre is 61. A wide control does not mean the same thing two cells apart.
  const h = harness({ cols: 120, rows: 30, store: { mux_widgets: '1' }, screenRect: { left: 0, top: 0, width: 1200, height: 600 } });
  h.sandbox.emapApply({ v: 1, gen: 1, cols: 120, rows: 30, els: [{ id: 'w', role: 'box', r: [2, 29, 120, 1], act: { t: 'ptr', x: 62, y: 29, b: 0 } }] });
  h.sandbox.emapTap('w');
  assert.deepEqual(h.sent, ['\x1b[<0;63;30M', '\x1b[<0;63;30m']);
});

test('with no published cell the press falls back to the rect centre', () => {
  const h = harness({ store: { mux_widgets: '1' } });
  h.sandbox.emapApply(frame([{ id: 'a', role: 'box', r: [2, 3, 10, 1], act: { t: 'ptr', b: 0 } }]));
  h.sandbox.emapTap('a');
  assert.deepEqual(h.sent, ['\x1b[<0;8;4M', '\x1b[<0;8;4m']);   // 2+5, 3+0 -> 8,4 1-based
});

test('a tap on a focus target focuses the widget instead of forging a mouse event', () => {
  const h = harness({ store: { mux_widgets: '1' } });
  h.sandbox.emapApply(frame([{ id: 'f', role: 'text', r: [1, 1, 20, 1], act: { t: 'focus' } }]));
  h.sandbox.emapTap('f');
  assert.equal(h.term.focused, true);
  assert.deepEqual(h.sent, []);
});

test('a tap over a bare shell says so and sends nothing', () => {
  const h = harness({ store: { mux_widgets: '1' }, noApp: true });
  h.sandbox.emapApply(frame([{ id: 'a', role: 'box', r: [2, 3, 10, 1], act: { t: 'ptr', x: 6, y: 3, b: 0 } }]));
  h.sandbox.emapTap('a');
  assert.deepEqual(h.sent, []);
  assert.equal(h.flashes.length, 1);
});

test('clear drops the map and every box, so a reattach cannot show the previous app controls', () => {
  const h = harness({ store: { mux_widgets: '1' } });
  h.sandbox.emapApply(frame([{ id: 'a', role: 'box', r: [2, 3, 10, 1], act: { t: 'ptr', x: 6, y: 3, b: 0 } }]));
  assert.equal(h.sandbox.emapNodesMap().size, 1);
  h.sandbox.emapClear();
  assert.equal(h.sandbox.emapState(), null);
  assert.equal(h.sandbox.emapNodesMap().size, 0);
  assert.equal(h.sandbox.emapLayerNode().children.length, 0);
});

test('the viewer never writes the OSC itself: the map arrives decoded and taps go out as input', () => {
  const block = section(START, END);
  assert.doesNotMatch(block, /x1b\]21338|u001b\]21338|\]21338;/, 'only the relay touches the wire format');
  assert.match(block, /sendMouseInput\(click\)/);
  assert.match(block, /sendMouseInput\(release\)/);
});
