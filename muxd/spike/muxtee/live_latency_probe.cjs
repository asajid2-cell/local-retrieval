'use strict';
/* What "laggy" means on the deployed page, in numbers. Three things, measured separately:
 *
 *  A. KEYSTROKE ECHO - one key the user presses, until the character is painted. Broken into
 *     keydown -> ws send -> term.write -> render, so a slow hop can be named instead of guessed.
 *  B. COMMAND OUTPUT - Enter until the command's own output is painted (`echo LAT-%RANDOM%`, so the
 *     marker exists only in the OUTPUT, never in the echo of the typed line).
 *  C. RUBBERBAND - geometry samples at 10 Hz plus every term.resize, at local ownership, during a take,
 *     and after the ack. A pleasant pane is one geometry that never moves; the metric is TRANSITIONS.
 *  D. RELIABILITY - ws open/close events, page errors, relay /api/health (pc rtt, host frame age).
 *
 * Nothing here changes what the page shows beyond typing a command and (optionally) taking the grid.
 * Usage: keysafe run hl-owner-session -Command '... node live_latency_probe.cjs'
 *        MUX_SESSION=hl-owner-session  MUX_TAKE=1 (default)  MUX_IDLE_MS=20000  MUX_POST_MS=20000 */
const path = require('path');
function loadPlaywright() { const c = ['C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright', 'playwright']; let l; for (const x of c) { try { return require(x); } catch (e) { l = e; } } throw l; }
const { chromium } = loadPlaywright();
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.env.MUX_SESSION || 'hl-owner-session';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });
const W = Number(process.env.MUX_W || 1440), H = Number(process.env.MUX_H || 900), DPR = Number(process.env.MUX_DPR || 1);
const TAKE = process.env.MUX_TAKE !== '0';
const IDLE_MS = Number(process.env.MUX_IDLE_MS || 20000);
const POST_MS = Number(process.env.MUX_POST_MS || 20000);
const pct = (a, p) => { if (!a.length) return null; const s = a.slice().sort((x, y) => x - y); return +s[Math.min(s.length - 1, Math.floor((p / 100) * s.length))].toFixed(1); };
const stat = a => a.length ? { n: a.length, min: +Math.min(...a).toFixed(1), med: pct(a, 50), p90: pct(a, 90), max: +Math.max(...a).toFixed(1) } : null;

const STATE = () => {
  const g = n => { try { return eval(n); } catch (e) { return undefined; } };
  const host = document.querySelector('#term');
  const scr = host && host.querySelector('.xterm-screen');
  let cell = null;
  try { const c = term._core._renderService.dimensions.css.cell; cell = +c.width.toFixed(2) + 'x' + +c.height.toFixed(2); } catch (e) {}
  return {
    owner: g('sizeGridOwner'), busy: g('_gridBusy'), cols: g('term.cols'), rows: g('term.rows'),
    font: g('term.options.fontSize'), cell,
    span: (scr && host) ? +(scr.getBoundingClientRect().width / host.clientWidth).toFixed(3) : null,
    pan: !!(host && host.classList.contains('pan')), fit: g('viewportFit'), win: g('winSize'),
    epoch: g('sizeGridEpoch'),
  };
};

async function instrument(page) {
  await page.evaluate(() => {
    const now = () => performance.now();
    const L = window.__lat = { keys: [], sends: [], writes: [], renders: [], resizes: [], ws: [], errors: [] };
    try { const o = ws.send.bind(ws); ws.send = p => { const t = now(); try { L.sends.push({ t, f: String(p)[0], s: String(p).slice(0, 80) }); } catch (e) {} return o(p); }; } catch (e) {}
    const dec = new TextDecoder('utf-8');
    try { const ow = term.write.bind(term); term.write = (d, cb) => { const t = now(); try { L.writes.push({ t, s: typeof d === 'string' ? d : dec.decode(d) }); } catch (e) {} return ow(d, cb); }; } catch (e) {}
    try { term.onRender(() => { L.renders.push(now()); }); } catch (e) {}
    try { const ors = term.resize.bind(term); term.resize = (c, r) => { L.resizes.push({ t: now(), c, r }); return ors(c, r); }; } catch (e) {}
    document.addEventListener('keydown', e => { L.keys.push({ t: now(), k: e.key }); }, true);
    try { ws.addEventListener('open', () => L.ws.push({ t: now(), what: 'open' })); ws.addEventListener('close', () => L.ws.push({ t: now(), what: 'close' }));
      ws.addEventListener('error', () => L.ws.push({ t: now(), what: 'error' })); } catch (e) {}
  });
}

