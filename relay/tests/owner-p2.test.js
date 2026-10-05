// P2 §7: a stream owner (a teed PC tab) drives two relay behaviours the rest of the fleet does not.
//
//   §7.1 targetSize: a lingering browser PIN must not resize a PC tab out from under its user. The tab's
//        own geometry is returned unchanged, exactly like the localOwned branch, only checked first.
//   §7.2 watch: a stream owner only forwards bytes while a viewer is watching, so the relay fires
//        `watch` on the first attach and `unwatch` on the last detach -- but only once muxd advertises
//        the `watch` cap, because an old muxd would ignore both and forward unconditionally.
//
// Runs under MUX_TEST_MODE (the loopback viewer bypass), like viewer-pin.test.js.

const test = require('node:test');
const assert = require('node:assert/strict');
const WebSocket = require('ws');

const { RelayHarness, HOST_CAPS, waitFor, sleep } = require('./harness');

const ORIGIN = 'https://relay-auth-gate.test';
const WATCH_CAPS = [...HOST_CAPS, 'watch'];

// A stream owner as muxd reports it: kind 'local-tab', owner true, no command. `cols`/`rows` are the
// hosted geometry the relay must hold.
function streamOwner(name, cols = 120, rows = 40) {
  return {
    name, alive: true, created: 1000, lastOut: 1000, cols, rows,
    hasCommand: false, shellOnly: false, ready: true, kind: 'local-tab', owner: true,
    localViewers: 1, localFirst: true, sessionId: '', aliases: [],
  };
}

function openViewer(port, session, dev, cols, rows) {
  const ws = new WebSocket(
    `ws://127.0.0.1:${port}/ws?session=${encodeURIComponent(session)}&cols=${cols}&rows=${rows}&dev=${dev}&label=${dev}`,
    { headers: { origin: ORIGIN } },
  );
  const dims = [];
  ws.on('message', raw => {
    const s = raw.toString();
    if (s[0] === 'd') { try { dims.push(JSON.parse(s.slice(1))); } catch {} }
  });
  return new Promise((resolve, reject) => {
    ws.on('open', () => resolve({ ws, dims }));
    ws.on('error', reject);
  });
}

async function lastDims(viewer, pred, label) {
  return await waitFor(() => {
    const d = viewer.dims[viewer.dims.length - 1];
    return d && pred(d) ? d : null;
  }, label);
}

test('§7.2: watch fires on the first attach and unwatch on the last detach', async t => {
  const h = new RelayHarness({ ALLOWED_WS_ORIGINS: ORIGIN });
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([streamOwner('localtab')], { caps: WATCH_CAPS });
  t.after(() => host.close());

  const A = await openViewer(h.port, 'localtab', 'devA', 120, 40);
  t.after(() => A.ws.terminate());
  await host.waitFor(m => m.t === 'watch' && m.s === 'localtab', 'watch on first attach');

  // A second viewer must NOT double-fire a watch.
  const B = await openViewer(h.port, 'localtab', 'devB', 120, 40);
  t.after(() => B.ws.terminate());
  await sleep(120);
  assert.equal(host.messages.filter(m => m.t === 'watch').length, 1, 'only the first attach fires watch');

  // Dropping one of two viewers must NOT unwatch.
  A.ws.terminate();
  await sleep(150);
  assert.equal(host.messages.some(m => m.t === 'unwatch'), false, 'a viewer remains, so no unwatch');

  // Dropping the last viewer fires exactly one unwatch.
  B.ws.terminate();
  await host.waitFor(m => m.t === 'unwatch' && m.s === 'localtab', 'unwatch on last detach');
});

test('§7.2: a host without the watch cap is never sent watch frames', async t => {
  const h = new RelayHarness({ ALLOWED_WS_ORIGINS: ORIGIN });
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([streamOwner('legacytab')]);   // no 'watch' in caps
  t.after(() => host.close());

  const A = await openViewer(h.port, 'legacytab', 'devA', 120, 40);
  t.after(() => A.ws.terminate());
  await sleep(200);
  assert.equal(host.messages.some(m => m.t === 'watch'), false, 'old muxd is not sent watch');
  assert.equal(host.messages.some(m => m.t === 'unwatch'), false, 'old muxd is not sent unwatch');
});

test('§7.1: a browser pin cannot resize a stream owner', async t => {
  const h = new RelayHarness({ ALLOWED_WS_ORIGINS: ORIGIN });
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([streamOwner('sizedtab', 120, 40)], { caps: WATCH_CAPS });
  t.after(() => host.close());

  const A = await openViewer(h.port, 'sizedtab', 'devA', 80, 24);
  t.after(() => A.ws.terminate());

  // The pinned device's 80x24 must NOT become the tab's geometry: the tab is the physical screen.
  A.ws.send('P');
  const d = await lastDims(A, x => x.cols === 120 && x.rows === 40, 'tab keeps its own size');
  assert.equal(d.cols, 120, 'pin must not shrink a teed PC tab');
  assert.equal(d.rows, 40, 'pin must not shrink a teed PC tab');
});

// §7.3: the UI reads two facts off the wire - the 'd' frame's tabOwned (the chip says "follows PC tab"
// and pinning is disabled) and the session payload's localTab (the badge and the PC-tabs group). Both
// are relay truth, not client guesswork, so they are asserted at the protocol boundary.
test('§7.3: the size frame marks a stream owner tabOwned', async t => {
  const h = new RelayHarness({ ALLOWED_WS_ORIGINS: ORIGIN });
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([streamOwner('tabowned', 120, 40)], { caps: WATCH_CAPS });
  t.after(() => host.close());

  const A = await openViewer(h.port, 'tabowned', 'devA', 120, 40);
  t.after(() => A.ws.terminate());
  const d = await lastDims(A, x => x.tabOwned === true, 'tabOwned on the size frame');
  assert.equal(d.tabOwned, true, 'a teed PC tab must be flagged so the UI can lock the pin');
  assert.equal(d.mode, 'auto', 'a tab-owned size is not a pin');
});

test('§7.3: a hosted non-owner session is NOT marked tabOwned', async t => {
  const h = new RelayHarness({ ALLOWED_WS_ORIGINS: ORIGIN });
  await h.start();
  t.after(() => h.stop());
  // A plain hosted owner:false tab is a mux session with local viewers, not a teed PC tab.
  const plain = { ...streamOwner('plainhost', 120, 40), kind: 'command', owner: false, hasCommand: true, shellOnly: false, localViewers: 1 };
  const host = await h.connectHost([plain], { caps: WATCH_CAPS });
  t.after(() => host.close());

  const A = await openViewer(h.port, 'plainhost', 'devA', 120, 40);
  t.after(() => A.ws.terminate());
  const d = await lastDims(A, x => typeof x.tabOwned === 'boolean', 'size frame arrives');
  assert.equal(d.tabOwned, false, 'only a stream owner locks the pin');
});

test('§7.3: the session list flags a teed PC tab with localTab', async t => {
  const h = new RelayHarness({ ALLOWED_WS_ORIGINS: ORIGIN });
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([streamOwner('listedtab')], { caps: WATCH_CAPS });
  t.after(() => host.close());

  const list = await h.json('GET', '/api/sessions');
  const row = list.find(s => s.name === 'listedtab');
  assert.ok(row, 'the teed tab appears in the session list');
  assert.equal(row.localTab, true, 'a local-tab kind must carry localTab for the badge + group');
  assert.equal(row.kind, 'local-tab', 'kind stays authoritative');
});
