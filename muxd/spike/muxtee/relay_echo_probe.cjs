'use strict';
/* Send a keystroke to the DEPLOYED relay from Node (same network path as the browser, no browser), and
 * time until the echoed byte comes back. Splits "the browser/page is slow" from "the relay path is slow":
 * if this is fast and the page is slow, the cost is in the page; if both are slow, it is the wire/relay.
 * Usage: keysafe run hl-owner-session -Command '... node relay_echo_probe.cjs [session] [key]' */
const path = require('path');
const WebSocket = require(path.join(__dirname, '..', '..', '..', 'relay', 'node_modules', 'ws'));
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const S = process.argv[2] || 'hl-lat-headless';
const KEY = process.argv[3] || '~';
const N = Number(process.env.MUX_N || 10);
const COOKIE = (process.env.MUX_COOKIE || '').trim();
// MUX_DIRECT=1 skips Cloudflare: dial the relay origin on the tailnet. The forwarded client headers are
// what nginx would have added, so hl-auth's test-session IP binding still matches.
const DIRECT = process.env.MUX_DIRECT === '1';
const url = DIRECT
  ? `ws://100.84.64.65:7682/ws?session=${encodeURIComponent(S)}&cols=209&rows=51&dev=node-probe&label=node`
  : BASE.replace(/^https:/, 'wss:') + `/ws?session=${encodeURIComponent(S)}&cols=209&rows=51&dev=node-probe&label=node`;
const headers = { Cookie: COOKIE, 'User-Agent': UA, Origin: 'https://harmonizerlabs.cc' };
if (DIRECT) { headers['X-Real-IP'] = '137.186.222.216'; headers['X-Forwarded-For'] = '137.186.222.216'; }
const ws = new WebSocket(url, { headers });
const samples = [];
let pending = null;
let ready = false;
ws.on('open', () => { console.log('open'); setTimeout(() => { ready = true; start(); }, 1800); });
ws.on('message', (d, b) => {
  const s = b ? Buffer.from(d).toString('latin1') : String(d);
  if (pending && s.indexOf(KEY) >= 0) { const dt = +(performance.now() - pending).toFixed(1); samples.push(dt); pending = null; console.log('echo +' + dt + 'ms'); }
});
ws.on('error', e => { console.error('WS ERROR', e.message); process.exit(2); });
ws.on('close', (c, r) => { console.error('WS CLOSE', c, String(r)); });
function start() {
  let i = 0;
  const tick = () => {
    if (i >= N) {
      const s = samples.slice().sort((a, b) => a - b);
      console.log('SAMPLES ' + JSON.stringify({ n: s.length, min: s[0], med: s[Math.floor(s.length / 2)], p90: s[Math.floor(s.length * 0.9)], max: s[s.length - 1] }));
      process.exit(0);
    }
    i += 1; pending = performance.now();
    ws.send('i' + KEY);
    setTimeout(tick, 450);
  };
  tick();
}
setTimeout(() => { console.error('TIMEOUT ready=' + ready + ' samples=' + samples.length); process.exit(3); }, 30000);
