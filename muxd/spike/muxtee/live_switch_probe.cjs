'use strict';
/* Two LOCAL teed grids in one viewer: walk A's chip to TAKING the grid, SWITCH the page to tab B (the same
 * call the tab strip makes), take B there, then switch back to A and release. Proves a switch starts the
 * new tab from its own grid - its own row, its own ownership, its own fit - instead of carrying A's pinned
 * state and geometry across.
 *
 * The chip is a LADDER on a teed tab: fills width -> fits rows -> this device (take) -> release. The probe
 * therefore walks it a tap at a time and reports each state, instead of assuming one tap means "take".
 */
const path = require('path');
function loadPlaywright() {
  const c = ['C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright', 'playwright'];
  let last; for (const x of c) { try { return require(x); } catch (e) { last = e; } } throw last;
}
const { chromium } = loadPlaywright();
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const A = process.env.MUX_LIVE_SESSION || 'hl-owner-session';
const B = process.env.MUX_LIVE_SESSION_B || 'hl-owner-session-b';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });
const rows = [];
const check = (name, ok, detail) => { rows.push(ok); console.log(`${ok ? 'PASS' : 'FAIL'} ${name} - ${detail}`); };

const STATE = () => {
  const g = n => { try { return eval(n); } catch (e) { return undefined; } };
  const host = document.querySelector('#term');
  const scr = host && host.querySelector('.xterm-screen');
  const row = (() => { try { return (window._sessions || []).find(s => s.name === current) || null; } catch (e) { return null; } })();
  return {
    session: g('current'), chip: ((document.querySelector('#sizebtn') || {}).textContent || '').replace(/\s+/g, ' ').trim(),
    owner: g('sizeGridOwner'), pinned: g('sizePinned'), mine: g('sizeMine'), tabOwned: g('sizeTabOwned'),
    mode: g('tabFitMode'), busy: g('_gridBusy'), paintReady: g('_paintReady'),
    cols: g('term.cols'), rows: g('term.rows'), pan: !!host && host.classList.contains('pan'),
    span: scr ? +(scr.getBoundingClientRect().width / host.clientWidth).toFixed(3) : null,
    rowCols: row && row.cols, rowRows: row && row.rows, rowKind: row && row.kind, rowOwner: row && row.owner,
  };
};

