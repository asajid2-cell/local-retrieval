'use strict';
/* Same screen, WebGL vs DOM renderer, same phone viewport: which one paints the grid 1:1?
 *
 * Discovered 2026-11-12: on the owned 49-column grid the WebGL canvas has a 376 px backing store (CSS
 * pixels) while the cell it is asked to draw is 23 DEVICE px wide - so each glyph is rasterised ~1.8x wider
 * than the cell it must fit, and the phone shows a handful of giant clipped letters. Measuring both
 * renderers on the same buffer settles whether that is a WebGL-only defect and which one is correct.
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

async function run(tag, query) {
  const b = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const c = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, isMobile: true, hasTouch: true, locale: 'en-US', userAgent: UA });
  await c.addCookies(pairs.map(x => ({ ...x, domain: 'harmonizerlabs.cc', path: '/', httpOnly: true, secure: true, sameSite: 'Lax' })));
  const p = await c.newPage();
  await p.goto(`${BASE}/?s=${encodeURIComponent(S)}&${query}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await p.waitForFunction(() => { try { return typeof term !== 'undefined' && term.cols > 1 && current; } catch (e) { return false; } }, null, { timeout: 30000 });
  await assertSession(p, S);
  await p.waitForTimeout(3000);
  // Take the grid so both runs measure the SAME owned geometry.
  await p.evaluate(() => { const e = document.querySelector('#statusmeta'); if (e) e.click(); });
  for (let i = 0; i < 40; i++) { await p.waitForTimeout(250); if ((await p.evaluate(() => term.cols)) < 120) break; }
  await p.waitForTimeout(2500);
  const info = await p.evaluate(() => {
    const host = document.querySelector('#term');
    const bb = term.buffer.active;
    const lines = [];
    for (let i = bb.viewportY; i < Math.min(bb.length, bb.viewportY + term.rows); i++) { const l = bb.getLine(i); lines.push(l ? l.translateToString(true) : ''); }
    let dim = null; try { const d = term._core._renderService.dimensions; dim = { devW: d.device.cell.width, cssW: d.css.cell.width, devH: d.device.cell.height, cssH: d.css.cell.height }; } catch (e) {}
    const cvs = Array.from(host.querySelectorAll('canvas')).map((cv, i) => ({ i, cls: cv.className, attrW: cv.width, cssW: Math.round(cv.getBoundingClientRect().width) }));
    return { cols: term.cols, rows: term.rows, font: term.options.fontSize, dpr: devicePixelRatio, dim, cvs, lines,
      rowsDom: host.querySelectorAll('.xterm-rows > div').length, paneW: host.clientWidth };
  });
  await p.locator('#term').screenshot({ path: 'cmp-' + tag + '.png' });
  const img = readPNG('cmp-' + tag + '.png');
  const perCss = img.w / info.paneW;
  // Longest ASCII line, then ink extent inside its band.
  let pick = -1, best = 0;
  info.lines.forEach((t, i) => { const clean = t.replace(/\s+$/, ''); if (/^[\x20-\x7e]+$/.test(clean) && clean.length > best) { best = clean.length; pick = i; } });
  let measured = null;
  if (pick >= 0 && info.dim) {
    const cellH = info.dim.cssH;
    const y0 = Math.max(0, Math.floor(pick * cellH * perCss)), y1 = Math.min(img.h, Math.ceil((pick + 1) * cellH * perCss));
    let first = -1, last = -1;
    for (let y = y0; y < y1; y++) for (let x = 0; x < img.w; x++) { const i2 = y * img.stride + x * 3; if (img.data[i2] + img.data[i2 + 1] + img.data[i2 + 2] > 300) { if (first < 0 || x < first) first = x; if (x > last) last = x; } }
    const extentCss = first < 0 ? null : (last - first + 1) / perCss;
    const text = info.lines[pick].replace(/\s+$/, '');
    measured = { lineNo: pick, chars: text.length, text: text.slice(0, 40),
      pitchCss: extentCss == null ? null : Math.round((extentCss / (text.length - 1)) * 100) / 100,
      cellCss: Math.round(info.dim.cssW * 100) / 100,
      ratio: extentCss == null ? null : Math.round((extentCss / (text.length - 1) / info.dim.cssW) * 100) / 100,
      natural95: null };
  }
  await p.evaluate(() => { const e = document.querySelector('#statusmeta'); if (e) e.click(); });
  await p.waitForTimeout(2500);
  await c.close(); await b.close();
  console.log(tag.toUpperCase(), JSON.stringify({ cols: info.cols, rows: info.rows, font: info.font, dpr: info.dpr, dim: info.dim, cvs: info.cvs, rowsDom: info.rowsDom, measured }, null, 1));
}

(async () => {
  await run('webgl', '');
  await run('dom', 'mux_render=dom');
})().catch(e => { console.error('ERR', e && (e.stack || e.message)); process.exit(2); });
