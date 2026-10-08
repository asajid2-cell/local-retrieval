// Regression: a late attach to a SPAWNED (muxd-hosted) session must still get the app's mouse arm, on
// BOTH reattach paths, and the wheel must reach the app as a decoded SGR cell.
//
// The relay has two reattach paths for a hosted session, chosen by whether its screen model is warm:
//   COLD - the model is not parsed yet, so the viewer gets CLEAR_SCREEN + muxd's BYTE REPLAY (buf), which
//          still carries the app's DECSET verbatim;
//   WARM - the model is parsed, so the viewer gets CLEAR_SCREEN + the serialized SNAPSHOT, which emits the
//          mouseTrackingMode (1000/1002/1003) but DROPS the SGR-encoding flag (1006).
// Both must leave the client's mouse guard ARMED, because inputAllowed() reads tracking (1000), not the
// encoding flag. The failure this pins: a late attach that arrives unarmed silently scrolls the mirror
// locally and the app's own wheel handler never sees the notch. The spawned path is the one that carries
// the arm, so it is the one worth pinning.
//
// The negatives are the other half of the same claim - an arm must be EARNED:
//   * a shell that never armed the mouse keeps its native scrollback and forwards nothing;
//   * an app that armed and then DISARMED (?1000l) is not tracking and forwards nothing;
//   * an ADOPTED console stays local, and this is a documented LIMIT, not a bug to fix here: muxrun reads
//     the console CELL GRID and re-synthesizes its frames, so the app's DECSET never reaches muxd or the
//     relay and the arm is structurally unknowable. Its clicks and keys still land (they need no arm); the
//     wheel does not.
//
//     We also checked whether the console INPUT mode could recover the arm - GetConsoleMode(hin) bit 0x10
//     (ENABLE_MOUSE_INPUT) or 0x200 (ENABLE_VIRTUAL_TERMINAL_INPUT) - and REJECTED it (measured 2026-10-07).
//     Neither bit moves with the arm state; the bits track the RUNTIME, not the arm (a plain shell or an
//     ncurses/python TUI stays 0x01F7; a VT-input runtime such as bun/cc or nvim is 0x0208), and cc never
//     calls SetConsoleMode, so its console mode is arm-invariant by construction. Do not retry it.
//     Evidence: muxdiag/gcm.ps1, gcm2.ps1, gcm-attach.ps1, gcmtui.py, gcm-tui-*.json, nvim-probe*.txt.
const test = require('node:test');
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const path = require('node:path');

const { RelayHarness, REPO, freePort, launchBrowser, waitFor, sleep } = require('./harness');

const COLS = 80;
const ROWS = 24;
const TEST_TIMEOUT_MS = 8000;
const INSTALL = 'npm install --save-dev playwright --package-lock=false\nnpx playwright install chromium';

// The app's own head: alt screen, mouse tracking, SGR encoding. Then a long log so the byte replay (buf) is
// far larger than the serialized snapshot, and a visible footer the test can wait on.
const APP_HEAD = '\x1b[?1049h\x1b[?1000h\x1b[?1006h';
const FILLER = Array.from({ length: 3000 }, (_, i) => `APP line ${String(i).padStart(4, '0')}`).join('\r\n');
const APP_LOG = `${APP_HEAD}${FILLER}\r\nVISIBLE FOOTER\r\n\x1b[8;1HWHEEL=0 CLICK=0 KEY=0`;

let browser = null;
let browserError = null;

test.before(async () => {
  try { browser = await launchBrowser(); }
  catch (error) { browserError = error; console.warn(`Chromium unavailable; skipping the live half.\n${INSTALL}\n${error.message}`); }
});

test.after(async () => { if (browser) await browser.close(); });

