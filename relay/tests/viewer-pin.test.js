// PER-CLIENT PINNING (A5) — the server holds ONE authoritative "THIS DEVICE" geometry per session.
//
// Ahmed's ruling: the pin is keyed on the client/device, NOT on cols/rows. Whichever client pins LAST
// is held; the pinned client's cols/rows become the session's authoritative geometry for EVERY viewer;
// and a client can only pin ITSELF. A non-pinned client that merely resizes its viewport must NOT move
// the shared geometry. This suite is the verifier the plan calls for, on the real viewer socket:
//
//   1. device A pins itself -> geometry becomes A's size, and A is told `mine`.
//   2. a NON-pinned device B reports a different viewport -> geometry does NOT move.
//   3. device B then pins itself -> last pin wins -> geometry becomes B's size.
//   4. a device cannot pin another device by name unless it is told to (`P#<dev>` is the long-press
//      path); the plain `P` pins SELF - asserted by step 1/3 targeting the sender.
//
// Runs under MUX_TEST_MODE (the loopback viewer bypass) — this suite is about pin SEMANTICS, not the
// auth gate (native-viewer-auth.test.js covers that), so the bypass is the right posture here.

const test = require('node:test');
const assert = require('node:assert/strict');
const WebSocket = require('ws');

const { RelayHarness, waitFor, sleep } = require('./harness');

const ORIGIN = 'https://relay-auth-gate.test';

function shellSession(name) {
  return {
    name, alive: true, created: 1000, lastOut: 1000, cols: 100, rows: 30,
    hasCommand: false, shellOnly: true, ready: true, kind: 'shell', sessionId: '', aliases: [],
  };
}

// Attach a viewer and collect every 'd' (dimensions/pin) frame it receives. `dev` is the persistent
// device identity the pin is keyed on; cols/rows seed its viewport.
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

function sendViewport(viewer, cols, rows) {
  viewer.ws.send('v' + JSON.stringify({ cols, rows, vis: true, act: true }));
}

test('the server holds one authoritative pin per session: last pin wins, a non-pinned client cannot move geometry', async t => {
  const h = new RelayHarness({ ALLOWED_WS_ORIGINS: ORIGIN });
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('pincase')]);
  t.after(() => host.close());

  const A = await openViewer(h.port, 'pincase', 'devA', 100, 30);
  t.after(() => A.ws.terminate());
  sendViewport(A, 100, 30);
  const B = await openViewer(h.port, 'pincase', 'devB', 60, 20);
  t.after(() => B.ws.terminate());
  sendViewport(B, 60, 20);

  // 1. A pins ITSELF with the plain `P`. The event is asynchronous (persist -> recompute), so wait for
  // the resulting frame rather than reading state.
  A.ws.send('P');
  const aPinned = await lastDims(A, d => d.mode === 'pinned' && d.mine === true, 'A pinned itself');
  assert.equal(aPinned.cols, 100, 'the pinned device\'s cols become the shared geometry');
  assert.equal(aPinned.rows, 30, 'the pinned device\'s rows become the shared geometry');
  // B must be held at A's geometry and told it is NOT the pin holder.
  const bHeld = await lastDims(B, d => d.mode === 'pinned' && d.mine === false && d.cols === 100, 'B follows A\'s pin');
  assert.equal(bHeld.rows, 30);

  // 2. A NON-pinned client resizing its own viewport must not move the shared geometry.
  sendViewport(B, 200, 50);
  await sleep(300);   // give recompute a chance to (wrongly) act
  const stillA = B.dims[B.dims.length - 1];
  assert.equal(stillA.cols, 100, 'a non-pinned client cannot change the pinned geometry (cols)');
  assert.equal(stillA.rows, 30, 'a non-pinned client cannot change the pinned geometry (rows)');

  // 3. B pins itself -> LAST PIN WINS -> geometry becomes B's current viewport.
  B.ws.send('P');
  const bPinned = await lastDims(B, d => d.mode === 'pinned' && d.mine === true, 'B pinned itself');
  assert.equal(bPinned.cols, 200, 'the last device to pin drives the geometry (cols)');
  assert.equal(bPinned.rows, 50, 'the last device to pin drives the geometry (rows)');
  const aFollows = await lastDims(A, d => d.mode === 'pinned' && d.mine === false && d.cols === 200, 'A follows B\'s pin');
  assert.equal(aFollows.rows, 50);

  // 4. A client cannot be pinned by another client's plain `P`: A's `P` must move the pin back to A
  //    (self), never leave it on B.
  A.ws.send('P');
  const backToA = await lastDims(A, d => d.mode === 'pinned' && d.mine === true && d.cols === 100, 'A re-pins self');
  assert.equal(backToA.rows, 30);
});

test('unpinning clears the held device and the session falls back without error', async t => {
  const h = new RelayHarness({ ALLOWED_WS_ORIGINS: ORIGIN });
  await h.start();
  t.after(() => h.stop());
  const host = await h.connectHost([shellSession('unpincase')]);
  t.after(() => host.close());

  const A = await openViewer(h.port, 'unpincase', 'devA', 100, 30);
  t.after(() => A.ws.terminate());
  sendViewport(A, 100, 30);
  const B = await openViewer(h.port, 'unpincase', 'devB', 80, 24);
  t.after(() => B.ws.terminate());
  sendViewport(B, 80, 24);

  A.ws.send('P');
  await lastDims(A, d => d.mode === 'pinned' && d.mine === true, 'A pinned');

  // `P0` unpins. The relay must return to a non-pinned mode (auto/hosted) and keep serving frames.
  A.ws.send('P0');
  const back = await lastDims(A, d => d.mode !== 'pinned', 'A unpinned');
  assert.ok(back.cols > 1 && back.rows > 1, 'geometry stays valid after unpin');
  await sleep(100);
  assert.equal(A.ws.readyState, WebSocket.OPEN, 'the viewer socket stays open across pin/unpin');
});
