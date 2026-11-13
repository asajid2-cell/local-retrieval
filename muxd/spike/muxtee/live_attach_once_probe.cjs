'use strict';
/* Did the attach pay for the history once or twice? Measured on the DEPLOYED page.
 *
 * The double replay (contract 10a) sent a new viewer's history twice: once as muxd's scrollback reply and
 * once as the tee's watch-flip resync, and both had to parse before the first keystroke could paint. The
 * fix lets the attach wait for the resync. This probe watches the page's own websocket frames and counts
 * how many of them carry the full screen, and how long the pane took to be painted.
 *
 * It types NOTHING. It only attaches, counts, and detaches.
 * Usage: keysafe run hl-owner-session -Command '... node live_attach_once_probe.cjs'
 *        MUX_SESSION=<name> (must already exist - the probe refuses otherwise)
 */
const { chromium } = require('C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright');
const { assertSession } = require('./probe_guard.cjs');
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.env.MUX_SESSION || 'hl-ga1-live';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });
const pct = (a, p) => { if (!a.length) return null; const s = a.slice().sort((x, y) => x - y); return +s[Math.min(s.length - 1, Math.floor((p / 100) * s.length))].toFixed(1); };

(async () => {
  if (!pairs.length) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'en-US', userAgent: UA });
  await ctx.addCookies(pairs.map(c => ({ ...c, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const page = await ctx.newPage();
  // Count inbound websocket text frames by their kind, and record the first paint. `t.write` is wrapped
  // BEFORE the page script runs so every byte the relay sends is seen exactly as the viewer saw it.
  await page.addInitScript(() => {
    const L = window.__ao = { frames: [], writes: [], t0: 0 };
    const dec = new TextDecoder('utf-8');
    const hookWs = () => {
      const Orig = window.WebSocket;
      if (!Orig || Orig.__aoPatched) { setTimeout(hookWs, 5); return; }
      Orig.__aoPatched = true;
      const orig = Orig.prototype.send;
      window.WebSocket = function (url, protos) {
        const ws = protos ? new Orig(url, protos) : new Orig(url);
        ws.addEventListener('message', ev => {
          if (typeof ev.data === 'string') {
            const head = ev.data.slice(0, 1);
            L.frames.push({ t: performance.now(), kind: head, n: ev.data.length });
          } else if (ev.data) {
            L.frames.push({ t: performance.now(), kind: '[bin]', n: ev.data.byteLength || 0 });
          }
        });
        return ws;
      };
      window.WebSocket.prototype = Orig.prototype;
      window.WebSocket.CONNECTING = Orig.CONNECTING;
      window.WebSocket.OPEN = Orig.OPEN;
      window.WebSocket.CLOSING = Orig.CLOSING;
      window.WebSocket.CLOSED = Orig.CLOSED;
      void orig;
    };
    hookWs();
    const boot = () => {
      if (typeof term === 'undefined' || !term || !term.write) { setTimeout(boot, 5); return; }
      const ow = term.write.bind(term);
      term.write = (d, cb) => {
        const s = typeof d === 'string' ? d : dec.decode(d);
        L.writes.push({ t: performance.now(), n: s.length, tag: s.slice(0, 24) });
        return ow(d, cb);
      };
    };
    boot();
  });
  const t0 = Date.now();
  await page.goto(`${BASE}/?s=${encodeURIComponent(S)}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });
  await assertSession(page, S);
  // Wait until the pane has a non-blank screen OR the banner, then stop counting.
  await page.waitForFunction(() => {
    try {
      const b = term.buffer.active;
      for (let i = 0; i < b.length; i++) {
        const line = b.getLine(i); if (!line) continue;
        const t = line.translateToString(true);
        if (t && t.trim()) return true;
      }
      return /scrollback unavailable/.test(document.body.innerText || '');
    } catch (e) { return false; }
  }, null, { timeout: 30000 });
  const paintedAt = Date.now() - t0;
  await page.waitForTimeout(1200);
  const L = await page.evaluate(() => window.__ao);
  const screen = await page.evaluate(() => { try { const b = term.buffer.active; const out = []; for (let i = 0; i < Math.min(b.length, b.viewportY + term.rows); i++) { const l = b.getLine(i); if (l) out.push(l.translateToString(true)); } return out.join('\n'); } catch (e) { return String(e); } });
  // The banner in the FIRST rows may be scrollback, not the live screen: a banner that scrolled away is
  // not a banner the user sees. Report the viewport separately from the whole buffer.
  const viewport = await page.evaluate(() => { try { const b = term.buffer.active; const out = []; for (let i = b.viewportY; i < Math.min(b.length, b.viewportY + term.rows); i++) { const l = b.getLine(i); if (l) out.push(l.translateToString(true)); } return out.join('\n'); } catch (e) { return String(e); } });
  const banner = /scrollback unavailable/.test(screen);
  // A full-screen replay is a big `d`/binary blob; a live keystroke echo is tiny. Count the big ones.
  const big = L.frames.filter(f => f.n > 2000);
  const sizes = big.map(f => f.n);
  console.log(JSON.stringify({
    session: S,
    paintedMs: paintedAt,
    banner,
    totalFrames: L.frames.length,
    // What the relay ACTUALLY sent, by kind. A banner paints one tiny write; a real replay is a large one.
    frameKinds: L.frames.reduce((a, f) => { const k = f.kind + '@' + (f.n > 2000 ? 'big' : f.n > 200 ? 'mid' : 'small'); a[k] = (a[k] || 0) + 1; return a; }, {}),
    writeTimeline: L.writes.slice(0, 24).map(w => `${w.t.toFixed(0)}:${w.n}:${w.tag.replace(/[\x00-\x1f]/g, '.').slice(0, 18)}`),
    frameTimeline: L.frames.slice(0, 24).map(f => `${f.t.toFixed(0)}:${f.kind}:${f.n}`),
    bigFrames: big.length,
    bigSizes: sizes,
    bigKindCounts: big.reduce((a, f) => { a[f.kind] = (a[f.kind] || 0) + 1; return a; }, {}),
    writes: L.writes.length,
    firstWriteMs: L.writes.length ? +L.writes[0].t.toFixed(1) : null,
    screenHead: screen.split('\n').slice(0, 6),
    viewportHead: viewport.split('\n').slice(0, 8),
    viewportBanner: /scrollback unavailable/.test(viewport),
    viewportTail: viewport.split('\n').filter(l => l.trim()).slice(-6),
  }, null, 1));
  // THE ASSERTION, not just a report: on a teed tab this fix must repaint from exactly ONE ring reply and
  // must never leave the viewer on the banner. A duplicate replay shows up as two big rings in one attach.
  const ok = !banner && big.length === 1;
  console.log(JSON.stringify({ VERDICT: ok ? 'SINGLE-REPLAY' : 'FAILED', banner, ringReplies: big.length }));
  await ctx.close(); await browser.close();
  process.exit(ok ? 0 : 1);
})().catch(e => { console.error('PROBE ERROR', e && (e.stack || e.message)); process.exit(2); });
