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
    const sr = scr ? scr.getBoundingClientRect() : null;
    const active = document.activeElement;
    return {
      cols: g('term.cols'), rows: g('term.rows'), fontSize: g('term.options.fontSize'),
      transform: tf, sx, sy,
      winSize: g('winSize'), tabOwned: g('sizeTabOwned'), pinned: g('sizePinned'),
      mine: g('sizeMine'), modeLabel: g('sizeModeLabel'), mode: g('tabFitMode'),
      gridOwner: d && d.gridOwner, gridEpoch: d && d.gridEpoch,
      chip: (document.querySelector('#sizebtn') || {}).textContent || '',
      dFrames: (window.__d || []).length,
      resizeSends: (window.__sent || []).filter((f) => f && f.t === 'resize').length,
      hostW: host ? host.clientWidth : 0,
      screenW: sr ? Math.round(sr.width) : 0,
      pan: !!(host && host.classList.contains('pan')),
      // focus is part of the contract: after any chip tap the keyboard must be back on the terminal
      focusedTerminal: !!(active && (active.classList.contains('xterm-helper-textarea') || (active.closest && active.closest('#term')))),
    };
  });
}

async function installFrameTap(page) {
  await page.evaluate(() => {
    window.__d = []; window.__dTimes = []; window.__dMark = 0;
    // What this viewer ASKS muxd for. Local ownership means no resize request, whatever the render
    // mode: a mode that reaches the wire has already taken the grid.
    window.__sent = [];
    // Timing and churn: a switch is only good if it LANDS FAST and SETTLES. State-only assertions let a
    // 4-second, font-thrashing handover pass, so the harness records when the commit arrived, when the
    // grid was painted, and how many times the page changed its font on the way.
    window.__t0 = 0; window.__commitAt = 0; window.__paintAt = 0;
    window.__fontLog = []; window.__fitCalls = 0; window.__alignCalls = 0; window.__healCalls = 0;
    window.__mark = () => {
      window.__t0 = performance.now(); window.__commitAt = 0; window.__paintAt = 0;
      window.__fontLog = []; window.__fitCalls = 0; window.__alignCalls = 0; window.__healCalls = 0;
      window.__dMark = window.__d.length;
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
    wrap('fitTabOwnedGrid', '__fitCalls');
    wrap('alignTerminalSurface', '__alignCalls');
    wrap('schedulePaintHeal', '__healCalls');
    // ONE listener per socket. The old version re-added on every 200 ms grab(), so a single 'd' frame
    // was counted once per accumulated listener - dFrames read 77 when the true number was a handful.
    // That made a quiet page look like a repaint storm. Guard the receive tap exactly like the send tap.
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
              if (typeof payload === 'string' && payload[0] === '{') window.__sent.push(JSON.parse(payload));
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
    // Per-window churn: dFrames is the delta since mark(), fps is that over the window, and maxGap
    // catches a page that answers a burst then goes silent for a long beat (the "sticky" feel).
    let maxGap = 0;
    for (let i = 1; i < times.length; i += 1) maxGap = Math.max(maxGap, times[i] - times[i - 1]);
    const span = times.length > 1 ? times[times.length - 1] - times[0] : 0;
    return {
      commitMs: window.__commitAt, paintMs: window.__paintAt,
      fonts: (window.__fontLog || []).length, fontSizes: (window.__fontLog || []).map((x) => x.f),
      fitCalls: window.__fitCalls, alignCalls: window.__alignCalls, healCalls: window.__healCalls,
      dFrames: times.length,
      fps: span > 0 ? Math.round((times.length - 1) * 1000 / span) : 0,
      maxGap,
      vSends: (window.__sent || []).filter((f) => f && f.t === 'v').length,
      resizeSends: (window.__sent || []).filter((f) => f && f.t === 'resize').length,
    };
  });
}

// The chip is a one-way ladder while the pane owns the grid: fill -> fit -> TAKE THE GRID. There is no
// 'flex' mode any more (the transform is deleted), so the proof must not click in a loop - each extra
// click is a pin or a release, and a loop turns the ownership it is trying to prove into a flap.
async function clickChip(page) {
  await page.evaluate(() => document.querySelector('#sizebtn').click());
  await page.waitForTimeout(700);
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

    // A quiet window is the baseline: any steady stream of 'd' frames means the page is repainting for
    // no reason, and a steady stream of 'v' sends means the page and the relay are resizing each other.
    await mark(page);
    const quiet = await timings(page, 1500);
    check('V0 quiet window is quiet', quiet.dFrames <= 2 && quiet.vSends === 0,
      `dFrames=${quiet.dFrames} vSends=${quiet.vSends} fitCalls=${quiet.fitCalls} healCalls=${quiet.healCalls}`);

    const localOwner = (s.gridOwner === 'local') || (s.gridOwner === undefined && s.tabOwned === true);
    check('V1 local ownership', localOwner && Math.abs(s.sx - 1) < 1e-6 && Math.abs(s.sy - 1) < 1e-6,
      `gridOwner=${s.gridOwner} tabOwned=${s.tabOwned} transform=${s.transform} term=${s.cols}x${s.rows} host=${HOST_COLS}x${HOST_ROWS}`);

    // Local ownership must survive a mode change: fill and fit are pure rendering, so neither may put a
    // resize on the wire nor move the grid off the pane's. The ladder is one-way (fill -> fit -> take),
    // so only step to fit from fill.
    const modeRow = async (mode, at) => {
      await page.waitForTimeout(1200);
      const m = await readState(page);
      check(`V1 ${mode} renders without taking the grid`,
        at === mode && m.gridOwner === 'local' && m.cols === HOST_COLS && m.rows === HOST_ROWS
        && m.resizeSends === 0 && Math.abs(m.sx - 1) < 1e-6 && Math.abs(m.sy - 1) < 1e-6,
        `mode=${m.mode} gridOwner=${m.gridOwner} term=${m.cols}x${m.rows} `
        + `host=${HOST_COLS}x${HOST_ROWS} resizeSends=${m.resizeSends} transform=${m.transform}`);
    };
    let mode = s.mode;
    if (mode === 'fill') {
      await modeRow('fill', mode);
      await clickChip(page);
      mode = (await readState(page)).mode;
    }
    await modeRow('fit', mode);

    // The next tap is the handover, not a rendering mode. Wait for the ack-published owner: the page may
    // not paint the new grid before muxd commits it (contract G2).
    await mark(page);
    await clickChip(page);
    const pinned = await waitForOwner(page, 'mine', 15000);
    const takeT = await timings(page, 1200);
    check('V2 the chip takes the grid', pinned.gridOwner === 'mine' && pinned.cols !== s.cols,
      `gridOwner=${pinned.gridOwner} mine=${pinned.mine} term=${pinned.cols}x${pinned.rows} `
      + `host=${HOST_COLS}x${HOST_ROWS} chip=${JSON.stringify(pinned.chip)}`);
    check('V2 the take lands quickly', takeT.commitMs > 0 && takeT.commitMs < 1500,
      `commitMs=${takeT.commitMs} paintMs=${takeT.paintMs} dFrames=${takeT.dFrames} vSends=${takeT.vSends}`);
    check('V2 the take settles without font thrash', takeT.fonts <= 3,
      `fonts=${takeT.fonts} sizes=${JSON.stringify(takeT.fontSizes)} fitCalls=${takeT.fitCalls} `
      + `healCalls=${takeT.healCalls}`);
    check('V3 no distortion while owned', pinned.gridOwner === 'mine' && Math.abs(pinned.sx - 1) < 1e-6 && Math.abs(pinned.sy - 1) < 1e-6,
      `gridOwner=${pinned.gridOwner} transform=${pinned.transform}`);
    // The owning device must paint its grid ACROSS the viewport, not left-aligned with a bezel on the
    // right: at 1:1 the painted screen spans the host on both axes, so the widths must agree closely.
    const laid = await readState(page);
    const span = laid.hostW ? laid.screenW / laid.hostW : 0;
    check('V3 the owned grid spans the viewport', laid.gridOwner === 'mine' && span > 0.98 && span <= 1.02 && !laid.pan,
      `screenW=${laid.screenW} hostW=${laid.hostW} span=${span.toFixed(3)} pan=${laid.pan}`);
    check('V3 the keyboard is still on the terminal', laid.focusedTerminal,
      `focusedTerminal=${laid.focusedTerminal}`);

    await mark(page);
    await clickChip(page);
    const released = await waitForOwner(page, 'local', 15000);
    const relT = await timings(page, 800);
    check('V4 release returns to local', released.gridOwner === 'local'
      && released.cols === HOST_COLS && released.rows === HOST_ROWS,
      `gridOwner=${released.gridOwner} term=${released.cols}x${released.rows} `
      + `host=${HOST_COLS}x${HOST_ROWS} chip=${JSON.stringify(released.chip)}`);
    check('V4 the release stays quick and quiet', relT.fonts <= 3,
      `commitMs=${relT.commitMs} paintMs=${relT.paintMs} fonts=${relT.fonts} `
      + `sizes=${JSON.stringify(relT.fontSizes)} fitCalls=${relT.fitCalls}`);

    // V6: switching is repeatable. A handover that works once and then leaves the chip confused is a
    // worse product than one that never worked, so the cycle is driven three times and every landing is
    // asserted, not just the first.
    let cycles = 0;
    const cycleFailures = [];
    for (let i = 0; i < 3; i += 1) {
      await clickChip(page);
      const took = await waitForOwner(page, 'mine', 15000);
      if (took.gridOwner !== 'mine') { cycleFailures.push(`take@${i}:${took.gridOwner}`); continue; }
      await clickChip(page);
      const back = await waitForOwner(page, 'local', 15000,
        (st) => st.cols === HOST_COLS && st.rows === HOST_ROWS);
      if (back.gridOwner !== 'local' || back.cols !== HOST_COLS) {
        cycleFailures.push(`release@${i}:${back.gridOwner}@${back.cols}x${back.rows}`);
        continue;
      }
      cycles += 1;
    }
    check('V6 take and release repeat', cycles === 3,
      `cycles=${cycles}/3 failures=${cycleFailures.join(',') || 'none'}`);

    // V5: the owning device simply goes away. The relay must release the grid, not wait for muxd's TTL -
    // contract section 5's disconnect arm. A fresh context is a different device, so what it reads is
    // what the relay published after the owner left.
    await clickChip(page);                      // fit -> take again
    const reowned = await waitForOwner(page, 'mine', 15000);
    check('V5 the grid is taken once more', reowned.gridOwner === 'mine',
      `gridOwner=${reowned.gridOwner} term=${reowned.cols}x${reowned.rows}`);
    await ctx.close();
    const ctx2 = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'en-US' });
    const page2 = await ctx2.newPage();
    page2.on('pageerror', (e) => errors.push(String((e && e.message) || e)));
    await page2.goto(url, { waitUntil: 'domcontentloaded', timeout: 60000 });
    await installFrameTap(page2);
    await page2.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1; } catch (e) { return false; } }, null, { timeout: 30000 });
    // Ownership and geometry are two commits on two frames: the relay drops the pin at once (local, old
    // grid) and muxd publishes the pane geometry when the tee acks the restore. The row is about the
    // grid coming back, so it waits for both.
    const away = await waitForOwner(page2, 'local', 15000,
      (st) => st.cols === HOST_COLS && st.rows === HOST_ROWS);
    check('V5 owner disconnect returns the grid to local', away.gridOwner === 'local'
      && away.cols === HOST_COLS && away.rows === HOST_ROWS,
      `gridOwner=${away.gridOwner} term=${away.cols}x${away.rows} host=${HOST_COLS}x${HOST_ROWS} `
      + `chip=${JSON.stringify(away.chip)}`);

    await page2.screenshot({ path: path.join(__dirname, 'grid-viewer-proof.png'), fullPage: false });
    await ctx2.close();
    const failed = rows.filter((r) => !r.ok).map((r) => r.id);
    console.log(failed.length ? `VIEWER PROOF FAIL: ${failed.join(', ')}` : 'VIEWER PROOF PASS');
    process.exitCode = failed.length ? 1 : 0;
    return process.exitCode;
  } finally {
    try { await ctx.close(); } catch (_) {}     // V5 may already have closed it on purpose
    await browser.close();
  }
})().catch((e) => { console.error('ASSERT ERROR', e && (e.stack || e.message)); process.exit(2); });
