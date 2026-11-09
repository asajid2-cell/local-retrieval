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
    window.__d = [];
    // What this viewer ASKS muxd for. Local ownership means no resize request, whatever the render
    // mode: a mode that reaches the wire has already taken the grid.
    window.__sent = [];
    const grab = () => {
      try {
        if (typeof ws === 'undefined' || !ws) return false;
        ws.addEventListener('message', (e) => {
          const s = typeof e.data === 'string' ? e.data : '';
          if (s && s[0] === 'd') {
            try { window.__d.push(JSON.parse(s.slice(1))); } catch (_) {}
          }
        });
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
    await clickChip(page);
    const pinned = await waitForOwner(page, 'mine', 15000);
    check('V2 the chip takes the grid', pinned.gridOwner === 'mine' && pinned.cols !== s.cols,
      `gridOwner=${pinned.gridOwner} mine=${pinned.mine} term=${pinned.cols}x${pinned.rows} `
      + `host=${HOST_COLS}x${HOST_ROWS} chip=${JSON.stringify(pinned.chip)}`);
    check('V3 no distortion while owned', pinned.gridOwner === 'mine' && Math.abs(pinned.sx - 1) < 1e-6 && Math.abs(pinned.sy - 1) < 1e-6,
      `gridOwner=${pinned.gridOwner} transform=${pinned.transform}`);
    // The owning device must paint its grid ACROSS the viewport, not left-aligned with a bezel on the
    // right: at 1:1 the painted screen spans the host on both axes, so the widths must agree closely.
    await page.waitForTimeout(1200);
    const laid = await readState(page);
    const span = laid.hostW ? laid.screenW / laid.hostW : 0;
    check('V3 the owned grid spans the viewport', laid.gridOwner === 'mine' && span > 0.98 && span <= 1.02 && !laid.pan,
      `screenW=${laid.screenW} hostW=${laid.hostW} span=${span.toFixed(3)} pan=${laid.pan}`);
    check('V3 the keyboard is still on the terminal', laid.focusedTerminal,
      `focusedTerminal=${laid.focusedTerminal}`);

    await clickChip(page);
    const released = await waitForOwner(page, 'local', 15000);
    check('V4 release returns to local', released.gridOwner === 'local'
      && released.cols === HOST_COLS && released.rows === HOST_ROWS,
      `gridOwner=${released.gridOwner} term=${released.cols}x${released.rows} `
      + `host=${HOST_COLS}x${HOST_ROWS} chip=${JSON.stringify(released.chip)}`);

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
