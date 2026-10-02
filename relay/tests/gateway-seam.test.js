// Regression: the two mux-side halves of the Gateway<->mux seam.
//
// (b) XTVERSION. An app running INSIDE a session identifies its terminal with CSI > 0 q and expects
//     DCS >| name ST back. This build answered nothing, so Claude Code / Gateway classified the client
//     as a native terminal and drove the wrong wheel tuning for an xterm.js client. The reply must go
//     out as INPUT (it rides the pty to the app's stdin), and its name must start with "xterm.js" — that
//     is the literal test the app applies (gateway src/ink/terminal.ts isXtermJs()).
//
// (c) MOUSE MODES ACROSS ATTACH. mouseGuard derives the app's mouse intent from the session's OWN output.
//     It used to be wiped on EVERY attach, so a session whose app armed the mouse once and never
//     re-emitted DECSET lost the wheel forever: the app is still tracking, nothing re-arms us, and every
//     later notch scrolls native scrollback instead of reaching the app. A re-attach of the SAME session
//     must keep the modes; a switch to a DIFFERENT session must still clear them (keeping them there is
//     the original bug — reports forwarded into a shell that never asked for the mouse).
const test = require('node:test');
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const { RelayHarness, REPO, freePort, launchBrowser, waitFor, sleep } = require('./harness');

const source = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');

function extractFn(name) {
  const start = source.indexOf('function ' + name + '(');
  assert.notEqual(start, -1, 'missing function ' + name);
  let depth = 0, end = -1;
  for (let i = source.indexOf('{', start); i < source.length; i++) {
    if (source[i] === '{') depth++;
    else if (source[i] === '}') { depth--; if (depth === 0) { end = i + 1; break; } }
  }
  assert.notEqual(end, -1, 'unbalanced braces for ' + name);
  const ctx = {};
  vm.runInNewContext(source.slice(start, end) + '\nthis.' + name + ' = ' + name + ';', ctx);
  return ctx[name];
}

// ---- (b) source gate: the client must answer CSI > 0 q with DCS >| xterm.js ST, as input ------------

