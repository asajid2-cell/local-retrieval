'use strict';
/*
 * LIVE grid-ownership proof against the deployed page, driven by a real browser.
 *
 * It uses the durable hl-auth TEST SESSION (keysafe `hl-owner-session`: a bound owner cookie minted at
 * sign-in with test_session=1), presents the same User-Agent it was bound to, and attaches to a real
 * teed muxtee tab. Nothing is printed except status, geometry, epochs and timing.
 *
 * Rows:
 *   L0  the deployed page is the grid-ownership build and the terminal attaches
 *   L0  a quiet window: no resize traffic, no 'P', no repaint storm while local owns
 *   L1  local ownership is the default: gridOwner=local, term grid == pane grid, identity transform
 *   L1  fit renders without taking the grid (pure rendering, zero resize sends)
 *   L2  a double tap takes the real grid: one 'P', owner=mine, new grid, committed epoch
 *   L2  the tee itself resized its child pty to the acked grid (log is the evidence, not the page)
 *   L3  the owned grid paints 1:1: identity transform, spans the host, integer font
 *   L3  a viewport change while owning commits a second grid and moves the pty again
 *   L4  release returns the grid to the pane and the tee restores the pane geometry
 *   L4  local ownership after release absorbs a viewport change as pure rendering
 */
const fs = require('fs');
const path = require('path');

function loadPlaywright() {
  const candidates = [
    process.env.MUX_RELAY_DIR ? path.join(process.env.MUX_RELAY_DIR, 'node_modules', 'playwright') : null,
    'C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright',
    'playwright',
  ].filter(Boolean);
  let last;
  for (const c of candidates) { try { return require(c); } catch (e) { last = e; } }
  throw last;
}
const { chromium } = loadPlaywright();

const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const SESSION = process.env.MUX_LIVE_SESSION || 'hl-owner-session';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const UA = process.env.MUX_UA
  || 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BUILD = '2026-11-09-grid-ownership';
const TEE_LOG = process.env.MUX_TEE_LOG
  || path.join(process.env.LOCALAPPDATA || 'C:/Users/Ahmed/AppData/Local', 'muxtee', 'muxtee.log');
const HEADFUL = process.argv.includes('--headful');

const rows = [];
function check(id, ok, detail) {
  rows.push({ id, ok, detail });
  console.log(`${ok ? 'PASS' : 'FAIL'} ${id}${detail ? ' - ' + detail : ''}`);
}

function cookiePairs(header) {
  return String(header || '').split(';').map((p) => p.trim()).filter(Boolean).map((p) => {
    const i = p.indexOf('=');
    return { name: p.slice(0, i), value: p.slice(i + 1) };
  }).filter((c) => c.name && c.value);
}

