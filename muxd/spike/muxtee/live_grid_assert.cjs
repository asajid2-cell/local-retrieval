'use strict';
/*
 * LIVE proof against the deployed page, driven as a real user: attach, render, tap the chip through the
 * ladder, take the grid, paint it 1:1, release it, and report the wall-clock round trip.
 *
 * The gate credential is read from the environment by keysafe's runner (`run hl-owner-session -EnvVar
 * HLOWNER`) and placed in a cookie. It is never printed, logged, or written anywhere: every assertion
 * below reports state and timing only.
 */
const path = require('path');
function loadPlaywright() {
  const candidates = [
    process.env.MUX_RELAY_DIR ? path.join(process.env.MUX_RELAY_DIR, 'node_modules', 'playwright') : null,
    'C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright',
    'playwright',
  ].filter(Boolean);
  let last;
  for (const c of candidates) { try { return require(c); } catch (e) { last = e; } }
  throw last;
}
const { chromium } = loadPlaywright();
const { assertSession } = require('./probe_guard.cjs');

const BASE = process.env.MUX_LIVE_URL || 'https://harmonizerlabs.cc/multiplex/';
const SESSION = process.env.MUX_LIVE_SESSION || 'hl-owner-session';
const TOKEN = process.env.HLOWNER || '';
// hl-auth binds the session to the UA it was minted with; present exactly that one.
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const HEADFUL = process.argv.includes('--headful');

const rows = [];
function check(id, ok, detail) {
  rows.push({ id, ok, detail });
  console.log(`${ok ? 'PASS' : 'FAIL'} ${id}${detail ? ' - ' + detail : ''}`);
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function state(page) {
  return page.evaluate(() => {
    const host = document.querySelector('#term');
    const scr = host && host.querySelector('.xterm-screen');
    const tf = scr ? getComputedStyle(scr).transform : 'none';
    let sx = 1, sy = 1;
    if (tf && tf !== 'none') { const m = tf.match(/matrix\(([^)]+)\)/); if (m) { const p = m[1].split(',').map(Number); sx = p[0]; sy = p[3]; } }
    const g = (n) => { try { return eval(n); } catch (e) { return undefined; } };
    const d = (window.__d && window.__d.length) ? window.__d[window.__d.length - 1] : null;
    const sr = scr ? scr.getBoundingClientRect() : null;
    return {
      cols: g('term.cols'), rows: g('term.rows'), fontSize: g('term.options.fontSize'),
      transform: tf, sx, sy, gridOwner: d && d.gridOwner, gridEpoch: d && d.gridEpoch,
      mode: g('tabFitMode'), chip: (document.querySelector('#sizebtn') || {}).textContent || '',
      hostW: host ? host.clientWidth : 0, screenW: sr ? Math.round(sr.width) : 0,
      timing: g('window.__muxGridTiming') || null,
    };
  });
}

