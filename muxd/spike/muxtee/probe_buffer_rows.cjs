'use strict';
/* Can a thread renderer be built from the buffer we already have?
 *
 * The plan is a phone render of the SAME teemux bytes under the grid ownership we already built - not a
 * second data plane. Before writing a renderer, confirm what the live page already holds per row: text,
 * wrap flags, and whether the buffer is the normal screen (so an agent's prose is real rows) or the
 * alternate screen (a full-screen TUI owns every cell and rows are not messages).
 * Read-only: attaches, reads the buffer, never types.
 * Usage: MUX_SESSION=hl-lat-probe node probe_buffer_rows.cjs */
const path = require('path');
const { chromium } = require('C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright');
const { assertSession } = require('./probe_guard.cjs');
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.env.MUX_SESSION || 'hl-lat-probe';
const N = Number(process.env.MUX_LINES || 16);
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });

(async () => {
  if (!pairs.length) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  await page.goto(`${BASE}/?s=${encodeURIComponent(S)}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });
  await assertSession(page, S);
  await page.waitForTimeout(2000);
  const out = await page.evaluate(n => {
    const B = term.buffer.active;
    const rows = [];
    const start = Math.max(0, B.length - n);
    for (let i = start; i < B.length; i += 1) {
      const L = B.getLine(i);
      if (!L) { rows.push(null); continue; }
      rows.push({ i, text: L.translateToString(true), wrapped: L.isWrapped, len: L.translateToString(true).length });
    }
    // What a thread renderer would need: a stable per-row text plus wrap continuation, so one logical
    // paragraph can be rebuilt from several physical rows.
    const paragraphs = [];
    for (const r of rows) {
      if (!r) continue;
      if (r.wrapped && paragraphs.length) paragraphs[paragraphs.length - 1] += r.text;
      else paragraphs.push(r.text);
    }
    return { current, bufferType: B.type, cols: term.cols, rows: term.rows, bufferLength: B.length, rowsSample: rows, paragraphsSample: paragraphs };
  }, N);
  console.log(JSON.stringify(out, null, 1));
  await ctx.close(); await browser.close();
})().catch(e => { console.error('BUFFER ROW PROBE ERROR', e && (e.stack || e.message)); process.exit(2); });
