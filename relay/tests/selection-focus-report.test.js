// Regression: a mouse drag-select must SURVIVE the focus report xterm emits on helper-textarea blur.
//
// The bug (Ahmed, 2026-10-04): "its nothing selects, terminal text doesnt select nor does bottom bar and
// its all on harmonizerlabs.cc/multiplex". A real CDP shift-drag on the deployed page selected correctly
// for the duration of the gesture (the client's own forceSel built it, the .xterm-selection rects were
// painted, the clipboard copy worked) and then the highlight vanished the instant the drag ended.
//
// The stack captured off the deployed page was unambiguous:
//
//   term.clearSelection()
//     at index.html (the onData handler's "typing = you're done selecting" line)
//       <- Object.fire (xterm onData)
//         <- _handleTextAreaBlur
//
// When the app enables focus tracking (DECSET 1004 - codex/claude/vim do), xterm's textarea fires
// ESC[O on blur and ESC[I on focus. A drag blurs the textarea, so onData receives ESC[O. That payload
// was NOT recognised as a terminal-generated report by takeTerminalReport, so it fell through to the
// handler's "a keystroke arrived -> clear the selection" branch and wiped the selection the user had
// just made (and leaked the report to the pty as input, too).
//
// The fix: ESC[I / ESC[O are reports ABOUT this terminal, like the CPR/DA reports already dropped, so
// takeTerminalReport recognises them and stripTerminalGeneratedReports drops them - the keystroke path
// never sees them. Real typing batched with a report (ESC[Oabc) still falls through and clears, because
// that IS typing.
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

// ---- source gate: the focus reports are classified as terminal-generated --------------------------
function takeTerminalReportSource() {
  const start = source.indexOf('function takeTerminalReport');
  assert.notEqual(start, -1, 'takeTerminalReport is gone - the report classifier cannot be checked');
  const end = source.indexOf('function stripTerminalGeneratedReports', start);
  assert.notEqual(end, -1, 'could not find the end of takeTerminalReport');
  return source.slice(start, end).split('\n').filter(l => !/^\s*\/\//.test(l)).join('\n');
}

test('the input classifier recognises the focus in/out reports (ESC[I / ESC[O)', () => {
  const fn = takeTerminalReportSource();
  assert.match(fn, /'I'\s*\|\|\s*s\[i\+2\]\s*===\s*'O'|'O'\s*\|\|\s*s\[i\+2\]\s*===\s*'I'/,
    'takeTerminalReport no longer recognises ESC[I / ESC[O, so a blur report will reach the '
    + 'typing-clears-selection branch again and wipe every drag-select on a focus-tracking pane');
});

// ---- the real behaviour, on a loopback relay with a fake host -------------------------------------

const TEST_TIMEOUT_MS = 8000;
const SESSION = 'sel-focus';
const INSTALL = 'npm install --save-dev playwright --package-lock=false\nnpx playwright install chromium';
const FOCUS_ON = '\x1b[?1004h';   // what codex/claude/vim emit to ask for focus in/out reports

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
  await waitFor(() => harness.stdout.includes(`multiplex-app on 127.0.0.1:${harness.port}`), 'browser relay start', 10000);
}

const LINES = Array.from({ length: 24 }, (_, i) =>
  `SELFOCUS line ${String(i + 1).padStart(2, '0')} alpha beta gamma delta epsilon zeta eta theta`);