async function startBrowserRelay(harness) {
  harness.port = await freePort();
  harness.env.ALLOWED_WS_ORIGINS = `http://127.0.0.1:${harness.port}`;
  harness.proc = childProcess.spawn(process.execPath, ['server.js'], {
    cwd: REPO,
    env: {
      ...process.env,
      PORT: String(harness.port),
      MUX_HOST_TOKEN: 'test-token',
      MUX_TEST_MODE: '1',
      MUX_TEST_FIXTURE: '1',
      MUX_BIND_HOST: '127.0.0.1',
      MUX_AUTOHEAL: '0',
      MUX_STATE_DIR: harness.tmp,
      MUX_TEST_PERSIST_FAULT_FILE: path.join(harness.tmp, '.persist-fault.json'),
      MUX_HOST_SB_WAIT_MS: '40',
      MUX_COMMAND_LEASE_MS: '1000',
      HLAUTH_BASE: 'http://127.0.0.1:1',
      ...harness.env,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  harness.stdout = '';
  harness.stderr = '';
  harness.proc.stdout.on('data', d => { harness.stdout += d.toString(); });
  harness.proc.stderr.on('data', d => { harness.stderr += d.toString(); });
  await waitFor(() => harness.stdout.includes(`multiplex-app on 127.0.0.1:${harness.port}`), 'browser relay start', 5000);
}

// The SPAWNED shape: muxd launches the child and owns the PTY, real bytes flow through it, and no local
// console owns the size (owner:false). That last part matters: the relay only runs its screen model for a
// session whose geometry IT decides - a tab-owned (owner:true) console is served the byte replay always.
function hostedSession(name, owner = false) {
  return {
    name, alive: true, created: Date.now(), lastOut: Date.now(), cols: COLS, rows: ROWS,
    hasCommand: true, shellOnly: false, ready: true, owner, kind: 'command',
    sessionId: name + '-session', aliases: [],
  };
}

// Open a viewer and start counting the bytes the relay puts on its socket. The counter is installed BEFORE
// navigation so it sees the attach frames - that is what tells the compact snapshot apart from the replay.
async function openViewer(context, port, session) {
  const page = await context.newPage();
  const wire = { bytes: 0, text: '' };
  page.on('websocket', ws => {
    ws.on('framereceived', frame => {
      const p = frame.payload;
      if (typeof p === 'string') { wire.bytes += Buffer.byteLength(p, 'utf8'); wire.text += p; return; }
      wire.bytes += p.length;
      wire.text += Buffer.from(p).toString('latin1');
    });
  });
  await page.goto(`http://127.0.0.1:${port}/?s=${session}`, { waitUntil: 'domcontentloaded' });
  await page.locator('#term').waitFor({ state: 'visible' });
  return { page, wire };
}

const armed = page => page.evaluate(() => !!(window.__muxMouseGuard && window.__muxMouseGuard.inputAllowed()));

// Wheel once over the middle of the terminal and return the decoded SGR reports the host received.
async function wheelAndDecode(host, page) {
  const box = await page.locator('#term').boundingBox();
  const cx = box.x + box.width / 2, cy = box.y + box.height / 2;
  const expected = await page.evaluate(([x, y]) => {
    const scr = document.querySelector('#term .xterm-screen');
    const r = scr.getBoundingClientRect(), cols = term.cols | 0, rows = term.rows | 0;
    const col = Math.floor((x - r.left) / (r.width / cols));
    const row = Math.floor((y - r.top) / (r.height / rows));
    return [col, row + (term.buffer.active.viewportY | 0)];
  }, [cx, cy]);

  host.messages.length = 0;
  await page.mouse.move(cx, cy);
  await page.mouse.wheel(0, -220);
  await sleep(250);

  const reports = [];
  for (const m of host.messages) {
    if (m.t !== 'i') continue;
    const text = Buffer.from(m.d || '', 'base64').toString('latin1');
    for (const mm of text.matchAll(/\x1b\[<(\d+);(\d+);(\d+)([Mm])/g)) {
      reports.push({ button: +mm[1], col: +mm[2], row: +mm[3], kind: mm[4] });
    }
  }
  return { reports, expected };
}

// ---- COLD late attach: the FIRST viewer gets the byte replay, and it still arms the wheel -----------

test('a cold attach to a spawned session arms the wheel from the byte replay', async t => {
  if (!browser) return t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`);

  const harness = new RelayHarness();
  harness.start = () => startBrowserRelay(harness);
  let host = null, context = null;
  try {
    await harness.start();
    host = await harness.connectHost([hostedSession('late-cold')]);
    context = await browser.newContext({ viewport: { width: 1000, height: 700 } });
    context.setDefaultTimeout(TEST_TIMEOUT_MS);

    const { page, wire } = await openViewer(context, harness.port, 'late-cold');
    const sb = await host.waitFor(m => m.t === 'sb' && m.s === 'late-cold', 'scrollback request');
    host.sendScrollback('late-cold', APP_LOG, sb);
    await waitFor(() => armed(page), 'the cold attach armed the guard from the byte replay', TEST_TIMEOUT_MS);

    const { reports, expected } = await wheelAndDecode(host, page);
    const up = reports.find(r => r.button === 64);
    assert.ok(up, `the wheel-up report must reach the app (got ${JSON.stringify(reports)})`);
    assert.deepEqual([up.col, up.row], [expected[0] + 1, expected[1] + 1],
      `the report must name the cell under the pointer (got ${JSON.stringify([up.col, up.row])}, want ${JSON.stringify([expected[0] + 1, expected[1] + 1])})`);
    assert.ok(wire.bytes > 0, 'the viewer received the attach frames');
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
});

// ---- WARM late attach: a SECOND viewer gets the compact snapshot, and it still arms the wheel ------

test('a warm late attach arms the wheel from the serialized snapshot, not the byte log', async t => {
  if (!browser) return t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`);

  const harness = new RelayHarness();
  harness.start = () => startBrowserRelay(harness);
  let host = null, context = null;
  try {
    await harness.start();
    host = await harness.connectHost([hostedSession('late-warm')]);
    context = await browser.newContext({ viewport: { width: 1000, height: 700 } });
    context.setDefaultTimeout(TEST_TIMEOUT_MS);

    // First viewer: cold, pays the byte replay, and warms the relay's screen model as a side effect.
    const firstViewer = await openViewer(context, harness.port, 'late-warm');
    const first = firstViewer.page, firstWire = firstViewer.wire;
    const sbOne = await host.waitFor(m => m.t === 'sb' && m.s === 'late-warm', 'first scrollback request');
    host.sendScrollback('late-warm', APP_LOG, sbOne);
    await waitFor(() => armed(first), 'the first viewer armed', TEST_TIMEOUT_MS);
    await sleep(200);   // let xterm-headless finish parsing before the late attach reads the model

    // Late attach while the first is still open, so the warm model survives.
    const secondViewer = await openViewer(context, harness.port, 'late-warm');
    const second = secondViewer.page, secondWire = secondViewer.wire;
    await waitFor(() => host.messages.filter(m => m.t === 'sb' && m.s === 'late-warm').length === 2,
      'second scrollback request', TEST_TIMEOUT_MS);
    const sbTwo = [...host.messages].reverse().find(m => m.t === 'sb' && m.s === 'late-warm');
    host.sendScrollback('late-warm', APP_LOG, sbTwo);
    await waitFor(() => armed(second), 'the warm late attach armed the guard from the snapshot', TEST_TIMEOUT_MS);

    const { reports, expected } = await wheelAndDecode(host, second);
    const up = reports.find(r => r.button === 64);
    assert.ok(up, `the wheel-up report must reach the app from the late attach (got ${JSON.stringify(reports)})`);
    assert.deepEqual([up.col, up.row], [expected[0] + 1, expected[1] + 1],
      `the late attach must report the cell under the pointer (got ${JSON.stringify([up.col, up.row])})`);

    // The late attach took the compact snapshot, not the whole log again: the second viewer's wire is a
    // small screen, the first viewer paid the 3000-line replay.
    assert.ok(secondWire.bytes > 0, 'the late attach received bytes');
    assert.ok(secondWire.bytes < firstWire.bytes / 2,
      `the warm late attach must serve the snapshot, not the log: second=${secondWire.bytes}, first=${firstWire.bytes}`);

    // Say WHY the snapshot still arms the wheel, on the wire itself: it carries the alternate screen and the
    // mouse TRACKING mode the app armed, and drops the SGR-encoding flag (1006). The first viewer's wire is
    // the control: the byte log DOES carry 1006, so the second's absence is a real difference between the two
    // paths and not a claim about a string nothing on the wire ever emits.
    assert.match(firstWire.text, /\x1b\[\?1006h/, 'the byte log carries the SGR encoding flag (the control)');
    assert.match(secondWire.text, /\x1b\[\?1049h/, 'the snapshot must carry the app\'s alternate screen');
    assert.match(secondWire.text, /\x1b\[\?1000h/, 'the snapshot must carry the mouse tracking mode the app armed');
    assert.doesNotMatch(secondWire.text, /\x1b\[\?1006h/,
      'the snapshot drops the SGR encoding flag - its absence proves this is the serialized screen, not the byte log');
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
});

// ---- negatives: an arm that was never earned forwards nothing -------------------------------------

async function assertStaysLocal(t, { name, log, altScreen, owner = false }) {
  if (!browser) return t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`);

  const harness = new RelayHarness();
  harness.start = () => startBrowserRelay(harness);
  let host = null, context = null;
  try {
    await harness.start();
    host = await harness.connectHost([hostedSession(name, owner)]);
    context = await browser.newContext({ viewport: { width: 1000, height: 700 } });
    context.setDefaultTimeout(TEST_TIMEOUT_MS);

    const { page } = await openViewer(context, harness.port, name);
    const sb = await host.waitFor(m => m.t === 'sb' && m.s === name, 'scrollback request');
    host.sendScrollback(name, log, sb);
    await waitFor(async () => {
      const text = await page.locator('#status').innerText();
      return text.includes(name) && text.includes('live') ? true : null;
    }, 'live terminal status', TEST_TIMEOUT_MS);

    assert.equal(await armed(page), false,
      `${name}: the guard must stay unarmed (no DECSET 1000/1002/1003 ever arrived)`);

    // The mirror settles at the bottom a beat after the attach replay; sample the scroll position only once
    // it has, or the "before" reading is a mid-attach 0 and the wheel looks like it moved the wrong way.
    if (!altScreen) {
      await waitFor(() => page.evaluate(() => { const b = term.buffer.active; return b.baseY > 10 && b.viewportY === b.baseY; }),
        'the mirror settled at the bottom', TEST_TIMEOUT_MS);
    }
    const before = altScreen ? null : await page.evaluate(() => term.buffer.active.viewportY);
    const box = await page.locator('#term').boundingBox();
    const cx = box.x + box.width / 2, cy = box.y + box.height / 2;
    host.messages.length = 0;
    await page.mouse.move(cx, cy);
    for (let i = 0; i < 3; i++) { await page.mouse.wheel(0, -220); await sleep(50); }
    await sleep(250);

    // The claim is narrow and exact: an unarmed console must never receive a MOUSE REPORT. On the NORMAL
    // buffer nothing at all is forwarded (the wheel scrolls the mirror); on the alternate screen xterm's own
    // alternateScroll converts the wheel to arrow keys, which is what a real terminal does for a
    // non-mouse alt-screen app - arrows, not SGR reports.
    const sent = host.messages.filter(m => m.t === 'i')
      .map(m => Buffer.from(m.d || '', 'base64').toString('latin1'));
    const reports = sent.filter(s => /\x1b\[</.test(s));
    assert.equal(reports.length, 0,
      `${name}: an unarmed console must receive no mouse report (got ${JSON.stringify(reports)})`);
    if (!altScreen) {
      assert.equal(sent.length, 0, `${name}: a normal-buffer shell must forward nothing (got ${JSON.stringify(sent)})`);
      const after = await page.evaluate(() => term.buffer.active.viewportY);
      assert.ok(after < before, `${name}: the wheel must scroll the local scrollback (${before} -> ${after})`);
    }
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
}

test('a shell that never armed keeps its scrollback and forwards nothing', async t => {
  // A plain owner shell: lines on the NORMAL buffer, no alt screen, no mouse tracking.
  const log = Array.from({ length: 80 }, (_, i) => `SHELL line ${String(i + 1).padStart(3, '0')}`).join('\r\n') + '\r\n';
  await assertStaysLocal(t, { name: 'late-shell', log, altScreen: false });
});

test('an app that armed and then disarmed stays local', async t => {
  // DECSET 1000 then DECRST 1000 - the app took the mouse and gave it back (a TUI that exited, or a pane
  // that turned reporting off). The guard must read the CURRENT state, so nothing is forwarded.
  const filler = Array.from({ length: 80 }, (_, i) => `DISARMED context ${String(i + 1).padStart(3, '0')}`).join('\r\n');
  const log = `\x1b[?1000h\x1b[?1006hARMED\r\n${filler}\r\n\x1b[?1000l\x1b[?1006lDISARMED\r\n`;
  await assertStaysLocal(t, { name: 'late-disarmed', log, altScreen: false });
});

test('an adopted console stays local - the documented limit', async t => {
  // The adopted path is modelled honestly: muxrun reads the console CELL GRID and re-synthesizes the frame,
  // so the app's DECSET never crosses the wire. The frame below is exactly that shape - an alt-screen
  // repaint with content but NO mouse mode - and it must not arm the wheel.
  const log = '\x1b[?1049h' + Array.from({ length: ROWS }, (_, i) => `ADOPTED row ${String(i + 1).padStart(2, '0')}`).join('\r\n');
  await assertStaysLocal(t, { name: 'late-adopted', log, altScreen: true, owner: true });
});