function teeMark() {
  try { return fs.statSync(TEE_LOG).size; } catch (e) { return 0; }
}
function teeSince(offset) {
  try {
    const buf = fs.readFileSync(TEE_LOG);
    return buf.slice(Math.min(offset, buf.length)).toString('utf8');
  } catch (e) { return ''; }
}
function lastTeeResize(text) {
  const all = [...text.matchAll(/remote resize pty to (\d+)x(\d+) epoch=(\d+)/g)];
  return all.length ? { cols: +all[all.length - 1][1], rows: +all[all.length - 1][2], epoch: +all[all.length - 1][3] } : null;
}
function lastPaneRestore(text) {
  const all = [...text.matchAll(/pane restored to (\d+)x(\d+) epoch=(\d+)/g)];
  return all.length ? { cols: +all[all.length - 1][1], rows: +all[all.length - 1][2], epoch: +all[all.length - 1][3] } : null;
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
    // 1:1 means the WHOLE chain paints at scale 1, not just .xterm-screen: a fit/pan transform on an
    // ancestor scales the painted screen without touching the screen's own computed transform.
    let eff = 1;
    for (let el = scr; el && el !== host && el.parentElement; el = el.parentElement) {
      const t = getComputedStyle(el).transform;
      if (t && t !== 'none') {
        const m = t.match(/matrix\(([^)]+)\)/);
        if (m) { const p = m[1].split(',').map(Number); eff *= Math.abs(p[0]); }
      }
    }
    const g = (n) => { try { return eval(n); } catch (e) { return undefined; } };
    const d = (window.__d && window.__d.length) ? window.__d[window.__d.length - 1] : null;
    const sr = scr ? scr.getBoundingClientRect() : null;
    const active = document.activeElement;
    return {
      cols: g('term.cols'), rows: g('term.rows'), fontSize: g('term.options.fontSize'),
      transform: tf, sx, sy, eff,
      winSize: g('winSize'), tabOwned: g('sizeTabOwned'), pinned: g('sizePinned'),
      mine: g('sizeMine'), modeLabel: g('sizeModeLabel'), mode: g('tabFitMode'),
      gridOwner: d && d.gridOwner, gridEpoch: d && d.gridEpoch,
      chip: (document.querySelector('#sizebtn') || {}).textContent || '',
      dFrames: (window.__d || []).length,
      resizeSends: (window.__sent || []).filter((f) => f && f.t === 'resize').length,
      hostW: host ? host.clientWidth : 0,
      screenW: sr ? Math.round(sr.width) : 0,
      pan: !!(host && host.classList.contains('pan')),
      focusedTerminal: !!(active && (active.classList.contains('xterm-helper-textarea') || (active.closest && active.closest('#term')))),
      build: window.__muxBuild || '',
      wsState: (() => { try { return typeof ws !== 'undefined' && ws ? ws.readyState : -1; } catch (e) { return -1; } })(),
    };
  });
}

async function installFrameTap(page) {
  await page.evaluate(() => {
    window.__d = []; window.__dTimes = []; window.__dMark = 0;
    window.__sent = []; window.__sentRaw = [];
    window.__t0 = 0; window.__commitAt = 0; window.__paintAt = 0; window.__commitCols = 0;
    window.__fontLog = []; window.__healCalls = 0;
    window.__mark = () => {
      window.__t0 = performance.now(); window.__commitAt = 0; window.__paintAt = 0;
      window.__fontLog = []; window.__healCalls = 0;
      window.__dMark = window.__d.length;
      window.__sentMark = window.__sent.length;
      window.__sentRawMark = window.__sentRaw.length;
    };
    let lastFont = null;
    setInterval(() => {
      try {
        const f = term && term.options ? term.options.fontSize : null;
        if (f !== lastFont) { lastFont = f; window.__fontLog.push({ t: Math.round(performance.now() - (window.__t0 || performance.now())), f }); }
        if (window.__commitAt && !window.__paintAt && term && term.cols === window.__commitCols) {
          window.__paintAt = Math.round(performance.now() - window.__t0);
        }
      } catch (_) {}
    }, 25);
    const wrap = (name, counter) => {
      try {
        const orig = window[name];
        if (typeof orig !== 'function' || orig.__wrapped) return;
        const w = function (...a) { window[counter] = (window[counter] || 0) + 1; return orig.apply(this, a); };
        w.__wrapped = true; window[name] = w;
      } catch (_) {}
    };
    wrap('schedulePaintHeal', '__healCalls');
    const grab = () => {
      try {
        if (typeof ws === 'undefined' || !ws) return false;
        if (!ws.__tapRecv) {
          ws.__tapRecv = true;
          ws.addEventListener('message', (e) => {
            const s = typeof e.data === 'string' ? e.data : '';
            if (s && s[0] === 'd') {
              let m = null; try { m = JSON.parse(s.slice(1)); } catch (_) {}
              if (m) {
                window.__d.push(m);
                window.__dTimes.push(Math.round(performance.now()));
                if (m.gridOwner === 'mine' && window.__t0 && !window.__commitAt) {
                  window.__commitAt = Math.round(performance.now() - window.__t0);
                  window.__commitCols = m.cols;
                }
              }
            }
          });
        }
        if (!ws.__tapSend) {
          const send = ws.send.bind(ws);
          ws.__tapSend = true;
          ws.send = (payload) => {
            try {
              if (typeof payload === 'string') {
                if (payload[0] === '{') window.__sent.push(JSON.parse(payload));
                else window.__sentRaw.push(payload.slice(0, 64));
              }
            } catch (_) {}
            return send(payload);
          };
        }
        return true;
      } catch (_) { return false; }
    };
    grab();
    window.__tap = setInterval(grab, 200);
  });
}

