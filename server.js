// Multiplex app: relays browser terminals to muxd on the Windows PC. muxd owns the ConPTY sessions
// locally; this VPS mirrors them and stores lightweight web state. Legacy tmux sessions are reported
// only as blocking diagnostics. They are never created, attached, renamed, or killed by this relay.
const express = require('express');
const http = require('http');
const { WebSocketServer } = require('ws');
const fs = require('fs');
const crypto = require('crypto');
const { execSync, execFile } = require('child_process');
// crash-safe state writes: write a temp then rename (rename is atomic) so an unclean VPS reboot can't
// leave a half-written pins/autoheal/projects/commands file (those reboots happen — see full-review.md).
function atomicWrite(file, data) { try { fs.writeFileSync(file + '.tmp', data); fs.renameSync(file + '.tmp', file); } catch {} }
function hostTokenOk(t) { if (!HOST_TOKEN || !t || t.length !== HOST_TOKEN.length) return false; try { return crypto.timingSafeEqual(Buffer.from(t), Buffer.from(HOST_TOKEN)); } catch { return false; } }

const app = express();
const TEST_MODE = process.env.MUX_TEST_MODE === '1';
const STATE_DIR = process.env.MUX_STATE_DIR || __dirname;
try { fs.mkdirSync(STATE_DIR, { recursive: true }); } catch {}
app.use(express.json({ limit: '6mb' }));   // the desktop app pushes its whole projects projection
app.use((err, req, res, next) => {
  if (err && err.type === 'entity.parse.failed') return res.status(400).json({ error: 'invalid JSON body' });
  return next(err);
});

// --- hl-auth gate: OWNER ONLY ----------------------------------------------------------------------
// Self-gate like the other harmonizerlabs apps: read the hl_session cookie, ask hl-auth's internal
// verify oracle, and allow only the owner. Not signed in -> bounce to /auth/login; signed in but not
// owner -> 403. Decision cached ~60s per cookie. Fails closed.
const HLAUTH_BASE = process.env.HLAUTH_BASE || 'http://127.0.0.1:4200';
const HL_KEY = process.env.HL_INTERNAL_KEY || '';
const HL_COOKIE = process.env.HLAUTH_COOKIE || 'hl_session';
const HL_LOGIN = (process.env.HLAUTH_PUBLIC_BASE || 'https://harmonizerlabs.cc') + '/auth/login';
const _authCache = new Map();
function cookieVal(req, name) {
  const m = (req.headers.cookie || '').match(new RegExp('(?:^|;\\s*)' + name + '=([^;]+)'));
  return m ? decodeURIComponent(m[1]) : null;
}
async function isOwner(token) {
  if (!token) return false;
  const hit = _authCache.get(token);
  if (hit && hit.exp > Date.now()) return hit.owner;
  try {
    const r = await fetch(HLAUTH_BASE + '/internal/verify', { headers: { 'x-internal-key': HL_KEY, 'x-session-token': token } });
    const j = await r.json();
    const owner = !!(j.authenticated && j.user && j.user.isOwner);
    _authCache.set(token, { exp: Date.now() + 60000, owner });
    return owner;
  } catch { return false; }
}
function isTrustedLocal(req) {
  // Direct loopback with NO nginx forwarding header = a trusted local caller (the owner's desktop app
  // reaching in over its own owner-only SSH to this VPS). Public traffic always arrives via nginx,
  // which sets X-Forwarded-For, so it can never spoof this.
  const ra = req.socket.remoteAddress || '';
  return !req.headers['x-forwarded-for'] && (ra === '127.0.0.1' || ra === '::1' || ra === '::ffff:127.0.0.1');
}
app.use(async (req, res, next) => {
  if (isTrustedLocal(req)) return next();
  if (await isOwner(cookieVal(req, HL_COOKIE))) return next();
  const tok = cookieVal(req, HL_COOKIE);
  if (!tok) {
    const prefix = req.headers['x-forwarded-prefix'] || process.env.PUBLIC_RETURN || '/multiplex';
    if ((req.headers.accept || '').includes('text/html')) return res.redirect(HL_LOGIN + '?next=' + encodeURIComponent(prefix));
    return res.status(401).json({ error: 'login required' });
  }
  return res.status(403).send('Forbidden — owner only.');
});

app.use(express.static(__dirname + '/public', {
  setHeaders(res, filePath) {
    if (String(filePath || '').endsWith('.html')) {
      res.setHeader('Cache-Control', 'no-store, no-cache, must-revalidate, proxy-revalidate');
      res.setHeader('Pragma', 'no-cache');
      res.setHeader('Expires', '0');
    }
  }
}));

const SAFE = s => String(s || '').replace(/[^A-Za-z0-9_.-]/g, '').slice(0, 48);
// ---- PC SESSION HOST link (P2/P3 — the ownership flip) ---------------------------------------------
// muxd on the PC owns each session's ConPTY locally and dials OUT to us over one multiplexed WebSocket
// (/host, token-gated). Sessions live on the PC: Wi-Fi drops / VPS reboots / relay deploys only cost the
// VIEWER a blip — the agent never notices. If this link is down, creation/attach fail loudly instead of
// making a VPS tmux twin that can silently diverge.
const HOST_TOKEN = process.env.MUX_HOST_TOKEN || '';
let hostWs = null;                 // the PC's muxd link (one at a time; newest wins)
let hostLabel = '';
const hostSessions = new Map();    // name -> { alive, created, lastOut, tail }
// A just-created hosted session muxd hasn't reported back yet. A muxd status push (built before it
// processed our `create`) must NOT evict this optimistic entry — otherwise the imminent /ws attach or a
// boot-recreate sees no hosted session, makes a tmux TWIN, and two agents resume one transcript (A2 #1).
const pendingCreates = new Map();  // name -> expiry ts
function markPending(name) { pendingCreates.set(name, Date.now() + 8000); }
// Clear-scrollback + clear-screen + home: prefixes a scrollback replay so a reconnecting viewer that
// still shows the pre-drop screen doesn't get the replay stacked ON TOP of it (A2 #2/#3).
const CLEAR_SCREEN = Buffer.from('\x1b[3J\x1b[2J\x1b[H');
// Hosted sessions are PC-local first. Web attach must become live quickly; scrollback is a bounded
// convenience replay, not something allowed to stall live terminal bytes for multiple seconds.
const HOST_SB_BYTES = +process.env.MUX_HOST_SB_BYTES || 800000;
const HOST_SB_WAIT_MS = +process.env.MUX_HOST_SB_WAIT_MS || 900;
const REQUIRED_HOST_PROTOCOL = 2;
const REQUIRED_HOST_CAPS = new Set(['create', 'kill', 'rename', 'heal', 'tail', 'scrollback']);
let hostProtocol = { protocol: 0, caps: [] };
const hostUp = () => !!(hostWs && hostWs.readyState === 1);
function sendHost(obj) { if (hostUp()) { try { hostWs.send(JSON.stringify(obj)); return true; } catch {} } return false; }
const hostedHas = name => hostUp() && hostSessions.has(name);
function normalizedCommand(cmd) { return String(cmd || '').trim(); }
function commandSig(cmd) {
  const c = normalizedCommand(cmd);
  return c ? crypto.createHash('sha256').update(c, 'utf8').digest('hex').slice(0, 16) : '';
}
function hostedCompatibleWithCommand(existing, cmd) {
  const requestedSig = commandSig(cmd);
  if (!requestedSig) return true;
  if (!existing || existing.alive === false) return false;
  if (existing.shellOnly || existing.hasCommand === false) return false;
  return !!existing.cmdSig && existing.cmdSig === requestedSig;
}
function hostProtocolOk() {
  if (!hostUp() || hostProtocol.protocol < REQUIRED_HOST_PROTOCOL) return false;
  const caps = new Set(hostProtocol.caps || []);
  for (const c of REQUIRED_HOST_CAPS) if (!caps.has(c)) return false;
  return true;
}
function hostProtocolDetail() {
  if (!hostUp()) return 'muxd is not connected';
  return `muxd protocol ${hostProtocol.protocol || 'unknown'} lacks the required capabilities; restart MuxdSessionHost to load the current muxd`;
}
function requireHostProtocol(res, action) {
  if (hostProtocolOk()) return true;
  failHost(res, 503, 'PC mux host protocol mismatch', `${action}: ${hostProtocolDetail()}`);
  return false;
}
function waitForHostState(check, timeoutMs = 6000) {
  return new Promise(resolve => {
    const started = Date.now();
    const tick = () => {
      let value = null;
      try { value = check(); } catch {}
      if (value) return resolve({ ok: true, value });
      if (Date.now() - started >= timeoutMs) return resolve({ ok: false, error: 'muxd did not confirm the change' });
      setTimeout(tick, 100);
    };
    tick();
  });
}
function failHost(res, status, error, detail) {
  return res.status(status).json({ ok: false, error, detail: detail || '' });
}
// E4: on-demand deep tail from muxd (previews) — request/response correlated by rid, 2.5s timeout.
const pendingTails = new Map(); let _rid = 0;
function requestHostTail(name, lines) {
  if (!hostUp()) return Promise.resolve(null);
  const rid = 'r' + (++_rid);
  return new Promise(resolve => {
    const to = setTimeout(() => { pendingTails.delete(rid); resolve(null); }, 2500);
    pendingTails.set(rid, txt => { clearTimeout(to); resolve(txt); });
    sendHost({ t: 'tail', s: name, lines, rid });
  });
}
function legacyTmuxNames() {
  try {
    const out = execSync(`tmux list-sessions -F '#{session_name}' 2>/dev/null`, { encoding: 'utf8', timeout: 1500 });
    return out.trim().split('\n').map(SAFE).filter(Boolean);
  } catch { return []; }
}
function tmuxHas(name) { const n = SAFE(name); return !!n && legacyTmuxNames().includes(n); }
function legacyDetail(name) {
  return `legacy VPS tmux session "${name}" exists; relay refuses to attach/manage tmux. Clean it manually after confirming it is not a live agent.`;
}
function failLegacy(res, name) {
  return failHost(res, 409, 'legacy tmux session exists', legacyDetail(name));
}

