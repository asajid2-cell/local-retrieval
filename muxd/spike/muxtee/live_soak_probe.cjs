'use strict';
/* Soak: stay attached for minutes and count what actually goes wrong - reconnects, geometry churn,
 * owner changes, relay health edges, page errors, and keystroke latency sampled throughout. This is the
 * "it fixes itself after some time, the inconsistency is the issue" check with a clock on it.
 * Usage: MUX_SESSION=hl-lat-probe MUX_SOAK_MS=240000 node live_soak_probe.cjs */
const path = require('path');
function loadPlaywright() { const c = ['C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright', 'playwright']; let l; for (const x of c) { try { return require(x); } catch (e) { l = e; } } throw l; }
const { chromium } = loadPlaywright();
const { assertSession } = require('./probe_guard.cjs');
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.env.MUX_SESSION || 'hl-lat-probe';
const SOAK = Number(process.env.MUX_SOAK_MS || 240000);
const PROBE_EVERY = Number(process.env.MUX_PROBE_EVERY_MS || 30000);
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });
(async () => {
  if (!pairs.length) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  const errs = []; page.on('pageerror', e => errs.push(String(e && e.message || e)));
  const t0 = Date.now();
  await page.goto(`${BASE}/?s=${encodeURIComponent(S)}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });
  await assertSession(page, S);   // fail closed: never type into a pane this probe was not given
  await page.waitForTimeout(2000);
  await page.evaluate(() => {
    const L = window.__soak = { ws: [], writes: [], renders: [], keys: [], sends: [], resizes: [] };
    const now = () => performance.now();
    const dec = new TextDecoder('utf-8');
    const ow = term.write.bind(term);
    term.write = (d, cb) => { try { L.writes.push(now()); } catch (e) {} return ow(d, cb); };
    try { term.onRender(() => L.renders.push(now())); } catch (e) {}
    try { const ors = term.resize.bind(term); term.resize = (c, r) => { L.resizes.push({ t: Math.round(now()), c, r }); return ors(c, r); }; } catch (e) {}
    document.addEventListener('keydown', e => L.keys.push({ t: now(), k: e.key }), true);
    const o = ws.onmessage;
    ws.onmessage = e => { try { const s = typeof e.data === 'string' ? e.data : ''; if (s[0] === 'd') { const m = JSON.parse(s.slice(1)); L.lastD = { t: Math.round(now()), cols: m.cols, rows: m.rows, owner: m.gridOwner, epoch: m.gridEpoch }; } } catch (x) {} return o && o.call(ws, e); };
    try { ws.addEventListener('close', () => L.ws.push({ at: Math.round(now()), what: 'close' })); ws.addEventListener('open', () => L.ws.push({ at: Math.round(now()), what: 'open' })); } catch (e) {}
  });
  const box = await page.locator('#term .xterm-screen').boundingBox();
  await page.mouse.click(box.x + 80, box.y + 40);
  await page.waitForTimeout(200);
  const samples = [];
  let geomChanges = 0, prevGeom = '';
  const health = async () => page.evaluate(async base => { try { const h = await (await fetch(base + '/api/health')).json(); return { pc: h.pc && h.pc.rttMs, reach: h.pc && h.pc.reachable, frames: h.host && h.host.connected, age: h.host && h.host.frameAgeMs, degraded: h.degraded, armed: h.armed }; } catch (e) { return { error: String(e) }; } }, BASE);
  while (Date.now() - t0 < SOAK) {
    const st = await page.evaluate(() => ({ cols: term.cols, rows: term.rows, owner: (typeof sizeGridOwner !== 'undefined' ? sizeGridOwner : null), d: window.__soak.lastD || null, counts: { w: window.__soak.writes.length, r: window.__soak.renders.length }, ws: window.__soak.ws.length, resizes: window.__soak.resizes.length }));
    const geom = `${st.cols}x${st.rows}`;
    if (prevGeom && geom !== prevGeom) { geomChanges += 1; console.log(`+${Date.now() - t0}ms GEOM ${prevGeom} -> ${geom}`); }
    prevGeom = geom;
    // Type one character and time its echo, so latency is tracked through the soak, not just at the start.
    const before = await page.evaluate(() => ({ k: window.__soak.keys.length, w: window.__soak.writes.length }));
    await page.keyboard.press('Escape');          // cmd.exe: clear the input line so the echo is this key's
    await page.waitForTimeout(80);
    const before2 = await page.evaluate(() => ({ k: window.__soak.keys.length, w: window.__soak.writes.length }));
    await page.keyboard.press('k');
    await page.waitForTimeout(700);
    const late = await page.evaluate(b => {
      const L = window.__soak;
      const key = L.keys.slice(b.k).find(x => x.k === 'k');
      const w = key ? L.writes.slice(b.w).find(t => t >= key.t) : null;
      return { send: key ? Math.round(key.t) : null, echo: w ? +(w - key.t).toFixed(1) : null };
    }, before2);
    samples.push(late.echo);
    console.log(`+${Date.now() - t0}ms ${geom} owner=${st.owner} echo=${late.echo}ms writes=${st.counts.w} renders=${st.counts.r} wsEvents=${st.ws} resizes=${st.resizes} health=${JSON.stringify(await health())}`);
    await page.waitForTimeout(PROBE_EVERY);
  }
  const valid = samples.filter(x => typeof x === 'number');
  valid.sort((a, b) => a - b);
  const res = await page.evaluate(() => ({ ws: window.__soak.ws, resizes: window.__soak.resizes, renders: window.__soak.renders.length, writes: window.__soak.writes.length, lastD: window.__soak.lastD || null }));
  console.log('SOAK ' + JSON.stringify({
    minutes: +(SOAK / 60000).toFixed(1), geometryChanges: geomChanges,
    echo: valid.length ? { n: valid.length, med: valid[Math.floor(valid.length / 2)], max: valid[valid.length - 1] } : null,
    wsEvents: res.ws.length, resizes: res.resizes.length, renders: res.renders, writes: res.writes, lastD: res.lastD,
  }));
  console.log('ERRORS ' + JSON.stringify(errs));
  await ctx.close(); await browser.close();
})().catch(e => { console.error('SOAK PROBE ERROR', e && (e.stack || e.message)); process.exit(2); });
