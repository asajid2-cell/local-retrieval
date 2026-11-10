'use strict';
/* Read-only diagnostic of the deployed page on one session: who owns the grid, what geometry the page
 * thinks it has, what it is actually painting, and whether the keyboard is focused. Prints one JSON blob.
 * Usage: MUX_SESSION=tab-6ad4 [MUX_W/H/DPR] node live_diag_probe.cjs */
const path = require('path');
function loadPlaywright() { const c = ['C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright', 'playwright']; let l; for (const x of c) { try { return require(x); } catch (e) { l = e; } } throw l; }
const { chromium } = loadPlaywright();
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.env.MUX_SESSION || 'hl-owner-session';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });
const W = Number(process.env.MUX_W || 1440), H = Number(process.env.MUX_H || 900), DPR = Number(process.env.MUX_DPR || 1);

const STATE = () => {
  const g = n => { try { return eval(n); } catch (e) { return undefined; } };
  const host = document.querySelector('#term');
  const scr = host && host.querySelector('.xterm-screen');
  const cs = host ? getComputedStyle(host) : null;
  const act = document.activeElement;
  const row = (() => { try { return (window._sessions || []).find(s => s.name === current) || null; } catch (e) { return null; } })();
  const cell = (() => { try { const c = term._core._renderService.dimensions.css.cell; return { w: +c.width.toFixed(3), h: +c.height.toFixed(3) }; } catch (e) { return null; } })();
  const m = (() => { try { return cellMetrics(); } catch (e) { return null; } })();
  return {
    session: g('current'), chip: ((document.querySelector('#sizebtn') || {}).textContent || '').replace(/\s+/g, ' ').trim(),
    build: g('window.__muxBuild'), owner: g('sizeGridOwner'), pinned: g('sizePinned'), mine: g('sizeMine'),
    tabOwned: g('sizeTabOwned'), busy: g('_gridBusy'), mode: g('tabFitMode'), epoch: g('sizeGridEpoch'),
    term: g('term') && { cols: g('term.cols'), rows: g('term.rows'), font: g('term.options.fontSize') },
    appliedFont: g('appliedFont'), viewportFit: g('viewportFit'), winSize: g('winSize'),
    row: row && { cols: row.cols, rows: row.rows, kind: row.kind, owner: row.owner, gridOwner: row.gridOwner, gridEpoch: row.gridEpoch },
    dpr: window.devicePixelRatio, innerW: window.innerWidth, innerH: window.innerHeight,
    host: host && { clientW: host.clientWidth, clientH: host.clientHeight, scrollW: host.scrollWidth, scrollH: host.scrollHeight,
      scrollLeft: host.scrollLeft, scrollTop: host.scrollTop, pan: host.classList.contains('pan'),
      padL: cs && cs.paddingLeft, padR: cs && cs.paddingRight },
    screen: scr && { cssW: +scr.getBoundingClientRect().width.toFixed(1), styleW: scr.style.width, styleH: scr.style.height,
      canvasW: (() => { const c = scr.querySelector('canvas'); return c ? c.width : null; })() },
    span: (scr && host) ? +(scr.getBoundingClientRect().width / host.clientWidth).toFixed(3) : null,
    cell, cellMetrics: m && { w: m.w, h: m.h },
    rowsDom: (() => { try { const r = host.querySelector('.xterm-rows'); return r ? { count: r.children.length, w: +r.getBoundingClientRect().width.toFixed(1) } : null; } catch (e) { return null; } })(),
    focus: { tag: act && act.tagName, cls: act && act.className, inTerm: !!(act && host && host.contains(act)) },
    guard: (() => { try { return mouseGuard.stats(); } catch (e) { return null; } })(),
    bar: g('window.__muxGridTiming'),
  };
};

(async () => {
  if (!pairs.length) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: W, height: H }, deviceScaleFactor: DPR, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  const errs = []; page.on('pageerror', e => errs.push(String(e && e.message || e)));
  await page.goto(`${BASE}/?s=${encodeURIComponent(S)}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });
  await page.waitForTimeout(3000);
  console.log(JSON.stringify({ probe: { S, W, H, DPR }, state: await page.evaluate(STATE), errors: errs }, null, 1));
  await ctx.close(); await browser.close();
})().catch(e => { console.error('DIAG ERROR', e && (e.stack || e.message)); process.exit(2); });