async function mark(page) { await page.evaluate(() => window.__mark && window.__mark()); }

async function timings(page, settleMs = 1500) {
  await page.waitForTimeout(settleMs);
  return page.evaluate(() => {
    const from = window.__dMark || 0;
    const times = (window.__dTimes || []).slice(from);
    let maxGap = 0;
    for (let i = 1; i < times.length; i += 1) maxGap = Math.max(maxGap, times[i] - times[i - 1]);
    const span = times.length > 1 ? times[times.length - 1] - times[0] : 0;
    return {
      commitMs: window.__commitAt, paintMs: window.__paintAt,
      fonts: (window.__fontLog || []).length, fontSizes: (window.__fontLog || []).map((x) => x.f),
      healCalls: window.__healCalls,
      dFrames: times.length,
      fps: span > 0 ? Math.round((times.length - 1) * 1000 / span) : 0,
      maxGap,
      vSends: (window.__sent || []).slice(window.__sentMark || 0).filter((f) => f && f.t === 'v').length,
      resizeSends: (window.__sent || []).slice(window.__sentMark || 0).filter((f) => f && f.t === 'resize').length,
      pSends: (window.__sentRaw || []).slice(window.__sentRawMark || 0).filter((s) => s && s[0] === 'P').length,
    };
  });
}

async function clickChip(page) {
  let idle = true;
  try {
    await page.waitForFunction(() => { try { return !_gridBusy; } catch (e) { return true; } }, null, { timeout: 10000 });
  } catch (e) { idle = false; }
  await page.evaluate(() => document.querySelector('#sizebtn').click());
  await page.waitForTimeout(700);
  return idle;
}

async function waitForOwner(page, want, timeoutMs, predicate) {
  const end = Date.now() + timeoutMs;
  let s = await readState(page);
  while (Date.now() < end) {
    if (s.gridOwner === want && (!predicate || predicate(s))) return s;
    await page.waitForTimeout(250);
    s = await readState(page);
  }
  return s;
}

