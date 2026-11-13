'use strict';
/* Print the session rows the deployed page holds for the named sessions: what the click predicate can see
 * (hasCommand / shellOnly / owner / kind) and whether the mouse guard is armed on this page. */
const path = require('path');
function loadPlaywright() { const c = ['C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright', 'playwright']; let l; for (const x of c) { try { return require(x); } catch (e) { l = e; } } throw l; }
const { chromium } = loadPlaywright();
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });
const raw = process.env.MUX_ROWS || 'hl-owner-session,hl-owner-session-b';
const names = raw === 'all' ? null : raw.split(',');
(async () => {
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  await page.goto(`${BASE}/?s=${encodeURIComponent((names && names[0]) || 'hl-owner-session')}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && (window._sessions || []).length > 0; } catch (e) { return false; } }, null, { timeout: 30000 });
  await page.waitForTimeout(2500);
  const out = await page.evaluate(list => {
    const pick = s => s && ({ name: s.name, kind: s.kind, owner: s.owner, adopted: s.adopted, hasCommand: s.hasCommand, shellOnly: s.shellOnly,
      cols: s.cols, rows: s.rows, gridOwner: s.gridOwner, sessionId: s.sessionId });
    const guard = (() => { try { return mouseGuard.stats(); } catch (e) { return null; } })();
    const pred = (() => { try { return { clickForwardable: clickForwardable(), appTakesMouse: appTakesMouse(), ownerClickPane: ownerClickPane(), current }; } catch (e) { return String(e); } })();
    const all = (window._sessions || []).filter(s => s && (s.kind === 'local-tab' || s.kind === 'adopted-local'));
    return { rows: list ? list.map(n => pick((window._sessions || []).find(s => s.name === n))) : all.map(pick), guard, pred };
  }, names);
  console.log(JSON.stringify(out, null, 1));
  await browser.close();
})().catch(e => { console.error('ROWS PROBE ERROR', e && (e.stack || e.message)); process.exit(2); });
