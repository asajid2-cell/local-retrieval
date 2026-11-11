'use strict';
/* Browser <-> relay round trip, isolated from the pty. Sends a 'v' (viewport) frame and waits for the
 * 'd' (state) broadcast recompute() produces. That is the network + relay work with no keystroke and no
 * ConPTY in it, so (keypress latency - this) is the PC-side/tee share.
 * Usage: MUX_SESSION=hl-lat-probe node live_rtt_probe.cjs [trials] */
const path = require('path');
function loadPlaywright() { const c = ['C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright', 'playwright']; let l; for (const x of c) { try { return require(x); } catch (e) { l = e; } } throw l; }
const { chromium } = loadPlaywright();
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.env.MUX_SESSION || 'hl-lat-probe';
const N = Number(process.argv[2] || 10);
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
  await page.waitForTimeout(1500);
  const out = await page.evaluate(async n => {
    const seen = [];
    const o = ws.onmessage;
    ws.onmessage = e => { try { const s = typeof e.data === 'string' ? e.data : ''; if (s[0] === 'd') seen.push(performance.now()); } catch (x) {} return o && o.call(ws, e); };
    const rows = [];
    for (let i = 0; i < n; i += 1) {
      const mark = seen.length;
      const t = performance.now();
      try { ws.send('v' + JSON.stringify({ cols: term.cols, rows: term.rows, vis: true, act: false, probe: i })); } catch (e) { rows.push({ error: String(e) }); continue; }
      const until = t + 5000;
      let got = null;
      while (performance.now() < until) { if (seen.length > mark) { got = seen[seen.length - 1]; break; } await new Promise(r => setTimeout(r, 4)); }
      rows.push(got ? +(got - t).toFixed(1) : null);
      await new Promise(r => setTimeout(r, 350));
    }
    ws.onmessage = o;
    return { rows, sent: rows.filter(x => typeof x === 'number') };
  }, N);
  const s = out.sent.slice().sort((a, b) => a - b);
  console.log('RTT ' + JSON.stringify(out));
  if (s.length) console.log('RTT-STAT ' + JSON.stringify({ n: s.length, min: s[0], med: s[Math.floor(s.length / 2)], p90: s[Math.floor(s.length * 0.9)], max: s[s.length - 1] }));
  await ctx.close(); await browser.close();
})().catch(e => { console.error('RTT PROBE ERROR', e && (e.stack || e.message)); process.exit(2); });
