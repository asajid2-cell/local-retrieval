'use strict';
// Switching tabs is a NEW SCREEN. The page keeps per-session grid state (who holds the grid, whether this
// device pinned it) and one "this surface is already painted" latch. Both belong to the session they were
// measured on. Carrying them into the next tab meant the new tab's first 'd' took the nothing-changed path:
// no attach heal, no fit, no pan - so the tab you switched TO painted the geometry of the tab you left and
// the chip kept a pin that belonged to another grid. Reported live as "opening a new cc does not let me
// switch between the 2 local grids and the viewer grid and the viewer grid does not even fit or draw
// native". A re-attach of the SAME session must keep both, because its grid and its painted surface really
// are still there.
//
// The assertion is made at the moment the new session's first control frame arrives, BEFORE onControl
// processes it - that is the only instant where "what the page carried over" is visible, since the frame
// itself immediately corrects the grid values.
const test = require('node:test');
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const { RelayHarness, REPO, freePort, launchBrowser, waitFor, sleep } = require('./harness');

const source = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');

// ---- source gate -----------------------------------------------------------------------------------
test('a switch to a different session resets the paint latch and the grid state', () => {
  const at = source.indexOf("mouseGuard.reset(prevName === name);");
  assert.notEqual(at, -1, 'connect() no longer distinguishes a re-attach from a switch');
  const block = source.slice(at, at + 1400);
  assert.match(block, /if\(prevName !== name\)\{/,
    'connect() does not branch on the session actually changed — a switch and a re-attach are treated alike');
  assert.match(block, /_paintReady = false;/,
    'a switch no longer clears the painted latch: the new tab takes the nothing-changed path and never heals or fits');
  assert.match(block, /sizeGridOwner='local'/,
    'a switch no longer clears the previous tab\'s grid ownership: the chip keeps a pin from another screen');
  assert.match(block, /sizePinned=false;/,
    'a switch no longer clears the previous tab\'s pin, so the ladder answers for the wrong grid');
});

const TEST_TIMEOUT_MS = 10000;
const A = 'switch-a';
const B = 'switch-b';
const INSTALL = 'npm install --save-dev playwright --package-lock=false\nnpx playwright install chromium';

let browser = null;
let browserError = null;

test.before(async () => {
  try { browser = await launchBrowser(); }
  catch (error) { browserError = error; console.warn(`Chromium unavailable; skipping the live half.\n${INSTALL}\n${error.message}`); }
});
test.after(async () => { if (browser) await browser.close(); });

function row(name) {
  return {
    name, alive: true, created: Date.now(), cols: 100, rows: 30,
    hasCommand: true, shellOnly: false, ready: true, kind: 'local-tab', owner: true,
    localFirst: true, localViewers: 1, sessionId: `${name}-id`, generationId: 'gen-switch',
    streamBytes: 0, aliases: [], gridOwner: 'local', gridOwnerLabel: '', gridEpoch: 20,
  };
}

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

test('the tab you switch TO starts from its own grid, and a re-attach keeps the one it has', async t => {
  if (!browser) return t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`);

  const harness = new RelayHarness();
  harness.start = () => startBrowserRelay(harness);
  let context = null;
  try {
    await harness.start();
    const host = await harness.connectHost([row(A), row(B)]);
    t.after(() => host.close());

    context = await browser.newContext({ viewport: { width: 1000, height: 700 } });
    context.setDefaultTimeout(TEST_TIMEOUT_MS);
    const page = await context.newPage();
    await page.goto(`http://127.0.0.1:${harness.port}/?s=${A}`, { waitUntil: 'domcontentloaded' });
    await page.locator('#term').waitFor({ state: 'visible' });
    await waitFor(() => host.messages.some(m => m.t === 'sb' && m.s === A), 'scrollback request A', TEST_TIMEOUT_MS);
    host.sendScrollback(A, 'switch A screen\n');
    await waitFor(async () => {
      const text = await page.locator('#status').innerText();
      return text.includes(A) && text.includes('live') ? true : null;
    }, 'live A', TEST_TIMEOUT_MS);

    // Record the page state at the instant each control frame lands, before onControl touches the grid.
    await page.evaluate(() => {
      window.__switchLog = [];
      const orig = onControl;
      window.onControl = function (s) {
        window.__switchLog.push({ session: current, paintReady: _paintReady, pinned: sizePinned, owner: sizeGridOwner });
        return orig.apply(this, arguments);
      };
    });

    // A state the page would carry over: A is the grid holder, this device pinned it, and the surface is
    // already painted. Every one of those is wrong for B.
    await page.evaluate(() => { sizePinned = true; sizeMine = true; sizeGridOwner = 'other'; _paintReady = true; });

    await page.evaluate(name => connect(name), B);
    await waitFor(() => host.messages.some(m => m.t === 'sb' && m.s === B), 'scrollback request B', TEST_TIMEOUT_MS);
    host.sendScrollback(B, 'switch B screen\n');
    await waitFor(async () => {
      const text = await page.locator('#status').innerText();
      return text.includes(B) && text.includes('live') ? true : null;
    }, 'live B', TEST_TIMEOUT_MS);
    await sleep(300);

    const log = await page.evaluate(() => window.__switchLog);
    const firstB = log.find(e => e.session === B);
    assert.ok(firstB, `no control frame arrived for ${B}: ${JSON.stringify(log)}`);
    assert.equal(firstB.paintReady, false,
      `a switch carried the painted latch into ${B}, so its first frame never heals or fits: ${JSON.stringify(firstB)}`);
    assert.equal(firstB.pinned, false,
      `a switch carried ${A}'s pin into ${B}, so the ladder answers for the wrong grid: ${JSON.stringify(firstB)}`);
    assert.equal(firstB.owner, 'local',
      `a switch carried ${A}'s grid ownership into ${B}: ${JSON.stringify(firstB)}`);

    // The new screen must then be painted and fitted under its own frame.
    assert.equal(await page.evaluate(() => _paintReady), true, `${B} never became paint-ready after its first frame`);

    // Re-attaching the SAME session is not a switch: its grid and painted surface are still real.
    await page.evaluate(name => connect(name, true), B);
    await waitFor(() => host.messages.filter(m => m.t === 'sb' && m.s === B).length >= 2, 'scrollback request B (re-attach)', TEST_TIMEOUT_MS);
    host.sendScrollback(B, 'switch B screen\n');
    await waitFor(async () => {
      const text = await page.locator('#status').innerText();
      return text.includes(B) && text.includes('live') ? true : null;
    }, 'live B again', TEST_TIMEOUT_MS);
    await sleep(300);
    const log2 = await page.evaluate(() => window.__switchLog);
    const reattach = log2.filter(e => e.session === B).slice(1)[0];
    assert.ok(reattach, `a re-attach of ${B} produced no control frame: ${JSON.stringify(log2)}`);
    assert.equal(reattach.paintReady, true,
      `a re-attach of the SAME session was treated as a switch — its live surface was thrown away: ${JSON.stringify(reattach)}`);
  } catch (e) {
    console.error(`HARNESS STDOUT:\n${(harness.stdout || '').slice(-3000)}`);
    console.error(`HARNESS STDERR:\n${(harness.stderr || '').slice(-3000)}`);
    throw e;
  } finally {
    if (context) await context.close();
    await harness.stop();
  }
});
