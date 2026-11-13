'use strict';
/* Why does an owned grid sometimes paint wider than its host? Samples the numbers, repeatedly. */
const fs = require('fs');
const path = require('path');
function loadPlaywright() {
  const c = ['C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright', 'playwright'];
  let last; for (const x of c) { try { return require(x); } catch (e) { last = e; } } throw last;
}
const { chromium } = loadPlaywright();
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = 'https://harmonizerlabs.cc/multiplex';
const SESSION = process.env.MUX_LIVE_SESSION || 'hl-owner-session';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map((p) => p.trim()).filter(Boolean).map((p) => {
  const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) };
});

const SAMPLE = () => {
  const host = document.querySelector('#term');
  const scr = host && host.querySelector('.xterm-screen');
  const cv = scr && scr.querySelector('canvas');
  const dim = (() => { try { return term._core._renderService.dimensions.css.cell; } catch (e) { return null; } })();
  const r = scr ? scr.getBoundingClientRect() : null;
  return {
    t: Math.round(performance.now()),
    cols: term.cols, rows: term.rows, font: term.options.fontSize,
    hostW: host ? host.clientWidth : 0,
    screenW: r ? Math.round(r.width) : 0,
    screenStyleW: scr ? scr.style.width : '',
    canvasW: cv ? cv.getAttribute('width') : '',
    canvasStyleW: cv ? cv.style.width : '',
    xtermCellW: dim ? Math.round(dim.width * 1000) / 1000 : null,
    xtermCellH: dim ? Math.round(dim.height * 1000) / 1000 : null,
    appliedFont: (() => { try { return appliedFont; } catch (e) { return null; } })(),
    dpr: window.devicePixelRatio,
  };
};

(async () => {
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map((c) => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  await page.goto(`${BASE}/?s=${encodeURIComponent(SESSION)}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1; } catch (e) { return false; } }, null, { timeout: 30000 });
  await page.waitForTimeout(2500);
  const before = await page.evaluate(SAMPLE);
  console.log('before ' + JSON.stringify(before));
  // two taps: fill -> fit, then fit -> take
  await page.evaluate(() => { const b = document.querySelector('#sizebtn'); b.click(); });
  await page.waitForTimeout(800);
  // The proof's take is a DOUBLE tap issued in one task (the slow-link race), not two settled clicks.
  await page.evaluate(() => { const b = document.querySelector('#sizebtn'); b.click(); b.click(); });
  for (let i = 0; i < 24; i += 1) {
    await page.waitForTimeout(150);
    const s = await page.evaluate(SAMPLE);
    s.span = s.hostW ? Math.round((s.screenW / s.hostW) * 1000) / 1000 : 0;
    s.cellFromScreen = s.cols ? Math.round((s.screenW / s.cols) * 100) / 100 : 0;
    console.log('s' + String(i).padStart(2, '0') + ' ' + JSON.stringify(s));
  }
  await page.evaluate(() => { const b = document.querySelector('#sizebtn'); b.click(); });
  await page.waitForTimeout(1500);
  console.log('after release ' + JSON.stringify(await page.evaluate(SAMPLE)));
  await browser.close();
})().catch((e) => { console.error('SPAN PROBE ERROR', e && (e.stack || e.message)); process.exit(2); });