// Persistent per-session status for the tab dots. We classify what's in each pane:
//   green  = a claude/codex turn is IN FLIGHT (always shows "esc to interrupt" + live markers)
//   yellow = an agent is up but IDLE, waiting for your next prompt (its footer/input box is showing)
//   white  = a neutral shell, no agent has run here
//   red    = the agent is GONE (a bare shell, but an agent WAS here) — so a batch-file crash / codex
//            insta-disconnect shows up immediately as the dot going red.
const _sessState = new Map();   // name -> { everAgent }  (lets a bare shell read RED instead of WHITE)
const _stateCache = new Map();  // name -> { at, state }   throttle hosted-tail classification bursts
                                 // polls (4s interval × several clients) must not spawn a subprocess per call
// NOTE: named paneAgentState (NOT sessionState) — there's already a sessionState() for window-sizing below.
function paneAgentState(name, content) {
  if (content === undefined) {
    const cached = _stateCache.get(name);
    if (cached && Date.now() - cached.at < 1500) return cached.state;
    content = '';
  }
  const mem = _sessState.get(name) || { everAgent: false };
  // Only the TAIL (bottom status line + footer) is reliable: a turn in flight shows "esc to interrupt"
  // there, and an idle agent shows its footer there. Scanning the whole pane would false-green an idle
  // agent off a previous turn's "(45s · 12k tokens)" left in scrollback (validated against live sessions).
  // Strip trailing blank/newline padding so the window isn't off-by-one, and widen to 10 lines so the live status line ABOVE
  // the input box + footer is included (that's where "esc to interrupt" sits during a turn).
  const tail = content.replace(/\s+$/, '').split('\n').slice(-10).join('\n');
  const working = /esc to inter|still thinking/i.test(tail);
  const agentUI = working || /⏵⏵|auto mode (on|off)|for agents|shift\+tab to cycle|\? for shortcuts|\/goal (active|paused)|\b(gpt-[0-9][\w.-]*|opus [0-9]|sonnet [0-9]|haiku [0-9])\b/i.test(tail);
  if (agentUI) mem.everAgent = true;
  _sessState.set(name, mem);
  const state = working ? 'green' : agentUI ? 'yellow' : (mem.everAgent ? 'red' : 'white');
  _stateCache.set(name, { at: Date.now(), state });
  return state;
}
function listSessions() {
  let list = [];
  const legacy = new Set(legacyTmuxNames());
  // PC-HOSTED sessions are the only actionable sessions. If a legacy tmux name collides, surface the
  // conflict on the hosted row instead of silently hiding or killing the legacy process.
  if (hostUp()) {
    for (const [name, h] of hostSessions) {
      const attached = (sessions.get(name) ? [...sessions.get(name).clients.values()].some(c => c.hosted) : false);
      const protocolOk = hostProtocolOk();
      const alive = h.alive !== false;
      const dormant = !alive;
      const detachedLocal = dormant && locallyRunningMuxName(name);
      list.push({ name, windows: 1, created: h.created || 0, attached, activity: h.lastOut || 0,
                  state: detachedLocal ? 'detached' : (dormant ? 'dormant' : paneAgentState(name, h.tail || '')), autoheal: _healOn.has(name), hosted: true,
                  alive, dormant, detachedLocal, cols: h.cols || 0, rows: h.rows || 0,
                  hasCommand: !!h.hasCommand, shellOnly: !!h.shellOnly, ready: !!h.ready,
                  kind: h.kind || (dormant ? 'dormant' : (h.shellOnly ? 'shell' : 'command')), cmdSig: h.cmdSig || '',
                  localViewers: h.localViewers || 0, localFirst: !!h.localFirst,
                  detail: detachedLocal ? 'Local agent process is still running, but the muxd mirror is detached. New muxrun sessions re-register automatically; restart this one through mux to restore web terminal control.'
                         : dormant ? 'Dormant mux session: no shell or agent is running until you relaunch it or run mux locally.' : '',
                  hostProtocol: hostProtocol.protocol || 0, hostProtocolOk: protocolOk,
                  hostProtocolDetail: protocolOk ? '' : hostProtocolDetail(),
                  legacyTwin: legacy.has(name),
                  legacyDetail: legacy.has(name) ? legacyDetail(name) : '' });
    }
  }
  for (const name of legacy) {
    if (list.some(s => s.name === name)) continue;
    list.push({ name, windows: 0, created: 0, attached: false, activity: 0, state: 'red',
                autoheal: _healOn.has(name), hosted: false, legacy: true, legacyBlocked: true,
                detail: legacyDetail(name) });
  }
  const live = new Set(list.map(s => s.name));   // prune state memory for sessions that no longer exist
  for (const k of _sessState.keys()) if (!live.has(k)) _sessState.delete(k);
  for (const k of _stateCache.keys()) if (!live.has(k)) _stateCache.delete(k);
  return list;
}

app.get('/api/sessions', (req, res) => res.json(listSessions()));

// Create (or reuse) a session, optionally injecting a command (e.g. a claude/codex resume) once the
// shell is up. This is the "append multiplex setup, send the command in the multiplex" flow.
app.post('/api/sessions', async (req, res) => {
  const body = req.body || {};
  const name = SAFE(body.name);
  if (!name) return res.status(400).json({ error: 'name required' });
  const hasCommand = Object.prototype.hasOwnProperty.call(body, 'command');
  const cmd = typeof body.command === 'string' ? body.command : '';
  if (tmuxHas(name)) return failLegacy(res, name);
  const existing = hostSessions.get(name);
  if (hostedHas(name) && existing && existing.alive !== false && hostedCompatibleWithCommand(existing, cmd)) return res.json({ ok: true, name, created: false, hosted: true, owner: !!existing.owner, hostProtocol: hostProtocol.protocol || 0 });
  if (!hasCommand) return res.status(400).json({ error: 'command required to start a mux session' });
  if (!requireHostProtocol(res, 'refusing to start PC-local mux session')) return;
  if (!sendHost({ t: 'create', s: name, cmd, cols: 140, rows: 40, heal: _healOn.has(name) })) {
    return failHost(res, 503, 'PC mux host offline', 'host socket closed before create could be sent');
  }
  markPending(name);
  hostSessions.set(name, { alive: true, created: Date.now(), lastOut: Date.now(), tail: '', heal: _healOn.has(name), cols: 140, rows: 40, owner: false, localFirst: false, localViewers: 0,
                           hasCommand: !!normalizedCommand(cmd), shellOnly: !normalizedCommand(cmd), ready: true, kind: normalizedCommand(cmd) ? 'command' : 'shell', cmdSig: commandSig(cmd) });
  const confirmed = await waitForHostState(() => {
    const h = hostSessions.get(name);
    return h && h.alive !== false && !pendingCreates.has(name) && hostedCompatibleWithCommand(h, cmd) ? h : null;
  }, 15000);
  if (!confirmed.ok) return failHost(res, 504, 'PC-local mux session not confirmed', confirmed.error);
  res.json({ ok: true, name, created: true, hosted: true, owner: !!confirmed.value.owner, hostProtocol: hostProtocol.protocol || 0 });
});

