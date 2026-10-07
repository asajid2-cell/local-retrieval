// Regression: a plain click on an APP pane must reach the pty whether or not the app ARMED mouse
// reporting, and a plain DRAG on the same pane must still select - both without a modifier. A bare
// shell's stdin must never receive a mouse report.
//
// The bug (Ahmed, 2026-10-03): "in the case when selection wins then we can never click even with shift.
// it's an either/or ... the bad selection where it forces us to be either click or selection, instead of
// letting us click and select both as needed." Two successive gates each picked ONE winner per pane:
// first appTakesMouse() (arm state) then appOwnsScreen() (buffer type). Either way the decision was a
// single boolean, so a pane got click-or-selection, never both, and Shift was the only escape - which
// only proved the defect existed.
//
// The fix is that the gesture stops being an either/or. What wants the click is neither the buffer type
// nor the arm state: it is whether an APP is behind the pane at all (hasCommand / a chat-bound session
// that is not shellOnly). And the click-vs-drag question is decided at RELEASE, not press - a press that
// moved is a selection (run by us, so it works on an armed pane where xterm's own selection is off), a
// press that did not move is a click. This file pins the source gate and the behaviour, on a loopback
// relay with a fake host.
const test = require('node:test');
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const {
  RelayHarness,
  REPO,
  freePort,
  launchBrowser,
  waitFor,
  sleep,
} = require('./harness');

const source = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');

// ---- source gate: the click handler must gate on the SCREEN, not the mouse being armed --------------

