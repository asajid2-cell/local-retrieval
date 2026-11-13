'use strict';
/* Print what the DEPLOYED relay publishes for a session row - the exact input the attach path gates on.
 * Read-only: attaches nothing, types nothing.
 * Usage: keysafe run hl-owner-session -Command '... node probe_session_row.cjs [name]' */
const { chromium } = require('C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright');
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const WANT = process.argv[2] || process.env.MUX_SESSION || 'hl-ga1-live';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });
(async () => {
  if (!pairs.length) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: 1200, height: 800 }, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  await page.goto(`${BASE}/?s=${encodeURIComponent(WANT)}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForTimeout(4000);
  const rows = await page.evaluate((want) => {
    const list = (window._sessions || []).slice();
    const pick = list.find(s => s.name === want) || null;
    return { want, count: list.length, row: pick, names: list.map(s => s.name) };
  }, WANT);
  const api = await page.evaluate(async (want) => {
    try {
      const r = await fetch('/api/sessions', { credentials: 'same-origin' });
      const j = await r.json();
      const l = Array.isArray(j) ? j : (j.list || j.sessions || []);
      const pick = l.find(s => s && s.name === want);
      return { status: r.status, found: !!pick, row: pick || null };
    } catch (e) { return { error: String(e) }; }
  }, WANT);
  const pick = r => r ? { name: r.name, kind: r.kind, watchAck: r.watchAck, owner: r.owner, alive: r.alive, cols: r.cols, rows: r.rows, gridOwner: r.gridOwner } : null;
  console.log(JSON.stringify({ pageSessions: { count: rows.count, row: pick(rows.row) }, api: { status: api.status, row: pick(api.row), error: api.error } }, null, 1));
  await ctx.close(); await browser.close();
})().catch(e => { console.error('PROBE ERROR', e && (e.stack || e.message)); process.exit(2); });
