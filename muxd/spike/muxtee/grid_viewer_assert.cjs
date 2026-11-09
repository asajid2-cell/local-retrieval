'use strict';
/*
 * Browser half of the grid-ownership proof. Attaches to a locally-served relay (test posture, loopback
 * trust), reads what the relay actually published, taps the size chip, and reports V0-V4.
 *
 * It does not need a cookie: the relay only grants this in MUX_TEST_MODE with a 127.0.0.1 bind, which
 * is the same combination its own tests use. Never point this at a deployed host.
 */
const path = require('path');
// Node resolves a bare require from THIS file's directory, not the relay's, so look for the relay's own
// install explicitly rather than assuming the harness and the relay share a node_modules.
function loadPlaywright() {
  const candidates = [
    'playwright',
    process.env.MUX_RELAY_DIR ? path.join(process.env.MUX_RELAY_DIR, 'node_modules', 'playwright') : null,
    'C:/Users/Ahmed/mux-track/wt-p2b/relay/node_modules/playwright',
  ].filter(Boolean);
  let last;
  for (const c of candidates) {
    try { return require(c); } catch (e) { last = e; }
  }
  throw last;
}
const { chromium } = loadPlaywright();

function arg(name, dflt) {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && process.argv[i + 1] ? process.argv[i + 1] : dflt;
}
const PORT = arg('port');
const SESSION = arg('session');
const HOST_COLS = Number(arg('cols', '0'));
const HOST_ROWS = Number(arg('rows', '0'));
const PAGE_PATH = arg('path', '/index.html');
const HEADFUL = process.argv.includes('--headful');

const rows = [];
function check(id, ok, detail) {
  rows.push({ id, ok, detail });
  console.log(`${ok ? 'PASS' : 'FAIL'} ${id}${detail ? ' - ' + detail : ''}`);
}

async function readState(page) {
  return page.evaluate(() => {
    const host = document.querySelector('#term');
    const scr = host && host.querySelector('.xterm-screen');
    const tf = scr ? getComputedStyle(scr).transform : 'none';
    let sx = 1, sy = 1;
    if (tf && tf !== 'none') {
      const m = tf.match(/matrix\(([^)]+)\)/);
      if (m) { const p = m[1].split(',').map(Number); sx = p[0]; sy = p[3]; }
    }
    const g = (n) => { try { return eval(n); } catch (e) { return undefined; } };
    const d = (window.__d && window.__d.length) ? window.__d[window.__d.length - 1] : null;
    return {
      cols: g('term.cols'), rows: g('term.rows'), fontSize: g('term.options.fontSize'),
      transform: tf, sx, sy,
      winSize: g('winSize'), tabOwned: g('sizeTabOwned'), pinned: g('sizePinned'),
      mine: g('sizeMine'), modeLabel: g('sizeModeLabel'),
      gridOwner: d && d.gridOwner, gridEpoch: d && d.gridEpoch,
      chip: (document.querySelector('#sizebtn') || {}).textContent || '',
      dFrames: (window.__d || []).length,
    };
  });
}

async function installFrameTap(page) {
  await page.evaluate(() => {
    window.__d = [];
    const grab = () => {
      try {
        if (typeof ws === 'undefined' || !ws) return false;
        ws.addEventListener('message', (e) => {
          const s = typeof e.data === 'string' ? e.data : '';
          if (s && s[0] === 'd') {
            try { window.__d.push(JSON.parse(s.slice(1))); } catch (_) {}
          }
        });
        return true;
      } catch (_) { return false; }
    };
    grab();
    window.__tap = setInterval(grab, 200);
  });
}

(async () => {
  const browser = await chromium.launch({ headless: !HEADFUL, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'en-US' });
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(String((e && e.message) || e)));
  // The relay serves public/ at the root and the page derives its ws path from location.pathname, so a
  // local run must use /index.html (base === ''), not the deployed /multiplex/ prefix.
  const url = `http://127.0.0.1:${PORT}${PAGE_PATH}?s=${encodeURIComponent(SESSION)}`;
  try {
    const resp = await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 60000 });
    await installFrameTap(page);
    try {
      await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1; } catch (e) { return false; } }, null, { timeout: 30000 });
    } catch (e) {
      const diag = await page.evaluate(() => ({
        href: location.href, title: document.title,
        text: (document.body && document.body.innerText || '').slice(0, 400),
        hasTerm: typeof term !== 'undefined', statusText: document.readyState,
      })).catch(() => null);
      console.error('ATTACH DIAG', JSON.stringify({ status: resp && resp.status(), errors, diag }, null, 2));
      throw e;
    }
    await page.waitForTimeout(2500);

    let s = await readState(page);
    check('V0 attach', s.cols > 10 && s.rows > 5 && errors.length === 0,
      `${s.cols}x${s.rows} errors=${errors.length} frames=${s.dFrames}`);
    if (!(s.cols > 10)) { console.log(JSON.stringify({ errors, state: s }, null, 2)); return 1; }
    if (s.dFrames === 0) {
      const diag = await page.evaluate(() => ({
        sessions: (window._sessions || []).map((x) => x.name).slice(0, 8),
        wsState: (() => { try { return typeof ws !== 'undefined' && ws ? ws.readyState : null; } catch (e) { return 'err'; } })(),
        status: (document.querySelector('#statustext') || {}).textContent || '',
        href: location.href,
      })).catch(() => null);
      console.log('NO-D-FRAME DIAG ' + JSON.stringify(diag));
    }

    const localOwner = (s.gridOwner === 'local') || (s.gridOwner === undefined && s.tabOwned === true);
    check('V1 local ownership', localOwner && Math.abs(s.sx - 1) < 1e-6 && Math.abs(s.sy - 1) < 1e-6,
      `gridOwner=${s.gridOwner} tabOwned=${s.tabOwned} transform=${s.transform} term=${s.cols}x${s.rows} host=${HOST_COLS}x${HOST_ROWS}`);

    await page.evaluate(() => document.querySelector('#sizebtn').click());
    await page.waitForTimeout(3000);
    const pinned = await readState(page);
    const took = pinned.gridOwner === 'mine';
    check('V2 flex takes the grid', took && pinned.cols !== s.cols,
      `gridOwner=${pinned.gridOwner} mine=${pinned.mine} term=${pinned.cols}x${pinned.rows} chip=${JSON.stringify(pinned.chip)}`);
    check('V3 no distortion while owned', pinned.gridOwner === 'mine' && Math.abs(pinned.sx - 1) < 1e-6 && Math.abs(pinned.sy - 1) < 1e-6,
      `gridOwner=${pinned.gridOwner} transform=${pinned.transform}`);

    await page.evaluate(() => document.querySelector('#sizebtn').click());
    await page.waitForTimeout(3000);
    const released = await readState(page);
    const back = released.gridOwner === 'local';
    check('V4 release returns to local', back && released.cols === s.cols,
      `gridOwner=${released.gridOwner} term=${released.cols}x${released.rows} chip=${JSON.stringify(released.chip)}`);

    await page.screenshot({ path: path.join(__dirname, 'grid-viewer-proof.png'), fullPage: false });
    const failed = rows.filter((r) => !r.ok).map((r) => r.id);
    console.log(failed.length ? `VIEWER PROOF FAIL: ${failed.join(', ')}` : 'VIEWER PROOF PASS');
    process.exitCode = failed.length ? 1 : 0;
    return process.exitCode;
  } finally {
    await ctx.close();
    await browser.close();
  }
})().catch((e) => { console.error('ASSERT ERROR', e && (e.stack || e.message)); process.exit(2); });