test('a shift-drag on a focus-tracking pane keeps a visible, copyable selection', async t => {
  if (!browser) return t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`);

  const harness = new RelayHarness();
  const context = await browser.newContext({
    viewport: { width: 1200, height: 800 },
    permissions: ['clipboard-read', 'clipboard-write'],
  });
  context.setDefaultTimeout(TEST_TIMEOUT_MS);
  try {
    await startBrowserRelay(harness);

    const page = await context.newPage();
    await page.goto(`http://127.0.0.1:${harness.port}/?s=${SESSION}`, { waitUntil: 'domcontentloaded' });
    await page.locator('#term').waitFor({ state: 'visible' });
    const host = await harness.connectHost([{
      name: SESSION, alive: true, created: Date.now(), cols: 80, rows: 24,
      hasCommand: false, shellOnly: false, ready: true, owner: false, sessionId: 'sel-focus-session',
    }]);
    await waitFor(() => host.messages.some(m => m.t === 'sb' && m.s === SESSION), 'scrollback request', TEST_TIMEOUT_MS);
    host.sendScrollback(SESSION, 'boot\n');
    await waitFor(async () => {
      const text = await page.locator('#status').innerText();
      return text.includes('live') ? true : null;
    }, 'live terminal status', TEST_TIMEOUT_MS);
    host.sendOutput(SESSION, '\x1b[2J\x1b[H' + LINES.join('\r\n') + '\r\n');
    host.sendOutput(SESSION, FOCUS_ON);   // the app asks for focus reports, exactly as codex/claude do
    await sleep(400);

    const geo = await page.evaluate(() => {
      const scr = document.querySelector('#term .xterm-screen');
      const r = scr.getBoundingClientRect();
      const b = term.buffer.active;
      let textRow = null;
      for (let y = b.length - 1; y >= 0; y--) {
        const line = b.getLine(y);
        if (line && /SELFOCUS/.test(line.translateToString(true))) { textRow = y; break; }
      }
      let cw = r.width / (term.cols | 0), ch = r.height / (term.rows | 0);
      try { const d = term._core._renderService.dimensions.css.cell; if (d && d.width > 0) { cw = d.width; ch = d.height; } } catch (_) {}
      return { left: r.left, top: r.top, cw, ch, vp: b.viewportY | 0, textRow: textRow == null ? 3 : textRow };
    });

    const y = geo.top + geo.ch * (geo.textRow - geo.vp + 0.5);
    const x1 = geo.left + geo.cw * 3, x2 = geo.left + geo.cw * 34;
    const cdp = await context.newCDPSession(page);
    const ev = (type, x, ypos) => cdp.send('Input.dispatchMouseEvent', {
      type, x, y: ypos, button: 'left',
      buttons: type === 'mouseMoved' ? 1 : (type === 'mousePressed' ? 1 : 0),
      clickCount: type === 'mousePressed' ? 1 : 0, modifiers: 8,   // Shift held (real input, not sugar)
    });
    const key = (type, k) => cdp.send('Input.dispatchKeyEvent', {
      type, key: k, code: 'ShiftLeft', windowsVirtualKeyCode: 16, nativeVirtualKeyCode: 16,
      modifiers: type === 'keyUp' ? 0 : 8,
    });

    await key('rawKeyDown', 'Shift');
    await ev('mousePressed', x1, y); await sleep(40);
    for (let i = 1; i <= 6; i++) await ev('mouseMoved', x1 + (x2 - x1) * i / 6, y);
    await sleep(40);
    await ev('mouseReleased', x2, y);
    await key('keyUp', 'Shift');
    await sleep(350);

    const visible = await page.evaluate(() => {
      let has = false, text = '';
      try { has = term.hasSelection(); text = term.getSelection() || ''; } catch (_) {}
      return {
        has, len: text.length,
        visualRects: document.querySelectorAll('#term .xterm-selection div').length,
      };
    });
    assert.ok(visible.has, 'the drag-select cleared itself - a focus report reached the typing path');
    assert.ok(visible.visualRects > 0, `the selection has no visible highlight (rects=${visible.visualRects})`);
    assert.ok(visible.len > 0, 'the selection is empty - nothing was actually selected');

    // the blur report must not have leaked to the pty as an input frame either
    const leaked = host.messages
      .filter(m => m.t === 'i')
      .map(m => Buffer.from(m.d, 'base64').toString('latin1'))
      .filter(s => s === '\x1b[O' || s === '\x1b[I');
    assert.equal(leaked.length, 0,
      `a focus report leaked into the pty as input: ${JSON.stringify(leaked)}`);
  } finally {
    await context.close();
    await harness.stop();
  }
});