// One keystroke end to end: keydown -> the 'i' frame carrying it -> the write that echoes it -> the
// next render. Returns each hop in ms, relative to the keydown.
async function keyTrial(page, ch) {
  const before = await page.evaluate(() => ({ sends: window.__lat.sends.length, writes: window.__lat.writes.length, keys: window.__lat.keys.length }));
  await page.keyboard.press(ch);
  await page.waitForTimeout(700);
  return page.evaluate(({ ch, before }) => {
    const L = window.__lat;
    const key = L.keys.slice(before.keys).find(k => k.k === ch || k.k === ch.toUpperCase());
    if (!key) return { ch, error: 'no keydown recorded' };
    const send = L.sends.slice(before.sends).find(s => s.f === 'i' && s.s.indexOf(ch) >= 0);
    const write = send ? L.writes.slice(before.writes).find(w => w.t >= send.t && String(w.s).indexOf(ch) >= 0) : null;
    const render = write ? L.renders.find(t => t >= write.t) : null;
    return {
      ch,
      toSend: send ? +(send.t - key.t).toFixed(1) : null,
      net: (send && write) ? +(write.t - send.t).toFixed(1) : null,
      toPaint: render ? +(render - key.t).toFixed(1) : null,
      writeToPaint: (write && render) ? +(render - write.t).toFixed(1) : null,
    };
  }, { ch, before });
}

// Enter -> the command's OUTPUT written and painted. `%RANDOM%` never appears in the typed line.
async function outputTrial(page) {
  await page.keyboard.press('Escape');           // cmd.exe: clear whatever is on the input line
  await page.waitForTimeout(120);
  const before = await page.evaluate(() => ({ sends: window.__lat.sends.length, keys: window.__lat.keys.length, writes: window.__lat.writes.length }));
  await page.keyboard.type('echo LAT-%RANDOM%', { delay: 15 });
  await page.keyboard.press('Enter');
  await page.waitForTimeout(1400);
  return page.evaluate(before => {
    const L = window.__lat;
    const key = L.keys.slice(before.keys).find(k => k.k === 'Enter');
    if (!key) return { error: 'no Enter keydown' };
    const send = L.sends.slice(before.sends).find(s => s.f === 'i' && s.s.indexOf('\\r') >= 0);
    const write = L.writes.slice(before.writes).find(w => w.t >= (send ? send.t : key.t) && /LAT-\d+/.test(String(w.s)));
    const render = write ? L.renders.find(t => t >= write.t) : null;
    return {
      m: write ? (String(write.s).match(/LAT-\d+/) || [])[0] : null,
      net: (send && write) ? +(write.t - send.t).toFixed(1) : null,
      toPaint: render ? +(render - key.t).toFixed(1) : null,
    };
  }, before);
}

// Sample geometry at 10 Hz and count TRANSITIONS; also report every term.resize inside the window.
async function churn(page, ms, label, onState) {
  const t0 = Date.now();
  let prevKey = '', first = null, last = null, transitions = 0, distinct = new Set(), samples = 0;
  const seq = [];
  const resizes0 = await page.evaluate(() => window.__lat.resizes.length);
  while (Date.now() - t0 < ms) {
    const s = await page.evaluate(STATE);
    samples += 1;
    const key = `${s.owner}|${s.cols}x${s.rows}|${s.cell}|${s.span}|${s.pan}`;
    distinct.add(key);
    if (key !== prevKey) {
      transitions += 1; seq.push(`+${String(Date.now() - t0).padStart(5)}ms ${s.cols}x${s.rows} cell=${s.cell} span=${s.span} owner=${s.owner}${s.pan ? ' pan' : ''}`);
      prevKey = key;
    }
    if (!first) first = s; last = s;
    if (onState) onState(s);
    await page.waitForTimeout(100);
  }
  const resizes = await page.evaluate(n => window.__lat.resizes.slice(n).map(r => ({ t: Math.round(r.t), c: r.c, r: r.r })), resizes0);
  console.log(`CHURN ${label} ${JSON.stringify({ samples, transitions, distinctGeometries: distinct.size, resizes: resizes.length, first: first && `${first.cols}x${first.rows}`, last: last && `${last.cols}x${last.rows}` })}`);
  seq.slice(0, 14).forEach(l => console.log(`  ${label} ${l}`));
  if (resizes.length) console.log(`  ${label} resizes ${JSON.stringify(resizes.slice(0, 12))}`);
  return { transitions, resizes: resizes.length };
}

