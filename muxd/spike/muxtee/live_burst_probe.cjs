'use strict';
/* Burst lag: how long a big output burst takes to reach the screen, and the worst gap inside it.
 * "Pleasant" means the pane keeps up with a command that prints a lot, not just with one echoed key.
 * Sends `for /l` on a cmd.exe session (hl-lat-probe) and measures Enter -> the final marker, plus the
 * distribution of gaps between consecutive writes and renders while the burst streams.
 * Usage: MUX_SESSION=hl-lat-probe MUX_LINES=1500 node live_burst_probe.cjs */
const path = require('path');
function loadPlaywright() { const c = ['C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright', 'playwright']; let l; for (const x of c) { try { return require(x); } catch (e) { l = e; } } throw l; }
const { chromium } = loadPlaywright();
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.env.MUX_SESSION || 'hl-lat-probe';
const LINES = Number(process.env.MUX_LINES || 1500);
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });
const gaps = a => { const g = []; for (let i = 1; i < a.length; i += 1) g.push(+(a[i] - a[i - 1]).toFixed(1)); return g; };
const stat = a => a.length ? { n: a.length, med: a.slice().sort((x, y) => x - y)[Math.floor(a.length / 2)], max: Math.max(...a) } : null;
(async () => {
  if (!pairs.length) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  const errs = []; page.on('pageerror', e => errs.push(String(e && e.message || e)));
  await page.goto(`${BASE}/?s=${encodeURIComponent(S)}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });
  await page.waitForTimeout(2000);
  await page.evaluate(() => {
    const L = window.__burst = { writes: [], renders: [], keys: [] };
    const now = () => performance.now();
    const dec = new TextDecoder('utf-8');
    const ow = term.write.bind(term);
    term.write = (d, cb) => { try { L.writes.push({ t: now(), s: typeof d === 'string' ? d : dec.decode(d) }); } catch (e) {} return ow(d, cb); };
    try { term.onRender(() => L.renders.push(now())); } catch (e) {}
    document.addEventListener('keydown', e => L.keys.push({ t: now(), k: e.key }), true);
  });
  const box = await page.locator('#term .xterm-screen').boundingBox();
  await page.mouse.click(box.x + 80, box.y + 40);
  await page.waitForTimeout(300);
  // A directory walk prints thousands of lines with no quoting hazards. The end is "writes have gone
  // quiet", which is what a viewer actually experiences: the burst has stopped and the screen settles.
  const n0 = await page.evaluate(() => ({ w: window.__burst.writes.length, r: window.__burst.renders.length, k: window.__burst.keys.length }));
  const t0 = Date.now();
  const CMD = process.env.MUX_BURST_CMD || 'dir /s /b C:\\Windows\\System32';
  await page.keyboard.type(CMD, { delay: 2 });
  await page.keyboard.press('Enter');
  let lastCount = -1, quietSince = 0, doneMs = 0;
  for (let i = 0; i < 300; i += 1) {
    const n = await page.evaluate(() => window.__burst.writes.length);
    if (n === lastCount) { if (!quietSince) quietSince = Date.now(); if (Date.now() - quietSince > 1200) { doneMs = quietSince - t0; break; } }
    else { lastCount = n; quietSince = 0; }
    await page.waitForTimeout(80);
  }
  if (!doneMs) doneMs = Date.now() - t0;
  await page.waitForTimeout(300);
  const res = await page.evaluate(n => {
    const L = window.__burst;
    const enter = L.keys[n.k] || null;
    const tEnter = enter ? enter.t : 0;
    const ws = L.writes.slice(n.w);
    const bytes = ws.reduce((a, w) => a + String(w.s).length, 0);
    const lastAt = ws.length ? +(ws[ws.length - 1].t - tEnter).toFixed(1) : null;
    return { enterAt: enter ? Math.round(tEnter) : null, bytes, lastAt, writes: ws.map(w => +(w.t - tEnter).toFixed(1)), renders: L.renders.slice(n.r).map(t => +(t - tEnter).toFixed(1)), text: (() => { try { return termVisibleText().slice(-80); } catch (e) { return ''; } })() };
  }, n0);
  const wg = gaps(res.writes), rg = gaps(res.renders);
  console.log('BURST ' + JSON.stringify({ session: S, cmd: CMD, wallMs: doneMs, writes: res.writes.length, bytes: res.bytes, lastWriteAt: res.lastAt, renders: res.renders.length, writeGaps: stat(wg), renderGaps: stat(rg) }));
  console.log('TAIL ' + JSON.stringify(res.text));
  console.log('ERRORS ' + JSON.stringify(errs));
  await ctx.close(); await browser.close();
})().catch(e => { console.error('BURST PROBE ERROR', e && (e.stack || e.message)); process.exit(2); });
