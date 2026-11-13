'use strict';
/* What can a thread renderer key off, using ONLY the bytes already in the buffer?
 *
 * The plan is a phone render of the same teemux output under the grid ownership we already built, so the
 * renderer must segment an agent's screen into message-like groups without any new wire metadata. This
 * dumps every buffer row with its leading glyph escaped, plus a naive classification, so the segmenter is
 * designed against real output instead of a guess.
 * Read-only: attaches and reads; never types.
 * Usage: MUX_SESSION=tab-5bec node probe_thread_markers.cjs */
const { chromium } = require('C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright');
const { assertSession } = require('./probe_guard.cjs');
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.env.MUX_SESSION || 'hl-lat-probe';
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
  await page.waitForTimeout(2500);
  const out = await page.evaluate(() => {
    const B = term.buffer.active;
    const esc = s => s.replace(/[\u0000-\u001f]/g, c => '\\x' + c.charCodeAt(0).toString(16).padStart(2, '0'));
    const rows = [];
    for (let i = 0; i < B.length; i += 1) {
      const L = B.getLine(i);
      if (!L) continue;
      const t = L.translateToString(true);
      if (!t.trim()) continue;
      // Leading non-space glyph, the candidate the segmenter would branch on.
      const m = t.match(/^(\s*)(\S)/);
      rows.push({
        i,
        lead: m ? esc(m[2]) : '',
        leadCode: m ? m[2].codePointAt(0) : null,
        indent: m ? m[1].length : 0,
        wrapped: L.isWrapped,
        text: t,
      });
    }
    // Every distinct leading glyph with a count and one example, which is the whole marker vocabulary.
    const byLead = {};
    for (const r of rows) {
      const k = r.lead || '(none)';
      byLead[k] = byLead[k] || { n: 0, indents: new Set(), example: r.text.slice(0, 70) };
      byLead[k].n += 1;
      byLead[k].indents.add(r.indent);
    }
    const vocab = Object.entries(byLead).map(([lead, v]) => ({ lead, count: v.n, indents: [...v.indents].sort(), example: v.example }));
    return { current, bufferType: B.type, cols: term.cols, rows: term.rows, nonEmpty: rows.length, vocab, rows };
  });
  console.log(JSON.stringify(out, null, 1));
  await ctx.close(); await browser.close();
})().catch(e => { console.error('THREAD MARKER PROBE ERROR', e && (e.stack || e.message)); process.exit(2); });
