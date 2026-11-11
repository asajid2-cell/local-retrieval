'use strict';
// Does a real browser CLICK on the viewer reach the app? Reads the page's own input log.
const path = require('path');
function loadPlaywright() { const c = ['C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright', 'playwright']; let l; for (const x of c) { try { return require(x); } catch (e) { l = e; } } throw l; }
const { chromium } = loadPlaywright();
const { assertSession } = require('./probe_guard.cjs');
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });
(async () => {
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  await page.goto('https://harmonizerlabs.cc/multiplex/?s=hl-owner-session', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1; } catch (e) { return false; } }, null, { timeout: 30000 });
  await assertSession(page, S);   // fail closed: never type into a pane this probe was not given
  await page.waitForTimeout(2500);
  const pre = await page.evaluate(() => ({ armed: (() => { try { return mouseGuard.mouseActive(); } catch (e) { return null; } })(), allowed: (() => { try { return mouseGuard.inputAllowed(); } catch (e) { return null; } })(), solo: (() => { try { return soleTerminal; } catch (e) { return null; } })() }));
  await page.evaluate(() => { window.__out = []; try { const o = ws.send.bind(ws); ws.send = (p) => { window.__out.push(String(p).slice(0, 48)); return o(p); }; } catch (e) {} });
  const box = await page.locator('#term .xterm-screen').boundingBox();
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
  await page.waitForTimeout(800);
  const post = await page.evaluate(() => ({
    out: (window.__out || []).slice(-8),
    log: (window.__muxInputLog || []).slice(-6),
    sel: (() => { try { return term.hasSelection(); } catch (e) { return null; } })(),
    frozen: (() => { try { return isFrozen(); } catch (e) { return null; } })(),
  }));
  console.log('pre ' + JSON.stringify(pre));
  console.log('post ' + JSON.stringify(post));
  await browser.close();
})().catch(e => { console.error('CLICK PROBE ERROR', e && (e.stack || e.message)); process.exit(2); });