(async () => {
  if (!pairs.length) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  const errs = [];
  page.on('pageerror', e => errs.push(String(e && e.message || e)));
  await page.goto(`${BASE}/?s=${encodeURIComponent(A)}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });
  await page.waitForTimeout(2500);
  await page.evaluate(() => {
    window.__d = []; window.__sent = [];
    try {
      ws.addEventListener('message', (e) => {
        const s = typeof e.data === 'string' ? e.data : '';
        if (s[0] === 'd') { try { const m = JSON.parse(s.slice(1)); window.__d.push({ owner: m.gridOwner, cols: m.cols, rows: m.rows, pin: m.pinned, mode: m.mode, tabOwned: m.tabOwned }); if (window.__d.length > 40) window.__d.shift(); } catch (_) {} }
      });
      const send = ws.send.bind(ws);
      ws.send = (p) => { try { window.__sent.push(String(p).slice(0, 60)); if (window.__sent.length > 40) window.__sent.shift(); } catch (e) {} return send(p); };
    } catch (e) {}
  });
  const tap = async () => { await page.evaluate(() => document.querySelector('#sizebtn').click()); await page.waitForTimeout(2600); return page.evaluate(STATE); };
  const sw = async (name) => {
    await page.evaluate(n => connect(n), name);
    await page.waitForFunction(n => { try { return current === n && ws && ws.readyState === 1 && _paintReady; } catch (e) { return false; } }, name, { timeout: 20000 });
    await page.waitForTimeout(1500);
    return page.evaluate(STATE);
  };
  // Walk the ladder until this device owns the grid (or give up after four taps, reporting each rung).
  const take = async (label) => {
    const rungs = [];
    for (let i = 0; i < 4; i += 1) {
      const s = await tap();
      rungs.push(`${s.chip} | owner=${s.owner} term=${s.cols}x${s.rows}`);
      if (s.owner === 'mine') return { ok: true, s, rungs };
    }
    console.error(`${label} ladder: ${JSON.stringify(rungs)}`);
    console.error(`${label} frames: ${JSON.stringify(await page.evaluate(() => (window.__d || []).slice(-10)))}`);
    console.error(`${label} sent: ${JSON.stringify(await page.evaluate(() => (window.__sent || []).slice(-10)))}`);
    return { ok: false, s: await page.evaluate(STATE), rungs };
  };
  const ownerOf = async () => (await page.evaluate(STATE)).owner === 'mine' ? 'mine' : 'local';
  const release = async () => { const s = await tap(); return s; };

  // --- A: a local grid, and its chip is a ladder ------------------------------------------------
  const a0 = await page.evaluate(STATE);
  check('A attaches as a teed local grid', a0.session === A && a0.rowKind === 'local-tab' && a0.rowOwner === true && a0.owner === 'local',
    `session=${a0.session} kind=${a0.rowKind} rowOwner=${a0.rowOwner} owner=${a0.owner} term=${a0.cols}x${a0.rows}`);
  const aTake = await take('A');
  check('A walks the ladder to its own grid', aTake.ok,
    `rungs=${JSON.stringify(aTake.rungs)} owner=${aTake.s.owner} term=${aTake.s.cols}x${aTake.s.rows}`);

  // --- switch to B: B must start from ITS grid, not A's pin ------------------------------------
  const b0 = await sw(B);
  check('the switch to B starts from B\'s own grid, not A\'s pin',
    b0.session === B && b0.owner === 'local' && b0.pinned === false && b0.mine === false && b0.paintReady === true,
    `session=${b0.session} owner=${b0.owner} pinned=${b0.pinned} mine=${b0.mine} paintReady=${b0.paintReady} chip="${b0.chip}"`);
  check('B paints at its OWN row geometry, not the grid A had committed',
    b0.cols === b0.rowCols && b0.rows === b0.rowRows && !b0.pan,
    `page=${b0.cols}x${b0.rows} row=${b0.rowCols}x${b0.rowRows} span=${b0.span} pan=${b0.pan} mode=${b0.mode}`);

  const bTake = await take('B');
  check('B takes its own grid at its own commit', bTake.ok && bTake.s.span > 0.96 && bTake.s.span <= 1.02 && !bTake.s.pan
      && bTake.s.cols === bTake.s.rowCols && bTake.s.rows === bTake.s.rowRows,
    `rungs=${JSON.stringify(bTake.rungs)} term=${bTake.s.cols}x${bTake.s.rows} row=${bTake.s.rowCols}x${bTake.s.rowRows} span=${bTake.s.span} pan=${bTake.s.pan}`);

  // --- back to A: leaving A's viewer disconnected released its pin, so A is local again ---------
  const a2 = await sw(A);
  check('switching away released A back to its own pane', a2.session === A && a2.owner === 'local' && a2.pinned === false
      && a2.cols === a2.rowCols && a2.rows === a2.rowRows,
    `session=${a2.session} owner=${a2.owner} pinned=${a2.pinned} chip="${a2.chip}" term=${a2.cols}x${a2.rows} row=${a2.rowCols}x${a2.rowRows}`);

  // --- B is still the owner (we are not viewing it); go back and release it ----------------------
  const b2 = await sw(B);
  if (await ownerOf() === 'mine') await release();
  await page.waitForFunction(() => { try { const s=(window._sessions||[]).find(x=>x.name===current); return s && s.cols===term.cols && s.rows===term.rows; } catch(e){ return false; } }, null, { timeout: 8000 }).catch(() => {});
  const b3 = await page.evaluate(STATE);
  check('B releases too, leaving both tabs local', b3.owner === 'local' && !b3.pinned && b3.cols === b3.rowCols && !b3.pan,
    `owner=${b3.owner} pinned=${b3.pinned} chip="${b3.chip}" term=${b3.cols}x${b3.rows} row=${b3.rowCols}x${b3.rowRows}`);
  check('no page errors during the whole switch walk', errs.length === 0, `errors=${JSON.stringify(errs)}`);
  console.log(rows.every(Boolean) ? 'LIVE SWITCH PASS' : `LIVE SWITCH FAIL (${rows.filter(x => !x).length} rows)`);
  await ctx.close(); await browser.close();
  process.exit(rows.every(Boolean) ? 0 : 1);
})().catch(e => { console.error('SWITCH PROBE ERROR', e && (e.stack || e.message)); process.exit(2); });
