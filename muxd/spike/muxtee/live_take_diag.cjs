'use strict';
/* Take the grid and watch it converge: samples the page every 300 ms for 8 s (grid, cell, span, font, pan,
 * focus, busy), then types a marker and checks the child actually echoed it. This is the "taking the grid
 * was broken, rendering weird, and I could not type - then it fixed itself" report, measured.
 * Usage: MUX_SESSION=hl-owner-session [MUX_W=2520 MUX_H=1186 MUX_DPR=1.5] node live_take_diag.cjs */
const path = require('path');
function loadPlaywright() { const c = ['C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright', 'playwright']; let l; for (const x of c) { try { return require(x); } catch (e) { l = e; } } throw l; }
const { chromium } = loadPlaywright();
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.env.MUX_SESSION || 'hl-owner-session';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });
const W = Number(process.env.MUX_W || 1440), H = Number(process.env.MUX_H || 900), DPR = Number(process.env.MUX_DPR || 1);
const MARK = 'MUXTYPE-' + Date.now().toString(36);

const STATE = () => {
  const g = n => { try { return eval(n); } catch (e) { return undefined; } };
  const host = document.querySelector('#term');
  const scr = host && host.querySelector('.xterm-screen');
  const act = document.activeElement;
  return {
    owner: g('sizeGridOwner'), busy: g('_gridBusy'), cols: g('term.cols'), rows: g('term.rows'),
    font: g('term.options.fontSize'), cell: (() => { try { const c = term._core._renderService.dimensions.css.cell; return +c.width.toFixed(2) + 'x' + +c.height.toFixed(2); } catch (e) { return null; } })(),
    span: (scr && host) ? +(scr.getBoundingClientRect().width / host.clientWidth).toFixed(3) : null,
    pan: !!(host && host.classList.contains('pan')), fit: g('viewportFit'), win: g('winSize'),
    focusInTerm: !!(act && host && host.contains(act)), chip: ((document.querySelector('#sizebtn') || {}).textContent || '').replace(/\s+/g, ' ').trim(),
    cols_acked: g('sizeGridEpoch'),
  };
};
const screenText = () => { try { return termVisibleText(); } catch (e) { return ''; } };

(async () => {
  if (!pairs.length) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: W, height: H }, deviceScaleFactor: DPR, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  const errs = []; page.on('pageerror', e => errs.push(String(e && e.message || e)));
  await page.goto(`${BASE}/?s=${encodeURIComponent(S)}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });
  await page.waitForTimeout(2500);
  await page.evaluate(() => {
    window.__sent = [];
    try { const o = ws.send.bind(ws); ws.send = (p) => { try { window.__sent.push(String(p).slice(0, 40)); } catch (e) {} return o(p); }; } catch (e) {}
  });
  console.log('pre ' + JSON.stringify(await page.evaluate(STATE)));
  const typeAndCheck = async (tag) => {
    const mark = 'MUXTYPE-' + tag + '-' + Date.now().toString(36);
    const before = await page.evaluate(() => (window.__sent || []).length);
    const box0 = await page.locator('#term .xterm-screen').boundingBox();
    await page.mouse.click(box0.x + 60, box0.y + 40);
    await page.waitForTimeout(200);
    await page.keyboard.type('echo ' + mark, { delay: 40 });
    await page.keyboard.press('Enter');
    await page.waitForTimeout(2500);
    const text = await page.evaluate(screenText);
    const sent = await page.evaluate(n => (window.__sent || []).slice(n), before);
    console.log(`type-${tag} ` + JSON.stringify({ marker: mark, echoed: text.indexOf(mark) >= 0, sentFrames: sent.length, first: sent.slice(0, 6) }));
    if (text.indexOf(mark) < 0) console.log(`screen-${tag} ` + JSON.stringify(text.slice(-300)));
    return text.indexOf(mark) >= 0;
  };
  await typeAndCheck('local');
  // Walk to the take (fill -> fit -> take, a tap at a time, bounded). MUX_SKIP_TAKE=1 is the CONTROL:
  // type without ever taking, to prove the harness's typing path works at all.
  const SKIP = process.env.MUX_SKIP_TAKE === '1';
  for (let i = 0; !SKIP && i < 4; i += 1) {
    const s = await page.evaluate(STATE);
    if (s.owner === 'mine') break;
    await page.evaluate(() => document.querySelector('#sizebtn').click());
    await page.waitForTimeout(1200);
    console.log(`tap${i + 1} ` + JSON.stringify(await page.evaluate(STATE)));
  }
  // Converge watch.
  const t0 = Date.now();
  const WATCH = Number(process.env.MUX_WATCH_MS || 8000);
  let prevKey = '';
  while (Date.now() - t0 < WATCH) {
    const s = await page.evaluate(STATE);
    // Print on CHANGE only: the question is whether the committed grid moves again after the ack.
    const key = `${s.owner}|${s.cols}x${s.rows}|${s.font}|${s.span}|${s.pan}`;
    if (key !== prevKey) { prevKey = key; console.log(`t+${String(Date.now() - t0).padStart(5)}ms ` + JSON.stringify(s)); }
    await page.waitForTimeout(300);
  }
  // Type into the page as a real user would (click the visible grid first, then type).
  const box = await page.locator('#term .xterm-screen').boundingBox();
  await page.mouse.click(box.x + 60, box.y + 40);
  await page.waitForTimeout(300);
  console.log('afterClick ' + JSON.stringify(await page.evaluate(STATE)));
  await page.keyboard.type(`echo ${MARK}`, { delay: 40 });
  await page.keyboard.press('Enter');
  await page.waitForTimeout(2500);
  const text = await page.evaluate(screenText);
  const echoed = text.indexOf(MARK) >= 0;
  console.log('echo ' + JSON.stringify({ marker: MARK, echoed }));
  if (!echoed) console.log('screen ' + JSON.stringify(text.slice(-600)));
  console.log('post ' + JSON.stringify(await page.evaluate(STATE)));
  console.log('errors ' + JSON.stringify(errs));
  // Leave the grid where we found it.
  if (!SKIP && (await page.evaluate(STATE)).owner === 'mine') { await page.evaluate(() => document.querySelector('#sizebtn').click()); await page.waitForTimeout(1500); console.log('release ' + JSON.stringify(await page.evaluate(STATE))); }
  await ctx.close(); await browser.close();
})().catch(e => { console.error('TAKE DIAG ERROR', e && (e.stack || e.message)); process.exit(2); });
