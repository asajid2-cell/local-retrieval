'use strict';
/* Dump the live buffer's rows, numbered, exactly as the phone renderer sees them.
 *
 * The thread view groups these rows into messages. To fix its output - a repeated sender label, a terminal
 * status bar painted as a message - I need the rows themselves, not a screenshot. Read-only: never types.
 * Usage: MUX_SESSION=tab-5bec node dump_thread_rows.cjs
 */
const { chromium } = require('C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright');
const { assertSession } = require('./probe_guard.cjs');
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.env.MUX_SESSION || 'tab-5bec';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });
(async () => {
  if (!pairs.length) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  await page.goto(`${BASE}/?s=${encodeURIComponent(S)}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });
  await assertSession(page, S);
  await page.waitForTimeout(2500);
  const out = await page.evaluate(() => {
    const b = term.buffer.active;
    const rows = [];
    for (let i = 0; i < b.length; i++) {
      const l = b.getLine(i); if (!l) continue;
      const t = l.translateToString(true);
      if (!t.trim()) continue;
      rows.push({ i, wrapped: !!l.isWrapped, c0: t.charCodeAt(0).toString(16), text: t.slice(0, 110) });
    }
    return { kind: b.type, baseY: b.baseY, viewportY: b.viewportY, length: b.length, rows: rows.slice(-40) };
  });
  console.log(JSON.stringify(out, null, 1));
  await ctx.close(); await browser.close();
})().catch(e => { console.error('DUMP ERROR', e && (e.stack || e.message)); process.exit(2); });
