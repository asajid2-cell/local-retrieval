'use strict';
/* Visual check of the phone thread view against a LOOPBACK relay with a fake host, so it never touches the
 * deployed page or a live session. Writes a PNG and prints what the view grouped.
 * Usage: node tests/shot-threads.cjs [out.png] */
const childProcess = require('node:child_process');
const { RelayHarness, REPO, freePort, launchBrowser, waitFor, sleep } = require('./harness');

const SESSION = 'threads-shot';
const OUT = process.argv[2] || 'threads-phone.png';

const SCREEN = [
  '',
  '⏺ Paired and drove the set, and found the real watch failure.',
  '',
  '  What the set did at 22:37-22:39 (measured, nginx + events.log):',
  '',
  '  Then playback died, in this order:',
  '  1. Cold start: encoder silent 44.6 s, first segment at 50 s.',
  '  2. The manifest long-poll hit its 28 s stall deadline.',
  '  3. The set uses the native HLS path, so it cannot read the coded body.',
  '',
  '● Bash(echo LAT-1)',
  '  ⎿  LAT-1',
  '',
  '✻ Cooked for 46m 39s · 3 shells still running',
  '❯ ',
].join('\r\n');

(async () => {
  const browser = await launchBrowser();
  const harness = new RelayHarness();
  harness.port = await freePort();
  harness.env.ALLOWED_WS_ORIGINS = `http://127.0.0.1:${harness.port}`;
  harness.proc = childProcess.spawn(process.execPath, ['server.js'], {
    cwd: REPO,
    env: {
      ...process.env, PORT: String(harness.port), MUX_HOST_TOKEN: 'test-token',
      MUX_TEST_MODE: '1', MUX_TEST_FIXTURE: '1', MUX_BIND_HOST: '127.0.0.1',
      MUX_AUTOHEAL: '0', MUX_STATE_DIR: harness.tmp, MUX_HOST_SB_WAIT_MS: '400',
      HLAUTH_BASE: 'http://127.0.0.1:1', ...harness.env,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  harness.stdout = '';
  harness.proc.stdout.on('data', d => { harness.stdout += d.toString(); });
  await waitFor(() => harness.stdout.includes(`multiplex-app on 127.0.0.1:${harness.port}`), 'relay start', 6000);
  const host = await harness.connectHost([{
    name: SESSION, alive: true, created: Date.now(), cols: 100, rows: 30, hasCommand: false,
    shellOnly: true, ready: true, kind: 'local-tab', owner: true, localFirst: true, localViewers: 1,
    sessionId: '', generationId: 'gen-shot', streamBytes: 0, aliases: [], gridOwner: 'local', gridEpoch: 7,
  }]);
  const ctx = await browser.newContext({
    viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true,
    userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1',
  });
  const page = await ctx.newPage();
  await page.goto(`http://127.0.0.1:${harness.port}/?s=${SESSION}`, { waitUntil: 'domcontentloaded' });
  await page.locator('#term').waitFor({ state: 'visible' });
  await waitFor(() => host.messages.some(m => m.t === 'sb' && m.s === SESSION), 'scrollback request', 8000);
  host.sendScrollback(SESSION, SCREEN);
  await sleep(1200);
  await page.locator('[data-mobile-view="threads"]').click();
  await page.locator('#threads').waitFor({ state: 'visible' });
  await sleep(900);
  const info = await page.evaluate(() => ({
    msgs: [...document.querySelectorAll('#threads .th-msg')].map(m => ({
      role: [...m.classList].find(c => c !== 'th-msg'),
      who: m.querySelector('.th-who').textContent,
      text: m.querySelector('.th-body').textContent.slice(0, 70),
    })),
    overflowX: document.documentElement.scrollWidth > window.innerWidth,
    composerVisible: !!document.querySelector('#composer') && getComputedStyle(document.querySelector('#composer')).display !== 'none',
  }));
  console.log(JSON.stringify(info, null, 1));
  await page.screenshot({ path: OUT });
  console.log('screenshot ' + OUT);
  await ctx.close(); await browser.close(); host.close(); await harness.stop();
  process.exit(0);
})().catch(e => { console.error('SHOT ERROR', e && (e.stack || e.message)); process.exit(2); });
