'use strict';
/* THE decisive paint check: measure the OWNED grid's real column pitch after the screen has gone QUIET.
 *
 * Earlier probes screenshotted during the re-layout, so a 209-column frame at the old font was what got
 * captured while the state read 49 columns. This one waits for the buffer to stop changing AND for two
 * consecutive frames to agree on cols/rows/cell before it reads a pixel.
 *
 * The measurement: the longest pure-ASCII buffer line, its character count, and the ink extent of the row
 * band it occupies. pitch == cell.width means the paint is 1:1; pitch == cell.width * k means the renderer
 * is drawing k times wider than the grid it publishes.
 */
const { chromium } = require('C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright');
const { assertSession } = require('./probe_guard.cjs');
const fs = require('fs'), zlib = require('zlib');
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.env.MUX_SESSION || 'tab-26f4';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const pairs = COOKIE.split(';').map(p => p.trim()).filter(Boolean).map(p => { const i = p.indexOf('='); return { name: p.slice(0, i), value: p.slice(i + 1) }; });

function readPNG(f) {
  const b = fs.readFileSync(f); let p = 8, w = 0, h = 0, idat = [];
  while (p < b.length) { const len = b.readUInt32BE(p); const type = b.toString('ascii', p + 4, p + 8); const d = b.slice(p + 8, p + 8 + len);
    if (type === 'IHDR') { w = d.readUInt32BE(0); h = d.readUInt32BE(4); } else if (type === 'IDAT') idat.push(d); else if (type === 'IEND') break; p += 12 + len; }
  const raw = zlib.inflateSync(Buffer.concat(idat)); const bpp = 3, stride = w * bpp; const out = Buffer.alloc(w * h * bpp); let pos = 0;
  for (let y = 0; y < h; y++) { const ft = raw[pos++]; const line = raw.slice(pos, pos + stride); pos += stride;
    for (let x = 0; x < stride; x++) { const a = x >= bpp ? out[y * stride + x - bpp] : 0; const bi = y > 0 ? out[(y - 1) * stride + x] : 0; const ci = (x >= bpp && y > 0) ? out[(y - 1) * stride + x - bpp] : 0; let v = line[x];
      if (ft === 1) v += a; else if (ft === 2) v += bi; else if (ft === 3) v += ((a + bi) >> 1); else if (ft === 4) { const pp = a + bi - ci; const pa = Math.abs(pp - a), pb = Math.abs(pp - bi), pc = Math.abs(pp - ci); v += (pa <= pb && pa <= pc) ? a : (pb <= pc ? bi : ci); }
      out[y * stride + x] = v & 255; } }
  return { w, h, stride, data: out };
}
const lum = (img, x, y) => { const i = y * img.stride + x * 3; return img.data[i] + img.data[i + 1] + img.data[i + 2]; };

