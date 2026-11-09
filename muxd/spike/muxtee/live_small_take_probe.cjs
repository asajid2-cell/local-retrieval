'use strict';
// A small window (like a laptop half-screen or a phone) taking the grid: what cell did it measure, what
// did it report, and what did the relay commit? The mismatch is the "does not fit" report.
const path = require('path');
function loadPlaywright() { const c = ['C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright', 'playwright']; let l; for (const x of c) { try { return require(x); } catch (e) { l = e; } } throw l; }
const { chromium } = loadPlaywright();
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = 'https://harmonizerlabs.cc/multiplex';
const SESSION = process.env.MUX_LIVE_SESSION || 'hl-owner-session';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });
const W = Number(process.env.MUX_W || 1024), H = Number(process.env.MUX_H || 700);
(async () => {
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: W, height: H }, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  await page.goto(`${BASE}/?s=${encodeURIComponent(SESSION)}&dev=small&label=small`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1; } catch (e) { return false; } }, null, { timeout: 30000 });
  await page.waitForTimeout(2500);
  const pre = await page.evaluate(() => ({ font: term.options.fontSize, applied: (() => { try { return appliedFont; } catch (e) { return null; } })(), fontSize: (() => { try { return fontSize; } catch (e) { return null; } })(), vf: viewportFit, cols: term.cols }));
  console.log('pre ' + JSON.stringify(pre));
  await page.evaluate(() => { document.querySelector('#sizebtn').click(); });
  await page.waitForTimeout(1200);
  await page.evaluate(() => { document.querySelector('#sizebtn').click(); });
  await page.waitForTimeout(3000);
  const post = await page.evaluate(() => ({
    font: term.options.fontSize, applied: (() => { try { return appliedFont; } catch (e) { return null; } })(), vf: viewportFit,
    cols: term.cols, rows: term.rows, chip: document.querySelector('#sizebtn').textContent.replace(/\s+/g, ' ').trim(),
    nd: (window.__ndLog || []).slice(-4), v: (window.__vLog || []).slice(-4),
    hostW: document.querySelector('#term').clientWidth, hostH: document.querySelector('#term').clientHeight,
    span: (() => { try { const h = document.querySelector('#term'); const s = h.querySelector('.xterm-screen'); return +(s.getBoundingClientRect().width / h.clientWidth).toFixed(3); } catch (e) { return null; } })(),
  }));
  console.log('post ' + JSON.stringify(post));
  await page.evaluate(() => { document.querySelector('#sizebtn').click(); });
  await page.waitForTimeout(1500);
  await browser.close();
})().catch(e => { console.error('SMALL TAKE ERROR', e && (e.stack || e.message)); process.exit(2); });
