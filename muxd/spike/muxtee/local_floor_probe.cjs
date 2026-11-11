'use strict';
/* The PC-side floor: attach to muxd's LOCAL server (127.0.0.1:7699) with a real WS client, send one
 * key, wait for its echo. This is the tee + ConPTY + muxd path with no browser, no relay, no Cloudflare -
 * the number a local viewer can never beat. If this is fast and the deployed page is slow, the WAN/CF
 * leg is the cost.
 * Usage: node local_floor_probe.cjs [session] [key] */
const path = require('path');
const WebSocket = require(path.join(__dirname, '..', '..', '..', 'relay', 'node_modules', 'ws'));
const S = process.argv[2] || 'hl-lat-probe';
const KEY = process.argv[3] || '~';
const PORT = Number(process.env.MUXD_PORT || 7699);
const N = Number(process.env.MUX_N || 12);
const samples = [];
const ws = new WebSocket(`ws://127.0.0.1:${PORT}/`);
let pending = null;
let started = false;
ws.on('open', () => {
  ws.send(JSON.stringify({ t: 'attach', s: S, cols: 209, rows: 51, sb: 600 }));
  setTimeout(() => { if (!started) { started = true; start(); } }, 1500);
});
ws.on('message', (data, isBinary) => {
  const text = isBinary ? Buffer.from(data).toString('latin1') : String(data);
  if (process.env.MUX_VERBOSE === '1') console.log('RX ' + JSON.stringify(text.slice(0, 100)));
  if (pending && text.indexOf(KEY) >= 0) {
    const dt = +(performance.now() - pending).toFixed(1);
    samples.push(dt); pending = null;
    console.log(`echo +${dt}ms`);
  }
});
ws.on('error', e => { console.error('WS ERROR', e.message); process.exit(2); });
function start() {
  let i = 0;
  const tick = () => {
    if (i >= N) {
      const s = samples.slice().sort((a, b) => a - b);
      console.log('SAMPLES ' + JSON.stringify({ n: s.length, min: s[0], med: s[Math.floor(s.length / 2)], p90: s[Math.floor(s.length * 0.9)], max: s[s.length - 1] }));
      try { ws.close(); } catch (e) {}
      process.exit(0);
    }
    i += 1;
    pending = performance.now();
    ws.send(JSON.stringify({ t: 'i', d: Buffer.from(KEY).toString('base64') }));
    setTimeout(tick, 400);
  };
  setTimeout(tick, 500);
}
setTimeout(() => { console.error('TIMEOUT started=' + started + ' samples=' + samples.length); process.exit(3); }, 25000);