(async () => {
  if (!TOKEN) { console.error('HLOWNER missing: run under keysafe'); process.exit(2); }
  // The credential is the full Cookie HEADER (hl_session + CSRF), not a bare session value: hl-auth binds
  // the session to the exact header the owner test credential was minted with. Every other live probe
  // splits it into pairs the same way, so this one does too rather than inventing a second shape.
  const pairs = TOKEN.split(';').map(x => x.trim()).filter(Boolean).map(x => {
    const i = x.indexOf('='); return { name: x.slice(0, i), value: x.slice(i + 1) };
  });
  if (!pairs.length) { console.error('HLOWNER has no cookie pairs'); process.exit(2); }
  const browser = await chromium.launch({ headless: !HEADFUL, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(String((e && e.message) || e)));
  const url = `${BASE}?s=${encodeURIComponent(SESSION)}`;
  let status = 0;
  try {
    const resp = await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 60000 });
    status = resp ? resp.status() : 0;
    await page.evaluate(() => {
      window.__d = [];
      let grabbed = false;
      const grab = () => {
        try {
          if (typeof ws === 'undefined' || !ws || grabbed) return;
          grabbed = true;
          ws.addEventListener('message', (e) => {
            const s = typeof e.data === 'string' ? e.data : '';
            if (s && s[0] === 'd') { try { window.__d.push(JSON.parse(s.slice(1))); } catch (_) {} }
          });
        } catch (_) {}
      };
      grab(); setInterval(grab, 200);
    });
    // Wait for the session LIST, not just the terminal: the guard below reads window._sessions, and a
    // terminal that exists before the list arrives attaches to nothing (current === null), which the
    // guard would - correctly - refuse. The other live probes wait on `current` for the same reason.
    await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });
  await assertSession(page, SESSION);   // fail closed: never type into a pane this probe was not given
    await sleep(2500);
    let s = await state(page);
    check('L0 the deployed page attaches with the real session cookie',
      status === 200 && !/\/login/.test(page.url()) && s.cols > 10 && s.rows > 5 && errors.length === 0,
      `http=${status} term=${s.cols}x${s.rows} errors=${errors.length} frames=${(s.gridOwner !== undefined)}`);
    if (!(s.cols > 10)) { console.log(JSON.stringify({ errors, url: page.url() })); process.exitCode = 1; return; }

    const localOwner = s.gridOwner === 'local' || (s.gridOwner === undefined && /fits|fills/.test(s.chip));
    check('L1 local ownership is the default on the live page',
      localOwner && Math.abs(s.sx - 1) < 1e-6 && Math.abs(s.sy - 1) < 1e-6,
      `gridOwner=${s.gridOwner} transform=${s.transform} chip=${JSON.stringify(s.chip)} term=${s.cols}x${s.rows}`);

    // The doneWhen row the ladder probe watches but never asserts: with LCAL ownership holding, the pane
    // must stay put. Fill mode first (the default), then a 3 s window in which NO resize frame and no
    // commit may appear - a fill mode that quietly resized the pty would show up here as a 'v' or an 'r'
    // or a gridEpoch bump, and the term size must not move a cell.
    await page.evaluate(() => { window.__localStart = { d: (window.__d || []).length, epoch: (window.__d || []).slice(-1)[0] && window.__d.slice(-1)[0].gridEpoch }; });
    const before = await state(page);
    await sleep(3000);
    const after = await state(page);
    const frames = await page.evaluate(() => {
      const d = window.__d || [];
      const start = window.__localStart ? window.__localStart.d : 0;
      const fresh = d.slice(start);
      return {
        n: fresh.length,
        kinds: fresh.map(f => String(f.t || f.kind || '?')),
        epochStart: window.__localStart ? window.__localStart.epoch : null,
        epochEnd: d.length ? d[d.length - 1].gridEpoch : null,
      };
    });
    check('L1b local ownership sends NO resize traffic and never moves the pane',
      before.mode === after.mode && before.cols === after.cols && before.rows === after.rows
        && frames.n === 0,
      `mode=${before.mode}->${after.mode} term=${before.cols}x${before.rows}->${after.cols}x${after.rows} `
      + `frames=${frames.n} epoch=${frames.epochStart}->${frames.epochEnd}`);

    // Walk the ladder to "take". fill -> fit -> take; never loop (each extra tap is a pin/release).
    const tap = async () => {
      await page.evaluate(() => document.querySelector('#sizebtn').click());
      await sleep(900);
    };
    let mode = s.mode;
    if (mode === 'fill') { await tap(); mode = (await state(page)).mode; }
    check('L2 fit paints the pane grid without taking it',
      mode === 'fit' && (await state(page)).gridOwner === 'local',
      `mode=${mode} chip=${JSON.stringify((await state(page)).chip)}`);

    await tap();
    let took = null;
    const end = Date.now() + 15000;
    while (Date.now() < end) {
      const st = await state(page);
      if (st.gridOwner === 'mine') { took = st; break; }
      await sleep(250);
    }
    const rt = took && took.timing && took.timing.ackAt ? took.timing.ackAt - took.timing.clickAt : 0;
    check('L3 the chip takes the grid on the live page', !!took,
      took ? `term=${took.cols}x${took.rows} chip=${JSON.stringify(took.chip)}` : 'no committed ownership inside 15 s');
    if (took) {
      check('L3 the handover round trip is fast', rt > 0 && rt < 1500, `clickToCommitMs=${rt}`);
      const span = took.hostW ? took.screenW / took.hostW : 0;
      check('L3 the owned grid paints 1:1 with no distortion',
        Math.abs(took.sx - 1) < 1e-6 && Math.abs(took.sy - 1) < 1e-6 && span > 0.98 && span <= 1.02
        && Number.isInteger(took.fontSize),
        `transform=${took.transform} span=${span.toFixed(3)} font=${took.fontSize} pan=${!!(took.transform !== 'none')}`);
    }

    await tap();
    let back = null;
    const end2 = Date.now() + 15000;
    while (Date.now() < end2) {
      const st = await state(page);
      if (st.gridOwner === 'local') { back = st; break; }
      await sleep(250);
    }
    check('L4 a second tap returns the grid to the PC', !!back,
      back ? `term=${back.cols}x${back.rows} chip=${JSON.stringify(back.chip)}` : 'still owned after 15 s');

    const failed = rows.filter((r) => !r.ok).map((r) => r.id);
    console.log(failed.length ? `LIVE PROOF FAIL: ${failed.join(', ')}` : 'LIVE PROOF PASS');
    process.exitCode = failed.length ? 1 : 0;
  } finally {
    await ctx.close();
    await browser.close();
  }
})().catch((e) => { console.error('LIVE ASSERT ERROR', e && (e.stack || e.message)); process.exit(2); });
