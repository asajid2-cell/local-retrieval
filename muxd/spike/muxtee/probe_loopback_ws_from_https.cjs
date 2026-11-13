'use strict';
/* Does Chrome let an HTTPS page open ws://127.0.0.1:7699? This decides whether the mux viewer can take
 * the direct local path (bypassing Cloudflare) at all. Serves a throwaway HTTPS page with CSP that
 * ALLOWS the loopback source, then opens the socket from it.
 *  - a 1008 close  => allowed: mixed content is not a blocker and muxd's origin gate is what answered
 *  - an error/timeout with a console "Mixed Content"/"blocked" line => NOT possible this way
 * Usage: node probe_loopback_ws_from_https.cjs */
const path = require('path');
const fs = require('fs');
const os = require('os');
const https = require('https');
const { execFileSync } = require('child_process');
function loadPlaywright() { const c = ['C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright', 'playwright']; let l; for (const x of c) { try { return require(x); } catch (e) { l = e; } } throw l; }
const { chromium } = loadPlaywright();

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'lows-'));
const key = path.join(dir, 'key.pem'), cert = path.join(dir, 'cert.pem');
execFileSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', key, '-out', cert, '-days', '1', '-subj', '/CN=127.0.0.1']);
const html = `<!doctype html><meta charset="utf-8"><title>probe</title><body>probe</body>`;
const server = https.createServer({ key: fs.readFileSync(key), cert: fs.readFileSync(cert) }, (req, res) => {
  res.writeHead(200, {
    'Content-Type': 'text/html; charset=utf-8',
    'Content-Security-Policy': "default-src 'none'; connect-src ws://127.0.0.1:7699; script-src 'unsafe-inline'",
  });
  res.end(html);
});
server.listen(0, '127.0.0.1', async () => {
  const port = server.address().port;
  const browser = await chromium.launch({ headless: true, args: ['--no-proxy-server'] });
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await ctx.newPage();
  const logs = [];
  page.on('console', m => logs.push(m.type() + ': ' + m.text()));
  await page.goto(`https://127.0.0.1:${port}/`, { waitUntil: 'domcontentloaded' });
  const res = await page.evaluate(() => new Promise(resolve => {
    const out = { origin: location.origin, secure: window.isSecureContext };
    let ws;
    try { ws = new WebSocket('ws://127.0.0.1:7699/'); } catch (e) { resolve({ ...out, threw: String(e) }); return; }
    ws.onopen = () => { out.opened = true; try { ws.close(); } catch (e) {} resolve(out); };
    ws.onerror = () => { out.errored = true; };
    ws.onclose = e => { out.closed = { code: e.code, reason: e.reason }; resolve(out); };
    setTimeout(() => { out.timeout = true; try { ws.close(); } catch (e) {} resolve(out); }, 6000);
  }));
  console.log('RESULT ' + JSON.stringify(res));
  console.log('CONSOLE ' + JSON.stringify(logs.filter(l => /mixed|blocked|network|websocket|error/i.test(l)).slice(0, 8)));
  await ctx.close(); await browser.close(); server.close();
  process.exit(0);
});
