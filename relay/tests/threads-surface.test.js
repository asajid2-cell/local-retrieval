'use strict';
// The conversation surface as a REPLACEMENT for the terminal on a phone.
//
// Ahmed's ask: "I want the chat thing to have full / command capability, the ability to switch between leafs
// and stuff too, see the work being done, see the thought traces and message and respond to the agent, all
// well robustly and efficiently through the mobile messages render."
//
// These run the real page in Chromium at 390x844 against a fake host and assert on what the host actually
// received - the only place a phantom send or a lost send is visible. The page is not a second data plane:
// every row it renders came off the same pty the terminal mirrors.
const test = require('node:test');
const assert = require('node:assert/strict');
const { RelayHarness, freePort, launchBrowser } = require('./harness');

const INSTALL = 'npm install --save-dev playwright --package-lock=false\nnpx playwright install chromium';
const A = 'alpha', B = 'beta';

let browser = null, browserError = null;
test.before(async () => {
  try { browser = await launchBrowser(); } catch (e) { browserError = e; }
});
test.after(async () => { if (browser) await browser.close(); });

function paintBottom(lines, rows, cols) {
  const top = Math.max(0, rows - lines.length);
  let out = '\x1b[2J\x1b[H';
  lines.forEach((text, i) => { out += `\x1b[${top + i + 1};1H${String(text).slice(0, cols)}`; });
  return out;
}
// The grid is decided from the VIEWER's viewport, so a fixture must paint at the rows the page holds.
async function sendBottom(host, page, name, lines) {
  const geo = await page.evaluate(() => ({ rows: term.rows, cols: term.cols }));
  host.sendOutput(name, paintBottom(lines, geo.rows, geo.cols));
  await page.waitForTimeout(400);
}
function inputSince(host, mark) {
  return host.messages.slice(mark)
    .filter(m => m && m.t === 'i')
    .map(m => { try { return Buffer.from(String(m.d || ''), 'base64').toString('utf8'); } catch (e) { return ''; } })
    .join('');
}

async function withPhone(t, fn) {
  if (!browser) { t.skip(`Chromium unavailable.\n${INSTALL}\n${browserError && browserError.message}`); return; }
  const harness = new RelayHarness();
  let host = null, context = null;
  try {
    harness.fixedPort = await freePort();
    harness.env.ALLOWED_WS_ORIGINS = `http://127.0.0.1:${harness.fixedPort}`;
    await harness.start();
    host = await harness.connectHost([
      { name: A, alive: true, created: Date.now(), cols: 100, rows: 30, owner: 'muxtee', gridOwner: 'local', hasCommand: false, shellOnly: true, ready: true },
      { name: B, alive: true, created: Date.now(), cols: 100, rows: 30, owner: 'muxtee', gridOwner: 'local', hasCommand: false, shellOnly: true, ready: true },
    ]);
    context = await browser.newContext({
      viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, isMobile: true, hasTouch: true, locale: 'en-US',
    });
    context.setDefaultTimeout(15000);
    const page = await context.newPage();
    page.on('pageerror', e => console.log('PAGE ERROR:', e.message));
    await page.goto(`http://127.0.0.1:${harness.port}/?s=${A}`, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => window._sessions && window._sessions.some(s => s.name === 'alpha'));
    await page.evaluate(() => connect('alpha'));
    await page.waitForFunction(() => !!document.querySelector('#term .xterm-helper-textarea'));
    await page.waitForTimeout(400);
    await page.evaluate(() => setMobileView('threads'));
    await page.waitForTimeout(300);
    await fn({ harness, host, page });
  } finally {
    if (context) await context.close();
    if (host) host.close();
    await harness.stop();
  }
}

const LEAF_BAR = '  < Leaf 1: alpha  |  Leaf 2: beta  |  Leaf 3: gamma >   All 3';

test('the conversation view carries its own session and leaf switcher', async t => {
  await withPhone(t, async ({ host, page }) => {
    await sendBottom(host, page, A, [
      '⏺ Ready to help.',
      '',
      '─'.repeat(60),
      '❯ ',
      '─'.repeat(60),
      '  cd: C:\\work   usage: 37% left',
      LEAF_BAR,
    ]);
    const chips = await page.evaluate(() => ({
      sessions: [...document.querySelectorAll('#threadnav .tchip:not(.leaf)')].map(b => b.textContent.replace(/\s+/g, ' ').trim()),
      leaves: [...document.querySelectorAll('#threadnav .tchip.leaf')].map(b => b.textContent.trim()),
      view: document.querySelector('#app').className,
    }));
    assert.ok(chips.sessions.some(s => s.startsWith('alpha')), `alpha chip missing: ${JSON.stringify(chips)}`);
    assert.ok(chips.sessions.some(s => s.startsWith('beta')), 'beta chip missing');
    assert.deepEqual(chips.leaves, ['Leaf 1: alpha', 'Leaf 2: beta', 'Leaf 3: gamma']);
    assert.ok(/mobile-view-threads/.test(chips.view), `still on the conversation view: ${chips.view}`);
  });
});

test('switching session from the conversation view stays in the conversation view', async t => {
  await withPhone(t, async ({ page }) => {
    await page.evaluate(() => connect('beta'));
    await page.waitForTimeout(500);
    const state = await page.evaluate(() => ({ view: document.querySelector('#app').className, cur: current }));
    assert.equal(state.cur, 'beta');
    assert.ok(/mobile-view-threads/.test(state.view), `a session switch must not eject the reader into the grid: ${state.view}`);
  });
});