(async () => {
  if (!pairs.length) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: W, height: H }, deviceScaleFactor: DPR, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  const errs = []; page.on('pageerror', e => errs.push(String(e && e.message || e)));
  const tNav = Date.now();
  await page.goto(`${BASE}/?s=${encodeURIComponent(S)}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });
  const navMs = Date.now() - tNav;
  await page.waitForTimeout(2500);
  await instrument(page);
  const health = await page.evaluate(async base => { try { const h = await (await fetch(base + '/api/health')).json(); return { ok: h.ok, degraded: h.degraded, pcRttMs: h.pc && h.pc.rttMs, pcReach: h.pc && h.pc.reachable, hostFrames: h.host && h.host.connected, frameAgeMs: h.host && h.host.frameAgeMs, armed: h.armed, node: h.node }; } catch (e) { return { error: String(e) }; } }, BASE);
  console.log('HEALTH ' + JSON.stringify(health) + ' nav=' + navMs + 'ms');
  console.log('PRE ' + JSON.stringify(await page.evaluate(STATE)));

  // Focus the way a user does.
  const box = await page.locator('#term .xterm-screen').boundingBox();
  await page.mouse.click(box.x + 80, box.y + 40);
  await page.waitForTimeout(300);

  // A. keystroke echo
  const keyRows = [];
  for (const ch of ['z', 'y', 'x', 'w', 'v', 'u', 't', 's', 'r', 'q']) keyRows.push(await keyTrial(page, ch));
  await page.keyboard.press('Escape'); await page.waitForTimeout(200);
  console.log('KEY ' + JSON.stringify(keyRows.filter(r => !r.error)));
  for (const k of ['toSend', 'net', 'writeToPaint', 'toPaint']) {
    const v = keyRows.filter(r => r && !r.error && r[k] != null).map(r => r[k]);
    console.log(`KEY-${k} ` + JSON.stringify(stat(v)));
  }

  // B. command output
  const outRows = [];
  for (let i = 0; i < 6; i += 1) outRows.push(await outputTrial(page));
  console.log('OUT ' + JSON.stringify(outRows));
  console.log('OUT-net ' + JSON.stringify(stat(outRows.filter(r => r.net != null).map(r => r.net))));
  console.log('OUT-toPaint ' + JSON.stringify(stat(outRows.filter(r => r.toPaint != null).map(r => r.toPaint))));

  // C. rubberband while locally owned (the default posture)
  await churn(page, IDLE_MS, 'idle-local');

  // D. take the grid, time the commit, then watch it settle
  if (TAKE) {
    const clickBefore = await page.evaluate(() => window.__lat.sends.length);
    let takeAck = null;
    for (let i = 0; i < 4; i += 1) {
      const s = await page.evaluate(STATE);
      if (s.owner === 'mine') break;
      await page.evaluate(() => { window.__muxGridTiming = { clickAt: Math.round(performance.now()), ackAt: 0 }; document.querySelector('#sizebtn').click(); });
      const clicked = Date.now();
      try { await page.waitForFunction(() => { try { return sizeGridOwner === 'mine'; } catch (e) { return false; } }, null, { timeout: 12000 }); takeAck = Date.now() - clicked; }
      catch (e) { console.log('TAKE tap' + (i + 1) + ' no ack in 12s ' + JSON.stringify(await page.evaluate(STATE))); }
      await page.waitForTimeout(600);
      console.log(`TAKE tap${i + 1} ` + JSON.stringify(await page.evaluate(STATE)) + (takeAck != null ? ` ack=${takeAck}ms` : ''));
      if (takeAck != null) break;
    }
    console.log('TAKE-res ' + JSON.stringify(await page.evaluate(() => window.__muxGridTiming || null)));
    await churn(page, POST_MS, 'owned');
    // Typing must still land while owned.
    const owned = await keyTrial(page, 'n');
    console.log('OWNED-KEY ' + JSON.stringify(owned));
  }

  // E. release, and time it
  const relBefore = await page.evaluate(STATE);
  if (relBefore.owner === 'mine') {
    const clicked = Date.now();
    for (let i = 0; i < 4; i += 1) { await page.evaluate(() => document.querySelector('#sizebtn').click()); await page.waitForTimeout(500); if ((await page.evaluate(STATE)).owner === 'local') break; }
    console.log('RELEASE ' + JSON.stringify(await page.evaluate(STATE)) + ' in ' + (Date.now() - clicked) + 'ms');
    await churn(page, 5000, 'released');
  }

  const wsEvents = await page.evaluate(() => window.__lat.ws);
  const wCounts = await page.evaluate(() => ({ sends: window.__lat.sends.length, writes: window.__lat.writes.length, renders: window.__lat.renders.length, resizes: window.__lat.resizes.length }));
  console.log('WS ' + JSON.stringify(wsEvents));
  console.log('COUNTS ' + JSON.stringify(wCounts));
  console.log('PAGEERRORS ' + JSON.stringify(errs));
  const healthEnd = await page.evaluate(async base => { try { const h = await (await fetch(base + '/api/health')).json(); return { pcRttMs: h.pc && h.pc.rttMs, frameAgeMs: h.host && h.host.frameAgeMs, degraded: h.degraded, gaveUp: h.gaveUp }; } catch (e) { return { error: String(e) }; } }, BASE);
  console.log('HEALTH-END ' + JSON.stringify(healthEnd));
  await ctx.close(); await browser.close();
})().catch(e => { console.error('LATENCY PROBE ERROR', e && (e.stack || e.message)); process.exit(2); });