test('the client answers XTVERSION (CSI > 0 q) with a DCS xterm.js reply', () => {
  assert.match(source, /registerCsiHandler\(\{\s*prefix:'>',\s*final:'q'\s*\}/,
    'no CSI > q handler is registered, so the client never answers XTVERSION');
  // The reply payload must begin with xterm.js — the app tests startsWith('xterm.js').
  assert.match(source, /\\x1bP>\|xterm\.js\\x1b\\\\/,
    'the XTVERSION reply is not a DCS ">|xterm.js" terminated with ST');
  // It is a terminal RESPONSE: it must leave as input, not be rendered into the buffer.
  assert.match(source, /send\('i',\s*'\\x1bP>\|xterm\.js\\x1b\\\\'\)/,
    'the XTVERSION reply is not sent as input');
});

// ---- (c) source gate: reset() must take keepModes, and connect must pass same-session ----------------

test('mouseGuard.reset keeps the modes only for a same-session re-attach', () => {
  const at = source.indexOf('function reset(keepModes)');
  assert.notEqual(at, -1, 'reset() no longer takes keepModes');
  const body = source.slice(at, source.indexOf('\n  }', at));
  assert.match(body, /if\(!keepModes\)\{[^}]*modes\s*=\s*Object\.create\(null\)/,
    'reset(keepModes) must clear the mode map ONLY when keepModes is false');
  assert.match(source, /mouseGuard\.reset\(prevName === name\)/,
    'connect() must keep the modes when re-attaching the same session');
});

// ---- (c) behaviour: run the shipped guard directly --------------------------------------------------

const DECSET = '\x1b[?1002h\x1b[?1006h';      // 1002 = tracking, the mode inputAllowed() reads

test('reset(true) keeps an armed mouse across a re-attach; reset(false) clears it', () => {
  const guard = extractFn('createMouseGuard')();
  guard.ingestOutput(DECSET, 0);
  assert.equal(guard.inputAllowed(), true, 'the guard did not arm on a DECSET');

  guard.reset(true);                                    // re-attach of the SAME session
  assert.equal(guard.inputAllowed(), true,
    'a same-session re-attach lost the app\'s mouse mode — the wheel would stop reaching the app');

  guard.reset(false);                                   // attach of a DIFFERENT session
  assert.equal(guard.inputAllowed(), false,
    'a different session kept the previous session\'s mouse mode — reports would go to a shell that never asked');
});

test('reset(true) still clears the leak latch so a re-attach gets a fresh chance', () => {
  const guard = extractFn('createMouseGuard')();
  guard.ingestOutput(DECSET, 0);
  // Forward a click and have the session echo it back: that is the leak, and it latches `blocked`.
  guard.noteForwarded('\x1b[<0;5;5M', 1000);
  guard.ingestOutput('echoed\x1b[<0;5;5M\r\n', 1100);
  assert.equal(guard.stats().blocked, true, 'the leak detector did not latch');
  assert.equal(guard.inputAllowed(), false, 'a latched guard must not forward');

  guard.reset(true);
  assert.equal(guard.stats().blocked, false, 'reset(true) left the leak latch armed');
});

// ---- (b) the real wire: CSI > 0 q in the session output must produce an input frame ------------------

const TEST_TIMEOUT_MS = 10000;

let browser = null, browserError = null;
test.before(async () => {
  try { browser = await launchBrowser(); }
  catch (error) { browserError = error; console.warn(`Chromium unavailable; skipping the live half.\n${error.message}`); }
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
  // Generous: a loaded box has been measured booting the relay well past 20s.
  await waitFor(() => harness.stdout.includes(`multiplex-app on 127.0.0.1:${harness.port}`), 'relay start', 90000);
}

const SESSION = 'gateway-seam';

test('CSI > 0 q on the wire yields a DCS >|xterm.js ST input frame', async t => {
  if (!browser) return t.skip(`Chromium unavailable.\n${browserError && browserError.message}`);
  const harness = new RelayHarness();
  harness.start = () => startBrowserRelay(harness);
  let host = null, context = null;
  try {
    await harness.start();
    host = await harness.connectHost([{
      name: SESSION, alive: true, created: Date.now(), cols: 80, rows: 24,
      hasCommand: true, shellOnly: false, ready: true, owner: false, sessionId: 'gateway-seam-id',
    }]);
    context = await browser.newContext({ viewport: { width: 1000, height: 700 } });
    context.setDefaultTimeout(TEST_TIMEOUT_MS);
    const page = await context.newPage();
    await page.goto(`http://127.0.0.1:${harness.port}/?s=${SESSION}`, { waitUntil: 'domcontentloaded' });
    await page.locator('#term').waitFor({ state: 'visible' });
    await waitFor(() => host.messages.some(m => m.t === 'sb' && m.s === SESSION), 'scrollback request', TEST_TIMEOUT_MS);
    host.sendScrollback(SESSION, 'boot\n');
    await waitFor(async () => {
      const txt = await page.locator('#status').innerText();
      return txt.includes(SESSION) && txt.includes('live') ? true : null;
    }, 'live status', TEST_TIMEOUT_MS);

    host.messages.length = 0;
    host.sendOutput(SESSION, '\x1b[>0q');                 // the app asking the terminal to identify itself
    await waitFor(() => host.messages.some(m => m.t === 'i'), 'XTVERSION reply', TEST_TIMEOUT_MS);
    const frames = host.messages.filter(m => m.t === 'i')
      .map(m => Buffer.from(m.d || '', 'base64').toString('latin1'));
    const reply = frames.find(f => f.startsWith('\x1bP>|'));
    assert.ok(reply, `no DCS reply was sent; frames=${JSON.stringify(frames)}`);
    const name = /^\x1bP>\|(.*?)(?:\x07|\x1b\\)$/s.exec(reply)[1];
    assert.ok(name.startsWith('xterm.js'),
      `the XTVERSION name must start with "xterm.js" (got ${JSON.stringify(name)})`);
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
});