test('typing "/" raises the command tray, and the command is sent as typed', async t => {
  await withPhone(t, async ({ host, page }) => {
    await page.evaluate(() => { const ta = document.querySelector('#threadinput'); ta.value = '/com'; ta.dispatchEvent(new Event('input')); });
    await page.waitForTimeout(150);
    const tray = await page.evaluate(() => ({
      hidden: document.querySelector('#slashTray').hidden,
      items: [...document.querySelectorAll('#slashTray button .cmd')].map(e => e.textContent),
    }));
    assert.equal(tray.hidden, false, 'the tray must open on a leading slash');
    assert.ok(tray.items.includes('/compact'), `expected /compact in ${JSON.stringify(tray.items)}`);
    await page.evaluate(() => [...document.querySelectorAll('#slashTray button')].find(b => b.textContent.startsWith('/compact')).click());
    assert.equal(await page.evaluate(() => document.querySelector('#threadinput').value), '/compact ');
    const mark = host.messages.length;
    await page.evaluate(() => document.querySelector('#sendthread').click());
    await page.waitForTimeout(400);
    assert.equal(inputSince(host, mark), '/compact \r', 'a slash command must reach the pty exactly as typed, plus Enter');
  });
});

test('an on-screen picker becomes tappable, and a numbered choice sends its digit', async t => {
  await withPhone(t, async ({ host, page }) => {
    // A permission/menu screen: no composer sandwich, one option carries the cursor.
    await sendBottom(host, page, A, [
      '⏺ Choose a model:',
      '  1. Sonnet 4.5',
      '❯ 2. Opus 4.1',
      '  3. Haiku 3.5',
      '  Esc to cancel',
    ]);
    const ctl = await page.evaluate(() => ({
      on: document.querySelector('#tuiControls').classList.contains('on'),
      opts: [...document.querySelectorAll('#tuiControls button.opt')].map(b => b.textContent),
    }));
    assert.equal(ctl.on, true, 'a picker must raise the TUI control strip');
    assert.ok(ctl.opts.some(o => /Opus/.test(o)), `picker options missing: ${JSON.stringify(ctl.opts)}`);
    const mark = host.messages.length;
    await page.evaluate(() => [...document.querySelectorAll('#tuiControls button.opt')].find(b => /Opus/.test(b.textContent)).click());
    await page.waitForTimeout(300);
    const sent = inputSince(host, mark);
    assert.match(sent, /^(2\r|\x1b\[B\r)$/, `a numbered option must answer with its digit (or arrows): ${JSON.stringify(sent)}`);
  });
});

test('a thought trace is collapsed separately from the tool call that follows it', async t => {
  await withPhone(t, async ({ host, page }) => {
    await sendBottom(host, page, A, [
      '∴ Thinking…',
      '  Comparing the two parsers before editing.',
      '⏺ Bash(pwd)',
      '  ⎿ C:\\work',
      '⏺ Done.',
      '─'.repeat(60),
      '❯ ',
      '─'.repeat(60),
    ]);
    const dom = await page.evaluate(() => ({
      thought: !!document.querySelector('#threads .th-msg.thought details.th-thought'),
      toolcall: !!document.querySelector('#threads .th-msg.toolcall'),
      assistant: [...document.querySelectorAll('#threads .th-msg.assistant .th-body')].some(e => /Done\./.test(e.textContent)),
    }));
    assert.equal(dom.thought, true, 'a reasoning block must render as a collapsible thought');
    assert.equal(dom.toolcall, true, 'a tool invocation must render as a work step');
    assert.equal(dom.assistant, true, 'the reply after it is still an assistant message');
  });
});

test('the terminal-sized chip does not accuse the conversation surface of being too wide', async t => {
  await withPhone(t, async ({ host, page }) => {
    await sendBottom(host, page, A, ['⏺ Ready.', '', '─'.repeat(60), '❯ ', '─'.repeat(60)]);
    const chip = await page.evaluate(() => document.querySelector('#statusmeta').textContent);
    assert.equal(chip, '', `the grid chip is meaningless on the conversation surface, got ${JSON.stringify(chip)}`);
  });
});

test('history survives a session round trip', async t => {
  await withPhone(t, async ({ host, page }) => {
    await sendBottom(host, page, A, ['❯ first question', '⏺ first answer', '', '─'.repeat(60), '❯ ', '─'.repeat(60)]);
    const before = await page.evaluate(() => document.querySelectorAll('#threads .th-msg').length);
    assert.ok(before >= 2, `expected at least two messages, got ${before}`);
    await page.evaluate(() => connect('beta'));
    await page.waitForTimeout(400);
    await page.evaluate(() => connect('alpha'));
    await page.waitForTimeout(500);
    const after = await page.evaluate(() => [...document.querySelectorAll('#threads .th-body')].map(e => e.textContent));
    assert.ok(after.some(x => /first answer/.test(x)), `alpha history was lost: ${JSON.stringify(after)}`);
  });
});

test('a send that never reaches the session keeps the text in the box', async t => {
  await withPhone(t, async ({ page }) => {
    const kept = await page.evaluate(async () => {
      const ta = document.querySelector('#threadinput');
      ta.value = 'do not lose me';
      // Close the socket so paste has nowhere to go. The drop path used to clear the box and send an Enter
      // into nothing.
      try { ws && ws.close(); } catch (e) {}
      await new Promise(r => setTimeout(r, 120));
      await sendThreadMessage();
      await new Promise(r => setTimeout(r, 120));
      return ta.value;
    });
    assert.equal(kept, 'do not lose me', 'a dropped send must leave the text in the composer');
  });
});