// Tail preview of a session's live pane (on demand: long-press / hover / palette) so you can tell what
// a session is doing before attaching — last N lines, name-sanitized.
app.get('/api/sessions/:name/tail', async (req, res) => {
  const name = SAFE(req.params.name);
  const lines = Math.min(200, Math.max(1, +req.query.lines || 14));
  if (tmuxHas(name)) return failLegacy(res, name);
  if (hostedHas(name)) {   // hosted: pull a proper-depth tail from muxd (fallback to the cached status tail)
    if (!requireHostProtocol(res, 'refusing to read hosted tail')) return;
    const txt = await requestHostTail(name, lines);
    const h = hostSessions.get(name);
    return res.json({ name, tail: txt != null ? txt : String((h && h.tail) || '').split('\n').slice(-lines).join('\n') });
  }
  res.status(404).json({ error: 'session not found' });
});

// Rename a session (keeps it running) — "close tab" must never be the only way to manage a session.
app.patch('/api/sessions/:name', async (req, res) => {
  const name = SAFE(req.params.name);
  const to = SAFE(req.body && req.body.name);
  if (!to) return res.status(400).json({ error: 'name required' });
  if (to === name) return res.json({ ok: true, name: to });
  if (tmuxHas(to)) return failLegacy(res, to);
  if (hostSessions.has(to)) return res.status(409).json({ error: 'name already in use' });
  // A2 #8: rename must carry the tab's auto-resume + pin state, or an armed tab silently loses them.
  const migrate = () => {
    if (_healOn.has(name)) { _healOn.delete(name); _healOn.add(to); saveHealOn(); }
    if (_heal.has(name)) { _heal.set(to, _heal.get(name)); _heal.delete(name); }
    if (pins.has(name)) { pins.set(to, pins.get(name)); pins.delete(name); savePins(); }
  };
  if (hostedHas(name)) {   // E1: hosted rename → muxd renames the session key (keeps the pty), we migrate state
    if (tmuxHas(name)) return failLegacy(res, name);
    if (!requireHostProtocol(res, 'refusing to rename a hosted session')) return;
    if (!sendHost({ t: 'rename', s: name, to })) return failHost(res, 503, 'PC mux host offline', 'host socket closed before rename could be sent');
    const confirmed = await waitForHostState(() => hostSessions.has(to) && !hostSessions.has(name), 6000);
    if (!confirmed.ok) return failHost(res, 504, 'muxd rename not confirmed', confirmed.error);
    migrate();
    const st = sessions.get(name); if (st) { for (const c of st.clients.values()) { try { c.ws.close(4001, 'renamed'); } catch {} } sessions.delete(name); }  // viewers reconnect under the new name
    return res.json({ ok: true, name: to, hosted: true });
  }
  if (tmuxHas(name)) return failLegacy(res, name);
  res.status(404).json({ error: 'session not found' });
});

app.delete('/api/sessions/:name', async (req, res) => {
  const name = SAFE(req.params.name);
  if (tmuxHas(name)) return failLegacy(res, name);
  if (hostedHas(name)) {
    if (!requireHostProtocol(res, 'refusing to kill a hosted session')) return;
    if (!sendHost({ t: 'kill', s: name })) return failHost(res, 503, 'PC mux host offline', 'host socket closed before kill could be sent');
    const confirmed = await waitForHostState(() => !hostSessions.has(name), 6000);
    if (!confirmed.ok) return failHost(res, 504, 'muxd kill not confirmed', confirmed.error);
  }
  _healOn.delete(name); _heal.delete(name); saveHealOn();   // drop its auto-resume preference too
  res.json({ ok: true });
});

// PER-TAB auto-resume toggle. On = the watchdog re-runs THIS session's resume command if its agent dies
// while working (max 3×/10min, idle sessions left alone). Persisted by name → survives restarts/relaunches.
app.post('/api/sessions/:name/autoheal', async (req, res) => {
  const name = SAFE(req.params.name);
  if (!name) return res.status(400).json({ error: 'name required' });
  const on = !!(req.body && req.body.on);
  const wasOn = _healOn.has(name);
  if (tmuxHas(name)) return failLegacy(res, name);
  if (on) _healOn.add(name); else { _healOn.delete(name); _heal.delete(name); }
  saveHealOn();
  if (hostedHas(name)) {
    if (!requireHostProtocol(res, 'refusing to change hosted auto-resume')) {
      if (wasOn) _healOn.add(name); else { _healOn.delete(name); _heal.delete(name); }
      saveHealOn();
      return;
    }
    if (!sendHost({ t: 'heal', s: name, on })) {
      if (wasOn) _healOn.add(name); else { _healOn.delete(name); _heal.delete(name); }
      saveHealOn();
      return failHost(res, 503, 'PC mux host offline', 'host socket closed before auto-resume change could be sent');
    }
    const confirmed = await waitForHostState(() => {
      const h = hostSessions.get(name);
      return h && !!h.heal === on ? h : null;
    }, 6000);
    if (!confirmed.ok) {
      if (wasOn) _healOn.add(name); else { _healOn.delete(name); _heal.delete(name); }
      saveHealOn();
      return failHost(res, 504, 'muxd auto-resume change not confirmed', confirmed.error);
    }
  }
  res.json({ ok: true, name, autoheal: on });
});

// --- project sync: the desktop app pushes its collections/chats projection here while it's open, so
// the web can show your projects and resume chats remotely. POST is loopback-only (the app reaches in
// over its own SSH); GET is owner-gated (the web). `live` = the app pushed within the last ~45s. ------
const PROJECTS_FILE = STATE_DIR + '/projects.json';
let _projects = { decks: [], collections: [], host: '', syncedAt: 0, runningSessions: [] };
try { _projects = JSON.parse(fs.readFileSync(PROJECTS_FILE, 'utf8')); } catch {}
function appSyncedAt() { return _projects.appSyncedAt || _projects.syncedAt || 0; }
function runningSyncedAt() { return _projects.runningSyncedAt || _projects.syncedAt || 0; }
function appLive() { return Date.now() - appSyncedAt() < 45000; }
function bridgeLive() { return Date.now() - runningSyncedAt() < 45000; }
function projectsHealth() {
  const now = Date.now();
  const appAt = appSyncedAt();
  const runningAt = runningSyncedAt();
  let pendingCommands = 0;
  try { pruneCommands(); pendingCommands = _commands.filter(c => c.status === 'pending').length; } catch {}
  return {
    appLive: appAt > 0 && now - appAt < 45000,
    bridgeLive: runningAt > 0 && now - runningAt < 45000,
    appAgeMs: appAt > 0 ? now - appAt : null,
    runningAgeMs: runningAt > 0 ? now - runningAt : null,
    decks: Array.isArray(_projects.decks) ? _projects.decks.length : 0,
    collections: Array.isArray(_projects.collections) ? _projects.collections.length : 0,
    runningSessions: Array.isArray(_projects.runningSessions) ? _projects.runningSessions.length : 0,
    pendingCommands,
  };
}
function currentRunningIds() {
  return new Set((Array.isArray(_projects.runningSessions) ? _projects.runningSessions : [])
    .map(s => String(s && s.sessionId || '').toLowerCase())
    .filter(Boolean));
}
function locallyRunningMuxName(name) {
  if (!bridgeLive()) return false;
  const runningIds = currentRunningIds();
  if (!runningIds.size) return false;
  for (const col of (Array.isArray(_projects.collections) ? _projects.collections : [])) {
    for (const chat of (Array.isArray(col.chats) ? col.chats : [])) {
      if (String(chat && chat.muxName || '') === name && runningIds.has(String(chat && chat.id || '').toLowerCase())) return true;
    }
  }
  return false;
}
function normalizedCollectionsForCurrentRunning() {
  const cols = Array.isArray(_projects.collections) ? _projects.collections : [];
  if (!bridgeLive()) return cols;
  const runningIds = currentRunningIds();
  return cols.map(col => ({
    ...col,
    chats: Array.isArray(col.chats) ? col.chats.map(chat => ({
      ...chat,
      running: runningIds.has(String(chat && chat.id || '').toLowerCase()),
    })) : [],
  }));
}
app.post('/api/projects', (req, res) => {
  const b = req.body || {};
  const now = Date.now();
  _projects = {
    decks: Array.isArray(b.decks) ? b.decks : [],
    collections: Array.isArray(b.collections) ? b.collections : [],
    runningSessions: Array.isArray(b.runningSessions) ? b.runningSessions : [],
    host: String(b.host || ''),
    syncedAt: now, appSyncedAt: now, runningSyncedAt: now,
  };
  atomicWrite(PROJECTS_FILE, JSON.stringify(_projects));
  res.json({ ok: true, syncedAt: _projects.syncedAt });
});
app.get('/api/projects', (req, res) => {
  const a = appSyncedAt(), r = runningSyncedAt();
  res.json({
    decks: Array.isArray(_projects.decks) ? _projects.decks : [],
    collections: normalizedCollectionsForCurrentRunning(), host: _projects.host || '',
    runningSessions: _projects.runningSessions || [],
    syncedAt: a, appSyncedAt: a, runningSyncedAt: r,
    appLive: Date.now() - a < 45000,
    bridgeLive: Date.now() - r < 45000,
    live: Date.now() - r < 45000,
  });
});
// LIGHT partial update: the always-on headless server keeps the running list + `live` fresh when the
// heavy desktop app is closed, WITHOUT clobbering the collections projection the app last pushed. Loopback
// only (the server reaches in over its own SSH, same as the app's /api/projects push).
app.post('/api/running', (req, res) => {
  if (!isTrustedLocal(req)) return res.status(403).json({ error: 'forbidden' });
  const b = req.body || {};
  _projects.runningSessions = Array.isArray(b.runningSessions) ? b.runningSessions : [];
  if (b.host) _projects.host = String(b.host);
  _projects.runningSyncedAt = Date.now();   // bridge-only: do not make GUI-owned collection commands look available
  atomicWrite(PROJECTS_FILE, JSON.stringify(_projects));
  res.json({ ok: true, runningSyncedAt: _projects.runningSyncedAt });
});

