'use strict';
/* The first keystroke after a tab opens takes ~700 ms while every later one is ~57 ms. This measures
 * the write that the echo queues behind: every term.write with its size, start and completion, then one
 * key at a configurable delay. Serial in xterm is required for order - the question is what is IN the
 * queue and how long the parser takes on it.
 * Usage: MUX_SESSION=hl-lat-probe MUX_DELAY_MS=1200 node live_attach_latency_probe.cjs */
const path = require('path');
function loadPlaywright() { const c = ['C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright', 'playwright']; let l; for (const x of c) { try { return require(x); } catch (e) { l = e; } } throw l; }
const { chromium } = loadPlaywright();
const { assertSession } = require('./probe_guard.cjs');
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.env.MUX_SESSION || 'hl-lat-probe';
const DELAY = Number(process.env.MUX_DELAY_MS || 1200);
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });
(async () => {
  if (!pairs.length) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  // Instrument BEFORE the page script can write anything.
  await page.addInitScript(() => {
    window.__al = { writes: [], keys: [], marks: [] };
    const boot = () => {
      if (typeof term === 'undefined' || !term || !term.write) { setTimeout(boot, 5); return; }
      const L = window.__al; const now = () => performance.now();
      const dec = new TextDecoder('utf-8');
      L.t0 = now();
      const ow = term.write.bind(term);
      term.write = (d, cb) => {
        const rec = { t: now(), n: typeof d === 'string' ? d.length : (d && d.length) || 0, tag: String(typeof d === 'string' ? d : dec.decode(d)).slice(0, 40) };
        L.writes.push(rec);
        return ow(d, () => { rec.done = now(); try { if (cb) cb(); } catch (e) {} });
      };
      // Wire-level stamps: when the input actually left, and when each inbound message landed. A slow echo
      // that is already late ON THE WIRE is not a page problem and must not be "fixed" in the page.
      L.sends = []; L.recv = [];
      const bootWs = () => {
        if (typeof ws === 'undefined' || !ws || !ws.send) { setTimeout(bootWs, 5); return; }
        const os = ws.send.bind(ws);
        ws.send = p => { try { L.sends.push({ t: now(), s: String(p).slice(0, 8), n: String(p).length }); } catch (e) {} return os(p); };
        const om = ws.onmessage;
        ws.onmessage = e => { try { const b = typeof e.data !== 'string'; L.recv.push({ t: now(), b, n: b ? e.data.byteLength : String(e.data).length }); } catch (x) {} return om && om.call(ws, e); };
      };
      bootWs();
      // Main-thread health: long tasks, and the worst gap between 16 ms heartbeats. A slow echo that
      // coincides with a long task is a PAGE stall; one without is the wire.
      L.longtasks = []; L.heartbeatGaps = [];
      try { new PerformanceObserver(list => { for (const e of list.getEntries()) L.longtasks.push({ start: Math.round(e.startTime), dur: Math.round(e.duration) }); }).observe({ entryTypes: ['longtask'] }); } catch (e) {}
      let lastBeat = now();
      setInterval(() => { const t = now(); const g = t - lastBeat - 16; lastBeat = t; if (g > 60) L.heartbeatGaps.push({ at: Math.round(t), gap: Math.round(g) }); }, 16);
      document.addEventListener('keydown', e => L.keys.push({ t: now(), k: e.key }), true);
    };
    boot();
  });
  await page.goto(`${BASE}/?s=${encodeURIComponent(S)}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });
  await assertSession(page, S);   // fail closed: never type into a pane this probe was not given
  await page.waitForTimeout(DELAY);
  await page.evaluate(() => window.__al.marks.push({ t: performance.now(), what: 'click' }));
  const box = await page.locator('#term .xterm-screen').boundingBox();
  await page.mouse.click(box.x + 80, box.y + 40);
  await page.waitForTimeout(150);
  await page.evaluate(() => window.__al.marks.push({ t: performance.now(), what: 'key' }));
  await page.keyboard.press('k');
  await page.waitForTimeout(1500);
  await page.keyboard.press('j');
  await page.waitForTimeout(800);
  const out = await page.evaluate(() => {
    const L = window.__al; const t0 = L.t0;
    const at = (k) => { const key = L.keys.find(x => x.k === k); if (!key) return null; const echo = L.writes.find(w => w.t >= key.t); return { keyAt: Math.round(key.t - t0), echoAt: echo ? Math.round(echo.t - t0) : null, lag: echo ? +(echo.t - key.t).toFixed(1) : null, echoSize: echo ? echo.n : null }; };
    const wire = (k) => {
      const key = L.keys.find(x => x.k === k); if (!key) return null;
      const send = L.sends.find(s => s.t >= key.t);
      const recv = L.recv.filter(r => r.b && r.t >= (send ? send.t : key.t)).slice(0, 3);
      return { sendLag: send ? +(send.t - key.t).toFixed(1) : null, sendFrame: send ? send.s : null, firstRecvAfterSend: recv.length ? +(recv[0].t - send.t).toFixed(1) : null, recv: recv.map(r => ({ at: +(r.t - key.t).toFixed(1), n: r.n })) };
    };
    return {
      t0, first: at('k'), second: at('j'), wireFirst: wire('k'), wireSecond: wire('j'),
      biggest: L.writes.slice().sort((a, b) => b.n - a.n).slice(0, 6).map(w => ({ at: Math.round(w.t - t0), n: w.n, took: w.done ? +(w.done - w.t).toFixed(1) : null })),
      slowest: L.writes.filter(w => w.done).sort((a, b) => (b.done - b.t) - (a.done - a.t)).slice(0, 5).map(w => ({ at: Math.round(w.t - t0), n: w.n, took: +(w.done - w.t).toFixed(1) })),
      totalWrites: L.writes.length, totalBytes: L.writes.reduce((a, w) => a + w.n, 0),
      pending: L.writes.filter(w => !w.done).length,
      longtasks: L.longtasks, heartbeatGaps: L.heartbeatGaps.slice(0, 12),
    };
  });
  console.log('ATTACH ' + JSON.stringify({ session: S, delay: DELAY, ...out }, null, 1));
  await ctx.close(); await browser.close();
})().catch(e => { console.error('ATTACH PROBE ERROR', e && (e.stack || e.message)); process.exit(2); });
