'use strict';
/* Read-only: what is actually on the pane right now. Attaches as a viewer, prints the visible screen
 * (last N buffer lines) so a probe can tell a bare shell prompt from a TUI before it types anything.
 * Usage: MUX_SESSION=hl-owner-session node live_screen_probe.cjs */
const path = require('path');
function loadPlaywright() { const c = ['C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright', 'playwright']; let l; for (const x of c) { try { return require(x); } catch (e) { l = e; } } throw l; }
const { chromium } = loadPlaywright();
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.env.MUX_SESSION || 'hl-owner-session';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });
const N = Number(process.env.MUX_LINES || 24);
(async () => {
  if (!pairs.length) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  await page.goto(`${BASE}/?s=${encodeURIComponent(S)}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });
  await page.waitForTimeout(3000);
  const out = await page.evaluate(n => {
    const b = term.buffer.active;
    const start = Math.max(0, b.length - n);
    const lines = [];
    for (let i = start; i < b.length; i += 1) { const L = b.getLine(i); lines.push(L ? L.translateToString(true) : ''); }
    const row = (() => { try { return (window._sessions || []).find(s => s.name === current) || null; } catch (e) { return null; } })();
    return { lines, row: row && { kind: row.kind, owner: row.owner, hasCommand: row.hasCommand, shellOnly: row.shellOnly, cols: row.cols, rows: row.rows, gridOwner: row.gridOwner },
      modes: (() => { try { return term.modes; } catch (e) { return null; } })(), guard: (() => { try { return mouseGuard.stats(); } catch (e) { return null; } })() };
  }, N);
  console.log('ROW ' + JSON.stringify(out.row) + ' modes=' + JSON.stringify(out.modes && { applicationCursor: out.modes.applicationCursor, bracketedPaste: out.modes.bracketedPasteMode, mouseTracking: out.modes.mouseTrackingMode }));
  out.lines.forEach((l, i) => console.log(String(i).padStart(2) + '|' + l));
  await ctx.close(); await browser.close();
})().catch(e => { console.error('SCREEN PROBE ERROR', e && (e.stack || e.message)); process.exit(2); });
