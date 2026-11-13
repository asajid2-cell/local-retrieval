'use strict';
/* Hold a REAL relay viewer open so the session is already watched when a second viewer attaches.
 * This is the control for the first-keystroke question: a watch flip makes the tee re-send its snapshot,
 * so a probe that attaches first always measures the snapshot, not the input path.
 * Usage: keysafe run hl-owner-session -Command '... node hold_relay_viewer.cjs hl-lat-probe 90000' */
const path = require('path');
const WebSocket = require(path.join(__dirname, '..', '..', '..', 'relay', 'node_modules', 'ws'));
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.argv[2] || 'hl-lat-probe';
const MS = Number(process.argv[3] || 60000);
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const url = BASE.replace(/^https:/, 'wss:') + `/ws?session=${encodeURIComponent(S)}&cols=209&rows=51&dev=node-holder&label=holder`;
const ws = new WebSocket(url, { headers: { Cookie: COOKIE, 'User-Agent': UA, Origin: 'https://harmonizerlabs.cc' } });
let bytes = 0, frames = 0;
ws.on('open', () => console.log('HOLDER open ' + S + ' for ' + MS + 'ms'));
ws.on('message', (d, b) => { frames += 1; bytes += (b ? d.byteLength : String(d).length); });
ws.on('error', e => console.log('HOLDER error ' + e.message));
ws.on('close', (c, r) => console.log('HOLDER close ' + c + ' ' + String(r)));
setTimeout(() => { console.log(`HOLDER done frames=${frames} bytes=${bytes}`); process.exit(0); }, MS);
