'use strict';
/* Two real viewers on one teed tab: does the second device take the grid, does the first become a
 * viewer, and does closing the owner give the grid back? Prints each step. */
const path = require('path');
function loadPlaywright() {
  const c = ['C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright', 'playwright'];
  let last; for (const x of c) { try { return require(x); } catch (e) { last = e; } } throw last;
}
const { chromium } = loadPlaywright();
const { assertSession } = require('./probe_guard.cjs');
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const SESSION = process.env.MUX_LIVE_SESSION || 'hl-owner-session';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });

const STATE = () => {
  const g = n => { try { return eval(n); } catch (e) { return undefined; } };
  return { chip: ((document.querySelector('#sizebtn') || {}).textContent || '').replace(/\s+/g, ' ').trim(),
    owner: g('sizeGridOwner'), pinned: g('sizePinned'), mine: g('sizeMine'), tabOwned: g('sizeTabOwned'),
    busy: g('_gridBusy'), cols: g('term.cols'), rows: g('term.rows'), font: g('term.options.fontSize') };
};

async function open(browser, w, h, label) {
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  await page.goto(`${BASE}/?s=${encodeURIComponent(SESSION)}&dev=${label}&label=${label}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  // Wait for the list too: the guard reads window._sessions and `current`, which arrive with it.
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });
  await assertSession(page, SESSION);   // fail closed: never type into a pane this probe was not given
  await page.waitForTimeout(2000);
  return { ctx, page, label };
}

const tap = (page) => page.evaluate(() => { document.querySelector('#sizebtn').click(); });

(async () => {
  if (!pairs.length) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const A = await open(browser, 1440, 900, 'probeA');
  const B = await open(browser, 1024, 700, 'probeB');
  const show = async (tag) => console.log(tag + ' A=' + JSON.stringify(await A.page.evaluate(STATE)) + ' B=' + JSON.stringify(await B.page.evaluate(STATE)));

  await show('attach');
  await tap(A.page); await A.page.waitForTimeout(2500); await show('A-tap1');
  await tap(A.page); await A.page.waitForTimeout(2500); await show('A-tap2');
  await tap(B.page); await B.page.waitForTimeout(2500); await show('B-tap1');
  console.log('B-logs ' + JSON.stringify(await B.page.evaluate(() => ({
    nd: (window.__ndLog || []).slice(-4), v: (window.__vLog || []).slice(-4),
    vf: (() => { try { return viewportFit; } catch (e) { return null; } })(),
    font: term.options.fontSize, applied: (() => { try { return appliedFont; } catch (e) { return null; } })(),
    hostW: document.querySelector('#term').clientWidth,
    span: (() => { try { const h = document.querySelector('#term'); const s = h.querySelector('.xterm-screen'); return +(s.getBoundingClientRect().width / h.clientWidth).toFixed(3); } catch (e) { return null; } })(),
  }))));
  await tap(B.page); await B.page.waitForTimeout(2500); await show('B-tap2');
  await B.ctx.close(); await A.page.waitForTimeout(3000); await show('B-closed');
  await tap(A.page); await A.page.waitForTimeout(2500); await show('A-tap3');
  await A.ctx.close();
  await browser.close();
})().catch(e => { console.error('TWO-VIEWER ERROR', e && (e.stack || e.message)); process.exit(2); });
