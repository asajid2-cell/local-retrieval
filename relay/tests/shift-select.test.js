// Regression: the Shift-drag selection gesture must be the CLIENT's, for EVERY session, and must not
// depend on the app's state. It used to be gated on (appOwnsMouse() || isVisibleOwner()) - i.e. on the
// app taking the mouse OR the session being the visible owner - so the most common session there is, a
// plain shell created from the web (neither), got NO shift selection at all while a plain drag and a
// double-click still worked (those are xterm's own gestures). The user saw exactly that asymmetry:
// "double-click selects a word and it stays, but shift-drag does nothing". This file pins both halves:
// the source gate (cheap, always runs) and the real behaviour on a live non-owner shell (the proof).
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

// ---- source gate: the shift gesture must not consult the app's mouse/owner state -------------------

// The force-select mousedown handler is the one that calls forceSelStart(). Pull just that listener out
// of the shipped file (it is anonymous, so it is asserted against its source, not executed).
function forceSelectMousedownSource(){
  const call = source.indexOf('forceSelStart(e.clientX, e.clientY)');
  assert.notEqual(call, -1, 'no mousedown handler calls forceSelStart() — the shift gesture is gone');
  const start = source.lastIndexOf("addEventListener('mousedown'", call);
  assert.notEqual(start, -1, 'forceSelStart() is no longer reached from a mousedown listener');
  const end = source.indexOf('}, true);', call);
  assert.notEqual(end, -1, 'could not find the end of the force-select mousedown listener');
  return source.slice(start, end);
}

test('the shift-select mousedown gate does not depend on the app taking the mouse or owning the session', () => {
  const handler = forceSelectMousedownSource();
  assert.match(handler, /forceSelectGesture\(e\)/, 'the handler no longer checks the shift gesture at all');
  for(const forbidden of ['isVisibleOwner', 'appOwnsMouse', 'appTakesMouse', 'mouseActive', 'inputAllowed']){
    assert.doesNotMatch(
      handler, new RegExp(forbidden),
      `the shift gesture is gated on ${forbidden}() again — a plain non-owner shell would lose shift-select`,
    );
  }
});

test('the phone Sel drag is likewise not gated on the app state', () => {
  const call = source.indexOf("if(!selectMode || e.touches.length!==1) return;");
  assert.notEqual(call, -1, 'the touch force-select gate lost its selectMode/one-finger shape');
  const start = source.lastIndexOf("addEventListener('touchstart'", call);
  const handler = source.slice(start, source.indexOf('}, {capture:true, passive:false});', call));
  for(const forbidden of ['isVisibleOwner', 'appOwnsMouse', 'appTakesMouse', 'mouseActive']){
    assert.doesNotMatch(handler, new RegExp(forbidden), `the touch select gesture is gated on ${forbidden}() again`);
  }
});

// ---- the real behaviour: a non-owner shell must select, and the gesture must not leak to the PTY ----

const TEST_TIMEOUT_MS = 8000;
const SESSION = 'shift-select-shell';
const INSTALL = 'npm install --save-dev playwright --package-lock=false\nnpx playwright install chromium';

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
  `SHIFTSEL line ${String(i + 1).padStart(2, '0')} alpha beta gamma delta epsilon zeta eta theta`);

test('a non-owner shell selects on shift-drag and the gesture never reaches the PTY', async t => {
  if (!browser) return t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`);

  const harness = new RelayHarness();
  harness.start = () => startBrowserRelay(harness);
  let host = null, context = null;
  try {
    await harness.start();
    // owner:false is the whole point — a plain shell created from the web, the case the old gate broke.
    host = await harness.connectHost([{
      name: SESSION, alive: true, created: Date.now(), cols: 80, rows: 24,
      hasCommand: false, shellOnly: true, ready: true, owner: false, sessionId: 'shift-select-session',
    }]);
    context = await browser.newContext({ viewport: { width: 1000, height: 700 } });
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

    // A bare normal-buffer shell: no alt screen, no mouse tracking — exactly a plain bash session.
    host.sendOutput(SESSION, '\x1b[2J\x1b[H' + LINES.join('\r\n') + '\r\n');
    await sleep(400);

    const box = await page.locator('#term').boundingBox();
    const x1 = box.x + 40, x2 = box.x + 300, y1 = box.y + 16, y2 = box.y + 60;

    host.messages.length = 0;
    await page.keyboard.down('Shift');
    await page.mouse.move(x1, y1);
    await page.mouse.down();
    await page.mouse.move(x2, y2, { steps: 6 });
    await page.mouse.up();
    await page.keyboard.up('Shift');
    await sleep(120);

    const selected = await page.evaluate(() => {
      const t = (typeof term !== 'undefined') ? term : window.term;
      return t.getSelection();
    });
    assert.ok(selected && selected.length > 0,
      `shift-drag on a non-owner shell selected nothing (${JSON.stringify(selected)}) — the exact regression`);

    // The gesture is a selection, not input: nothing may have been written to the shell's stdin.
    assert.equal(host.messages.filter(m => m.t === 'i').length, 0,
      'the shift-drag leaked input frames to the PTY');

    // A live repaint must not eat the highlight (the freeze latches on the selection).
    host.sendOutput(SESSION, '\x1b[2J\x1b[H' + LINES.join('\r\n') + '\r\n');
    await sleep(250);
    const survived = await page.evaluate(() => {
      const t = (typeof term !== 'undefined') ? term : window.term;
      return t.hasSelection();
    });
    assert.equal(survived, true, 'a repaint cleared the shift selection');
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
});
