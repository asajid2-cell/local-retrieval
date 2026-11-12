'use strict';
/* Does ONE tap release the grid, and does the mirror it returns to keep the unreadable notice?
 * (The drive taps twice, which correctly release-then-retake - this isolates the release itself.)
 */
const { chromium } = require('C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright');
const { assertSession } = require('./probe_guard.cjs');
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.env.MUX_SESSION || 'tab-26f4';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });
(async () => {
  const b = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const c = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, isMobile: true, hasTouch: true, locale: 'en-US', userAgent: UA });
  await c.addCookies(pairs.map(x => ({ ...x, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const p = await c.newPage();
  await p.goto(`${BASE}/?s=${encodeURIComponent(S)}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await p.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });
  await assertSession(p, S);
  await p.waitForTimeout(3000);
  const st = () => p.evaluate(() => ({ cols: term.cols, font: term.options.fontSize, chip: (document.querySelector('#statusmeta') || {}).textContent,
    mine: typeof sizeMine !== 'undefined' && sizeMine, pinned: sizePinned, mode: typeof tabFitPaneMode !== 'undefined' ? tabFitPaneMode : null,
    notice: !!document.getElementById('narrowgrid'),
    clipped: (() => { const h = document.querySelector('#term'); const s = h.querySelector('.xterm-screen'); return s ? Math.round(s.getBoundingClientRect().width) > h.clientWidth + 1 : null; })() }));
  console.log('FOUND', JSON.stringify(await st()));
  await p.evaluate(() => { const e = document.querySelector('#statusmeta'); if (e) e.click(); });
  for (let i = 0; i < 40; i++) { await p.waitForTimeout(250); if ((await p.evaluate(() => term.cols)) < 120) break; }
  await p.waitForTimeout(1500);
  console.log('TAKEN', JSON.stringify(await st()));
  await p.evaluate(() => { const e = document.querySelector('#statusmeta'); if (e) e.click(); });
  await p.waitForTimeout(4000);
  console.log('RELEASED', JSON.stringify(await st()));
  await p.screenshot({ path: 'released-once.png' });
  await c.close(); await b.close();
})().catch(e => { console.error('ERR', e && (e.stack || e.message)); process.exit(2); });
