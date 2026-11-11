'use strict';
/* Keep a local muxd attach open so the session is ALREADY being read, then measure a second viewer's
 * first key. Splits "the first key of any viewer is slow" (per-viewer cost) from "the tee was not being
 * read yet" (the watch/snapshot transition). Read-only: attaches, discards bytes, sends nothing.
 * Usage: node hold_local_viewer.cjs <session> <ms> */
const path = require('path');
const WebSocket = require(path.join(__dirname, '..', '..', '..', 'relay', 'node_modules', 'ws'));
const S = process.argv[2] || 'hl-lat-probe';
const MS = Number(process.argv[3] || 60000);
const ws = new WebSocket('ws://127.0.0.1:7699/');
let bytes = 0;
ws.on('open', () => {
  ws.send(JSON.stringify({ t: 'attach', s: S, cols: 209, rows: 51, sb: 200 }));
  // muxd only streams live output to a local viewer if the session is being read; ask for the watcher
  // state the relay would set. Unknown frames are ignored by an old handler, so this is safe.
  try { ws.send(JSON.stringify({ t: 'watch', s: S })); } catch (e) {}
  console.log('HOLDER attached ' + S + ' for ' + MS + 'ms');
});
ws.on('message', d => { bytes += (d && d.length) || 0; });
ws.on('error', e => console.log('HOLDER error ' + e.message));
setTimeout(() => { console.log('HOLDER done bytes=' + bytes); process.exit(0); }, MS);
