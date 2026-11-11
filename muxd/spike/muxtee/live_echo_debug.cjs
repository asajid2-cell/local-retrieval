'use strict';
/* One keypress, then dump EVERY write and send around it, escaped. Answers "why did no echo match".
 * Usage: MUX_SESSION=hl-lat-probe node live_echo_debug.cjs [char] */
const path = require('path');
function loadPlaywright() { const c = ['C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright', 'playwright']; let l; for (const x of c) { try { return require(x); } catch (e) { l = e; } } throw l; }
const { chromium } = loadPlaywright();
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.env.MUX_SESSION || 'hl-lat-probe';
const CH = process.argv[2] || 'z';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });
const esc = s => String(s).replace(/\x1b/g, '\\e').replace(/\r/g, '\\r').replace(/\n/g, '\\n').slice(0, 140);
(async () => {
  if (!pairs.length) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  await page.goto(`${BASE}/?s=${encodeURIComponent(S)}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });
  await page.waitForTimeout(2500);
  await page.evaluate(() => {
    const L = window.__dbg = { sends: [], writes: [], onData: [], keys: [] };
    const now = () => performance.now();
    const o = ws.send.bind(ws);
    ws.send = p => { L.sends.push({ t: now(), s: String(p).slice(0, 60) }); return o(p); };
    const ow = term.write.bind(term);
    term.write = (d, cb) => { L.writes.push({ t: now(), s: typeof d === 'string' ? d : '[bin:' + (d && d.constructor && d.constructor.name) + ']' }); return ow(d, cb); };
    window.__onData = '';
    try { term.onData(d => { L.onData.push({ t: now(), s: d }); }); } catch (e) {}
    document.addEventListener('keydown', e => { L.keys.push({ t: now(), k: e.key, target: e.target && e.target.className }); }, true);
    const bin = ws.binaryType; L.binaryType = bin;
  });
  const box = await page.locator('#term .xterm-screen').boundingBox();
  await page.mouse.click(box.x + 80, box.y + 40);
  await page.waitForTimeout(300);
  const mark = await page.evaluate(() => ({ keys: window.__dbg.keys.length, sends: window.__dbg.sends.length, writes: window.__dbg.writes.length, onData: window.__dbg.onData.length }));
  await page.keyboard.press(CH);
  await page.waitForTimeout(1200);
  const out = await page.evaluate(m => {
    const L = window.__dbg;
    const t0 = (L.keys[m.keys] || {}).t || 0;
    return {
      binaryType: L.binaryType,
      focus: (() => { const a = document.activeElement; return { tag: a && a.tagName, cls: a && a.className, inTerm: !!(a && document.querySelector('#term').contains(a)) }; })(),
      keys: L.keys.slice(m.keys),
      sends: L.sends.slice(m.sends),
      onData: L.onData.slice(m.onData),
      writes: L.writes.slice(m.writes).map(w => ({ dt: +(w.t - t0).toFixed(1), s: w.s })),
    };
  }, mark);
  console.log('CH=' + JSON.stringify(CH) + ' t0=' + out.keys.map(k => k.k).join(','));
  console.log('focus ' + JSON.stringify(out.focus) + ' binaryType=' + out.binaryType);
  console.log('onData ' + JSON.stringify(out.onData.map(d => d.s)));
  console.log('sends ' + JSON.stringify(out.sends.map(s => ({ dt: +(s.t - (out.keys[0] || {}).t || 0).toFixed(1), s: s.s }))));
  out.writes.forEach(w => console.log('  write +' + w.dt + 'ms ' + esc(w.s)));
  await ctx.close(); await browser.close();
})().catch(e => { console.error('ECHO DEBUG ERROR', e && (e.stack || e.message)); process.exit(2); });