(async () => {
  const b = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const c = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, isMobile: true, hasTouch: true, locale: 'en-US', userAgent: UA });
  await c.addCookies(pairs.map(x => ({ ...x, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const p = await c.newPage();
  await p.goto(`${BASE}/?s=${encodeURIComponent(S)}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await p.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });
  await assertSession(p, S);
  await p.waitForTimeout(3000);

  // Quiet: the buffer's tail text and the geometry must be identical twice, 700 ms apart.
  const sig = () => p.evaluate(() => {
    const bb = term.buffer.active; let tail = '';
    for (let i = Math.max(0, bb.length - 3); i < bb.length; i++) { const l = bb.getLine(i); tail += (l ? l.translateToString(true) : ''); }
    let cell = null; try { const d = term._core._renderService.dimensions; cell = [Math.round(d.css.cell.width * 100) / 100, Math.round(d.css.cell.height * 100) / 100]; } catch (e) {}
    return { cols: term.cols, rows: term.rows, font: term.options.fontSize, cell, tail: tail.slice(-120) };
  });
  async function settle(what) {
    let prev = null;
    for (let i = 0; i < 30; i++) {
      const s = await sig();
      if (prev && prev.cols === s.cols && prev.rows === s.rows && prev.tail === s.tail
          && JSON.stringify(prev.cell) === JSON.stringify(s.cell)) return s;
      prev = s;
      await p.waitForTimeout(700);
    }
    console.log('WARN never settled:', what);
    return prev;
  }

  async function measure(tag, s) {
    await p.locator('#term').screenshot({ path: 'q-' + tag + '.png' });
    const img = readPNG('q-' + tag + '.png');
    const st = await p.evaluate(() => {
      const host = document.querySelector('#term');
      const bb = term.buffer.active;
      const lines = [];
      for (let i = bb.viewportY; i < Math.min(bb.length, bb.viewportY + term.rows); i++) {
        const l = bb.getLine(i); lines.push(l ? l.translateToString(true) : '');
      }
      const r = host.getBoundingClientRect();
      return { lines, viewportY: bb.viewportY, paneW: host.clientWidth, paneH: host.clientHeight,
        scrolledX: host.scrollLeft, hostX: r.x, hostY: r.y, hostW: r.width, hostH: r.height };
    });
    const perCss = img.w / st.paneW;
    const ch = s.cell ? s.cell[1] : 17;
    // Pick the longest ASCII line with no tabs and at least 20 chars.
    let pick = -1, bestLen = 0;
    st.lines.forEach((t, i) => { const clean = t.replace(/\s+$/, ''); if (/^[\x20-\x7e]+$/.test(clean) && clean.length > bestLen) { bestLen = clean.length; pick = i; } });
    if (pick < 0) return { tag, note: 'no ASCII line to measure' };
    const text = st.lines[pick].replace(/\s+$/, '');
    const bandTopCss = pick * ch, bandBotCss = bandTopCss + ch;
    let first = -1, last = -1;
    for (let y = Math.max(0, Math.floor(bandTopCss * perCss)); y < Math.min(img.h, Math.ceil(bandBotCss * perCss)); y++) {
      for (let x = 0; x < img.w; x++) if (lum(img, x, y) > 300) { if (first < 0 || x < first) first = x; if (x > last) last = x; }
    }
    const extentCss = first < 0 ? null : (last - first + 1) / perCss;
    return { tag, cols: s.cols, rows: s.rows, font: s.font, cellCss: s.cell,
      lineNo: pick, chars: text.length, text: text.slice(0, 60),
      inkExtentCss: extentCss == null ? null : Math.round(extentCss * 10) / 10,
      measuredPitchCss: extentCss == null || text.length < 2 ? null : Math.round((extentCss / (text.length - 1)) * 100) / 100,
      expectedPitchCss: s.cell ? s.cell[0] : null,
      ratio: extentCss == null || !s.cell || text.length < 2 ? null : Math.round((extentCss / (text.length - 1) / s.cell[0]) * 100) / 100,
      paneW: st.paneW, shotW: img.w };
  }

  const found = await settle('found');
  console.log('FOUND_SETTLED', JSON.stringify(found));
  console.log('M_FOUND', JSON.stringify(await measure('found', found), null, 1));

  await p.evaluate(() => { const b = document.querySelector('#statusmeta'); if (b) b.click(); });
  for (let i = 0; i < 40; i++) { await p.waitForTimeout(250); if ((await p.evaluate(() => term.cols)) < 120) break; }
  const owned = await settle('owned');
  console.log('OWNED_SETTLED', JSON.stringify(owned));
  console.log('M_OWNED', JSON.stringify(await measure('owned', owned), null, 1));

  await p.evaluate(() => { const b = document.querySelector('#statusmeta'); if (b) b.click(); });
  await p.waitForTimeout(2500);
  await c.close(); await b.close();
})().catch(e => { console.error('ERR', e && (e.stack || e.message)); process.exit(2); });