(async () => {
  if (!COOKIE) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const pairs = cookiePairs(COOKIE);
  if (!pairs.length) { console.error('MUX_COOKIE has no cookie pairs'); process.exit(2); }

  const browser = await chromium.launch({ headless: !HEADFUL, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({
    viewport: { width: 1440, height: 900 },
    locale: 'en-US',
    userAgent: UA,
  });
  await ctx.addCookies(pairs.map((c) => ({
    name: c.name, value: c.value, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax',
  })));
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(String((e && e.message) || e)));
  const url = `${BASE}/?s=${encodeURIComponent(SESSION)}`;
  const resp = await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await installFrameTap(page);
  try {
    await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1; } catch (e) { return false; } }, null, { timeout: 30000 });
  } catch (e) {
    const diag = await page.evaluate(() => ({
      href: location.href, title: document.title,
      text: (document.body && document.body.innerText || '').slice(0, 300),
      hasTerm: typeof term !== 'undefined', ready: document.readyState,
    })).catch(() => null);
    console.error('ATTACH DIAG ' + JSON.stringify({ status: resp && resp.status(), errors, diag }));
    await browser.close();
    process.exit(1);
  }
  await page.waitForTimeout(2500);

  let s = await readState(page);
  check('L0 the deployed page is the grid-ownership build and attaches',
    resp && resp.status() === 200 && s.build === BUILD && s.cols > 10 && s.rows > 5 && errors.length === 0,
    `status=${resp && resp.status()} build=${s.build} term=${s.cols}x${s.rows} ws=${s.wsState} errors=${errors.length}`);
  if (!(s.cols > 10)) { await browser.close(); process.exit(1); }
  if (s.build !== BUILD) check('L0 the loaded page is the build under proof', false, `build=${s.build} want=${BUILD}`);

  await mark(page);
  const quiet = await timings(page, 2000);
  check('L0 quiet while local: no resize traffic, no request, no storm',
    quiet.dFrames <= 2 && quiet.vSends === 0 && quiet.pSends === 0,
    `dFrames=${quiet.dFrames} vSends=${quiet.vSends} pSends=${quiet.pSends} healCalls=${quiet.healCalls}`);

  const HOST_COLS = s.cols, HOST_ROWS = s.rows, HOST_EPOCH = s.gridEpoch;
  check('L1 local ownership is the default',
    s.gridOwner === 'local' && Math.abs(s.sx - 1) < 1e-6 && Math.abs(s.sy - 1) < 1e-6 && Math.abs(s.eff - 1) < 0.005,
    `gridOwner=${s.gridOwner} term=${s.cols}x${s.rows} epoch=${s.gridEpoch} transform=${s.transform} effScale=${s.eff.toFixed(3)} chip=${JSON.stringify(s.chip)}`);

  // fill -> fit is rendering only. Step once if we start at fill.
  let mode = s.mode;
  if (mode === 'fill') { await clickChip(page); mode = (await readState(page)).mode; }
  await page.waitForTimeout(1000);
  const fit = await readState(page);
  check('L1 fit renders without taking the grid',
    fit.mode === 'fit' && fit.gridOwner === 'local' && fit.cols === HOST_COLS && fit.rows === HOST_ROWS && fit.resizeSends === 0,
    `mode=${fit.mode} gridOwner=${fit.gridOwner} term=${fit.cols}x${fit.rows} epoch=${fit.gridEpoch} resizeSends=${fit.resizeSends} chip=${JSON.stringify(fit.chip)}`);

  // --- take -------------------------------------------------------------------------------------
  const tee0 = teeMark();
  await mark(page);
  await page.evaluate(() => { const b = document.querySelector('#sizebtn'); b.click(); b.click(); });
  const pinned = await waitForOwner(page, 'mine', 20000, (st) => st.cols !== HOST_COLS);
  const takeT = await timings(page, 1500);
  const teeTake = teeSince(tee0);
  const resized = lastTeeResize(teeTake);
  check('L2 the tap takes the real grid',
    pinned.gridOwner === 'mine' && pinned.cols !== HOST_COLS,
    `gridOwner=${pinned.gridOwner} term=${pinned.cols}x${pinned.rows} pane=${HOST_COLS}x${HOST_ROWS} epoch=${pinned.gridEpoch} (was ${HOST_EPOCH}) chip=${JSON.stringify(pinned.chip)}`);
  check('L2 a double tap is exactly one grid request', takeT.pSends === 1, `pSends=${takeT.pSends}`);
  check('L2 the take lands fast and settles',
    takeT.commitMs > 0 && takeT.commitMs < 2500 && takeT.fonts <= 3,
    `commitMs=${takeT.commitMs} paintMs=${takeT.paintMs} dFrames=${takeT.dFrames} fps=${takeT.fps} maxGap=${takeT.maxGap} fonts=${takeT.fonts}${JSON.stringify(takeT.fontSizes)}`);
  check('L2 the tee resized its child pty to the acked grid',
    !!resized && resized.cols === pinned.cols && resized.rows === pinned.rows,
    resized ? `pty=${resized.cols}x${resized.rows} epoch=${resized.epoch} acked=${pinned.cols}x${pinned.rows} epoch=${pinned.gridEpoch}` : `no pty resize in the tee log (${JSON.stringify(teeTake.slice(-200).replace(/\s+/g, ' '))})`);

  // A commit can land a frame or two before the layout has re-laid, so this asserts the SETTLED paint:
  // it may not remain anything but 1:1 for longer than a bounded settle.
  let laid = await readState(page);
  for (let i = 0; i < 8; i += 1) {
    const sp = laid.hostW ? laid.screenW / laid.hostW : 0;
    if (sp > 0.98 && sp <= 1.02 && Math.abs(laid.eff - 1) < 0.005) break;
    await page.waitForTimeout(200);
    laid = await readState(page);
  }
  const span = laid.hostW ? laid.screenW / laid.hostW : 0;
  check('L3 the owned grid paints 1:1 with no distortion',
    laid.gridOwner === 'mine' && Math.abs(laid.sx - 1) < 1e-6 && Math.abs(laid.sy - 1) < 1e-6
    && Math.abs(laid.eff - 1) < 0.005
    && span > 0.98 && span <= 1.02 && !laid.pan && Number.isInteger(laid.fontSize),
    `gridOwner=${laid.gridOwner} transform=${laid.transform} effScale=${laid.eff.toFixed(3)} span=${span.toFixed(3)} screenW=${laid.screenW} hostW=${laid.hostW} pan=${laid.pan} font=${laid.fontSize} term=${laid.cols}x${laid.rows}`);

  // --- a viewport change while owning is a new grid ---------------------------------------------
  const tee1 = teeMark();
  const beforeCols = laid.cols;
  await mark(page);
  await page.setViewportSize({ width: 1180, height: 780 });
  const moved = await waitForOwner(page, 'mine', 20000, (st) => st.cols !== beforeCols);
  const moveT = await timings(page, 1200);
  const teeMove = teeSince(tee1);
  const resized2 = lastTeeResize(teeMove);
  check('L3 a viewport change while owning commits a new grid',
    moved.gridOwner === 'mine' && moved.cols !== beforeCols && moved.gridEpoch !== pinned.gridEpoch,
    `was=${beforeCols}x${laid.rows} now=${moved.cols}x${moved.rows} epoch=${moved.gridEpoch} commitMs=${moveT.commitMs}`);
  check('L3 the pty follows the new viewport',
    !!resized2 && resized2.cols === moved.cols && resized2.rows === moved.rows,
    resized2 ? `pty=${resized2.cols}x${resized2.rows} epoch=${resized2.epoch}` : 'no second pty resize');

  // --- release -----------------------------------------------------------------------------------
  const tee2 = teeMark();
  await mark(page);
  await clickChip(page);
  const released = await waitForOwner(page, 'local', 20000, (st) => st.cols === HOST_COLS);
  const relT = await timings(page, 1500);
  const teeRel = teeSince(tee2);
  const restored = lastPaneRestore(teeRel);
  check('L4 release returns the grid to the pane',
    released.gridOwner === 'local' && released.cols === HOST_COLS && released.rows === HOST_ROWS
    && Math.abs(released.sx - 1) < 1e-6 && Math.abs(released.sy - 1) < 1e-6,
    `gridOwner=${released.gridOwner} term=${released.cols}x${released.rows} epoch=${released.gridEpoch} transform=${released.transform} chip=${JSON.stringify(released.chip)}`);
  check('L4 the tee restored the pane geometry',
    !!restored && restored.cols === HOST_COLS && restored.rows === HOST_ROWS,
    restored ? `pane=${restored.cols}x${restored.rows} epoch=${restored.epoch}` : 'no pane restore in the tee log');
  check('L4 the release settles without storm', relT.fonts <= 3 && relT.dFrames <= 8,
    `dFrames=${relT.dFrames} fonts=${relT.fonts} vSends=${relT.vSends} resizeSends=${relT.resizeSends}`);

  // --- local ownership absorbs a viewport change as pure rendering --------------------------------
  await mark(page);
  await page.setViewportSize({ width: 1320, height: 860 });
  await page.waitForTimeout(1800);
  const after = await readState(page);
  const afterT = await timings(page, 400);
  check('L4 after release a viewport change stays pure rendering',
    after.gridOwner === 'local' && after.cols === HOST_COLS && after.rows === HOST_ROWS
    && afterT.resizeSends === 0 && afterT.pSends === 0,
    `gridOwner=${after.gridOwner} term=${after.cols}x${after.rows} resizeSends=${afterT.resizeSends} pSends=${afterT.pSends} dFrames=${afterT.dFrames}`);

  // --- a second device takes the grid; the first must paint the ACKED grid, never its own stale one --
  // This is the tab-2a3c failure in its general form: a viewer holding an older geometry must not paint
  // it while another device owns the grid. The follower has to repaint from the commit, at 1:1.
  const ctxB = await browser.newContext({ viewport: { width: 1024, height: 700 }, locale: 'en-US', userAgent: UA });
  await ctxB.addCookies(pairs.map((c) => ({
    name: c.name, value: c.value, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax',
  })));
  const pageB = await ctxB.newPage();
  await pageB.goto(url, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await installFrameTap(pageB);
  await pageB.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1; } catch (e) { return false; } }, null, { timeout: 30000 });
  await pageB.waitForTimeout(2000);
  const bLocal = await readState(pageB);
  const aBefore = await readState(page);

  await mark(pageB);
  await pageB.evaluate(() => { const b = document.querySelector('#sizebtn'); b.click(); b.click(); });
  const bOwn = await waitForOwner(pageB, 'mine', 20000, (st) => st.cols < 209);
  const bT = await timings(pageB, 1200);
  const aFollowed = await waitForOwner(page, 'mine', 12000, (st) => st.cols === bOwn.cols && st.gridEpoch === bOwn.gridEpoch);
  const aAfter = await readState(page);
  check('L5 a second device takes the grid at its own viewport',
    bOwn.gridOwner === 'mine' && bOwn.cols < 209 && bOwn.cols !== bLocal.cols,
    `deviceB=${bOwn.cols}x${bOwn.rows} epoch=${bOwn.gridEpoch} was=${bLocal.cols}x${bLocal.rows} commitMs=${bT.commitMs}`);
  // A viewer that is not the pinning device reads gridOwner 'other'; only the owner reads 'mine'. What
  // matters here is that A's paint is the ACKED grid at 1:1, not that it holds the lease.
  check('L5 the first viewer repaints the acked grid, not its stale one',
    (aFollowed.gridOwner === 'mine' || aFollowed.gridOwner === 'other')
    && aFollowed.cols === bOwn.cols && aFollowed.rows === bOwn.rows && aFollowed.gridEpoch === bOwn.gridEpoch
    && Math.abs(aAfter.sx - 1) < 1e-6 && Math.abs(aAfter.sy - 1) < 1e-6 && Math.abs(aAfter.eff - 1) < 0.005,
    `viewerA=${aFollowed.cols}x${aFollowed.rows} epoch=${aFollowed.gridEpoch} owner=${aFollowed.gridOwner} acked=${bOwn.cols}x${bOwn.rows} epoch=${bOwn.gridEpoch} `
    + `(A held ${aBefore.cols}x${aBefore.rows}) transform=${aAfter.transform} effScale=${aAfter.eff.toFixed(3)}`);

  // The owning device leaving is a release path: the grid must come back to the pane by itself.
  await ctxB.close();
  const aHome = await waitForOwner(page, 'local', 20000, (st) => st.cols === HOST_COLS);
  check('L5 the owning device disconnecting returns the grid to the pane',
    aHome.gridOwner === 'local' && aHome.cols === HOST_COLS && aHome.rows === HOST_ROWS,
    `gridOwner=${aHome.gridOwner} term=${aHome.cols}x${aHome.rows} epoch=${aHome.gridEpoch}`);

  await browser.close();
  const failed = rows.filter((r) => !r.ok).map((r) => r.id);
  console.log(failed.length ? `LIVE DEPLOYED FAIL: ${failed.join(', ')}` : 'LIVE DEPLOYED PASS');
  process.exitCode = failed.length ? 1 : 0;
})().catch((e) => { console.error('LIVE DEPLOYED ERROR', e && (e.stack || e.message)); process.exit(2); });
