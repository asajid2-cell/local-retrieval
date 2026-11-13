'use strict';
/* Walks the size chip one click at a time on the DEPLOYED page and prints the state after each tap, so a
 * ladder that sticks shows which tap did nothing instead of only failing at the end. */
const path = require('path');
const fs = require('fs');
function loadPlaywright() {
  const c = ['C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright', 'playwright'];
  let last; for (const x of c) { try { return require(x); } catch (e) { last = e; } } throw last;
}
const { chromium } = loadPlaywright();
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const SESSION = process.env.MUX_LIVE_SESSION || 'hl-owner-session';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const TEE_LOG = process.env.MUX_TEE_LOG || path.join(process.env.LOCALAPPDATA || 'C:/Users/Ahmed/AppData/Local', 'muxtee', 'muxtee.log');
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });

const STATE = () => {
  const d = (window.__d && window.__d.length) ? window.__d[window.__d.length - 1] : null;
  const g = n => { try { return eval(n); } catch (e) { return undefined; } };
  return {
    chip: ((document.querySelector('#sizebtn') || {}).textContent || '').replace(/\s+/g, ' ').trim(),
    mode: g('tabFitMode'), owner: g('sizeGridOwner'), pinned: g('sizePinned'), mine: g('sizeMine'),
    tabOwned: g('sizeTabOwned'), winSize: g('winSize'), viewportFit: g('viewportFit'),
    busy: g('_gridBusy'), cols: g('term.cols'), rows: g('term.rows'), font: g('term.options.fontSize'),
    dCols: d && d.cols, dRows: d && d.rows, dOwner: d && d.gridOwner, dEpoch: d && d.gridEpoch,
    dFrames: (window.__d || []).length, alt: (() => { try { return term.buffer.active.type; } catch (e) { return null; } })(),
    dpr: window.devicePixelRatio,
    armed: (() => { try { return mouseGuard.mouseActive(); } catch (e) { return null; } })(),
    inputAllowed: (() => { try { return mouseGuard.inputAllowed(); } catch (e) { return null; } })(),
    sessions: (() => { try { return (window._sessions || []).find(s => s.name === current) || null; } catch (e) { return null; } })(),
    span: (() => { try { const h = document.querySelector('#term'); const s = h.querySelector('.xterm-screen'); return +(s.getBoundingClientRect().width / h.clientWidth).toFixed(3); } catch (e) { return null; } })(),
    cell: (() => { try { const c = term._core._renderService.dimensions.css.cell; return [+c.width.toFixed(2), +c.height.toFixed(2)]; } catch (e) { return null; } })(),
  };
};

function tail(n) {
  try { return fs.readFileSync(TEE_LOG, 'utf8').split(/\r?\n/).filter(Boolean).slice(-n); } catch (e) { return []; }
}

(async () => {
  if (!pairs.length) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const DPR = Number(process.env.MUX_DPR || '1');
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'en-US', userAgent: UA, deviceScaleFactor: DPR });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  const errs = [];
  page.on('pageerror', e => errs.push(String(e && e.message || e)));
  await page.goto(`${BASE}/?s=${encodeURIComponent(SESSION)}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1; } catch (e) { return false; } }, null, { timeout: 30000 });
  await page.evaluate(() => {
    window.__d = [];
    window.__sent = [];
    try {
      ws.addEventListener('message', (e) => {
        const s = typeof e.data === 'string' ? e.data : '';
        if (s[0] === 'd') {
          try {
            const m = JSON.parse(s.slice(1));
            window.__d.push({ at: Math.round(performance.now()), owner: m.gridOwner, mine: m.mine, pin: m.pinned, mode: m.mode,
              cols: m.cols, rows: m.rows, tabOwned: m.tabOwned, local: m.local, clients: (m.clients || []).length });
            if (window.__d.length > 60) window.__d.shift();
          } catch (_) {}
        }
      });
      const send = ws.send.bind(ws);
      ws.send = (p) => { try { window.__sent.push({ at: Math.round(performance.now()), f: String(p).slice(0, 40) }); if (window.__sent.length > 60) window.__sent.shift(); } catch (e) {} return send(p); };
    } catch (e) {}
  });
  await page.waitForTimeout(2500);

  console.log('build=' + JSON.stringify(await page.evaluate(() => window.__muxBuild)));
  console.log('attach ' + JSON.stringify(await page.evaluate(STATE)));
  for (let i = 1; i <= 5; i += 1) {
    const before = tail(1);
    console.log(`pre${i}  ` + JSON.stringify(await page.evaluate(STATE)));
    await page.evaluate(() => { const b = document.querySelector('#sizebtn'); b.click(); });
    await page.waitForTimeout(2200);
    const st = await page.evaluate(STATE);
    const after = tail(1);
    const tl = await page.evaluate(() => ({ d: (window.__d || []).slice(-6), s: (window.__sent || []).slice(-6) }));
    console.log(`tap${i} ` + JSON.stringify(st) + (after[0] && after[0] !== before[0] ? `  tee="${after[0]}"` : ''));
    console.log('   d=' + JSON.stringify(tl.d));
    console.log('   s=' + JSON.stringify(tl.s));
  }
  console.log('errors=' + JSON.stringify(errs));
  await browser.close();
})().catch(e => { console.error('LADDER PROBE ERROR', e && (e.stack || e.message)); process.exit(2); });
