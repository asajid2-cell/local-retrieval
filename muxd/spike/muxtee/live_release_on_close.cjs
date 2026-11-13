'use strict';
/* One viewer takes the grid, then DISCONNECTS. Does the pane get it back, and how long does it take?
 * Prints the row (gridOwner/label/cols/rows) before, right after the close, and once a second until it is
 * local again - so a TTL backstop is distinguishable from the immediate release path. */
const path = require('path');
function loadPlaywright() { const c = ['C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright', 'playwright']; let l; for (const x of c) { try { return require(x); } catch (e) { l = e; } } throw l; }
const { chromium } = loadPlaywright();
const { assertSession } = require('./probe_guard.cjs');
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.env.MUX_SESSION || 'hl-owner-session';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });

async function open(browser, label) {
  const ctx = await browser.newContext({ viewport: { width: 1200, height: 780 }, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  await page.goto(`${BASE}/?s=${encodeURIComponent(S)}&dev=${label}&label=${label}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });
  await assertSession(page, S);   // fail closed: never type into a pane this probe was not given
  await page.waitForTimeout(2500);
  return { ctx, page, label };
}
const STATE = () => {
  const g = n => { try { return eval(n); } catch (e) { return undefined; } };
  const row = (() => { try { return (window._sessions || []).find(s => s.name === current) || null; } catch (e) { return null; } })();
  return { owner: g('sizeGridOwner'), pinned: g('sizePinned'), mine: g('sizeMine'), busy: g('_gridBusy'),
    cols: g('term.cols'), rows: g('term.rows'), chip: ((document.querySelector('#sizebtn') || {}).textContent || '').replace(/\s+/g, ' ').trim(),
    rowCols: row && row.cols, rowRows: row && row.rows };
};

(async () => {
  if (!pairs.length) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  // MUX_WATCHER=1 reproduces the harness case: a SECOND device keeps watching while the pinning device
  // leaves. That is the row that failed on the deployed run, and the single-viewer case passes, so the
  // difference has to be this.
  const WATCHER = process.env.MUX_WATCHER === '1';
  const A = await open(browser, 'releaseA');
  const W = WATCHER ? await open(browser, 'watchW') : null;
  for (let i = 0; i < 4; i += 1) {
    const s = await A.page.evaluate(STATE);
    if (s.owner === 'mine') break;
    await A.page.evaluate(() => document.querySelector('#sizebtn').click());
    await A.page.waitForTimeout(1400);
  }
  const took = await A.page.evaluate(STATE);
  console.log('taken ' + JSON.stringify(took));
  const t0 = Date.now();
  await A.ctx.close();
  // Watch from a SECOND viewer: after the pinning device is gone, the committed grid must return to the pane.
  const B = WATCHER ? W : await open(browser, 'watchB');
  console.log(`afterClose +${Date.now() - t0}ms (watcher=${WATCHER}) ` + JSON.stringify(await B.page.evaluate(STATE)));
  let last = null;
  for (let i = 0; i < 30; i += 1) {
    const s = await B.page.evaluate(STATE);
    if (s.owner === 'local' && s.cols === s.rowCols && s.rows === s.rowRows) {
      console.log(`released +${Date.now() - t0}ms ` + JSON.stringify(s));
      last = s;
      break;
    }
    if (JSON.stringify(s) !== JSON.stringify(last)) { console.log(`t+${Date.now() - t0}ms ` + JSON.stringify(s)); last = s; }
    await B.page.waitForTimeout(1000);
  }
  if (!last) console.log(`NEVER RELEASED within 30s ` + JSON.stringify(await B.page.evaluate(STATE)));
  console.log(last ? 'RELEASE-ON-CLOSE PASS' : 'RELEASE-ON-CLOSE FAIL');
  await B.ctx.close(); if (W && W !== B) await W.ctx.close(); await browser.close();
  process.exit(last ? 0 : 1);
})().catch(e => { console.error('RELEASE PROBE ERROR', e && (e.stack || e.message)); process.exit(2); });
