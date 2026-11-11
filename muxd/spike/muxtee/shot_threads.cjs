'use strict';
/* Screenshot the phone thread view for visual review, on a REAL session with a real agent screen.
 * Read-only: switches to the thread view and captures; never types.
 * Usage: MUX_SESSION=tab-5bec node shot_threads.cjs */
const { chromium } = require('C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright');
const { assertSession } = require('./probe_guard.cjs');
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.env.MUX_SESSION || 'tab-5bec';
const OUT = process.env.MUX_OUT || 'threads-phone.png';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });
(async () => {
  if (!pairs.length) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({
    viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true,
    locale: 'en-US', userAgent: UA,
  });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  await page.goto(`${BASE}/?s=${encodeURIComponent(S)}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });
  await assertSession(page, S);
  await page.waitForTimeout(2500);
  await page.locator('[data-mobile-view="threads"]').click();
  await page.locator('#threads').waitFor({ state: 'visible' });
  await page.waitForTimeout(900);
  const info = await page.evaluate(() => ({
    build: window.__muxBuild,
    msgs: [...document.querySelectorAll('#threads .th-msg')].map(m => ({
      role: [...m.classList].find(c => c !== 'th-msg'),
      who: m.querySelector('.th-who').textContent,
      text: m.querySelector('.th-body').textContent.slice(0, 90),
    })),
    overflowX: document.documentElement.scrollWidth > window.innerWidth,
  }));
  console.log(JSON.stringify(info, null, 1));
  await page.screenshot({ path: OUT, fullPage: false });
  console.log('screenshot ' + OUT);
  await ctx.close(); await browser.close();
})().catch(e => { console.error('SHOT ERROR', e && (e.stack || e.message)); process.exit(2); });