// ---- AUTO-HEAL (redundancy): auto-resume a managed agent session that DIED WHILE WORKING ----------------
// A CLI agent (codex especially) can get killed out from under its terminal; a VS Code agent has no
// terminal so it survives. So we watch each opted-in hosted muxd session and, when its agent clearly CRASHED —
// its "working" frame froze (a live agent's timer always ticks), or it exited to a shell right after
// working (red just after green) — we re-run that session's OWN resume command. Idle (yellow) agents and
// clean quits-from-idle are left alone. Settle delay + max 3 relaunches / 10min then GIVE UP, so a session
// that dies on every launch can't spin forever. ★ PER-TAB opt-in: only sessions the user enabled (in
// _healOn) are watched + relaunched — toggle via POST /api/sessions/:name/autoheal. MUX_AUTOHEAL=0 = master off.
const _heal = new Map();   // name -> { lastTail, lastChange, lastGreen, deaths[], lastRelaunch, gaveUp }
const AUTOHEAL_FILE = STATE_DIR + '/autoheal.json';
let _healOn = new Set();    // session names OPTED IN to auto-resume (per-tab, persisted across restarts)
try { _healOn = new Set(JSON.parse(fs.readFileSync(AUTOHEAL_FILE, 'utf8'))); } catch {}
function saveHealOn() { atomicWrite(AUTOHEAL_FILE, JSON.stringify([..._healOn])); }
function muxCommandFor(name) {
  let found = null;
  const scan = (o) => {
    if (found || !o || typeof o !== 'object') return;
    if (Array.isArray(o)) { for (const x of o) scan(x); return; }
    if (o.muxName === name && o.muxCommand) { found = String(o.muxCommand); return; }
    for (const k of Object.keys(o)) scan(o[k]);
  };
  try { scan(_projects.collections); } catch {}
  return found;
}
function autoHealTick() {
  let names = hostProtocolOk() ? [...hostSessions.keys()] : [];
  if (!names.length) return;
  const now = Date.now();
  for (const name of names) {
    if (!_healOn.has(name)) continue;   // per-tab opt-in: only watch/relaunch sessions the user turned on
    const hosted = hostSessions.get(name);
    if (!hosted) continue;
    let content = String(hosted.tail || '');
    const state = paneAgentState(name, content);                         // green(working)/yellow(idle agent)/red/white
    const lines = content.replace(/\s+$/, '').split('\n');
    const tail = lines.slice(-14).join('\n');
    const lastLine = (lines.filter(l => l.trim()).pop() || '').trim();
    const everAgent = (_sessState.get(name) || {}).everAgent;
    const cmd = muxCommandFor(name);
    const h = _heal.get(name) || { lastTail: tail, lastChange: now, acts: [], lastAct: 0, gaveUp: false, hadGoal: false, phase: null, resumedGoal: false };
    if (tail !== h.lastTail) { h.lastTail = tail; h.lastChange = now; }

    // Track whether this session is pursuing a codex/claude GOAL, so recovery can DEFER to the goal
    // loop/hook (which re-prompts on its own) instead of pasting its own "continue" that would fight it.
    if (state === 'green' || state === 'yellow') {
      if (/pursuing goal|\/goal\s+(active|running)/i.test(tail) && !/goal achieved|goal (complete|done|cleared)/i.test(tail)) h.hadGoal = true;
      else if (/goal achieved|goal (complete|done|cleared)|no active goal/i.test(tail)) h.hadGoal = false;
    }

    // DEATH = the pane is sitting at a SHELL PROMPT (the agent process is gone). Unambiguous: a live agent's
    // last line is its composer/footer, NEVER a shell prompt — so this cannot mis-fire on a slow turn.
    // (We removed the old "green but unchanged 45s = dead" rule: a long tool call / deep-think looks exactly
    // like that, and Ctrl-C'ing it mid-turn — then typing the resume into the live composer — was THE bug.)
    const atPS   = /^(PS\s+)?[A-Za-z]:\\[^>]*>$/.test(lastLine);          // PC PowerShell prompt (ssh held, agent exited)
    const atBash = /^[\w.\-]+@[\w.\-]+:[^#$]*[#$]$/.test(lastLine);       // VPS bash prompt (the ssh-back itself dropped)
    // Only recover a tab we've PERSONALLY watched go alive→dead (everAgent). This is the opt-in contract:
    // on server/watcher startup a tab that's ALREADY dead is left alone — we never auto-start tabs on boot,
    // we only restart a tab that died while we were watching it.
    const atShell = everAgent && (atPS || atBash);
    h.acts = h.acts.filter(t => now - t < 600000);

    if (!atShell) {
      // ALIVE (or a resume still loading). NEVER interrupt it.
      if (state === 'green' || state === 'yellow') h.gaveUp = false;      // healthy again → clear the give-up latch
      // Post-resume re-engagement: a freshly-resumed agent sits idle at its composer. If it was NOT on a
      // goal, nudge it to continue; if it WAS, leave it — the goal loop/hook re-prompts it on its own.
      if (h.phase === 'resumed') {
        if (now - h.lastAct > 90000) { h.phase = null; }                 // gave the resume long enough
        else if (now - h.lastAct > 6000) {
          if (state === 'green') { h.phase = null; }                     // already working again → done
          else if (state === 'yellow') {
            if (h.resumedGoal) { console.log(`[autoheal] ${name}: resumed with an active goal → leaving it to the goal hook`); }
            else {
              const NUDGE = 'Continue exactly what you were working on before the session was interrupted.';
              try {
                sendHost({ t: 'i', s: name, d: Buffer.from(NUDGE + '\r', 'utf8').toString('base64') });
                console.log(`[autoheal] ${name}: resumed (no active goal) → nudged it to continue`);
              } catch (e) {}
            }
            h.phase = null;
          }
        }
      }
      _heal.set(name, h); continue;
    }

    // --- AT A SHELL PROMPT: the agent exited. Recover it (no Ctrl-C — there is nothing running to interrupt). ---
    if (!cmd || h.gaveUp) { _heal.set(name, h); continue; }
    if (now - h.lastAct < 9000) { _heal.set(name, h); continue; }        // let the previous action settle
    if (h.acts.length >= 5) { if (!h.gaveUp) { h.gaveUp = true; console.log(`[autoheal] ${name}: GAVE UP (5 recovery actions/10min — PC or network likely down)`); } _heal.set(name, h); continue; }

    try {
      sendHost({ t: 'i', s: name, d: Buffer.from('cls; ' + cmd + '\r', 'utf8').toString('base64') });
      h.acts.push(now); h.lastAct = now; h.phase = 'resumed'; h.resumedGoal = h.hadGoal;
      console.log('[autoheal] ' + name + ': hosted agent exited -> resumed' + (h.hadGoal ? ' (goal active -> will defer to goal hook)' : ' (no goal -> will nudge to continue)') + ' (' + h.acts.length + '/5)');
    } catch (e) { console.log(`[autoheal] ${name}: recovery action failed: ${e.message}`); }
    _heal.set(name, h);
  }
  const live = new Set(names);
  for (const k of _heal.keys()) if (!live.has(k)) _heal.delete(k);
}
if (!TEST_MODE && process.env.MUX_AUTOHEAL !== '0') setInterval(autoHealTick, 12000);

// ---- BOOT-RECREATE (P0): on relay start, recreate every ARMED session (autoheal.json) as a PC-hosted
// muxd session when the host is connected and protocol-current. STRICTLY opt-in: never creates a session
// the user didn't arm. Legacy tmux names are blockers only; the relay never recreates or reuses tmux.
function bootRecreate() {
  if (!_healOn.size) return;
  const legacy = new Set(legacyTmuxNames());
  let n = 0;
  for (const name of _healOn) {
    if (hostedHas(name)) continue;                                       // lives on the PC host → NEVER make a tmux twin (double-resume corrupts the transcript)
    if (legacy.has(name)) { console.log(`[boot-recreate] ${name}: ${legacyDetail(name)} - skipped`); continue; }
    const cmd = muxCommandFor(name);
    if (!cmd) { console.log(`[boot-recreate] ${name}: armed but no resume command (not in a synced collection) — skipped`); continue; }
    try {
      if (hostProtocolOk()) {
        // recoveries land on the SAFE path: recreate as a PC-hosted session (muxd runs the resume)
        sendHost({ t: 'create', s: name, cmd, cols: 140, rows: 40, heal: true });
        markPending(name);
        hostSessions.set(name, { alive: true, created: Date.now(), lastOut: Date.now(), tail: '', heal: true,
                                 hasCommand: !!normalizedCommand(cmd), shellOnly: !normalizedCommand(cmd), ready: true,
                                 kind: normalizedCommand(cmd) ? 'command' : 'shell', cmdSig: commandSig(cmd) });
        n++; console.log(`[boot-recreate] ${name}: recreated HOSTED + resume queued`);
      } else if (hostUp()) {
        console.log(`[boot-recreate] ${name}: ${hostProtocolDetail()} - skipped`);
      } else {
        console.log(`[boot-recreate] ${name}: PC mux host down - skipped`);
      }
    } catch (e) { console.log(`[boot-recreate] ${name}: failed: ${e.message}`); }
  }
  if (n) console.log(`[boot-recreate] restored ${n} armed session(s) after a wipe`);
}
if (!TEST_MODE) setTimeout(bootRecreate, 25000);  // let _healOn load AND give muxd time to reconnect+hello first, so
                                  // hosted sessions are visible before any armed recreate is attempted

// ---- PC reachability probe + /api/health (P0 observability, D7). A lightweight TCP-connect to the PC's
// sshd port (no ssh process spawn) so health can report whether the ssh-back is even reachable + its RTT —
// the Jul 2 .146→.154 DHCP orphaning would have shown here instantly as pc.reachable=false.
let _winHost = '192.168.1.146';
try { const _c = fs.readFileSync((process.env.HOME || '') + '/.ssh/config', 'utf8'); const _m = _c.match(/Host\s+win\b[\s\S]*?HostName\s+(\S+)/i); if (_m) _winHost = _m[1]; } catch {}
let _pcHealth = TEST_MODE ? { reachable: true, rttMs: 0, host: 'test', at: Date.now() } : { reachable: null, rttMs: null, host: _winHost, at: 0 };
// When the PC becomes unreachable, it may just have moved to a new DHCP IP (Jul 2: .146→.154 orphaned the
// fleet). Kick the MAC-based resolver (runs the ping-sweep in its own subprocess — never blocks this loop),
// then reload the (possibly updated) HostName so the next ssh-back + boot-recreate target the new address.
let _lastReresolve = 0;
function reresolveWin() {
  const now = Date.now(); if (now - _lastReresolve < 60000) return; _lastReresolve = now;
  execFile((process.env.HOME || '') + '/bin/resolve-win.sh', { timeout: 20000 }, () => {
    try {
      const c = fs.readFileSync((process.env.HOME || '') + '/.ssh/config', 'utf8');
      const m = c.match(/Host\s+win\b[\s\S]*?HostName\s+(\S+)/i);
      if (m && m[1] !== _winHost) { console.log(`[dhcp] PC moved → win now ${m[1]} (was ${_winHost})`); _winHost = m[1]; setTimeout(probePc, 500); }
    } catch {}
  });
}
function probePc() {
  const net = require('net'); const t0 = Date.now();
  const sock = net.connect({ host: _winHost, port: 22, timeout: 4000 });
  const done = (ok) => { _pcHealth = { reachable: ok, rttMs: ok ? Date.now() - t0 : null, host: _winHost, at: Date.now() }; try { sock.destroy(); } catch {} if (!ok) reresolveWin(); };
  sock.on('connect', () => done(true));
  sock.on('error', () => done(false));
  sock.on('timeout', () => done(false));
}
if (!TEST_MODE) { setTimeout(probePc, 2000); setInterval(probePc, 30000); }
app.get('/api/health', (req, res) => {
  let tmuxAvailable = false;
  try { execSync(`tmux -V`, { encoding: 'utf8', timeout: 1500 }); tmuxAvailable = true; } catch {}
  const legacyNames = legacyTmuxNames();
  let gaveUp = 0; for (const h of _heal.values()) if (h.gaveUp) gaveUp++;
  const projects = projectsHealth();
  // A2 #9: if the PC host is down, armed sessions are hosted-and-unreachable (can't be healed) → surface
  // that as degraded instead of a falsely-green dot. Legacy tmux names are also degraded blockers.
  let hostedArmedDown = !hostUp() ? _healOn.size : 0;
  const degraded = TEST_MODE
    ? (!hostUp() || !hostProtocolOk() || legacyNames.length > 0)
    : (!hostUp() || !hostProtocolOk() || _pcHealth.reachable === false || gaveUp > 0 || hostedArmedDown > 0 || legacyNames.length > 0 || !projects.bridgeLive);
  res.json({ ok: !degraded, degraded, uptimeSec: Math.round(process.uptime()), tmuxAvailable, sessions: hostSessions.size,
             legacySessions: legacyNames.length, legacyNames, legacyPolicy: 'blocked', armed: _healOn.size, gaveUp, hostedArmedDown, pc: _pcHealth,
             projects,
             host: { connected: hostUp(), name: hostLabel, sessions: hostSessions.size, protocol: hostProtocol.protocol, caps: hostProtocol.caps, protocolOk: hostProtocolOk() },
             node: process.version, at: Date.now() });
});

// --- app command queue: the owner (web) enqueues actions for the desktop app; the app polls + acks them.
// Today: "kill" a live agent session. Enqueue is owner-gated (the global auth middleware above); pull +
// ack are loopback-only (only the app, reaching in over its own SSH, can read the queue or run anything).
const COMMANDS_FILE = STATE_DIR + '/app-commands.json';
let _commands = [];
try { _commands = JSON.parse(fs.readFileSync(COMMANDS_FILE, 'utf8')); } catch {}
function saveCommands() { atomicWrite(COMMANDS_FILE, JSON.stringify(_commands)); }
function pruneCommands() { const cut = Date.now() - 10 * 60 * 1000; _commands = _commands.filter(c => (c.ts || 0) > cut); }
let _cmdSeq = 0;
function enqueueAppCommand(b) {
  pruneCommands();
  const id = 'c' + Date.now().toString(36) + '-' + (++_cmdSeq).toString(36);
  const cmd = { id, type: String(b.type || ''), sessionId: String(b.sessionId || ''), tool: String(b.tool || ''),
                pid: Number(b.pid) || 0, uploadId: String(b.uploadId || ''), filename: String(b.filename || ''),
                title: String(b.title || '').slice(0, 200), keep: !!b.keep, label: String(b.label || ''),
                muxName: String(b.muxName || ''), sessionName: String(b.sessionName || ''), muxCommand: String(b.muxCommand || ''),
                collection: String(b.collection || '').slice(0, 200), collectionId: String(b.collectionId || '').slice(0, 200),
                deckId: String(b.deckId || '').slice(0, 200), deck: String(b.deck || '').slice(0, 200),
                deckName: String(b.deckName || '').slice(0, 200), ts: Date.now(), status: 'pending', detail: '' };
  _commands.push(cmd); saveCommands();
  return cmd;
}
function waitForCommandResult(id, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  return new Promise(resolve => {
    const tick = () => {
      const c = _commands.find(x => x.id === id);
      if (c && c.status !== 'pending') return resolve({ ok: c.status === 'done', detail: c.detail || '' });
      if (Date.now() >= deadline) return resolve({ ok: false, detail: 'timeout waiting for PC bridge ack' });
      setTimeout(tick, 200);
    };
    tick();
  });
}
const ALLOWED_CMDS = new Set(['kill', 'transcript', 'fetchfile', 'rename', 'addtocollection', 'startmux']);
app.post('/api/app-commands', (req, res) => {     // web (owner) enqueues
  const b = req.body || {};
  if (!ALLOWED_CMDS.has(b.type)) return res.status(400).json({ error: 'unsupported command' });
  if (b.type === 'kill' && !b.sessionId && !b.pid) return res.status(400).json({ error: 'sessionId or pid required' });
  if (b.type === 'transcript' && !b.sessionId) return res.status(400).json({ error: 'sessionId required' });
  if (b.type === 'fetchfile' && !b.uploadId) return res.status(400).json({ error: 'uploadId required' });
  if (b.type === 'rename' && (!b.sessionId || !String(b.title || '').trim())) return res.status(400).json({ error: 'sessionId and title required' });
  if (b.type === 'addtocollection' && (!(b.muxName || b.sessionName) || !(String(b.collection || '').trim() || String(b.collectionId || '').trim()))) return res.status(400).json({ error: 'muxName/sessionName and collection required' });
  if (b.type === 'addtocollection' && !appLive()) return res.status(409).json({ error: 'desktop app is not live; collection changes are disabled' });
  if (b.type === 'startmux' && !(b.muxName || b.sessionName)) return res.status(400).json({ error: 'muxName/sessionName required' });
  const cmd = enqueueAppCommand(b);
  res.json({ ok: true, id: cmd.id });
});
app.get('/api/app-commands', (req, res) => {      // app (loopback) pulls pending
  if (!isTrustedLocal(req)) return res.status(403).json({ error: 'forbidden' });
  pruneCommands();
  res.json(_commands.filter(c => c.status === 'pending'));
});
app.post('/api/app-commands/:id/ack', (req, res) => {   // app (loopback) reports a result
  if (!isTrustedLocal(req)) return res.status(403).json({ error: 'forbidden' });
  const c = _commands.find(x => x.id === req.params.id);
  if (c) {
    c.status = (req.body && req.body.ok) ? 'done' : 'failed'; c.detail = String((req.body && req.body.detail) || ''); c.doneAt = Date.now(); saveCommands();
    // a successful fetchfile carries the PC path it landed at — remember it on the workspace entry.
    if (c.type === 'fetchfile' && req.body && req.body.ok && c.uploadId) {
      const u = _uploads.find(x => x.id === c.uploadId);
      if (u) { u.pcPath = c.detail; saveUploadsMeta(); }
    }
  }
  res.json({ ok: true });
});
app.get('/api/app-commands/:id', (req, res) => {  // web (owner) polls a command's outcome
  const c = _commands.find(x => x.id === req.params.id);
  if (!c) return res.status(404).json({ error: 'not found' });
  res.json({ id: c.id, status: c.status, detail: c.detail || '' });
});

// --- file upload side-channel: phone -> VPS (stored here) -> the desktop app pulls it down to the PC
// (scp, via a `fetchfile` command) and points the model at the local path. Deliberately NOT routed
// through the terminal/tmux. Owner-gated by the global middleware; size-capped; pruned after an hour.
const UPLOADS_DIR = STATE_DIR + '/uploads';
try { fs.mkdirSync(UPLOADS_DIR, { recursive: true }); } catch {}
const UPLOADS_META = STATE_DIR + '/uploads-meta.json';
let _uploads = [];
try { _uploads = JSON.parse(fs.readFileSync(UPLOADS_META, 'utf8')); } catch {}
function saveUploadsMeta() { atomicWrite(UPLOADS_META, JSON.stringify(_uploads)); }
const uploadDir = id => UPLOADS_DIR + '/' + String(id).replace(/[^A-Za-z0-9_.-]/g, '');
const isImage = n => /\.(png|jpe?g|gif|webp|bmp|svg|heic)$/i.test(n || '');
function dropUpload(u) { try { fs.rmSync(uploadDir(u.id), { recursive: true, force: true }); } catch {} }
// "leave" (not kept) files are temporary: drop them on restart and after an hour. "keep" files persist.
function pruneUploads(all) {
  const cut = Date.now() - 60 * 60 * 1000;
  const survivors = [];
  for (const u of _uploads) {
    if (!u.keep && (all || (u.ts || 0) < cut)) dropUpload(u); else survivors.push(u);
  }
  if (survivors.length !== _uploads.length) { _uploads = survivors; saveUploadsMeta(); }
}
pruneUploads(true);   // restart sweep: non-kept uploads disappear
let _upSeq = 0;
// Upload a file into the per-session workspace. keep=1 makes it persist; otherwise it's swept on restart.
app.post('/api/upload', express.raw({ type: '*/*', limit: '64mb' }), (req, res) => {
  const raw = req.body;
  if (!raw || !raw.length) return res.status(400).json({ error: 'empty body' });
  const safe = (String(req.query.name || 'file').replace(/[^A-Za-z0-9._-]/g, '_').slice(0, 120)) || 'file';
  const session = SAFE(req.query.session || '');
  const keep = req.query.keep === '1';
  pruneUploads(false);
  const id = 'u' + Date.now().toString(36) + '-' + (++_upSeq).toString(36);
  try { fs.mkdirSync(uploadDir(id), { recursive: true }); fs.writeFileSync(uploadDir(id) + '/' + safe, raw); }
  catch (e) { return res.status(500).json({ error: 'write failed' }); }
  const u = { id, name: safe, session, ts: Date.now(), size: raw.length, keep, pcPath: '' };
  _uploads.push(u); saveUploadsMeta();
  res.json({ ok: true, uploadId: id, filename: safe, size: raw.length, keep });
});
// The workspace: files uploaded for a session (newest first). Drives the file panel + thumbnails.
app.get('/api/uploads', (req, res) => {
  const session = SAFE(req.query.session || '');
  const list = _uploads.filter(u => !session || u.session === session)
    .sort((a, b) => (b.ts || 0) - (a.ts || 0))
    .map(u => ({ id: u.id, name: u.name, size: u.size, keep: !!u.keep, ts: u.ts, image: isImage(u.name), onPc: !!u.pcPath, pcPath: u.pcPath || '' }));
  res.json(list);
});
app.get('/api/uploads/:id/raw', (req, res) => {
  const u = _uploads.find(x => x.id === req.params.id);
  if (!u) return res.status(404).end();
  res.sendFile(uploadDir(u.id) + '/' + u.name, {}, e => { if (e && !res.headersSent) res.status(404).end(); });
});
app.patch('/api/uploads/:id', (req, res) => {
  const u = _uploads.find(x => x.id === req.params.id);
  if (!u) return res.status(404).json({ error: 'not found' });
  if (req.body && typeof req.body.keep === 'boolean') { u.keep = req.body.keep; saveUploadsMeta(); }
  res.json({ ok: true, keep: u.keep });
});
app.delete('/api/uploads/:id', (req, res) => {
  const i = _uploads.findIndex(x => x.id === req.params.id);
  if (i >= 0) { dropUpload(_uploads[i]); _uploads.splice(i, 1); saveUploadsMeta(); }
  res.json({ ok: true });
});

const server = http.createServer(app);
// noServer + manual routing: two path-bound WebSocketServers on one http server BOTH grab 'upgrade'
// and the non-matching one aborts the handshake with a 400 before the right one sees it.
const wss = new WebSocketServer({ noServer: true });
const wssHost = new WebSocketServer({ noServer: true });
server.on('upgrade', (req, socket, head) => {
  const p = (req.url || '').split('?')[0];
  if (p === '/ws') wss.handleUpgrade(req, socket, head, ws => wss.emit('connection', ws, req));
  else if (p === '/host') {
    // reject a bad /host token BEFORE completing the handshake (constant-time) — no 101, no 'open'
    let ok = false; try { ok = hostTokenOk(new URL(req.url, 'http://x').searchParams.get('token')); } catch {}
    if (!ok) { try { socket.destroy(); } catch {} return; }
    wssHost.handleUpgrade(req, socket, head, ws => wssHost.emit('connection', ws, req));
  } else socket.destroy();
});

// ---- the PC session host's inbound link (muxd dials US — no inbound port on the PC) ---------------
wssHost.on('connection', (ws, req) => {
  const u = new URL(req.url, 'http://x');
  if (!hostTokenOk(u.searchParams.get('token'))) { try { ws.close(1008, 'bad token'); } catch {} return; }
  try { req.socket.setNoDelay(true); } catch {}                       // low-latency: no Nagle on the host link
  if (hostWs && hostWs !== ws) { try { hostWs.close(); } catch {} }   // newest link wins (old zombie replaced)
  hostWs = ws;
  console.log('[host] PC session host connected');
  ws.on('message', raw => {
    let m; try { m = JSON.parse(raw.toString()); } catch { return; }
    if (m.t === 'hello' || m.t === 'sessions') {
      if (m.t === 'hello') {
        hostLabel = String(m.host || 'pc');
        hostProtocol = { protocol: Number(m.protocol || 0), caps: Array.isArray(m.caps) ? m.caps.map(String) : [] };
        console.log(`[host] hello from ${hostLabel} (${(m.sessions || []).length} session(s), protocol ${hostProtocol.protocol || 'unknown'})`);
      }
      const list = m.t === 'hello' ? m.sessions : m.list;
      const reported = new Set(), incoming = new Map();
      for (const s of (list || [])) {
        const n = SAFE(s.name);
        if (n) {
          const alive = !!s.alive;
          const hasCommand = !!s.hasCommand;
          const shellOnly = Object.prototype.hasOwnProperty.call(s, 'shellOnly') ? !!s.shellOnly : (alive && !hasCommand);
          reported.add(n);
          incoming.set(n, { alive, created: s.created || 0, lastOut: s.lastOut || 0, tail: String(s.tail || ''),
                            cols: s.cols || 0, rows: s.rows || 0, heal: !!s.heal, owner: !!s.owner,
                            localFirst: !!s.localFirst, localViewers: s.localViewers || 0,
                            hasCommand, shellOnly, ready: Object.prototype.hasOwnProperty.call(s, 'ready') ? !!s.ready : alive,
                            kind: String(s.kind || (alive ? (shellOnly ? 'shell' : 'command') : 'dormant')),
                            cmdSig: String(s.cmdSig || '') });
        }
      }
      // A2 #1: don't let a status push evict an optimistic create muxd hasn't reported yet; confirm/expire pendings.
      for (const [n, exp] of [...pendingCreates]) {
        if (Date.now() > exp || reported.has(n)) pendingCreates.delete(n);
        else if (hostSessions.has(n) && !incoming.has(n)) incoming.set(n, hostSessions.get(n));
      }
      hostSessions.clear();
      for (const [n, v] of incoming) hostSessions.set(n, v);
      if (m.t === 'hello') {
        // A2 #4: muxd (re)connected — its ptys were re-spawned at their own size, so our per-session
        // resize dedup (st.cur) is stale. Clear it and re-assert sizes so viewers aren't stuck at old dims.
        for (const st of sessions.values()) st.cur = null;
        for (const n of hostSessions.keys()) recompute(n);
        const legacy = new Set(legacyTmuxNames());
        const twins = [...hostSessions.keys()].filter(n => legacy.has(n));
        if (twins.length) console.log(`[legacy] hosted/legacy name conflict(s): ${twins.join(', ')} - relay will refuse web attach/manage until cleaned manually`);
      }
    } else if (m.t === 'o') {
      const n = SAFE(m.s); const h = hostSessions.get(n); if (h) h.lastOut = Date.now();
      const st = sessions.get(n); if (!st) return;
      const buf = Buffer.from(m.d || '', 'base64');
      for (const c of st.clients.values()) {
        if (!c.hosted || c.ws.readyState !== 1) continue;
        if (c.sbWait) { (c.q = c.q || []).push(buf); if (c.q.length > 800) { c.sbWait = false; } continue; }  // hold live bytes until the scrollback replay lands
        try { c.ws.send(buf); } catch {}
      }
    } else if (m.t === 'sb') {
      const n = SAFE(m.s); const st = sessions.get(n); if (!st) return;
      const buf = Buffer.from(m.d || '', 'base64');
      for (const c of st.clients.values()) {
        if (!c.hosted || !c.sbWait) continue;                          // late/duplicate sb after a client went live → dropped (A2 #3)
        c.sbWait = false;
        try { c.ws.send(CLEAR_SCREEN); c.ws.send(buf); for (const q of (c.q || [])) c.ws.send(q); } catch {}  // clear → replay → queued-live (A2 #2)
        c.q = [];
      }
    } else if (m.t === 'tailr') { const f = pendingTails.get(m.rid); if (f) { pendingTails.delete(m.rid); f(String(m.text || '')); }
    } else if (m.t === 'killed') { hostSessions.delete(SAFE(m.s)); }
  });
  const ka = setInterval(() => { if (ws.readyState === 1) { try { ws.ping(); } catch {} } }, 20000);
  ws.on('close', () => { clearInterval(ka); if (hostWs === ws) { hostWs = null; hostProtocol = { protocol: 0, caps: [] }; console.log('[host] PC session host disconnected'); } });
});

// ---- shared window sizing + DEVICE-IDENTITY PINNING (multi-client mirror, ONE stable size) --------
// One shared size per session (tmux can't per-client-size a shared window; we hold every viewer at the
// server-chosen size and each PANs if it's bigger than their screen). PIN = "prefer THIS device": the
// pinned device's viewport drives the size for EVERYONE — last-pinner-wins — and it's keyed to a
// persistent deviceId (localStorage), so a Wi-Fi blip / reconnect / relay restart does NOT lose the pin
// (the old code pinned a connection id → gone on every reconnect). No pin = auto over the RECENTLY-ACTIVE
// viewers only, so a backgrounded desktop tab in another room can't force your phone to pan forever.
const PINS_FILE = STATE_DIR + '/pins.json';
let pins = new Map();   // session -> { deviceId, label, cols, rows, at }
try { pins = new Map(JSON.parse(fs.readFileSync(PINS_FILE, 'utf8'))); } catch {}
let _pinsDirty = false;
function savePins() { try { fs.writeFileSync(PINS_FILE + '.tmp', JSON.stringify([...pins])); fs.renameSync(PINS_FILE + '.tmp', PINS_FILE); _pinsDirty = false; } catch {} }
setInterval(() => { if (_pinsDirty) savePins(); }, 20000);
const ACTIVE_MS = +process.env.MUX_ACTIVE_MS || 180000;   // "recently active" window that auto-size considers (3 min; env-overridable for tests)
const PIN_HOLD_MS = 600000; // hold an absent pinned device's last size before falling back to auto (10 min)

const sessions = new Map(); // name -> { clients: Map<id,Client>, cur }
let _cid = 0;
function sessionState(name) {
  let st = sessions.get(name);
  if (!st) { st = { clients: new Map(), cur: null }; sessions.set(name, st); }
  return st;
}
function isActive(c) { return c.visible !== false || (Date.now() - (c.lastActive || c.connAt || 0) < ACTIVE_MS); }
function widest(list) { let b = list[0]; for (const c of list) if (c.vcols > b.vcols || (c.vcols === b.vcols && c.vrows > b.vrows)) b = c; return b; }
function targetSize(st, name) {
  const hosted = hostSessions.get(name);
  const localOwned = hosted && hosted.alive !== false
    && ((hosted.localViewers | 0) > 0 || !!hosted.owner);
  if (localOwned && (hosted.cols | 0) > 1 && (hosted.rows | 0) > 1) {
    // Local-first rule: an attached PC terminal owns the PTY size. Headless PC-hosted
    // sessions still resize to the active web viewer so the site behaves like a native terminal.
    return { cols: hosted.cols | 0, rows: hosted.rows | 0, pin: null, hostedSize: true };
  }
  const all = [...st.clients.values()].filter(c => c.vcols > 1 && c.vrows > 1);
  const pin = pins.get(name);
  if (pin) {
    const onDev = all.filter(c => (c.deviceId || ('sock-' + c.id)) === pin.deviceId);
    if (onDev.length) { const c = widest(onDev); if (c.vcols !== pin.cols || c.vrows !== pin.rows) { pin.cols = c.vcols; pin.rows = c.vrows; _pinsDirty = true; } return { cols: c.vcols, rows: c.vrows, pin }; }
    if (Date.now() - (pin.at || 0) < PIN_HOLD_MS && pin.cols > 1) return { cols: pin.cols, rows: pin.rows, pin };  // pinned device away → hold its size (grace)
    pins.delete(name); savePins();   // grace expired → drop the pin, fall to auto
  }
  if (!all.length) return null;
  const active = all.filter(isActive);
  const c = widest(active.length ? active : all);
  return { cols: c.vcols, rows: c.vrows, pin: null };
}
function recompute(name) {
  const st = sessions.get(name); if (!st) return;
  const sz = targetSize(st, name); if (!sz) return;
  const cols = Math.max(2, sz.cols | 0), rows = Math.max(2, sz.rows | 0);
  for (const c of st.clients.values()) { if (c.term) try { c.term.resize(cols, rows); } catch {} }
  // Hosted sessions are local-first: if muxd already reports a PTY size, web viewers follow it
  // and never resize the PC PTY. Only pending brand-new web-created sessions may send an initial size.
  if ([...st.clients.values()].some(c => c.hosted) && !sz.hostedSize) {
    if (!st.cur || st.cur.cols !== cols || st.cur.rows !== rows) { st.cur = { cols, rows }; sendHost({ t: 'resize', s: name, cols, rows }); }
  }
  const clients = [...st.clients.values()].map(c => ({ id: c.id, w: c.vcols, h: c.vrows, label: c.label || '', dev: c.deviceId || '', active: isActive(c) }));
  const pinned = !!sz.pin, pinLabel = pinned ? (sz.pin.label || 'a device') : '';
  for (const c of st.clients.values()) {
    if (c.ws.readyState !== 1) continue;
    const mine = pinned && sz.pin.deviceId === (c.deviceId || ('sock-' + c.id));
    const mode = sz.hostedSize ? 'local' : (pinned ? 'pinned' : 'auto');
    const modeLabel = sz.hostedSize ? `local · ${cols}×${rows}` : (pinned ? `📌 ${pinLabel} · ${cols}×${rows}` : `auto · ${cols}×${rows}`);
    try { c.ws.send('d' + JSON.stringify({ cols, rows, mode, pinLabel, mine, modeLabel, me: c.id, clients })); } catch {}
  }
}
function pinToDevice(name, client, on) {
  if (on) pins.set(name, { deviceId: client.deviceId || ('sock-' + client.id), label: client.label || 'this device', cols: client.vcols, rows: client.vrows, at: Date.now() });
  else pins.delete(name);
  savePins(); recompute(name);
}
function pinToDeviceId(name, deviceId) {   // long-press: pin to ANY listed device
  const st = sessions.get(name); if (!st) return;
  const c = [...st.clients.values()].find(x => (x.deviceId || ('sock-' + x.id)) === deviceId);
  if (c) pinToDevice(name, c, true);
}
function cycleMode(name, client) {   // the size chip / legacy 's': toggle pin-to-ME ↔ auto (last-pinner-wins)
  const pin = pins.get(name);
  const mine = pin && client && pin.deviceId === (client.deviceId || ('sock-' + client.id));
  pinToDevice(name, client, !mine);
}
function applyActivity(client, o) {
  if (o && typeof o.vis === 'boolean') client.visible = o.vis;
  if (o && o.act) client.lastActive = Date.now();
}
// Shared sizing/pin/activity message handler for BOTH paths ('i' input stays with each caller).
function handleClientMsg(name, client, s) {
  const t = s[0];
  if (t === 'v' || t === 'r') { try { const o = JSON.parse(s.slice(1)); if (o.cols) client.vcols = Math.max(2, o.cols | 0); if (o.rows) client.vrows = Math.max(2, o.rows | 0); applyActivity(client, o); } catch {} recompute(name); return true; }
  if (t === 'h') { try { applyActivity(client, JSON.parse(s.slice(1))); } catch {} return true; }
  if (t === 'P') { const arg = s.slice(1); if (arg && arg[0] === '#') pinToDeviceId(name, arg.slice(1)); else pinToDevice(name, client, arg !== '0'); return true; }
  if (t === 's') { cycleMode(name, client); return true; }
  return false;
}

wss.on('connection', async (ws, req) => {
  if (!isTrustedLocal(req) && !(await isOwner(cookieVal(req, HL_COOKIE)))) { try { ws.close(1008, 'unauthorized'); } catch {} return; }
  try { req.socket.setNoDelay(true); } catch {}                       // low-latency keystrokes: no Nagle on the viewer link
  const u = new URL(req.url, 'http://x');
  const name = SAFE(u.searchParams.get('session'));
  if (!name) return ws.close();
  const vcols = Math.max(2, +u.searchParams.get('cols') || 100);
  const vrows = Math.max(2, +u.searchParams.get('rows') || 30);
  const deviceId = (u.searchParams.get('dev') || '').replace(/[^A-Za-z0-9_-]/g, '').slice(0, 40);   // persistent device identity for pinning
  const label = (u.searchParams.get('label') || '').replace(/[^\w .·/+-]/g, '').slice(0, 32) || 'device';

  // ---- PC-HOSTED attach: bridge this viewer to muxd (no tmux, no ssh — the session lives on the PC).
  // Unknown web tabs create an empty PC-local muxd shell. That preserves the old pleasant multiplex flow
  // while keeping ownership local to the PC instead of the VPS.
  const legacyBlocked = tmuxHas(name);
  if (legacyBlocked) {
    try { ws.close(1013, legacyDetail(name)); } catch {}
    return;
  }
  if (hostUp() && (hostSessions.has(name) || pendingCreates.has(name) || !legacyBlocked)) {
    const known = hostSessions.get(name);
    if (known && !hostProtocolOk()) {
      try { ws.close(1013, hostProtocolDetail()); } catch {}
      return;
    }
    if (known && known.alive === false) {
      try { ws.close(1013, 'Dormant mux session: relaunch it from Projects or run mux ' + name + ' locally.'); } catch {}
      return;
    }
    if (!known) {
      if (!hostProtocolOk()) {
        try { ws.close(1013, hostProtocolDetail()); } catch {}
        return;
      }
      if (!sendHost({ t: 'create', s: name, cmd: '', cols: vcols, rows: vrows, heal: _healOn.has(name) })) {
        try { ws.close(1013, 'PC mux host offline'); } catch {}
        return;
      }
      markPending(name);
    }
    const id = 'c' + (++_cid);
    const st = sessionState(name);
    const client = { id, ws, term: null, hosted: true, vcols, vrows, sbWait: true, sbTok: ++_cid, q: [], deviceId, label, visible: true, lastActive: Date.now(), connAt: Date.now() };
    st.clients.set(id, client);
    if (!sendHost({ t: 'sb', s: name, max: HOST_SB_BYTES })) {           // bounded replay; live bytes queue briefly behind it
      st.clients.delete(id);
      try { ws.close(1013, 'PC mux host offline'); } catch {}
      return;
    }
    // If scrollback is slow or large, go live quickly. Local muxd keeps running either way.
    setTimeout(() => { if (client.sbWait) { client.sbWait = false; try { ws.send(CLEAR_SCREEN); for (const q of client.q) ws.send(q); } catch {} client.q = []; } }, HOST_SB_WAIT_MS);
    recompute(name);
    ws.on('message', m => {
      const s = m.toString();
      if (s[0] === 'i') {
        if (!sendHost({ t: 'i', s: name, d: Buffer.from(s.slice(1), 'utf8').toString('base64') })) {
          try { ws.close(1013, 'PC mux host offline'); } catch {}
          return;
        }
        client.lastActive = Date.now(); return;
      }
      handleClientMsg(name, client, s);
    });
    const ka = setInterval(() => { if (ws.readyState === 1) { try { ws.ping(); } catch {} } }, 25000);
    ws.on('close', () => { clearInterval(ka); st.clients.delete(id); recompute(name); });
    return;
  }

  try { ws.close(1013, 'PC mux host offline'); } catch {}
});

const PORT = +process.env.PORT || 7682;
// 0.0.0.0: the PC's muxd dials us directly over the LAN (ws://<vps>:7682/host, token-gated; ufw scopes
// the port to the LAN). Loopback-trust semantics are unchanged — a LAN caller is NOT trusted-local and
// still hits the hl-auth owner gate for everything except /host-with-token.
server.listen(PORT, '0.0.0.0', () => console.log('multiplex-app on 0.0.0.0:' + PORT));
