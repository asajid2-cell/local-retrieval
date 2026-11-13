'use strict';
/* Can an https page open a WebSocket to ws://127.0.0.1:7699 (muxd's local attach server)?
 * Two questions in one: does Chrome block it as MIXED CONTENT, and does muxd accept a browser Origin?
 * Prints the outcome of the socket attempt plus any console error. Read-only: it opens and closes. */
const path = require('path');
function loadPlaywright() { const c = ['C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright', 'playwright']; let l; for (const x of c) { try { return require(x); } catch (e) { l = e; } } throw l; }
const { chromium } = loadPlaywright();
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });
(async () => {
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: 1000, height: 700 }, locale: 'en-US', userAgent: UA });
  if (pairs.length) await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  const logs = [];
  page.on('console', m => logs.push(m.type() + ': ' + m.text()));
  page.on('pageerror', e => logs.push('pageerror: ' + (e && e.message)));
  await page.goto(BASE + '/', { waitUntil: 'domcontentloaded', timeout: 60000 }).catch(e => logs.push('goto: ' + e.message));
  const res = await page.evaluate(() => new Promise(resolve => {
    const out = { origin: location.origin, secure: window.isSecureContext };
    let ws;
    try { ws = new WebSocket('ws://127.0.0.1:7699/'); } catch (e) { resolve({ ...out, threw: String(e) }); return; }
    ws.onopen = () => { out.opened = true; try { ws.close(); } catch (e) {} resolve(out); };
    ws.onerror = () => { out.errored = true; };
    ws.onclose = e => { out.closed = { code: e.code, reason: e.reason, wasClean: e.wasClean }; resolve(out); };
    setTimeout(() => { out.timeout = true; try { ws.close(); } catch (e) {} resolve(out); }, 6000);
  }));
  console.log('MIXED-CONTENT ' + JSON.stringify(res, null, 1));
  console.log('CONSOLE ' + JSON.stringify(logs.slice(0, 20), null, 1));
  await ctx.close(); await browser.close();
})().catch(e => { console.error('PROBE ERROR', e && (e.stack || e.message)); process.exit(2); });
