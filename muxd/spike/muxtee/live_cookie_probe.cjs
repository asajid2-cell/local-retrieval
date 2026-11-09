'use strict';
/*
 * Does the stored test-session cookie open the live multiplex? Reads the Cookie header from
 * MUX_COOKIE (keysafe) and presents the SAME UA the session was minted with, because hl-auth
 * binds a test session to its address AND browser. Prints status codes and geometry only.
 */
const BASE = process.env.MUX_BASE || 'https://harmonizerlabs.cc/multiplex';
const COOKIE = (process.env.MUX_COOKIE || '').trim();
const UA = process.env.MUX_UA
  || 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';

(async () => {
  if (!COOKIE) { console.error('MUX_COOKIE missing'); process.exit(2); }
  const headers = { cookie: COOKIE, 'user-agent': UA };
  const page = await fetch(`${BASE}/`, { headers, redirect: 'manual' });
  const body = await page.text();
  console.log(`page: status=${page.status} loc=${page.headers.get('location') || '-'} bytes=${body.length} hasViewer=${/xterm|terminal/i.test(body)}`);

  const api = await fetch(`${BASE}/api/sessions`, { headers, redirect: 'manual' });
  const text = await api.text();
  console.log(`api/sessions: status=${api.status} bytes=${text.length}`);
  if (api.ok) {
    let list = [];
    try { const j = JSON.parse(text); list = Array.isArray(j) ? j : (j.sessions || []); } catch (_) {}
    console.log(JSON.stringify(list.map((s) => ({
      name: s.name, kind: s.kind, alive: s.alive, cols: s.cols, rows: s.rows,
      gridOwner: s.gridOwner, gridEpoch: s.gridEpoch, localTab: s.localTab,
    }))));
  } else {
    console.log('api/sessions refused: ' + text.slice(0, 160).replace(/\s+/g, ' '));
  }
})().catch((e) => { console.error('PROBE ERROR', e && (e.stack || e.message)); process.exit(2); });