// The owner-press handler is the one that builds an SGR report. Pull just that listener out of the
// shipped file (it is anonymous, so it is asserted against its source, not executed).
function ownerPressMousedownSource() {
  const call = source.indexOf('ownerPress={button:');
  assert.notEqual(call, -1, 'no mousedown handler records a click candidate — the click forwarding is gone');
  const start = source.lastIndexOf("addEventListener('mousedown'", call);
  assert.notEqual(start, -1, 'the owner-press candidate is no longer recorded from a mousedown listener');
  const end = source.indexOf('}, true);', call);
  assert.notEqual(end, -1, 'could not find the end of the owner-press mousedown listener');
  // Strip comment lines: the gate is asserted against CODE. A comment explaining the old gate names it,
  // and must not be mistaken for the gate itself.
  return source.slice(start, end).split('\n').filter(l => !/^\s*\/\//.test(l)).join('\n');
}

test('the click gate is "is there an app behind this pane", not the buffer type or the arm state', () => {
  const handler = ownerPressMousedownSource();
  // The gesture is decided at release, so the press handler records a candidate rather than forwarding.
  assert.match(handler, /moved:false/,
    'the press no longer records a click-or-drag candidate — the gesture is being decided at press time again');
  // The either/or gates must be GONE from the click decision: neither the buffer type nor the arm state
  // may select a single winner per pane.
  assert.doesNotMatch(handler, /appOwnsScreen\(\)/,
    'the click decision depends on the buffer type again — a normal-buffer app pane would lose every click');
  assert.match(handler, /hasCommand|shellOnly/,
    'the click gate does not consult whether an APP is behind the pane — a bare shell could take a report on its stdin');
  assert.doesNotMatch(handler, /isVisibleOwner/,
    'the click gate depends on the session being the visible owner again — a web-created pane would lose clicks');
});

// ---- the real behaviour, on a loopback relay with a fake host ---------------------------------------

const TEST_TIMEOUT_MS = 8000;
const SESSION = 'click-gate-shell';
const INSTALL = 'npm install --save-dev playwright --package-lock=false\nnpx playwright install chromium';
const ARMED = '\x1b[?1000h\x1b[?1002h\x1b[?1003h\x1b[?1006h';   // what a TUI emits when it takes the mouse
const ALT = '\x1b[?1049h';                                        // what a TUI emits when it owns the screen

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

const LINES = Array.from({ length: 24 }, (_, i) =>
  `CLICKGATE line ${String(i + 1).padStart(2, '0')} alpha beta gamma delta epsilon zeta eta theta`);

async function bootPage(harness, host) {
  const context = await browser.newContext({ viewport: { width: 1000, height: 700 } });
  context.setDefaultTimeout(TEST_TIMEOUT_MS);
  const page = await context.newPage();
  await page.goto(`http://127.0.0.1:${harness.port}/?s=${SESSION}`, { waitUntil: 'domcontentloaded' });
  await page.locator('#term').waitFor({ state: 'visible' });
  await waitFor(() => host.messages.some(m => m.t === 'sb' && m.s === SESSION), 'scrollback request', TEST_TIMEOUT_MS);
  host.sendScrollback(SESSION, 'boot\n');
  await waitFor(async () => {
    const text = await page.locator('#status').innerText();
    return text.includes(SESSION) && text.includes('live') ? true : null;
  }, 'live terminal status', TEST_TIMEOUT_MS);
  host.sendOutput(SESSION, '\x1b[2J\x1b[H' + LINES.join('\r\n') + '\r\n');
  await sleep(300);
  return { context, page };
}

// Click the center of the terminal; return the input frames the page sent to the pty as DECODED bytes.
// The fixture carries input bodies base64-encoded on the host wire (the relay sends `{t:'i',d:<b64>}`).
async function clickCenter(page, host) {
  const box = await page.locator('#term').boundingBox();
  host.messages.length = 0;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.up();
  await sleep(150);
  return host.messages.filter(m => m.t === 'i').map(m => Buffer.from(m.d, 'base64').toString('latin1'));
}

// Drag across the middle of the terminal; return {frames, selected}. A drag must SELECT and forward NOTHING.
async function dragCenter(page, host) {
  const box = await page.locator('#term').boundingBox();
  host.messages.length = 0;
  const y = box.y + box.height / 2;
  const x1 = box.x + box.width * 0.2, x2 = box.x + box.width * 0.6;
  await page.mouse.move(x1, y);
  await page.mouse.down();
  await page.mouse.move((x1 + x2) / 2, y, { steps: 4 });
  await page.mouse.move(x2, y, { steps: 4 });
  await page.mouse.up();
  await sleep(200);
  const frames = host.messages.filter(m => m.t === 'i').map(m => Buffer.from(m.d, 'base64').toString('latin1'));
  const selected = await page.evaluate(() => {
    // A selection is visible where the ACTIVE renderer draws it: DOM rects under the DOM renderer, a canvas
    // under the shipped WebGL renderer. hasSelection() is the renderer-independent truth, since the whole
    // point of this assertion is that the drag made a selection at all.
    try { return !!term.hasSelection() && !!term.getSelection(); } catch (e) { return false; }
  });
  return { frames, selected };
}

test('a click on an APP pane forwards press+release in EITHER arm state; a bare shell forwards nothing', async t => {
  if (!browser) return t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`);

  const harness = new RelayHarness();
  harness.start = () => startBrowserRelay(harness);
  let host = null, context = null;
  try {
    await harness.start();
    // An APP pane: bound to a chat and carrying a command (what findSession reports for a cc/Gateway tab).
    host = await harness.connectHost([{
      name: SESSION, alive: true, created: Date.now(), cols: 80, rows: 24,
      hasCommand: true, shellOnly: false, ready: true, owner: false, sessionId: 'click-gate-session',
    }]);
    const booted = await bootPage(harness, host);
    context = booted.context; const page = booted.page;

    // 1) ARMED APP PANE (alt screen + mouse modes): a click must forward press+release.
    host.sendOutput(SESSION, ALT + ARMED);
    await sleep(250);
    const armedFrames = await clickCenter(page, host);
    assert.equal(armedFrames.length, 2,
      `a click on an armed app pane did not forward exactly press+release: ${JSON.stringify(armedFrames)}`);
    assert.ok(armedFrames.some(d => /M$/.test(d)) && armedFrames.some(d => /m$/.test(d)),
      `the forwarded report is not an SGR press+release pair: ${JSON.stringify(armedFrames)}`);

    // 2) UNARMED APP PANE (alt screen, app never armed): the either/or regression — the click must STILL
    //    forward, because an app is behind the pane whether or not it armed the mouse.
    host.sendOutput(SESSION, '\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l');
    await sleep(250);
    const unarmedFrames = await clickCenter(page, host);
    assert.equal(unarmedFrames.length, 2,
      `a click on an UNARMED app pane forwarded nothing (the either/or regression): ${JSON.stringify(unarmedFrames)}`);
    assert.ok(unarmedFrames.some(d => /M$/.test(d)) && unarmedFrames.some(d => /m$/.test(d)),
      `the forwarded report is not an SGR press+release pair: ${JSON.stringify(unarmedFrames)}`);

    // 3) NORMAL-BUFFER APP PANE: the other half of the either/or — the user's cctest pane is an app on the
    //    NORMAL buffer, and it lost every click under the appOwnsScreen() gate. It must forward now.
    host.sendOutput(SESSION, '\x1b[?1049l');
    host.sendOutput(SESSION, '\x1b[2J\x1b[H' + LINES.join('\r\n') + '\r\n');
    await sleep(300);
    const normalAppFrames = await clickCenter(page, host);
    assert.equal(normalAppFrames.length, 2,
      `a click on a normal-buffer APP pane forwarded nothing: ${JSON.stringify(normalAppFrames)}`);

    // 4) DRAG on the SAME pane: both, as needed. A drag must select and forward NOTHING (a selection is not
    //    a click) — the other half of "click and select both", proven on the pane the click just worked on.
    const drag = await dragCenter(page, host);
    assert.equal(drag.frames.length, 0,
      `a drag also forwarded to the app (click won over selection): ${JSON.stringify(drag.frames)}`);
    assert.ok(drag.selected, 'a plain drag did not select on an app pane (selection lost to the click)');

    // 5) BARE SHELL (no app behind the pane): a click must NOT reach its stdin. Re-point the SAME page at
    //    a shell session record so nothing about the relay or the browser is re-created for this half.
    host.sendOutput(SESSION, '\x1b[2J\x1b[Hshell prompt $ ');
    await page.evaluate(() => {
      window._sessions = (window._sessions || []).map(s => s.name === 'click-gate-shell'
        ? { name: s.name, alive: true, cols: s.cols, rows: s.rows, hasCommand: false, shellOnly: true, ready: true, owner: false, sessionId: '' }
        : s);
    });
    await sleep(200);
    const shellFrames = await clickCenter(page, host);
    assert.equal(shellFrames.length, 0,
      `a click on a bare shell leaked to its stdin: ${JSON.stringify(shellFrames)}`);
  } finally {
    if (context) await context.close();
    await harness.stop();
  }
});

// The click-after-a-drag defect (Ahmed, 2026-10-06): "after I select, my next click on Led does nothing."
// On an ARMED pane we swallow the press so xterm's protocol cannot double-report it - but that also keeps
// xterm's selection service from dropping the old highlight, and a standing highlight latches the output
// freeze. The app DID get the click report and DID reply (the LedMenu), but the reply stayed buffered
// behind the freeze and never painted, so the click looked dead. Isolation proved it: forcing
// term.clearSelection() from the page flushed the withheld paint.
//
// The fix is one line inside the armed branch of the press handler: clear the standing selection itself.
// It is pinned as a SOURCE gate, not a behavioural one, because the loopback harness cannot reproduce the
// live armed condition - it forwards the click through a real pty either way, so a behavioural assertion
// here is FALSE-GREEN (it passes with the fix disabled). The behaviour was proven against the real client
// instead: a document-capture mousedown that mimics the fix made the chooser open where the control left it
// dead (muxdiag/dragoff-fix-proof.js: control clickOpenedChooser=false, fixed=true).
test('an armed press clears a standing selection itself, because we swallow the press that xterm would use', () => {
  const handler = ownerPressMousedownSource();
  const armedAt = handler.indexOf('appTakesMouse()');
  assert.notEqual(armedAt, -1, 'the armed-press branch is gone - the press is no longer kept out of xterm');
  const armed = handler.slice(armedAt);
  const clearAt = armed.indexOf('term.clearSelection()');
  const preventAt = armed.indexOf('e.preventDefault()');
  assert.notEqual(clearAt, -1,
    'an armed press no longer clears a standing selection: a click after a drag-select leaves the highlight up, the freeze latched, and the app reply unpainted - the click reads as dead');
  assert.notEqual(preventAt, -1, 'the armed press no longer swallows the event');
  assert.ok(clearAt < preventAt,
    'the selection is cleared after the press is swallowed; it must be cleared as part of taking the press');
});
