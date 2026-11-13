'use strict';
/* DRIVE the phone path the way a person does, and report what each step actually produces.
 *
 * This is the "real interactions" half of the phone work. It does not assert theory: it taps the notice,
 * waits for the grid to commit, reads the font/columns/clipping the renderer really has, types a message
 * into the conversation view and reads back what the app echoed.
 *
 * Read-only except the taps and one typed message (which the probe deletes from the composer only - it
 * DOES send, because that is the interaction under test). Usage:
 *   keysafe run hl-owner-session -Command 'powershell -File run_with_cookie.ps1 -Script phone_drive.cjs'
 *   MUX_SESSION=tab-26f4 (or whichever teed tab to drive)
 */
const { chromium } = require('C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright');
const { assertSession } = require('./probe_guard.cjs');
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.env.MUX_SESSION || 'tab-26f4';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });
const out = [];
function say(tag, o) { console.log(tag, JSON.stringify(o, null, 1)); out.push([tag, o]); }

(async () => {
  if (!pairs.length) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({
    viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, isMobile: true, hasTouch: true,
    locale: 'en-US', userAgent: UA,
  });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  const errs = [];
  page.on('pageerror', e => errs.push('PAGEERROR ' + String(e && e.message || e)));

  const snap = () => page.evaluate(() => {
    const host = document.querySelector('#term');
    const scr = host && host.querySelector('.xterm-screen');
    const notice = document.getElementById('narrowgrid');
    return {
      view: typeof mobileView !== 'undefined' ? mobileView : null,
      build: window.__muxBuild,
      chip: ((document.querySelector('#statusmeta') || {}).textContent || '').trim(),
      cols: term.cols, rows: term.rows, font: term.options.fontSize,
      paneW: host ? host.clientWidth : null,
      paneH: host ? host.clientHeight : null,
      screenW: scr ? Math.round(scr.getBoundingClientRect().width) : null,
      clipped: scr ? Math.round(scr.getBoundingClientRect().width) > host.clientWidth + 1 : null,
      notice: notice ? { text: (notice.innerText || '').replace(/\s+/g, ' ').trim(), rect: (() => { const r = notice.getBoundingClientRect(); return { top: Math.round(r.top), h: Math.round(r.height) }; })() } : null,
      pinned: typeof sizePinned !== 'undefined' ? sizePinned : null,
      mine: typeof sizeMine !== 'undefined' ? sizeMine : null,
      mode: typeof tabFitPaneMode !== 'undefined' ? tabFitPaneMode : null,
      hostCols: (() => { try { const s = (window._sessions || []).find(x => x.name === current); return s ? s.cols + 'x' + s.rows : null; } catch (e) { return null; } })(),
    };
  });
  const waitPainted = () => page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });

  await page.goto(`${BASE}/?s=${encodeURIComponent(S)}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await waitPainted();
  await assertSession(page, S);
  await page.waitForTimeout(3500);

  say('A0_BOOT', await snap());
  await page.screenshot({ path: 'drive-a0-boot.png' });

  // ---- STEP 1: the notice must be ON the pane, not below the nav -------------------------------
  const geometry = await page.evaluate(() => {
    const n = document.getElementById('narrowgrid');
    if (!n) return { present: false };
    const nr = n.getBoundingClientRect();
    const tr = document.querySelector('#term').getBoundingClientRect();
    return { present: true, top: Math.round(nr.top), bottom: Math.round(nr.bottom), h: Math.round(nr.height),
      paneTop: Math.round(tr.top), insidePane: nr.top >= tr.top - 2 && nr.top < tr.bottom,
      buttons: Array.from(n.querySelectorAll('button')).map(b => b.textContent.trim()) };
  });
  say('A1_NOTICE_GEOMETRY', geometry);

  // ---- STEP 2: "Resize to this screen" - the real fix, tapped like a user -----------------------
  const t0 = Date.now();
  const tapped = await page.evaluate(() => {
    const n = document.getElementById('narrowgrid');
    if (!n) return false;
    const b = Array.from(n.querySelectorAll('button')).find(x => /resize/i.test(x.textContent));
    if (!b) return false;
    b.click(); return true;
  });
  say('A2_TAP_TAKE', { tapped });
  if (tapped) {
    // Wait for the commit: the phone owns the grid and the columns fall into a readable range.
    let committed = false;
    for (let i = 0; i < 40; i++) {
      await page.waitForTimeout(250);
      const s = await snap();
      if (s.mine && s.cols < 120) { committed = true; break; }
    }
    say('A3_AFTER_TAKE', { committed, ms: Date.now() - t0, ...(await snap()) });
    await page.screenshot({ path: 'drive-a3-taken.png' });

    // ---- STEP 3: type into the terminal (the key path) and see it echo -------------------------
    const before = await page.evaluate(() => { const b = term.buffer.active; return { len: b.length, y: b.viewportY }; });
    await page.evaluate(() => { try { term.focus(); } catch (e) {} });
    await page.keyboard.type('echo phone-keypath-ok');
    await page.waitForTimeout(400);
    await page.keyboard.press('Enter');
    await page.waitForTimeout(1800);
    const echoed = await page.evaluate(() => {
      const b = term.buffer.active; const rows = [];
      for (let i = Math.max(0, b.length - 30); i < b.length; i++) { const l = b.getLine(i); if (l) { const t = l.translateToString(true); if (t.trim()) rows.push(t.slice(0, 90)); } }
      return { found: rows.some(r => /phone-keypath-ok/.test(r)), rows: rows.slice(-8) };
    });
    say('A4_KEY_ECHO', { before, ...echoed });
    await page.screenshot({ path: 'drive-a4-typed.png' });

    // ---- STEP 4: go back to the PC's grid (the chip), and confirm the notice returns -------------
    const tappedBack = await page.evaluate(() => { const b = document.querySelector('#statusmeta'); if (!b) return false; b.click(); return true; });
    await page.waitForTimeout(400);
    await page.evaluate(() => { const b = document.querySelector('#statusmeta'); if (b) b.click(); return true; });
    for (let i = 0; i < 30; i++) {
      await page.waitForTimeout(250);
      const s = await snap();
      if (!s.mine && s.cols > 150) break;
    }
    say('A5_AFTER_RELEASE', { tappedBack, ...(await snap()) });
    await page.screenshot({ path: 'drive-a5-released.png' });
  }

  // ---- STEP 5: the conversation view, and a real message sent from it ---------------------------
  await page.evaluate(() => { const b = document.querySelector('[data-mobile-view="threads"]'); if (b) b.click(); });
  await page.waitForTimeout(1200);
  const th = await page.evaluate(() => {
    const bodies = Array.from(document.querySelectorAll('#threads .th-body')).slice(-4).map(b => ({
      who: (b.parentNode.querySelector('.th-who') || {}).textContent || '', t: b.textContent.slice(0, 160),
    }));
    const worst = Array.from(document.querySelectorAll('#threads .th-body')).map(b => {
      const r = b.getBoundingClientRect(); return Math.round(r.width);
    });
    return { count: document.querySelectorAll('#threads .th-msg').length,
      overflowX: document.documentElement.scrollWidth > window.innerWidth + 1,
      maxBodyW: worst.length ? Math.max(...worst) : 0, last: bodies };
  });
  say('A6_THREADS', th);
  await page.screenshot({ path: 'drive-a6-threads.png' });

  await page.evaluate(() => { const t = document.querySelector('#threadinput'); if (t) { t.value = 'phone drive: reply path check'; t.dispatchEvent(new Event('input', { bubbles: true })); } });
  await page.waitForTimeout(200);
  const sent = await page.evaluate(() => {
    const f = document.querySelector('#composer');
    if (!f) return false;
    f.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    return true;
  });
  await page.waitForTimeout(2500);
  const afterSend = await page.evaluate(() => ({
    composer: (document.querySelector('#threadinput') || {}).value,
    echoed: (() => { try { const b = term.buffer.active; for (let i = b.length - 1; i >= Math.max(0, b.length - 40); i--) { const l = b.getLine(i); if (l && /reply path check/.test(l.translateToString(true))) return true; } } catch (e) {} return false; })(),
    count: document.querySelectorAll('#threads .th-msg').length,
  }));
  say('A7_SENT', { sent, ...afterSend });
  await page.screenshot({ path: 'drive-a7-sent.png' });

  console.log('ERRS', JSON.stringify(errs.slice(0, 5)));
  await ctx.close(); await browser.close();
})().catch(e => { console.error('DRIVE ERROR', e && (e.stack || e.message)); process.exit(2); });
