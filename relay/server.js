// Multiplex app: relays browser terminals to muxd on the Windows PC. muxd owns the ConPTY sessions
// locally; this VPS mirrors them and stores lightweight web state. Legacy tmux sessions are reported
// only as blocking diagnostics. They are never created, attached, renamed, or killed by this relay.
const express = require('express');
const http = require('http');
const { WebSocketServer } = require('ws');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { execSync, execFile } = require('child_process');
const { durableJsonLoad, durableJsonWrite, durableWrite, fsyncDirectory, recoveryWriteFailureReport } = require('./durable-state');
const { createHealthAlerts, startHealthAlerts } = require("./health-alerts");
const { createNotifier } = require("./notify");
const { createRetention, pruneTranscripts: pruneTranscriptRetention } = require('./retention');
// Every acknowledged state mutation commits through durable-state.js before it is published in memory.
const { createLeaseConduit } = require('./lease-conduit');
// The write lease lives in muxd. This relay holds no lease key and makes no lease decision; the
// conduit forwards client-signed frames byte-for-byte and caches muxd's notices as UI hints only.
const leaseConduit = createLeaseConduit();
// Global state retention (the periodic whole-directory pass) lives in retention.js; it is installed
// after the stores it prunes are declared. Until then every gauge reads as null.
let _retention = null;
function retentionGauges() { return _retention ? _retention.gauges() : null; }
function hostTokenOk(t) { if (!HOST_TOKEN || !t || t.length !== HOST_TOKEN.length) return false; try { return crypto.timingSafeEqual(Buffer.from(t), Buffer.from(HOST_TOKEN)); } catch { return false; } }

const app = express();
const TEST_MODE = process.env.MUX_TEST_MODE === '1';
const STATE_DIR = process.env.MUX_STATE_DIR || __dirname;
fs.mkdirSync(STATE_DIR, { recursive: true });
let persistenceFailure = '';
let persistenceBlocked = '';
function requirePersistenceWritable() {
  if (persistenceBlocked) throw new Error('persistence is blocked pending operator recovery: ' + persistenceBlocked);
}
function writeJsonState(file, value) {
  requirePersistenceWritable();
  durableJsonWrite(file, value);
  persistenceFailure = '';
}
function writeBytesState(file, value) {
  requirePersistenceWritable();
  durableWrite(file, value);
  persistenceFailure = '';
}
function recordPersistenceFailure(error) {
  persistenceFailure = String(error && error.message || error || 'unknown persistence failure');
  if (error && (error.unknown || error.recoveryError))
    persistenceBlocked = persistenceFailure;
  console.error('[persistence] ' + persistenceFailure);
  return persistenceFailure;
}
function failPersistence(res, error) {
  recordPersistenceFailure(error);
  return res.status(503).json({ error: 'state persistence failed' });
}
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
const AUTH_CACHE_TTL_MS = Math.max(1, +process.env.MUX_AUTH_CACHE_TTL_MS || 60000);
const AUTH_CACHE_MAX = Math.max(1, +process.env.MUX_AUTH_CACHE_MAX || 512);
const _authCache = new Map();
function cookieVal(req, name) {
  const m = (req.headers.cookie || '').match(new RegExp('(?:^|;\\s*)' + name + '=([^;]+)'));
  return m ? decodeURIComponent(m[1]) : null;
}
async function isOwner(token) {
  if (!token) return false;
  const now = Date.now();
  for (const [cachedToken, entry] of _authCache) {
    if (entry.exp <= now) _authCache.delete(cachedToken);
  }
  const hit = _authCache.get(token);
  if (hit) {
    _authCache.delete(token);
    _authCache.set(token, hit);
    return hit.owner;
  }
  try {
    const r = await fetch(HLAUTH_BASE + '/internal/verify', { headers: { 'x-internal-key': HL_KEY, 'x-session-token': token } });
    const j = await r.json();
    const owner = !!(j.authenticated && j.user && j.user.isOwner);
    _authCache.delete(token);
    while (_authCache.size >= AUTH_CACHE_MAX) _authCache.delete(_authCache.keys().next().value);
    _authCache.set(token, { exp: Date.now() + AUTH_CACHE_TTL_MS, owner });
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
// Loopback is NOT a trust boundary on this host: containers run with network_mode: host and share
// 127.0.0.1, so any compromised app satisfies isTrustedLocal(). Loopback therefore only admits the
// narrow desktop-bridge data routes below (each re-checks isTrustedLocal itself) and never grants
// terminal or general API access. Everything else requires the owner cookie.
const LOCAL_BRIDGE_ROUTES = [
  { m: 'GET',  p: /^\/api\/health\/?$/ },        // multiplex-healthcheck.timer polls this from loopback
  { m: 'POST', p: /^\/api\/projects\/?$/ },
  { m: 'POST', p: /^\/api\/running\/?$/ },
  { m: 'GET',  p: /^\/api\/app-commands\/?$/ },
  { m: 'POST', p: /^\/api\/app-commands\/lease\/?$/ },
  { m: 'POST', p: /^\/api\/app-commands\/[^/]+\/ack\/?$/ },
];
function isLocalBridgeRoute(req) {
  const p = String(req.originalUrl || req.url || '').split('?')[0];
  return LOCAL_BRIDGE_ROUTES.some(r => r.m === req.method && r.p.test(p));
}
// Dedicated dispatch capability for the ops-bot fix factory. Deliberately narrow: it authorises
// exactly ONE route (POST /api/sessions/:name/relaunch) for an allowlisted session name, and
// nothing else. It is NOT the hl-auth service key and NOT an owner session, so a compromised
// ops-bot cannot use it to list sessions, attach a terminal, upload, or enqueue app-commands.
// muxd still resolves the command from its own manifest, so the relay never carries an executable.
const DISPATCH_KEY = process.env.MUX_DISPATCH_KEY || '';
const DISPATCH_SESSIONS = new Set(String(process.env.MUX_DISPATCH_SESSIONS || 'fixbot-worker')
  .split(',').map(s => s.trim()).filter(Boolean));
function dispatchRouteOk(req) {
  if (!DISPATCH_KEY || req.method !== 'POST') return false;
  const p = String(req.originalUrl || req.url || '').split('?')[0];
  const m = p.match(/^\/api\/sessions\/([^/]+)\/relaunch\/?$/);
  if (!m) return false;
  let name; try { name = decodeURIComponent(m[1]); } catch { return false; }
  if (!DISPATCH_SESSIONS.has(name)) return false;
  const t = String(req.headers['x-mux-dispatch-key'] || '');
  if (t.length !== DISPATCH_KEY.length) return false;
  try { return crypto.timingSafeEqual(Buffer.from(t), Buffer.from(DISPATCH_KEY)); } catch { return false; }
}
// Cross-Site WebSocket Hijacking guard for /ws: WebSocket upgrades are NOT covered by CORS and the
// browser auto-attaches the hl_session cookie, so a browser upgrade must present an allowlisted Origin.
// A missing Origin is a non-browser client (tests/tools) — allowed only under TEST_MODE.
const ALLOWED_WS_ORIGINS = (process.env.ALLOWED_WS_ORIGINS || 'https://harmonizerlabs.cc')
  .split(',').map(s => s.trim()).filter(Boolean);
function wsOriginOk(req) {
  const origin = req.headers.origin;
  if (!origin) return TEST_MODE;   // no Origin = not a browser; /ws is browser-only in prod
  return ALLOWED_WS_ORIGINS.includes(origin);
}
// TEST-ONLY loopback trust, and the ONLY definition of it — the HTTP gate and the /ws upgrade must
// consult the same function or they drift, which is exactly what happened: aa74650 correctly removed
// ambient loopback trust from BOTH, and the campaign's test path was then restored on the HTTP side
// only, so every viewer-attach test answered 1008 while every HTTP test passed.
//
// The campaign's relay tests were authored against a relay that trusted loopback outright. The fix is
// NOT to restore that bypass — master removed it on purpose, because containers run with
// network_mode: host and share 127.0.0.1, so any compromised co-tenant satisfied isTrustedLocal().
// Requiring BOTH an explicit MUX_TEST_MODE=1 opt-in AND a loopback peer keeps production posture
// bit-identical: MUX_TEST_MODE is never set in a deploy, so this returns false there and both call
// sites fall through to the real owner check. Same precedent as wsOriginOk above.
//
// Both halves are load-bearing and tests/ws-auth-gate.test.js proves each one separately: drop the
// TEST_MODE half and an unauthenticated loopback caller gets a terminal; drop the isTrustedLocal half
// and anything arriving through nginx (which stamps X-Forwarded-For) does.
function testModeLocalTrust(req) {
  return TEST_MODE && isTrustedLocal(req);
}
// A credential SCOPED to one job: the desktop app pushes transcript pages with it, and it authorizes
// nothing else. Derived from the host credential so it never equals it, and it buys no read back — the
// GET side stays owner-only. Fails closed: no host credential and no explicit override = no pushes.
const TRANSCRIPT_BRIDGE_TOKEN = String(
  process.env.MUX_TRANSCRIPT_BRIDGE_TOKEN
  || (process.env.MUX_HOST_TOKEN
    ? crypto.createHash('sha256').update('mux-transcript-bridge:v1:' + process.env.MUX_HOST_TOKEN).digest('hex')
    : ''),
);
const TRANSCRIPT_BRIDGE_HEADER = 'x-mux-transcript-bridge';
function transcriptBridgeTokenOk(value) {
  const t = String(value || '');
  if (!TRANSCRIPT_BRIDGE_TOKEN || !t || t.length !== TRANSCRIPT_BRIDGE_TOKEN.length) return false;
  try { return crypto.timingSafeEqual(Buffer.from(t), Buffer.from(TRANSCRIPT_BRIDGE_TOKEN)); } catch { return false; }
}
function isTranscriptBridgePush(req) {
  return req.method === 'POST'
    && /^\/api\/transcripts\/[^/]+$/.test(String(req.path || ''))
    && transcriptBridgeTokenOk(req.headers[TRANSCRIPT_BRIDGE_HEADER]);
}
app.use(async (req, res, next) => {
  // INTEGRATION NOTE: r.2.16 arrived with a blanket `if (isTrustedLocal(req)) return next();` here.
  // That bypass was deliberately REMOVED on master — containers run network_mode: host and share
  // 127.0.0.1, so loopback is not a trust boundary and any compromised app satisfied it. Restoring it
  // while merging would have silently undone that fix, so the narrow route list below stands and the
  // transcript push gets its own scoped, token-checked exemption instead.
  if (isTranscriptBridgePush(req)) return next();   // re-verified in the route; see the transcript store
  if (isTrustedLocal(req) && isLocalBridgeRoute(req)) return next();   // loopback admits ONLY the desktop-bridge routes
  if (dispatchRouteOk(req)) return next();   // scoped fix-factory dispatch capability
  if (testModeLocalTrust(req)) return next();
  if (await isOwner(cookieVal(req, HL_COOKIE))) return next();
  const tok = cookieVal(req, HL_COOKIE);
  if (!tok) {
    const prefix = req.headers['x-forwarded-prefix'] || process.env.PUBLIC_RETURN || '/multiplex';
    if ((req.headers.accept || '').includes('text/html')) return res.redirect(HL_LOGIN + '?next=' + encodeURIComponent(prefix));
    return res.status(401).json({ error: 'login required' });
  }
  return res.status(403).send('Forbidden â€” owner only.');
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
function strictMuxName(value) {
  const raw = String(value || '').trim();
  const safe = SAFE(raw);
  return safe && safe === raw ? safe : '';
}
function opaqueIdentity(value) {
  const raw = String(value || '').trim();
  return raw && /^[A-Za-z0-9._-]+$/.test(raw) ? raw : '';
}
// ---- PC SESSION HOST link (P2/P3 â€” the ownership flip) ---------------------------------------------
// muxd on the PC owns each session's ConPTY locally and dials OUT to us over one multiplexed WebSocket
// (/host, token-gated). Sessions live on the PC: Wi-Fi drops / VPS reboots / relay deploys only cost the
// VIEWER a blip â€” the agent never notices. If this link is down, creation/attach fail loudly instead of
// making a VPS tmux twin that can silently diverge.
const HOST_TOKEN = process.env.MUX_HOST_TOKEN || '';
let hostWs = null;                 // the PC's muxd link (one at a time; newest wins)
let hostLabel = '';
const hostSessions = new Map();    // name -> { alive, created, lastOut, tail }
// A just-created hosted session muxd hasn't reported back yet. A muxd status push (built before it
// processed our `create`) must NOT evict this optimistic entry â€” otherwise the imminent /ws attach or a
// boot-recreate sees no hosted session, makes a tmux TWIN, and two agents resume one transcript (A2 #1).
// Clear-scrollback + clear-screen + home: prefixes a scrollback replay so a reconnecting viewer that
// still shows the pre-drop screen doesn't get the replay stacked ON TOP of it (A2 #2/#3).
// The muxd ring contains raw PTY bytes and can begin after a TUI entered private modes.
// Reset those modes before replay so stale alternate-screen/mouse state cannot poison a viewer.
const CLEAR_SCREEN = Buffer.from(
  '\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l\x1b[?1015l\x1b[?2004l' +
  '\x1b[?1049l\x1b[?25h\x1b[0m\x1b[3J\x1b[2J\x1b[H'
);
// Hosted sessions are PC-local first. Web attach must become live quickly; scrollback is a bounded
// convenience replay, not something allowed to stall live terminal bytes for multiple seconds.
const HOST_SB_BYTES = +process.env.MUX_HOST_SB_BYTES || 800000;
const HOST_SB_WAIT_MS = +process.env.MUX_HOST_SB_WAIT_MS || 900;
const HOST_SB_REQUEST_TIMEOUT_MS = Math.max(
  HOST_SB_WAIT_MS + 100,
  +process.env.MUX_HOST_SB_REQUEST_TIMEOUT_MS || 3000,
);
const MAX_TERM_COLS = 1000;
const MAX_TERM_ROWS = 300;
function clampTermDimension(value, fallback, maximum) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed >= 2
    ? Math.min(maximum, Math.floor(parsed))
    : fallback;
}
const REQUIRED_HOST_PROTOCOL = 4;
const REQUIRED_HOST_CAPS = new Set(['create', 'createAck', 'kill', 'rename', 'heal', 'tail', 'scrollback']);
let hostProtocol = { protocol: 0, caps: [] };
const hostInputOrigins = new Map();
function rememberHostInputOrigin(key, clientId) {
  const prior = hostInputOrigins.get(key);
  if (prior) clearTimeout(prior.timer);
  const timer = setTimeout(() => hostInputOrigins.delete(key), 20000);
  if (timer.unref) timer.unref();
  hostInputOrigins.set(key, { clientId, timer });
}
const hostUp = () => !!(hostWs && hostWs.readyState === 1);
function sendHost(obj) { if (hostUp()) { try { hostWs.send(JSON.stringify(obj)); return true; } catch {} } return false; }
const hostedHas = name => hostUp() && hostSessions.has(name);
const pendingHostCreates = new Map();
let _hostRequestSeq = 0;
function normalizeHostSession(s) {
  // agentTruth carries an "exe" key, which the forbidden-remote-key scan reads as the PC smuggling an
  // executable path into the relay — and a hit there closes the whole host link with 1008. It is a bare
  // image name (re-sanitized to one in normalizeAgentTruth), so scan the payload WITHOUT that subtree
  // instead of rejecting every session muxd reports.
  if (containsForbiddenRemoteKey(withoutAgentTruth(s))) return null;
  const name = strictMuxName(s && s.name);
  if (!name || name !== String(s && s.name || '')) return null;
  const sessionId = String(s.sessionId || '').trim();
  if (sessionId && !opaqueIdentity(sessionId)) return null;
  const rawAliases = Array.isArray(s.aliases) ? s.aliases.map(value => String(value || '').trim()) : [];
  if (rawAliases.some(value => value && !opaqueIdentity(value))) return null;
  const aliases = Array.isArray(s.aliases)
    ? rawAliases.filter(Boolean)
        .filter(id => id.toLowerCase() !== sessionId.toLowerCase())
        .filter((id, index, all) => all.findIndex(other => other.toLowerCase() === id.toLowerCase()) === index)
    : [];
  const alive = !!s.alive;
  const hasCommand = !!s.hasCommand;
  const shellOnly = Object.prototype.hasOwnProperty.call(s, 'shellOnly') ? !!s.shellOnly : (alive && !hasCommand);
  const value = {
    alive, created: s.created || 0, lastOut: s.lastOut || 0, tail: String(s.tail || ''),
    cols: clampTermDimension(s.cols, 0, MAX_TERM_COLS),
    rows: clampTermDimension(s.rows, 0, MAX_TERM_ROWS),
    heal: !!s.heal, owner: !!s.owner,
    localFirst: !!s.localFirst, localViewers: s.localViewers || 0,
    hasCommand, shellOnly, ready: Object.prototype.hasOwnProperty.call(s, 'ready') ? !!s.ready : alive,
    kind: String(s.kind || (alive ? (shellOnly ? 'shell' : 'command') : 'dormant')),
    sessionId, aliases, identityPending: !!s.identityPending,
    agentState: String(s.agentState || ''), agentLabel: String(s.agentLabel || ''),
    agentDetail: String(s.agentDetail || ''), agentConfidence: String(s.agentConfidence || ''),
    // OS-verified process truth (muxd cap "agentTruth"). Additive on protocol 4: a host that does not
    // send it leaves both of these falsy and every downstream decision falls back to today's behavior.
    agentStateSource: String(s.agentStateSource || ''), agentTruth: normalizeAgentTruth(s.agentTruth),
    sessionUuid: String(s.sessionUuid || '').slice(0, 128),
  };
  return value;
}
// Every `agentTruth` subtree removed, at any depth — the scan runs on what is left. The subtree is not
// exempt from scrutiny, it is scrutinised differently: normalizeAgentTruth() rebuilds it from a
// four-field allow-list and re-sanitizes `exe` down to a bare image name, so nothing path- or
// command-shaped inside it can ever reach hostSessions no matter what the PC put there.
function withoutAgentTruth(value) {
  if (Array.isArray(value)) return value.map(withoutAgentTruth);
  if (!value || typeof value !== 'object') return value;
  const rest = {};
  for (const [key, child] of Object.entries(value))
    if (key !== 'agentTruth') rest[key] = withoutAgentTruth(child);
  return rest;
}
function normalizeAgentTruth(truth) {
  if (!truth || typeof truth !== 'object' || Array.isArray(truth)) return null;
  // Bare image name only ("claude.exe"). Anything with a separator, space or quote is not an image name
  // and is dropped to '' — the relay must never end up holding something command-shaped from the PC.
  const exe = String(truth.exe || '').trim();
  return {
    procAlive: !!truth.procAlive,
    cpuActiveRecent: !!truth.cpuActiveRecent,
    exe: /^[A-Za-z0-9._+-]{1,60}$/.test(exe) ? exe : '',
    checkedUtc: String(truth.checkedUtc || '').slice(0, 64),
  };
}
function normalizeHostSessionList(list) {
  if (list != null && !Array.isArray(list)) return null;
  const incoming = new Map();
  for (const session of (list || [])) {
    const name = strictMuxName(session && session.name);
    if (!name || name !== String(session && session.name || '') || incoming.has(name)) return null;
    const value = normalizeHostSession(session);
    if (!value) return null;
    incoming.set(name, value);
  }
  return incoming;
}
function announcedHostProtocol(message) {
  const announced = {
    protocol: Number(message && message.protocol || 0),
    caps: Array.isArray(message && message.caps) ? message.caps.map(String) : [],
    instanceId: String(message && message.instanceId || '').slice(0, 128),
  };
  if (!Number.isInteger(announced.protocol) || announced.protocol !== REQUIRED_HOST_PROTOCOL) return null;
  const caps = new Set(announced.caps);
  for (const required of REQUIRED_HOST_CAPS) if (!caps.has(required)) return null;
  return announced;
}
function requestHostCreate(message, timeoutMs = 20000, suppliedIntentId = '') {
  if (!hostUp()) return Promise.resolve({ ok: false, detail: 'PC mux host offline' });
  const expectedName = strictMuxName(message && message.s);
  if (!expectedName) return Promise.resolve({ ok: false, detail: 'invalid mux session name' });
  const rid = commandIntentId(suppliedIntentId)
    || 'hc' + Date.now().toString(36) + '-' + (++_hostRequestSeq).toString(36);
  const fingerprint = crypto.createHash('sha256').update(stableJson(message)).digest('hex');
  const existing = pendingHostCreates.get(rid);
  if (existing) {
    if (existing.expectedName !== expectedName || existing.fingerprint !== fingerprint)
      return Promise.resolve({
        ok: false,
        status: 409,
        error: 'intent id conflict',
        detail: 'create intent id is already bound to a different payload',
      });
    return existing.promise;
  }
  const promise = new Promise(resolve => {
    const timer = setTimeout(() => {
      pendingHostCreates.delete(rid);
      resolve({ ok: false, detail: 'muxd did not acknowledge the create request' });
    }, timeoutMs);
    pendingHostCreates.set(rid, {
      expectedName,
      fingerprint,
      promise: null,
      finish: result => {
        clearTimeout(timer);
        resolve(result);
      },
    });
    if (!sendHost({ ...message, rid })) {
      clearTimeout(timer);
      pendingHostCreates.delete(rid);
      resolve({ ok: false, detail: 'host socket closed before create could be sent' });
    }
  });
  const pending = pendingHostCreates.get(rid);
  if (pending) pending.promise = promise;
  return promise;
}
function hostProtocolOk() {
  if (!hostUp() || hostProtocol.protocol !== REQUIRED_HOST_PROTOCOL) return false;
  const caps = new Set(hostProtocol.caps || []);
  for (const c of REQUIRED_HOST_CAPS) if (!caps.has(c)) return false;
  return true;
}
function hostSupportsCap(cap) {
  return hostUp() && (hostProtocol.caps || []).map(String).includes(cap);
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
function requireHostCapability(res, cap, action) {
  if (!requireHostProtocol(res, action)) return false;
  if (hostSupportsCap(cap)) return true;
  failHost(res, 503, 'PC mux host protocol mismatch', `${action}: muxd is missing capability "${cap}"; restart MuxdSessionHost to load the current muxd`);
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
// E4: on-demand deep tail from muxd (previews) â€” request/response correlated by rid, 2.5s timeout.
const pendingTails = new Map(); let _rid = 0;
function requestHostTail(name, lines) {
  if (!hostUp()) return Promise.resolve(null);
  const rid = 'r' + (++_rid);
  return new Promise(resolve => {
    const to = setTimeout(() => { pendingTails.delete(rid); resolve(null); }, 2500);
    // TRUST: a tail is terminal truth, so muxd may sign it. The relay never validates the signature
    // (it holds no key and is not the trusted origin) — it only remembers the latest one per session so
    // the fleet glance can hand it to the client that CAN verify it. No sig ⇒ forget the stale one.
    pendingTails.set(rid, (txt, sig) => {
      clearTimeout(to);
      if (sig) _tailSigs.set(name, sig); else _tailSigs.delete(name);
      resolve(txt);
    });
    sendHost({ t: 'tail', s: name, lines, rid });
  });
}
// SYNCHRONOUS SPAWN ON THE STREAMING LOOP. This is execSync, so every call FREEZES the event loop --
// no keystroke forwarded, no PTY output flushed, no ws frame written -- until the child exits. Measured
// on the dev box (win32, tmux NOT installed, so the number is only process spawn and contains no tmux
// IPC at all): 14.9ms p50, 18.2ms p95, 230.7ms max per call. Timer lateness p95 across the loop went
// 11.0ms -> 105.9ms (max 629.9ms) with these probes running. That is a lower bound, and the 1500ms
// timeout above is the tail.
//
// It is called from: every /ws upgrade (so it blocks attach), listSessions() on GET /api/sessions
// (polled every 4s BY EVERY OPEN TAB), /api/health (twice -- also `tmux -V`), and every mutating route.
// Six tabs idling is ~6 spawns per 4s window on the process whose entire job is relaying bytes
// promptly. This is the most concrete mechanical explanation available for the "stuttery" complaint.
//
// The proper fix is to make it async behind a snapshot, which ripples through several sync call sites
// (tmuxHas is used inline in route guards and in the upgrade handler). This is the contained half of
// it: a short TTL cache, which collapses the N-tabs-polling case -- the dominant one -- to a single
// probe per window without changing any call site's sync/async shape. legacyProbes on /api/health
// makes the remaining spawn rate observable instead of assumed.
//
// Staleness is acceptable here BY DESIGN: legacy tmux sessions are a blocking diagnostic that the
// relay never creates and that an operator is expected to clean up manually, so noticing one up to
// TTL late costs nothing. Do not reach for this cache for anything with real freshness requirements.
const LEGACY_TMUX_TTL_MS = Math.max(0, +process.env.MUX_LEGACY_TMUX_TTL_MS || 2000);
let _legacyTmux = { at: 0, names: [], probes: 0 };
function legacyTmuxNames() {
  const now = Date.now();
  if (_legacyTmux.at && now - _legacyTmux.at < LEGACY_TMUX_TTL_MS) return _legacyTmux.names;
  let names = [];
  try {
    const out = execSync(`tmux list-sessions -F '#{session_name}' 2>/dev/null`, { encoding: 'utf8', timeout: 1500 });
    names = out.trim().split('\n').map(SAFE).filter(Boolean);
  } catch { names = []; }
  _legacyTmux = { at: now, names, probes: _legacyTmux.probes + 1 };
  return names;
}
function tmuxHas(name) { const n = SAFE(name); return !!n && legacyTmuxNames().includes(n); }
function legacyDetail(name) {
  return `legacy VPS tmux session "${name}" exists; relay refuses to attach/manage tmux. Clean it manually after confirming it is not a live agent.`;
}
function failLegacy(res, name) {
  return failHost(res, 409, 'legacy tmux session exists', legacyDetail(name));
}

// Per-session attention status for the tab dots:
//   green  = command-backed agent is producing output recently (likely still working)
//   yellow = command-backed agent is alive but quiet (likely waiting for the human)
//   white  = neutral shell, no agent command is registered
//   red    = stopped/blocker: command returned to a shell, legacy conflict, detached mirror, etc.
const _sessState = new Map();   // name -> { everAgent }  (lets a bare shell read RED instead of WHITE)
const _stateCache = new Map();  // name -> { at, state }   throttle hosted-tail classification bursts
                                 // polls (4s interval Ã— several clients) must not spawn a subprocess per call
// NOTE: named paneAgentState (NOT sessionState) â€” there's already a sessionState() for window-sizing below.
function paneAgentState(name, content) {
  if (content === undefined) {
    const cached = _stateCache.get(name);
    if (cached && Date.now() - cached.at < 1500) return cached.state;
    content = '';
  }
  const mem = _sessState.get(name) || { everAgent: false };
  // Only the TAIL (bottom status line + footer) is reliable: a turn in flight shows "esc to interrupt"
  // there, and an idle agent shows its footer there. Scanning the whole pane would false-green an idle
  // agent off a previous turn's "(45s Â· 12k tokens)" left in scrollback (validated against live sessions).
  // Strip trailing blank/newline padding so the window isn't off-by-one, and widen to 10 lines so the live status line ABOVE
  // the input box + footer is included (that's where "esc to interrupt" sits during a turn).
  const tail = content.replace(/\s+$/, '').split('\n').slice(-10).join('\n');
  const working = /esc to inter|still thinking/i.test(tail);
  const agentUI = working || /âµâµ|auto mode (on|off)|for agents|shift\+tab to cycle|\? for shortcuts|\/goal (active|paused)|\b(gpt-[0-9][\w.-]*|opus [0-9]|sonnet [0-9]|haiku [0-9])\b/i.test(tail);
  if (agentUI) mem.everAgent = true;
  _sessState.set(name, mem);
  const state = working ? 'green' : agentUI ? 'yellow' : (mem.everAgent ? 'red' : 'white');
  _stateCache.set(name, { at: Date.now(), state });
  return state;
}
const AGENT_WORKING_FRESH_MS = Math.max(5000, Number(process.env.MUX_AGENT_WORKING_FRESH_MS || 25000));
const AGENT_STARTING_GRACE_MS = Math.max(5000, Number(process.env.MUX_AGENT_STARTING_GRACE_MS || 45000));
function cleanTerminalForState(s) {
  return String(s || '')
    .replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '')
    .replace(/\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)/g, '')
    .replace(/\x1b[@-Z\\-_]/g, '')
    // Older muxd builds stripped ESC from CSI sequences like ESC[0 q, leaving "[0 q".
    .replace(/\[[0-?]*[ -/]*[@-~]/g, '')
    .replace(/[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]/g, '');
}
function fmtAge(ms) {
  if (!Number.isFinite(ms) || ms < 0) return 'unknown';
  if (ms < 1500) return 'just now';
  if (ms < 60000) return Math.round(ms / 1000) + 's ago';
  if (ms < 3600000) return Math.round(ms / 60000) + 'm ago';
  return Math.round(ms / 3600000) + 'h ago';
}
function tailLooksAtShellPrompt(tail) {
  const lines = cleanTerminalForState(tail).split(/\r?\n/).map(l => l.trim()).filter(Boolean).slice(-8);
  return lines.some(l =>
    /^(?:PS\s+)?[A-Za-z]:\\[^>]{0,180}>\s*$/.test(l) ||
    /^[\w.\-]+@[\w.\-]+:[^#$]{0,180}[#$]\s*$/.test(l)
  );
}
// A probe older than this is not evidence any more — the process could have died (or been relaunched)
// in the gap. Applied symmetrically so a host clock running ahead of the relay does not look "fresh
// forever" in one direction and permanently stale in the other.
const AGENT_TRUTH_MAX_AGE_MS = Math.max(5000, Number(process.env.MUX_AGENT_TRUTH_MAX_AGE_MS || 45000));
// Fresh OS-level process truth from muxd, or null. Null covers every degraded case — host too old to
// advertise the capability, field absent, probe degraded to "heuristic", unparseable or stale
// timestamp — and null means "decide exactly the way we did before agentTruth existed".
function hostProcessTruth(h) {
  if (!hostSupportsCap('agentTruth')) return null;
  const truth = h && h.agentTruth;
  if (!truth || String(h.agentStateSource || '') !== 'process') return null;
  const checked = Date.parse(truth.checkedUtc || '');
  if (!Number.isFinite(checked)) return null;
  if (Math.abs(Date.now() - checked) > AGENT_TRUTH_MAX_AGE_MS) return null;
  return truth;
}
function hostAgentStatus(h) {
  const s = String(h && h.agentState || '').toLowerCase();
  if (!['working', 'attention', 'stopped', 'neutral', 'dormant'].includes(s)) return null;
  const state = s === 'working' ? 'green' : s === 'attention' ? 'yellow' :
                s === 'stopped' ? 'red' : s === 'dormant' ? 'dormant' : 'white';
  return {
    state,
    agentState: s,
    agentLabel: String(h.agentLabel || '') || (s === 'attention' ? 'waiting for you' : s),
    agentDetail: String(h.agentDetail || ''),
    agentConfidence: String(h.agentConfidence || 'medium'),
    needsAttention: s === 'attention' || s === 'stopped',
    // "stopped" is the host's heuristic read of a footer and is NOT proof of death; only an explicit
    // dormant declaration (muxd knows there is no process) opens the death-recovery gate here.
    healEligible: s === 'dormant',
  };
}
function attentionStatusForHosted(name, h, opts = {}) {
  if (opts.detachedLocal) return {
    state: 'detached', agentState: 'detached', agentLabel: 'detached local agent',
    agentDetail: 'agent process is alive locally, but muxd mirror is detached', agentConfidence: 'high',
    needsAttention: true,
  };
  // Explicit host dormancy is the one death signal that predates agentTruth and is still authoritative:
  // muxd is telling us the session has no process at all, so recovery is warranted without a probe.
  if (opts.dormant || !h || h.alive === false) return {
    state: 'dormant', agentState: 'dormant', agentLabel: 'dormant',
    agentDetail: 'no shell or agent is running until you relaunch it', agentConfidence: 'high',
    needsAttention: false, healEligible: true,
  };
  if (!h.hasCommand || h.shellOnly) return {
    state: 'white', agentState: 'neutral', agentLabel: 'plain shell',
    agentDetail: 'live terminal, no agent command registered', agentConfidence: 'high',
    needsAttention: false,
  };
  // Precedence: fresh OS truth > host heuristic (agentState) > our own pane regex. The OS is the only
  // source that can prove death, so it is also the only thing besides host dormancy that may mark a
  // session heal-eligible — a footer that merely LOOKS like a shell prompt never gets to kill an agent.
  const truth = hostProcessTruth(h);
  if (truth && !truth.procAlive) return {
    state: 'red', agentState: 'stopped', agentLabel: 'agent stopped',
    agentDetail: 'muxd confirms the agent process tree is gone' + (truth.exe ? ` (last seen: ${truth.exe})` : ''),
    agentConfidence: 'high', needsAttention: true, agentStateSource: 'process', healEligible: true,
  };
  const procAlive = !!(truth && truth.procAlive);
  const provided = hostAgentStatus(h);
  // A live process outranks a "stopped" call from either heuristic: the host's own footer read and our
  // pane regex both false-red on an agent that has simply printed something prompt-shaped.
  if (provided && !(procAlive && provided.state === 'red')) return provided;
  if (!provided && !procAlive && tailLooksAtShellPrompt(h.tail || '')) return {
    state: 'red', agentState: 'stopped', agentLabel: 'agent stopped',
    agentDetail: 'the command-backed session returned to a shell prompt', agentConfidence: 'high',
    needsAttention: true,
  };
  const now = Date.now();
  const lastOut = Number(h.lastOut || 0);
  const created = Number(h.created || 0);
  const lastOutAgeMs = lastOut ? now - lastOut : NaN;
  const createdAgeMs = created ? now - created : NaN;
  const source = procAlive ? { agentStateSource: 'process' } : {};
  if ((Number.isFinite(lastOutAgeMs) && lastOutAgeMs <= AGENT_WORKING_FRESH_MS) ||
      (Number.isFinite(createdAgeMs) && createdAgeMs <= AGENT_STARTING_GRACE_MS)) {
    return {
      state: 'green', agentState: 'working', agentLabel: 'agent working',
      agentDetail: 'terminal output updated ' + fmtAge(lastOutAgeMs), agentConfidence: 'medium',
      needsAttention: false, lastOutAgeMs, ...source,
    };
  }
  // A quiet agent that is still burning CPU is thinking, not waiting for you — only the OS can see that.
  if (procAlive && truth.cpuActiveRecent) return {
    state: 'green', agentState: 'working', agentLabel: 'agent working',
    agentDetail: 'no terminal output for ' + fmtAge(lastOutAgeMs) + ', but the agent process is using CPU',
    agentConfidence: 'high', needsAttention: false, lastOutAgeMs, ...source,
  };
  return {
    state: 'yellow', agentState: 'attention', agentLabel: 'waiting for you',
    agentDetail: 'no terminal output for ' + fmtAge(lastOutAgeMs), agentConfidence: 'medium',
    needsAttention: true, lastOutAgeMs, ...source,
  };
}
// ---- ATTENTION EPISODES -> one phone push per episode ---------------------------------------------
// An "episode" is one continuous stretch of a hosted session needing the human (agentState attention
// or stopped). We push ONCE per episode, and only after the state has HELD for a settle window, so the
// half-second yellow flicker between an agent's turns never buzzes a phone. The state has to clear
// (back to working/neutral/dormant) and re-enter before another push is allowed. Episodes are
// persisted, so a relay restart mid-episode does not re-fire a push the human already got.
// Deliberately NOT episodes here: host-link-down and heal-give-up. Those are ops-health alerts on the
// separate health lane; folding them in would let a VPS blip masquerade as "your agent is waiting".
// createNotifier is required once at the top of the file — the attention lane and the ops-health lane
// both use the same transport, and two independent leaves each added their own top-level require here.
const attentionNotifier = createNotifier();
const NOTIFY_SETTLE_MS = Math.max(TEST_MODE ? 10 : 1000, Number(process.env.MUX_NOTIFY_SETTLE_MS) || 20000);
// Deep link matches what the web UI's "copy link" builds (public/index.html): <base>/?s=<name>.
const NOTIFY_LINK_BASE = String(process.env.MUX_PUBLIC_BASE || process.env.HLAUTH_PUBLIC_BASE || '').trim().replace(/\/+$/, '');
const ATTENTION_EPISODES_FILE = STATE_DIR + '/attention-episodes.json';
const attentionEpisodes = new Map();   // name -> { since, notified }
{
  const loaded = durableJsonLoad(
    ATTENTION_EPISODES_FILE,
    [],
    value => Array.isArray(value) && value.every(entry => (
      Array.isArray(entry) && entry.length === 2
      && !!strictMuxName(entry[0])
      && entry[1] && typeof entry[1] === 'object'
      && Number.isFinite(Number(entry[1].since))
      && typeof entry[1].notified === 'boolean'
    )),
  );
  if (!Array.isArray(loaded)) throw new Error('persisted attention-episode state must be an entry array');
  for (const [name, episode] of loaded)
    attentionEpisodes.set(name, { since: Number(episode.since) || 0, notified: !!episode.notified });
}
function saveAttentionEpisodes() {
  // A push ledger must never take the host link down: record the failure like every other writer does
  // and keep serving. Worst case a restart re-pushes one episode, which beats dropping muxd's frame.
  try { writeJsonState(ATTENTION_EPISODES_FILE, [...attentionEpisodes]); }
  catch (error) { recordPersistenceFailure(error); }
}
// '' unless this session is in an episode-worthy state. Reuses the tab-dot classifier verbatim so the
// phone and the browser can never disagree about who is waiting.
function attentionEpisodeState(name) {
  const h = hostSessions.get(name);
  if (!h) return '';
  const dormant = h.alive === false;
  const attn = attentionStatusForHosted(name, h, { dormant, detachedLocal: dormant && locallyRunningMuxName(name) });
  const state = String(attn && attn.agentState || '');
  return (state === 'attention' || state === 'stopped') ? state : '';
}
// ---- PER-SESSION NOTIFICATION MUTE ---------------------------------------------------------------
// Muting a session does NOT stop the episode state machine — the episode still opens, settles and
// closes, only the outgoing push is swallowed. That way unmuting never dumps a backlog of stale buzzes
// for a session that has been waiting all night; you get the NEXT episode. Mutes are keyed by session
// name and are deliberately NOT pruned when the session disappears: a tab you silenced stays silenced
// across kill/relaunch, which is the whole point of a per-session mute.
const NOTIFY_MUTES_FILE = STATE_DIR + '/notify-mutes.json';
const notifyMutes = new Set();
{
  const loaded = durableJsonLoad(
    NOTIFY_MUTES_FILE,
    [],
    value => Array.isArray(value) && value.every(entry => !!strictMuxName(entry)),
  );
  if (!Array.isArray(loaded)) throw new Error('persisted notify-mute state must be a name array');
  for (const name of loaded) notifyMutes.add(String(name));
}
function saveNotifyMutes() { writeJsonState(NOTIFY_MUTES_FILE, [...notifyMutes]); }
function pushAttentionEpisode(name, state, heldMs) {
  if (!attentionNotifier.enabled) return;
  if (notifyMutes.has(name)) return;                     // muted: the episode is consumed, silently
  const h = hostSessions.get(name) || {};
  const label = String(h.agentLabel || '') || (state === 'stopped' ? 'agent stopped' : 'waiting for you');
  const detail = String(h.agentDetail || '');
  const click = NOTIFY_LINK_BASE ? NOTIFY_LINK_BASE + '/?s=' + encodeURIComponent(name) : '';
  attentionNotifier.push({
    title: name + ' - ' + label,
    body: `${name}: ${label}${detail ? ' (' + detail + ')' : ''}\nheld ${Math.round(heldMs / 1000)}s${click ? '\n' + click : ''}`,
    tags: [state === 'stopped' ? 'octagonal_sign' : 'bell'],
    priority: state === 'stopped' ? 'high' : 'default',
    click,
  }).then(result => {
    if (result && !result.ok && !result.disabled)
      console.error(`[notify] attention push for ${name} failed: ${result.error || result.status}`);
  }).catch(() => {});
}
let attentionSweepTimer = null;   // { timer, at } — one pending re-check, always the earliest due one
function scheduleAttentionSweep(delayMs) {
  if (!Number.isFinite(delayMs)) return;                 // nothing pending -> hold no timer at all
  const at = Date.now() + delayMs;
  if (attentionSweepTimer && attentionSweepTimer.at <= at) return;
  if (attentionSweepTimer) clearTimeout(attentionSweepTimer.timer);
  const timer = setTimeout(() => {
    attentionSweepTimer = null;
    try { sweepAttentionEpisodes(); }
    catch (error) { console.error('[notify] attention sweep failed: ' + (error && error.message || error)); }
  }, Math.max(1, delayMs));
  if (typeof timer.unref === 'function') timer.unref();  // never hold the process open for a maybe-push
  attentionSweepTimer = { timer, at };
}
// Runs on every host session update AND on its own settle timer: the settle window has to be able to
// elapse without muxd sending another frame, or a session that goes quiet and stays quiet never fires.
function sweepAttentionEpisodes() {
  if (!hostUp()) return;                                 // link-down is the health lane's alert, not ours
  const now = Date.now();
  let dirty = false;
  let soonestMs = Infinity;
  for (const name of [...attentionEpisodes.keys()]) {
    if (!hostSessions.has(name)) { attentionEpisodes.delete(name); dirty = true; }
  }
  for (const name of hostSessions.keys()) {
    const state = attentionEpisodeState(name);
    const open = attentionEpisodes.get(name);
    if (!state) {
      if (open) { attentionEpisodes.delete(name); dirty = true; }   // cleared: the next entry may push
      continue;
    }
    if (!open) {
      attentionEpisodes.set(name, { since: now, notified: false });
      dirty = true;
      soonestMs = Math.min(soonestMs, NOTIFY_SETTLE_MS);
      continue;
    }
    if (open.notified) continue;                         // already buzzed for THIS episode
    const heldMs = now - open.since;
    if (heldMs < NOTIFY_SETTLE_MS) { soonestMs = Math.min(soonestMs, NOTIFY_SETTLE_MS - heldMs); continue; }
    open.notified = true;
    dirty = true;
    pushAttentionEpisode(name, state, heldMs);
  }
  if (dirty) saveAttentionEpisodes();
  scheduleAttentionSweep(soonestMs);
}

function listSessions() {
  let list = [];
  const legacy = new Set(legacyTmuxNames());
  // PC-HOSTED sessions are the only actionable sessions. If a legacy tmux name collides, surface the
  // conflict on the hosted row instead of silently hiding or killing the legacy process.
  if (hostUp()) {
    for (const [name, h] of hostSessions) {
      const chat = projectedChatForHosted(h, name);
      const attached = (sessions.get(name) ? [...sessions.get(name).clients.values()].some(c => c.hosted) : false);
      const protocolOk = hostProtocolOk();
      const alive = h.alive !== false;
      const dormant = !alive;
      const detachedLocal = dormant && locallyRunningMuxName(name);
      const attn = attentionStatusForHosted(name, h, { dormant, detachedLocal });
      list.push({ name, windows: 1, created: h.created || 0, attached, activity: h.lastOut || 0,
                  state: attn.state, agentState: attn.agentState, agentLabel: attn.agentLabel,
                  agentDetail: attn.agentDetail, agentConfidence: attn.agentConfidence,
                  needsAttention: !!attn.needsAttention, lastOutAgeMs: attn.lastOutAgeMs,
                  notifyMuted: notifyMutes.has(name),   // per-session phone-push mute (attention episodes)
                  agentStateSource: String(attn.agentStateSource || ''),
                  // Death-recovery gate: true only when the OS says the process tree is gone, or muxd
                  // itself declared the session dormant. Never set off a pane-regex read alone.
                  healEligible: !!attn.healEligible,
                  autoheal: !!h.heal, hosted: true,
                  alive, dormant, detachedLocal, cols: h.cols || 0, rows: h.rows || 0,
                  hasCommand: !!h.hasCommand, shellOnly: !!h.shellOnly, ready: !!h.ready,
                  kind: h.kind || (dormant ? 'dormant' : (h.shellOnly ? 'shell' : 'command')),
                  sessionId: String(chat && chat.id || h.sessionId || ''), aliases: Array.isArray(h.aliases) ? h.aliases : [],
                  identityPending: !!h.identityPending,
                  tool: String(chat && chat.tool || ''),
                  chatTitle: String(chat && chat.title || ''), projectMuxName: String(chat && chat.muxName || ''),
                  nativeTitle: String(chat && chat.nativeTitle || ''), appTitle: String(chat && chat.appTitle || ''),
                  chatLinked: !!chat,
                  tabColor: tabMetaFor(name).color, tabKind: tabMetaFor(name).kind,   // per-tab tint + "remote-resumed"
                  tabHistory: tabHistoryFor(name),   // past chats this tab has hosted â†’ relaunch-picker + add-historical-to-collection
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
                agentState: 'blocked', agentLabel: 'legacy blocker', agentDetail: legacyDetail(name),
                agentConfidence: 'high', needsAttention: true, notifyMuted: notifyMutes.has(name),
                autoheal: false, hosted: false, legacy: true, legacyBlocked: true,
                detail: legacyDetail(name) });
  }
  const live = new Set(list.map(s => s.name));   // prune state memory for sessions that no longer exist
  for (const k of _sessState.keys()) if (!live.has(k)) _sessState.delete(k);
  for (const k of _stateCache.keys()) if (!live.has(k)) _stateCache.delete(k);
  return list;
}

app.get('/api/sessions', (req, res) => res.json(listSessions()));

// FLEET GLANCE: one request answers "what is every session doing right now" — every /api/sessions row
// plus a hard-bounded tail snippet. Snippets ride the same muxd tail command as the per-session
// preview, so they are terminal truth (muxd signs them; the trusted-origin client verifies the sig we
// pass through here — the relay only relays and briefly caches the plaintext). The glance must NEVER
// be the slow path: fetches fan out in parallel (total latency ≈ one 2.5s tail timeout, not N of them),
// a short cache absorbs refresh bursts, and a host that is offline or silent degrades each row to its
// last-known state + cached tail instead of failing the request.
const FLEET_SNIPPET_LINES = 2;
const FLEET_SNIPPET_BYTES = 2048;
const FLEET_CACHE_MS = 5000;
const _fleetCache = new Map();   // name -> { at, snippet, sig, degraded }
const _tailSigs = new Map();     // name -> muxd's signature over the last tail it sent
function fleetSnippet(text) {
  const lines = String(text == null ? '' : text).replace(/\s+$/, '').split('\n');
  const snippet = lines.slice(-FLEET_SNIPPET_LINES).join('\n');
  return snippet.length > FLEET_SNIPPET_BYTES ? snippet.slice(-FLEET_SNIPPET_BYTES) : snippet;
}
async function fleetSnippetFor(name, cachedTail) {
  const hit = _fleetCache.get(name);
  if (hit && Date.now() - hit.at < FLEET_CACHE_MS) return hit;
  const live = hostSupportsCap('tail') ? await requestHostTail(name, FLEET_SNIPPET_LINES) : null;
  const degraded = live == null;
  const entry = { at: Date.now(), snippet: fleetSnippet(degraded ? cachedTail : live),
                  sig: degraded ? '' : String(_tailSigs.get(name) || ''), degraded };
  _fleetCache.set(name, entry);
  return entry;
}
app.get('/api/fleet', async (req, res) => {
  const rows = listSessions();
  const liveNames = new Set(rows.map(r => r.name));
  for (const k of _fleetCache.keys()) if (!liveNames.has(k)) _fleetCache.delete(k);
  for (const k of _tailSigs.keys()) if (!liveNames.has(k)) _tailSigs.delete(k);
  const snippets = await Promise.all(rows.map(row => row.hosted
    ? fleetSnippetFor(row.name, (hostSessions.get(row.name) || {}).tail)
    : Promise.resolve({ snippet: '', sig: '', degraded: true })));
  res.json({
    hostUp: hostUp(),
    hostProtocolOk: hostProtocolOk(),
    snippetLines: FLEET_SNIPPET_LINES,
    snippetBytes: FLEET_SNIPPET_BYTES,
    at: Date.now(),
    sessions: rows.map((row, i) => ({
      ...row,
      snippet: snippets[i].snippet,
      snippetSig: snippets[i].sig,
      snippetDegraded: !!snippets[i].degraded,
    })),
  });
});

function hasForbiddenRemoteField(body) { return containsForbiddenRemoteKey(body); }
async function queueStartMuxAndWait(name, sessionId, tool, intentId = '', takeover = false) {
  if (!takeover) {
    const localOwner = ensureNoLocalOwnerForMuxName(name, sessionId);
    if (!localOwner.ok) return { ok: false, status: 409, error: 'local copy is already running', detail: localOwner.detail };
  }
  let queued;
  try {
    queued = enqueueAppCommand({ intentId, type: 'startmux', muxName: name, sessionId, tool, takeover: !!takeover });
  } catch (error) {
    if (error.intentConflict)
      return { ok: false, status: 409, error: 'intent id conflict', detail: error.message };
    return {
      ok: false,
      status: 503,
      error: 'state persistence failed',
      detail: recordPersistenceFailure(error),
    };
  }
  const command = queued.command;
  const result = await waitForCommandResult(command.id, 30000);
  if (!result.ok)
    return {
      ok: false,
      status: result.retryable ? 504 : 409,
      error: result.retryable ? 'PC bridge did not confirm mux start' : 'PC bridge refused mux start',
      detail: result.detail,
    };
  const visible = await waitForHostState(() => {
    const hosted = hostSessions.get(name);
    if (!hosted || hosted.alive === false || !hosted.hasCommand) return null;
    if (!sessionId) return hosted;
    const ids = [hosted.sessionId, ...(Array.isArray(hosted.aliases) ? hosted.aliases : [])]
      .filter(Boolean).map(id => String(id).toLowerCase());
    return ids.includes(String(sessionId).toLowerCase()) ? hosted : null;
  }, 15000);
  if (!visible.ok)
    return { ok: false, status: 504, error: 'PC bridge start was not visible on muxd', detail: visible.error };
  return { ok: true, created: true, hosted: true, via: 'pc-bridge', session: visible.value };
}

// Create a blank shell directly in muxd, or ask the PC bridge to start a trusted fresh CLI by tool.
// Executable commands never cross or persist on the relay.
app.post('/api/sessions', async (req, res) => {
  const body = req.body || {};
  if (hasForbiddenRemoteField(body)) return res.status(400).json({ error: 'executable commands and local paths are forbidden' });
  const name = strictMuxName(body.name);
  if (!name) return res.status(400).json({ error: 'name required' });
  if (tmuxHas(name)) return failLegacy(res, name);
  const tool = String(body.tool || '').toLowerCase();
  const intentId = commandIntentId(body.intentId)
    || 'session-' + Date.now().toString(36) + '-' + crypto.randomBytes(8).toString('hex');
  if (body.intentId && !commandIntentId(body.intentId))
    return res.status(400).json({ error: 'invalid intent id' });
  if (tool && !['claude', 'codex'].includes(tool)) return res.status(400).json({ error: 'tool must be claude or codex' });
  const existing = hostSessions.get(name);
  if (!tool && hostedHas(name) && existing && existing.alive !== false)
    return res.json({ ok: true, name, created: false, hosted: true, owner: !!existing.owner, hostProtocol: hostProtocol.protocol || 0 });
  if (tool) {
    const result = await queueStartMuxAndWait(name, '', tool, intentId);
    if (!result.ok) return failHost(res, result.status, result.error, result.detail);
    return res.json({ ok: true, name, ...result, hostProtocol: hostProtocol.protocol || 0 });
  }
  if (!requireHostProtocol(res, 'refusing to start PC-local mux session')) return;
  const result = await requestHostCreate(
    { t: 'create', s: name, cols: 140, rows: 40, heal: false },
    15000,
    intentId,
  );
  if (!result.ok || !result.session)
    return failHost(
      res,
      result.status || 504,
      result.error || 'PC-local mux session not confirmed',
      result.detail || 'muxd acknowledgement omitted the created session',
    );
  res.json({ ok: true, name, created: !!result.created, hosted: true, owner: !!(result.session && result.session.owner), hostProtocol: hostProtocol.protocol || 0 });
});

function hostSessionHasSavedCommand(h) {
  return !!(h && h.hasCommand && !h.shellOnly);
}

// Explicit relaunch is a user-confirmed ownership transfer. If a matching local writer exists, route
// through the PC bridge so it can stop and verify that exact owner before muxd starts the replacement.
app.post('/api/sessions/:name/relaunch', async (req, res) => {
  const name = strictMuxName(req.params.name);
  if (!name) return res.status(400).json({ error: 'name required' });
  if (tmuxHas(name)) return failLegacy(res, name);
  if (!requireHostCapability(res, 'relaunch', 'refusing to relaunch PC-local mux session')) return;

  const body = req.body || {};
  if (hasForbiddenRemoteField(body)) return res.status(400).json({ error: 'executable commands and local paths are forbidden' });
  const requestedSessionId = body.sessionId ? opaqueIdentity(body.sessionId) : '';
  const requestedTool = String(body.tool || '').trim().toLowerCase();
  const intentId = commandIntentId(body.intentId)
    || 'relaunch-' + Date.now().toString(36) + '-' + crypto.randomBytes(8).toString('hex');
  if (body.intentId && !commandIntentId(body.intentId))
    return res.status(400).json({ error: 'invalid intent id' });
  if (body.sessionId && !requestedSessionId) return res.status(400).json({ error: 'invalid session identity' });
  if (requestedTool && !['claude', 'codex'].includes(requestedTool))
    return res.status(400).json({ error: 'tool must be claude or codex' });
  const existing = hostSessions.get(name);
  const hostedIds = new Set([String(existing && existing.sessionId || ''), ...(Array.isArray(existing && existing.aliases) ? existing.aliases : [])].filter(Boolean).map(x => x.toLowerCase()));
  const canUseSaved = hostSessionHasSavedCommand(existing)
    && (!requestedSessionId || hostedIds.has(requestedSessionId.toLowerCase()));
  if (!canUseSaved && !requestedSessionId) {
    return res.status(400).json({
      error: 'session identity required',
      detail: 'This mux tab has no saved command identity; choose a projected chat so the PC can resolve it locally.',
    });
  }
  const localOwner = ensureNoLocalOwnerForMuxName(name, requestedSessionId || String(existing && existing.sessionId || ''));
  if (!canUseSaved || !localOwner.ok) {
    const bridgeSessionId = requestedSessionId || String(existing && existing.sessionId || '');
    const bridgeTool = requestedTool || String(existing && existing.tool || '');
    const result = await queueStartMuxAndWait(name, bridgeSessionId, bridgeTool, intentId, !localOwner.ok);
    if (!result.ok) return failHost(res, result.status, result.error, result.detail);
    return res.json({ ok: true, name, created: true, relaunched: true, hosted: true,
      stoppedLocal: !localOwner.ok, via: 'pc-bridge', hostProtocol: hostProtocol.protocol || 0 });
  }
  const createFrame = { t: 'create', s: name, relaunch: true };
  if (Number(body.cols) > 0) createFrame.cols = Number(body.cols);
  if (Number(body.rows) > 0) createFrame.rows = Number(body.rows);
  const result = await requestHostCreate(createFrame, 20000, intentId);
  if (!result.ok)
    return failHost(
      res,
      result.status || 504,
      result.error || 'PC-local mux relaunch not confirmed',
      result.detail,
    );
  res.json({ ok: true, name, created: true, relaunched: true, hosted: true,
             stoppedLocal: false, owner: !!(result.session && result.session.owner),
             hostProtocol: hostProtocol.protocol || 0 });
});

// Tail preview of a session's live pane (on demand: long-press / hover / palette) so you can tell what
// a session is doing before attaching â€” last N lines, name-sanitized.
app.get('/api/sessions/:name/tail', async (req, res) => {
  const name = strictMuxName(req.params.name);
  if (!name) return res.status(400).json({ error: 'invalid session name' });
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

// --- durable mutation intents ------------------------------------------------------------------
// Direct (non-queued) mutations — kill, rename, auto-resume, upload keep/delete — are as replay-prone
// as the queued app commands: a phone that loses the response retries, and without a dedup key the
// second request executes again. Clients carry an intentId from the browser intent journal; we record
// the first outcome under it and replay that outcome instead of re-running the handler. Same intentId
// + different payload is a client bug, so it is refused (409) rather than silently executing.
// Retryable outcomes (the ones the journal deliberately retries) drop the record so the retry runs.
const MUTATION_INTENTS_FILE = STATE_DIR + '/mutation-intents.json';
const MUTATION_INTENT_LIMIT = 512;
const MUTATION_INTENT_RETENTION_MS = 24 * 60 * 60 * 1000;
const MUTATION_INTENT_INFLIGHT_MS = 30000;
// An in-flight record that survived a restart was never observed to finish, so it is NOT replayable:
// only completed outcomes are reloaded, and anything else re-executes on the client's next retry.
let _mutationIntents = (durableJsonLoad(MUTATION_INTENTS_FILE, [], Array.isArray) || [])
  .filter(record => record && typeof record === 'object')
  .map(record => ({
    intentId: commandIntentId(record.intentId),
    fingerprint: /^[a-f0-9]{64}$/.test(String(record.fingerprint || '')) ? String(record.fingerprint) : '',
    ts: Number(record.ts) || 0,
    inflight: false,
    code: Number(record.code) || 0,
    body: record.body && typeof record.body === 'object' ? record.body : null,
  }))
  .filter(record => record.intentId && record.fingerprint && record.code >= 200 && record.code < 400);
function mutationFingerprint(scope, payload) {
  return crypto.createHash('sha256').update(scope + '\n' + stableJson(payload)).digest('hex');
}
function mutationIntentRetryable(code) {
  return code === 408 || code === 425 || code === 429 || code >= 500;
}
function compactMutationIntents(candidate, now = Date.now()) {
  return candidate
    .filter(record => record.inflight || now - record.ts <= MUTATION_INTENT_RETENTION_MS)
    .slice(-MUTATION_INTENT_LIMIT);
}
function commitMutationIntents(candidate) {
  candidate = compactMutationIntents(candidate);
  try {
    writeJsonState(MUTATION_INTENTS_FILE, candidate);
  } catch (error) {
    if (error.committed) _mutationIntents = candidate;
    throw error;
  }
  _mutationIntents = candidate;
}
// Wrap a mutating route so a client-supplied intentId makes it exactly-once. No intentId => the raw
// legacy path, unchanged, so an older cached page keeps working.
function withMutationIntent(scope, fingerprintOf, handler) {
  return async (req, res) => {
    const intentId = commandIntentId(req.body && req.body.intentId);
    if (!intentId) return await handler(req, res);
    const fingerprint = mutationFingerprint(scope, fingerprintOf(req));
    const now = Date.now();
    const existing = _mutationIntents.find(record => record.intentId === intentId);
    if (existing) {
      if (existing.fingerprint !== fingerprint)
        return res.status(409).json({ error: 'intent id already used for a different operation' });
      if (!existing.inflight) return res.status(existing.code).json(existing.body);
      if (now - existing.ts <= MUTATION_INTENT_INFLIGHT_MS)
        return res.status(409).json({ error: 'operation already in flight' });
    }
    const claimed = { intentId, fingerprint, ts: now, inflight: true, code: 0, body: null };
    try {
      commitMutationIntents([..._mutationIntents.filter(r => r.intentId !== intentId), claimed]);
    } catch (error) {
      return failPersistence(res, error);
    }
    let settled = false;
    const settle = body => {
      const code = res.statusCode || 200;
      const next = _mutationIntents.filter(record => record.intentId !== intentId);
      if (!mutationIntentRetryable(code))
        next.push({ ...claimed, ts: Date.now(), inflight: false, code, body: body && typeof body === 'object' ? body : null });
      try {
        commitMutationIntents(next);
      } catch (error) {
        // The response is already on its way out; losing the ledger write must not swallow it.
        _mutationIntents = next;
        console.error('[intents] could not record mutation outcome: ' + (error && error.message || error));
      }
    };
    const sendJson = res.json.bind(res);
    res.json = body => { if (!settled) { settled = true; settle(body); } return sendJson(body); };
    try {
      return await handler(req, res);
    } catch (error) {
      if (!settled) { settled = true; res.statusCode = 500; settle(null); }
      throw error;
    }
  };
}

// Rename a session (keeps it running) — "close tab" must never be the only way to manage a session.
app.patch('/api/sessions/:name', withMutationIntent('session.rename', req => ({
  name: strictMuxName(req.params.name),
  to: strictMuxName(req.body && req.body.name),
}), async (req, res) => {
  const name = strictMuxName(req.params.name);
  const to = strictMuxName(req.body && req.body.name);
  if (!name) return res.status(400).json({ error: 'invalid session name' });
  if (!to) return res.status(400).json({ error: 'name required' });
  if (to === name) return res.json({ ok: true, name: to });
  if (tmuxHas(to)) return failLegacy(res, to);
  if (hostSessions.has(to)) return res.status(409).json({ error: 'name already in use' });
  if (hostedHas(name)) {   // E1: hosted rename â†’ muxd renames the session key (keeps the pty), we migrate state
    if (tmuxHas(name)) return failLegacy(res, name);
    if (!requireHostProtocol(res, 'refusing to rename a hosted session')) return;
    let intent;
    try {
      intent = beginRenameIntent(name, to);
      if (!intent) return res.status(409).json({ error: 'another rename involving this session is pending' });
      applyRenameIntentPins(intent, true);
    } catch (error) {
      try {
        reconcileRenameIntents();
      } catch (reconcileError) {
        return failPersistence(res, reconcileError);
      }
      return failPersistence(res, error);
    }
    if (!sendHost({ t: 'rename', s: name, to })) {
      try {
        applyRenameIntentPins(intent, false);
        completeRenameIntent(intent.id);
      } catch (error) {
        return failPersistence(res, error);
      }
      return failHost(res, 503, 'PC mux host offline', 'host socket closed before rename could be sent');
    }
    const confirmed = await waitForHostState(() => hostSessions.has(to) && !hostSessions.has(name), 6000);
    if (!confirmed.ok) {
      return failHost(res, 504, 'muxd rename not confirmed', confirmed.error);
    }
    try {
      completeRenameIntent(intent.id);
    } catch (error) {
      return failPersistence(res, error);
    }
    const st = sessions.get(name); if (st) { for (const c of st.clients.values()) { try { c.ws.close(4001, 'renamed'); } catch {} } sessions.delete(name); }  // viewers reconnect under the new name
    return res.json({ ok: true, name: to, hosted: true });
  }
  if (tmuxHas(name)) return failLegacy(res, name);
  res.status(404).json({ error: 'session not found' });
}));

app.delete('/api/sessions/:name', withMutationIntent('session.kill', req => ({
  name: strictMuxName(req.params.name),
}), async (req, res) => {
  const name = strictMuxName(req.params.name);
  if (!name) return res.status(400).json({ error: 'invalid session name' });
  if (tmuxHas(name)) return failLegacy(res, name);
  if (hostedHas(name)) {
    if (!requireHostProtocol(res, 'refusing to kill a hosted session')) return;
    if (!sendHost({ t: 'kill', s: name })) return failHost(res, 503, 'PC mux host offline', 'host socket closed before kill could be sent');
    const confirmed = await waitForHostState(() => !hostSessions.has(name), 6000);
    if (!confirmed.ok) return failHost(res, 504, 'muxd kill not confirmed', confirmed.error);
  }
  res.json({ ok: true });
}));

// PER-TAB auto-resume toggle. muxd persists and enforces the policy locally.
app.post('/api/sessions/:name/autoheal', withMutationIntent('session.autoheal', req => ({
  name: strictMuxName(req.params.name),
  on: !!(req.body && req.body.on),
}), async (req, res) => {
  const name = strictMuxName(req.params.name);
  if (!name) return res.status(400).json({ error: 'name required' });
  const on = !!(req.body && req.body.on);
  if (tmuxHas(name)) return failLegacy(res, name);
  if (!hostedHas(name)) return res.status(404).json({ error: 'session not found' });
  if (!requireHostProtocol(res, 'refusing to change hosted auto-resume')) return;
  if (!sendHost({ t: 'heal', s: name, on }))
    return failHost(res, 503, 'PC mux host offline', 'host socket closed before auto-resume change could be sent');
  const confirmed = await waitForHostState(() => {
    const h = hostSessions.get(name);
    return h && !!h.heal === on ? h : null;
  }, 6000);
  if (!confirmed.ok)
    return failHost(res, 504, 'muxd auto-resume change not confirmed', confirmed.error);
  res.json({ ok: true, name, autoheal: on });
}));

// PER-TAB attention-notification mute. Owner-gated by the global middleware above like every other
// /api route. Unlike autoheal this never touches muxd: the mute lives entirely in relay state, so it
// still works while the PC host is offline (which is exactly when you want to silence a stuck tab).
app.post('/api/sessions/:name/notify', (req, res) => {
  const name = strictMuxName(req.params.name);
  if (!name) return res.status(400).json({ error: 'name required' });
  if (!req.body || typeof req.body.on !== 'boolean')
    return res.status(400).json({ error: 'on must be a boolean' });
  const on = req.body.on;
  const wasMuted = notifyMutes.has(name);
  if (wasMuted === on) {                                 // muted+on, or unmuted+off => a real change
    if (on) notifyMutes.delete(name); else notifyMutes.add(name);
    try { saveNotifyMutes(); }
    catch (error) {
      if (on) notifyMutes.add(name); else notifyMutes.delete(name);   // memory must match the disk
      return res.status(503).json({ error: 'state persistence failed', detail: recordPersistenceFailure(error) });
    }
  }
  res.json({ ok: true, name, notify: on, notifyMuted: !on });
});

// --- project sync: the desktop app pushes its collections/chats projection here while it's open, so
// the web can show your projects and resume chats remotely. POST is loopback-only (the app reaches in
// over its own SSH); GET is owner-gated (the web). `live` = the app pushed within the last ~45s. ------
const PROJECTS_FILE = STATE_DIR + '/projects.json';
const PROJECTION_SCHEMA_VERSION = 3;
const FORBIDDEN_REMOTE_KEYS = new Set([
  'muxcommand', 'command', 'cmd', 'cwd', 'pcpath', 'path', 'sourcepath',
  'workspace', 'workingdirectory', 'exe', 'executable', 'arguments',
]);
function containsForbiddenRemoteKey(value) {
  if (!value || typeof value !== 'object') return false;
  if (Array.isArray(value)) return value.some(containsForbiddenRemoteKey);
  for (const [key, child] of Object.entries(value))
    if (FORBIDDEN_REMOTE_KEYS.has(String(key).toLowerCase()) || containsForbiddenRemoteKey(child)) return true;
  return false;
}
function projectionHasInvalidIdentity(body) {
  const chats = [];
  for (const collection of (Array.isArray(body && body.collections) ? body.collections : []))
    chats.push(...(Array.isArray(collection && collection.chats) ? collection.chats : []));
  chats.push(...(Array.isArray(body && body.allChats) ? body.allChats : []));
  for (const chat of chats) {
    if (chat && chat.id && !opaqueIdentity(chat.id)) return true;
    if (chat && chat.muxName && !strictMuxName(chat.muxName)) return true;
    if (Array.isArray(chat && chat.aliases) && chat.aliases.some(alias => alias && !opaqueIdentity(alias))) return true;
  }
  for (const running of (Array.isArray(body && body.runningSessions) ? body.runningSessions : []))
    if (running && running.sessionId && !opaqueIdentity(running.sessionId)) return true;
  const tabs = body && body.muxTabChats;
  if (tabs && typeof tabs === 'object' && !Array.isArray(tabs)) {
    for (const [name, tab] of Object.entries(tabs)) {
      if (!strictMuxName(name)) return true;
      if (tab && tab.id && !opaqueIdentity(tab.id)) return true;
      for (const history of (Array.isArray(tab && tab.history) ? tab.history : []))
        if (history && history.id && !opaqueIdentity(history.id)) return true;
    }
  }
  const tabMeta = body && body.muxTabMeta;
  if (tabMeta && typeof tabMeta === 'object' && !Array.isArray(tabMeta))
    for (const name of Object.keys(tabMeta))
      if (!strictMuxName(name)) return true;
  return false;
}
const text = (v, max = 500) => String(v || '').slice(0, max);
function normalizeChat(chat) {
  chat = chat || {};
  const id = opaqueIdentity(chat.id);
  return {
    id,
    aliases: (Array.isArray(chat.aliases) ? chat.aliases : [])
      .map(opaqueIdentity).filter(Boolean)
      .filter(alias => alias.toLowerCase() !== id.toLowerCase())
      .filter((alias, index, all) => all.findIndex(other => other.toLowerCase() === alias.toLowerCase()) === index)
      .slice(0, 64),
    title: text(chat.title),
    nativeTitle: text(chat.nativeTitle), appTitle: text(chat.appTitle), tool: text(chat.tool, 20),
    muxName: strictMuxName(chat.muxName), running: !!chat.running, updatedAt: text(chat.updatedAt, 64),
    workspaceLabel: text(chat.workspaceLabel, 200), collection: chat.collection == null ? null : text(chat.collection, 200),
    collectionDeckId: chat.collectionDeckId == null ? null : text(chat.collectionDeckId, 200),
    collectionDeck: chat.collectionDeck == null ? null : text(chat.collectionDeck, 200),
  };
}
function normalizeRunning(row) {
  row = row || {};
  return {
    pid: Number(row.pid) || 0, tool: text(row.tool, 20), sessionId: opaqueIdentity(row.sessionId),
    parent: text(row.parent, 200), startedAt: text(row.startedAt, 64),
    title: row.title == null ? null : text(row.title), collection: row.collection == null ? null : text(row.collection, 200),
    collectionDeckId: row.collectionDeckId == null ? null : text(row.collectionDeckId, 200),
    collectionDeck: row.collectionDeck == null ? null : text(row.collectionDeck, 200),
    realTitle: row.realTitle == null ? null : text(row.realTitle),
  };
}
function normalizeMuxTabChats(value) {
  const out = {};
  if (!value || typeof value !== 'object' || Array.isArray(value)) return out;
  for (const [rawName, raw] of Object.entries(value)) {
    const name = strictMuxName(rawName);
    if (!name || !raw || typeof raw !== 'object') continue;
    out[name] = {
      id: opaqueIdentity(raw.id), tool: text(raw.tool, 20), title: text(raw.title),
      history: (Array.isArray(raw.history) ? raw.history : []).slice(0, 30).map(h => ({
        id: opaqueIdentity(h && h.id), tool: text(h && h.tool, 20), title: text(h && h.title),
        at: text(h && h.at, 64),
      })).filter(h => h.id),
    };
  }
  return out;
}
function normalizeMuxTabMeta(value) {
  const out = {};
  if (!value || typeof value !== 'object' || Array.isArray(value)) return out;
  for (const [rawName, raw] of Object.entries(value)) {
    const name = strictMuxName(rawName);
    if (!name || !raw || typeof raw !== 'object' || Array.isArray(raw)) continue;
    out[name] = { color: text(raw.color, 32), kind: text(raw.kind, 64) };
  }
  return out;
}
function emptyProjects() {
  return {
    schemaVersion: PROJECTION_SCHEMA_VERSION, decks: [], collections: [], allChats: [], host: '',
    syncedAt: 0, appSyncedAt: 0, runningSyncedAt: 0, runningSessions: [],
    muxTabChats: {}, muxTabMeta: {}, runningVerified: false, runningVerificationDetail: '',
  };
}
function normalizeProjects(body, previous = emptyProjects()) {
  const now = Date.now();
  return {
    schemaVersion: PROJECTION_SCHEMA_VERSION,
    decks: (Array.isArray(body.decks) ? body.decks : []).map(d => ({ id: text(d && d.id, 200), name: text(d && d.name, 200) })),
    collections: (Array.isArray(body.collections) ? body.collections : []).map(c => ({
      id: text(c && c.id, 200), name: text(c && c.name, 200), deckId: text(c && c.deckId, 200),
      deckName: text(c && c.deckName, 200), chats: (Array.isArray(c && c.chats) ? c.chats : []).map(normalizeChat),
    })),
    allChats: (Array.isArray(body.allChats) ? body.allChats : []).map(normalizeChat),
    runningSessions: (Array.isArray(body.runningSessions) ? body.runningSessions : []).map(normalizeRunning),
    muxTabChats: normalizeMuxTabChats(body.muxTabChats),
    muxTabMeta: normalizeMuxTabMeta(body.muxTabMeta),
    runningVerified: body.runningVerified === true,
    runningVerificationDetail: text(body.runningVerificationDetail, 1000),
    host: text(body.host, 200),
    syncedAt: now, appSyncedAt: now, runningSyncedAt: now,
  };
}
function validPersistedProjection(value) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  if (!Array.isArray(value.decks)
      || !Array.isArray(value.collections)
      || !Array.isArray(value.allChats)
      || !Array.isArray(value.runningSessions)) return false;
  if (!Object.prototype.hasOwnProperty.call(value, 'schemaVersion')) return true;
  return value.schemaVersion === PROJECTION_SCHEMA_VERSION
    && !containsForbiddenRemoteKey(value)
    && !projectionHasInvalidIdentity(value);
}
let _projects = emptyProjects();
{
  const loaded = durableJsonLoad(
    PROJECTS_FILE,
    null,
    validPersistedProjection,
  );
  const legacyProjection = !!loaded
    && typeof loaded === 'object'
    && !Array.isArray(loaded)
    && !Object.prototype.hasOwnProperty.call(loaded, 'schemaVersion');
  if (legacyProjection) {
    // Pre-schema projections contain the same user-facing collections but may also carry old
    // command/path fields. Normalize through the strict allowlist instead of refusing startup.
    _projects = normalizeProjects(loaded);
  } else if (
    loaded
    && loaded.schemaVersion === PROJECTION_SCHEMA_VERSION
    && !containsForbiddenRemoteKey(loaded)
    && !projectionHasInvalidIdentity(loaded)
  ) {
    _projects = normalizeProjects(loaded);
  }
  // A relay restart invalidates process-liveness assertions. Keep display data, but require a
  // fresh desktop/headless push before ownership-sensitive actions trust it.
  _projects.syncedAt = 0;
  _projects.appSyncedAt = 0;
  _projects.runningSyncedAt = 0;
  _projects.runningVerified = false;
}
// Rewrite recognized state through the allowlist on every boot. Invalid state is recovered by
// durableJsonLoad from its backup or fails startup without overwriting the primary.
writeJsonState(PROJECTS_FILE, _projects);
function appSyncedAt() { return _projects.appSyncedAt || _projects.syncedAt || 0; }
function runningSyncedAt() { return _projects.runningSyncedAt || _projects.syncedAt || 0; }
function appLive() { return Date.now() - appSyncedAt() < 45000; }
function bridgeLive() { return Date.now() - runningSyncedAt() < 45000; }
function runningVerified() { return bridgeLive() && _projects.runningVerified === true; }
function projectsHealth() {
  const now = Date.now();
  const appAt = appSyncedAt();
  const runningAt = runningSyncedAt();
  const pendingCommands = _commands.filter(c => c.status === 'pending' || c.status === 'leased').length;
  return {
    appLive: appAt > 0 && now - appAt < 45000,
    bridgeLive: runningAt > 0 && now - runningAt < 45000,
    runningVerified: runningVerified(),
    runningVerificationDetail: String(_projects.runningVerificationDetail || ''),
    appAgeMs: appAt > 0 ? now - appAt : null,
    runningAgeMs: runningAt > 0 ? now - runningAt : null,
    decks: Array.isArray(_projects.decks) ? _projects.decks.length : 0,
    collections: Array.isArray(_projects.collections) ? _projects.collections.length : 0,
    allChats: Array.isArray(_projects.allChats) ? _projects.allChats.length : 0,
    runningSessions: Array.isArray(_projects.runningSessions) ? _projects.runningSessions.length : 0,
    pendingCommands,
  };
}
function currentRunningIds() {
  return new Set((Array.isArray(_projects.runningSessions) ? _projects.runningSessions : [])
    .map(s => String(s && s.sessionId || '').toLowerCase())
    .filter(Boolean));
}
function chatIds(chat) {
  const ids = [];
  const add = v => {
    const s = String(v || '').trim();
    if (s && !ids.some(x => x.toLowerCase() === s.toLowerCase())) ids.push(s);
  };
  add(chat && chat.id);
  add(chat && chat.sessionId);
  if (Array.isArray(chat && chat.aliases)) for (const a of chat.aliases) add(a);
  if (Array.isArray(chat && chat.Aliases)) for (const a of chat.Aliases) add(a);
  return ids;
}
function projectedChatCandidates(name, sessionId = '') {
  const muxName = strictMuxName(name);
  const sid = String(sessionId || '').toLowerCase();
  return allProjectedChats().filter(chat => {
    if (!chat) return false;
    if (muxName && String(chat.muxName || '') === muxName) return true;
    if (sid && chatIds(chat).some(id => id.toLowerCase() === sid)) return true;
    return false;
  });
}
function locallyRunningMuxName(name) {
  if (!runningVerified()) return false;
  const runningIds = currentRunningIds();
  if (!runningIds.size) return false;
  for (const chat of allProjectedChats()) {
    if (String(chat && chat.muxName || '') === name && chatIds(chat).some(id => runningIds.has(id.toLowerCase()))) return true;
  }
  return false;
}
function allProjectedChats() {
  const out = [];
  for (const col of (Array.isArray(_projects.collections) ? _projects.collections : []))
    for (const chat of (Array.isArray(col.chats) ? col.chats : [])) out.push(chat);
  for (const chat of (Array.isArray(_projects.allChats) ? _projects.allChats : [])) out.push(chat);
  return out;
}
// Past chats a tab has hosted (from the app's per-tab session history), for the relaunch picker +
// add-historical-to-collection. Sanitized shape the web can render/act on.
// Per-tab presentation (color + kind) the app projected, for tinting the tab / marking a resumed-remote.
function tabMetaFor(name) {
  const mtm = _projects && _projects.muxTabMeta;
  const t = mtm && name && typeof mtm === 'object' ? mtm[name] : null;
  return { color: String((t && t.color) || ''), kind: String((t && t.kind) || '') };
}
function tabHistoryFor(name) {
  const mtc = _projects && _projects.muxTabChats;
  const t = mtc && name && typeof mtc === 'object' ? mtc[name] : null;
  const hist = t && Array.isArray(t.history) ? t.history : [];
  return hist.filter(h => h && h.id).slice(0, 30).map(h => ({
    id: String(h.id), tool: String(h.tool || ''), title: String(h.title || h.id), at: String(h.at || ''),
  }));
}
function projectedChatForHosted(hosted, name) {
  const hostedIds = new Set([
    String(hosted && hosted.sessionId || ''),
    ...(Array.isArray(hosted && hosted.aliases) ? hosted.aliases : []),
  ].filter(Boolean).map(id => id.toLowerCase()));
  if (hostedIds.size)
    for (const chat of allProjectedChats())
      if (chatIds(chat).some(id => hostedIds.has(id.toLowerCase()))) return chat;
  if (hostedIds.size) return null;
  // Shell-launched tab (no muxd command) â†’ the app's deterministic resolver linked it to its LIVE chat
  // (agent matched to this tab by ancestor pid; id from the resume flag or the tab's newest transcript).
  const mtc = _projects && _projects.muxTabChats;
  const t = mtc && name && typeof mtc === 'object' ? mtc[name] : null;
  if (t && t.id) return { id: String(t.id), tool: String(t.tool || ''), title: String(t.title || name), muxName: String(name) };
  return null;
}
function runningChatForMuxName(name) {
  const muxName = strictMuxName(name);
  if (!muxName || !runningVerified()) return null;
  const running = Array.isArray(_projects.runningSessions) ? _projects.runningSessions : [];
  if (!running.length) return null;
  const byId = new Map(running.map(r => [String(r && r.sessionId || '').toLowerCase(), r]));
  for (const chat of allProjectedChats()) {
    if (String(chat && chat.muxName || '') !== muxName) continue;
    const run = chatIds(chat).map(id => byId.get(id.toLowerCase())).find(Boolean);
    if (run) return { chat, run };
  }
  return null;
}
function ensureNoLocalOwnerForMuxName(name, sessionId = '') {
  const requestedId = String(sessionId || '').trim();
  const candidates = projectedChatCandidates(name, requestedId);
  if (!runningVerified()) {
    if (requestedId || candidates.length)
      return { ok: false, stopped: false, detail: 'could not verify local running sessions'
          + (_projects.runningVerificationDetail ? ` (${_projects.runningVerificationDetail})` : '')
          + '; refusing remote resume because it could create a second writer.' };
    return { ok: true, stopped: false };
  }
  const running = Array.isArray(_projects.runningSessions) ? _projects.runningSessions : [];
  const byId = new Map(running.map(r => [String(r && r.sessionId || '').toLowerCase(), r]));
  for (const chat of candidates) {
    const run = chatIds(chat).map(id => byId.get(id.toLowerCase())).find(Boolean);
    if (run) return localOwnerRefusal(name, chat, run);
  }
  if (requestedId) {
    const run = byId.get(requestedId.toLowerCase());
    if (run) return localOwnerRefusal(name, { id: requestedId, title: name }, run);
  }
  const found = runningChatForMuxName(name);
  if (!found) return { ok: true, stopped: false };
  return localOwnerRefusal(name, found.chat, found.run);
}
function localOwnerRefusal(name, chat, run) {
  chat = chat || {};
  run = run || {};
  const label = String(chat.title || name || 'this chat');
  const sid = String(chat.id || run.sessionId || '');
  const pid = Number(run.pid || 0);
  return {
    ok: false,
    stopped: false,
    detail: `${label} is already running locally${pid ? ` (pid ${pid})` : ''}${sid ? ` as ${sid}` : ''}; refusing remote relaunch because it would create a second writer.`,
  };
}
function normalizedCollectionsForCurrentRunning() {
  const cols = Array.isArray(_projects.collections) ? _projects.collections : [];
  if (!runningVerified()) return cols;
  const runningIds = currentRunningIds();
  return cols.map(col => ({
    ...col,
    chats: Array.isArray(col.chats) ? col.chats.map(chat => ({
      ...chat,
      running: chatIds(chat).some(id => runningIds.has(id.toLowerCase())),
    })) : [],
  }));
}
function normalizedAllChatsForCurrentRunning() {
  const chats = Array.isArray(_projects.allChats) ? _projects.allChats : [];
  if (!runningVerified()) return chats;
  const runningIds = currentRunningIds();
  return chats.map(chat => ({
    ...chat,
    running: chatIds(chat).some(id => runningIds.has(id.toLowerCase())),
  }));
}
app.post('/api/projects', (req, res) => {
  const b = req.body || {};
  if (Number(b.schemaVersion) !== PROJECTION_SCHEMA_VERSION)
    return res.status(409).json({ error: 'projection schema mismatch', expected: PROJECTION_SCHEMA_VERSION });
  if (containsForbiddenRemoteKey(b))
    return res.status(400).json({ error: 'projection contains executable command or local path fields' });
  if (projectionHasInvalidIdentity(b))
    return res.status(400).json({ error: 'projection contains an invalid opaque identity' });
  const candidate = normalizeProjects(b, _projects);
  try {
    writeJsonState(PROJECTS_FILE, candidate);
  } catch (error) {
    if (error.committed) _projects = candidate;
    return failPersistence(res, error);
  }
  _projects = candidate;
  res.json({ ok: true, syncedAt: candidate.syncedAt });
});
app.get('/api/projects', (req, res) => {
  const a = appSyncedAt(), r = runningSyncedAt();
  res.json({
    schemaVersion: PROJECTION_SCHEMA_VERSION,
    decks: Array.isArray(_projects.decks) ? _projects.decks : [],
    collections: normalizedCollectionsForCurrentRunning(), host: _projects.host || '',
    allChats: normalizedAllChatsForCurrentRunning(),
    runningSessions: _projects.runningSessions || [],
    runningVerified: runningVerified(),
    runningVerificationDetail: String(_projects.runningVerificationDetail || ''),
    syncedAt: a, appSyncedAt: a, runningSyncedAt: r,
    appLive: Date.now() - a < 45000,
    bridgeLive: Date.now() - r < 45000,
    live: Date.now() - r < 45000,
  });
});

// --- ARCHIVE INDEX: the resumable-chat picker's catalogue -------------------------------------------
// The desktop bridge pushes a capped, opaque-id-only index of resumable chats; the browser reads it to
// offer "resume this chat over there". Two DIFFERENT principals, two DIFFERENT credentials:
//   POST  = the desktop bridge, proving itself with the scoped MUX_BRIDGE_TOKEN (Authorization: Bearer).
//   GET   = the browser owner, proving itself with the hl_session owner cookie.
// Ambient loopback is NOT authorization here: reaching the socket says nothing about who is calling.
// An owner cookie cannot push, a bridge credential cannot read, and neither is the host-link token.
const ARCHIVE_INDEX_FILE = STATE_DIR + '/archive-index.json';
const ARCHIVE_INDEX_SCHEMA_VERSION = 1;
const ARCHIVE_INDEX_MAX_ROWS = 500;
const ARCHIVE_INDEX_MAX_BYTES = 2 * 1024 * 1024;
const ARCHIVE_INDEX_LIVE_MS = 45000;
// `cwd` is deliberately allowed here (a display string the picker shows) while every other executable
// or local-path key stays forbidden — the app-side contract carries cwd and nothing else path-shaped.
const ARCHIVE_INDEX_FORBIDDEN_KEYS = new Set([...FORBIDDEN_REMOTE_KEYS].filter(k => k !== 'cwd'));
function archiveIndexHasForbiddenKey(value) {
  if (!value || typeof value !== 'object') return false;
  if (Array.isArray(value)) return value.some(archiveIndexHasForbiddenKey);
  for (const [key, child] of Object.entries(value))
    if (ARCHIVE_INDEX_FORBIDDEN_KEYS.has(String(key).toLowerCase()) || archiveIndexHasForbiddenKey(child)) return true;
  return false;
}
// A distinct scoped credential for bridge-only routes. Unset => the route is closed, not open; equal to
// the host-link token => also closed, because interchangeable credentials are not scoped credentials.
const BRIDGE_TOKEN = process.env.MUX_BRIDGE_TOKEN || '';
const BRIDGE_TOKEN_USABLE = !!BRIDGE_TOKEN && BRIDGE_TOKEN !== HOST_TOKEN;
if (BRIDGE_TOKEN && !BRIDGE_TOKEN_USABLE)
  console.error('[bridge] MUX_BRIDGE_TOKEN must differ from MUX_HOST_TOKEN; bridge routes stay closed');
function bearerCredential(req) {
  const m = /^Bearer[ \t]+(\S+)$/i.exec(String(req.headers.authorization || '').trim());
  return m ? m[1] : '';
}
function bridgeTokenOk(t) {
  if (!BRIDGE_TOKEN_USABLE || !t || t.length !== BRIDGE_TOKEN.length) return false;
  try { return crypto.timingSafeEqual(Buffer.from(t), Buffer.from(BRIDGE_TOKEN)); } catch { return false; }
}
// Returns null when the caller is the bridge; otherwise the response has already been sent.
function refuseUnlessBridge(req, res) {
  if (!BRIDGE_TOKEN_USABLE) return res.status(503).json({ error: 'bridge credential not configured' });
  if (!bridgeTokenOk(bearerCredential(req))) return res.status(403).json({ error: 'bridge credential required' });
  return null;
}
function emptyArchiveIndex() {
  return { schemaVersion: ARCHIVE_INDEX_SCHEMA_VERSION, host: '', chats: [], updatedAt: 0 };
}
function normalizeArchiveChat(chat) {
  const c = chat && typeof chat === 'object' && !Array.isArray(chat) ? chat : {};
  const updatedAt = Number(c.updatedAt);
  return {
    id: opaqueIdentity(c.id),
    title: text(c.title, 200),
    tool: text(c.tool, 40),
    cwd: text(c.cwd, 300),
    workspaceLabel: text(c.workspaceLabel, 200),
    updatedAt: Number.isFinite(updatedAt) && updatedAt > 0 ? Math.floor(updatedAt) : 0,
    muxName: strictMuxName(c.muxName),
    resumable: c.resumable === true,
  };
}
function validArchiveChat(row) {
  return !!row.id && !!row.title && !!row.tool && row.updatedAt > 0;
}
function validPersistedArchiveIndex(value) {
  return !!value && typeof value === 'object' && !Array.isArray(value)
    && value.schemaVersion === ARCHIVE_INDEX_SCHEMA_VERSION
    && Array.isArray(value.chats)
    && value.chats.length <= ARCHIVE_INDEX_MAX_ROWS
    && !archiveIndexHasForbiddenKey(value);
}
let _archiveIndex = durableJsonLoad(ARCHIVE_INDEX_FILE, null, validPersistedArchiveIndex) || emptyArchiveIndex();
// `updatedAt` is data recency and survives a restart; liveness does not. Same doctrine as the projection
// above: after a relay restart the picker still lists chats, but must not claim the app is answering
// until a push lands on THIS process.
let _archiveIndexPushedAt = 0;
function archiveIndexLive() { return _archiveIndexPushedAt > 0 && Date.now() - _archiveIndexPushedAt < ARCHIVE_INDEX_LIVE_MS; }
app.post('/api/archive-index', (req, res) => {
  const refusal = refuseUnlessBridge(req, res);
  if (refusal) return refusal;
  const b = req.body || {};
  if (Number(b.schemaVersion) !== ARCHIVE_INDEX_SCHEMA_VERSION)
    return res.status(409).json({ error: 'archive index schema mismatch', expected: ARCHIVE_INDEX_SCHEMA_VERSION });
  const declared = Number(req.headers['content-length'] || 0);
  if (declared > ARCHIVE_INDEX_MAX_BYTES || Buffer.byteLength(JSON.stringify(b)) > ARCHIVE_INDEX_MAX_BYTES)
    return res.status(413).json({ error: 'archive index too large', maxBytes: ARCHIVE_INDEX_MAX_BYTES });
  if (!Array.isArray(b.chats))
    return res.status(400).json({ error: 'archive index requires a chats array' });
  if (b.chats.length > ARCHIVE_INDEX_MAX_ROWS)
    return res.status(413).json({ error: 'archive index too many rows', maxRows: ARCHIVE_INDEX_MAX_ROWS });
  if (archiveIndexHasForbiddenKey(b))
    return res.status(400).json({ error: 'archive index contains executable command or local path fields' });
  const chats = b.chats.map(normalizeArchiveChat);
  if (!chats.every(validArchiveChat))
    return res.status(400).json({ error: 'archive index row missing an opaque id, title, tool, or updatedAt' });
  const candidate = {
    schemaVersion: ARCHIVE_INDEX_SCHEMA_VERSION,
    host: text(b.host, 200),
    chats,
    updatedAt: Date.now(),
  };
  try {
    writeJsonState(ARCHIVE_INDEX_FILE, candidate);
  } catch (error) {
    if (error.committed) { _archiveIndex = candidate; _archiveIndexPushedAt = candidate.updatedAt; }
    return failPersistence(res, error);
  }
  _archiveIndex = candidate;
  _archiveIndexPushedAt = candidate.updatedAt;
  res.json({ ok: true, count: chats.length, updatedAt: candidate.updatedAt });
});
app.get('/api/archive-index', async (req, res) => {
  if (!(await isOwner(cookieVal(req, HL_COOKIE)))) return res.status(403).json({ error: 'owner only' });
  const updatedAt = _archiveIndex.updatedAt || 0;
  res.json({
    schemaVersion: ARCHIVE_INDEX_SCHEMA_VERSION,
    host: _archiveIndex.host || '',
    chats: Array.isArray(_archiveIndex.chats) ? _archiveIndex.chats : [],
    count: Array.isArray(_archiveIndex.chats) ? _archiveIndex.chats.length : 0,
    updatedAt,
    ageMs: updatedAt > 0 ? Date.now() - updatedAt : null,
    appLive: archiveIndexLive(),
  });
});
// LIGHT partial update: the always-on headless server keeps the running list + `live` fresh when the
// heavy desktop app is closed, WITHOUT clobbering the collections projection the app last pushed. Loopback
// only (the server reaches in over its own SSH, same as the app's /api/projects push).
app.post('/api/running', (req, res) => {
  if (!isTrustedLocal(req)) return res.status(403).json({ error: 'forbidden' });
  const b = req.body || {};
  if (Number(b.schemaVersion) !== PROJECTION_SCHEMA_VERSION)
    return res.status(409).json({ error: 'projection schema mismatch', expected: PROJECTION_SCHEMA_VERSION });
  if (containsForbiddenRemoteKey(b))
    return res.status(400).json({ error: 'running projection contains local path fields' });
  if (projectionHasInvalidIdentity(b))
    return res.status(400).json({ error: 'running projection contains an invalid opaque identity' });
  const candidate = {
    ..._projects,
    runningSessions: (Array.isArray(b.runningSessions) ? b.runningSessions : []).map(normalizeRunning),
    runningVerified: b.runningVerified === true,
    runningVerificationDetail: String(b.runningVerificationDetail || ''),
    host: b.host ? String(b.host) : _projects.host,
    runningSyncedAt: Date.now(),
  };
  try {
    writeJsonState(PROJECTS_FILE, candidate);
  } catch (error) {
    if (error.committed) _projects = candidate;
    return failPersistence(res, error);
  }
  _projects = candidate;
  res.json({ ok: true, runningSyncedAt: candidate.runningSyncedAt });
});

// muxd is the sole owner of persisted launch commands, boot recovery, and self-heal.
// The relay only toggles muxd policy and forwards explicit, opaque control intent.

// ---- PC reachability probe + /api/health (P0 observability, D7). A lightweight TCP-connect to the PC's
// sshd port (no ssh process spawn) so health can report whether the ssh-back is even reachable + its RTT â€”
// the Jul 2 .146â†’.154 DHCP orphaning would have shown here instantly as pc.reachable=false.
let _winHost = '192.168.1.146';
try { const _c = fs.readFileSync((process.env.HOME || '') + '/.ssh/config', 'utf8'); const _m = _c.match(/Host\s+win\b[\s\S]*?HostName\s+(\S+)/i); if (_m) _winHost = _m[1]; } catch {}
let _pcHealth = TEST_MODE ? { reachable: true, rttMs: 0, host: 'test', at: Date.now() } : { reachable: null, rttMs: null, host: _winHost, at: 0 };
// When the PC becomes unreachable, it may just have moved to a new DHCP IP (Jul 2: .146â†’.154 orphaned the
// fleet). Kick the MAC-based resolver (runs the ping-sweep in its own subprocess â€” never blocks this loop),
// then reload the (possibly updated) HostName so the next ssh-back + boot-recreate target the new address.
let _lastReresolve = 0;
function reresolveWin() {
  const now = Date.now(); if (now - _lastReresolve < 60000) return; _lastReresolve = now;
  execFile((process.env.HOME || '') + '/bin/resolve-win.sh', { timeout: 20000 }, () => {
    try {
      const c = fs.readFileSync((process.env.HOME || '') + '/.ssh/config', 'utf8');
      const m = c.match(/Host\s+win\b[\s\S]*?HostName\s+(\S+)/i);
      if (m && m[1] !== _winHost) { console.log(`[dhcp] PC moved â†’ win now ${m[1]} (was ${_winHost})`); _winHost = m[1]; setTimeout(probePc, 500); }
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
let _probeBootTimer = null, _probeTimer = null;   // held so the restart drain can let the event loop empty
if (!TEST_MODE) { _probeBootTimer = setTimeout(probePc, 2000); _probeTimer = setInterval(probePc, 30000); }
// r.1.4.3 opened an `app.get('/api/health', ...)` here, but r.1.17 had already lifted this body out
// into healthSnapshot() so the ops-alert lane could read health without going through HTTP — and it
// registers the route below. Taking both openers would have bound /api/health twice.
function healthSnapshot() {
  let tmuxAvailable = false;
  try { execSync(`tmux -V`, { encoding: "utf8", timeout: 1500 }); tmuxAvailable = true; } catch {}
  const legacyNames = legacyTmuxNames();
  const armed = [...hostSessions.values()].filter(h => h && h.heal).length;
  const gaveUp = 0;
  const projects = projectsHealth();
  const retention = retentionGauges();
  // A2 #9: if the PC host is down, armed sessions are hosted-and-unreachable (can't be healed) → surface
  // that as degraded instead of a falsely-green dot. Legacy tmux names are also degraded blockers.
  const hostedArmedDown = 0;
  // A store recovered from its .bak but whose primary could not be rewritten. The value in memory is
  // correct, so this is not an outage — but the primary on disk is still the bad copy until the next
  // save lands, so it stays visible instead of reading as a clean boot.
  const stateRecoveryFailures = recoveryWriteFailureReport();
  const degraded = TEST_MODE
    ? (!hostUp() || !hostProtocolOk() || legacyNames.length > 0 || !!persistenceFailure || renameIntents.length > 0 || uploadRecoveryWarnings.length > 0 || stateRecoveryFailures.length > 0)
    : (!hostUp() || !hostProtocolOk() || _pcHealth.reachable === false || gaveUp > 0 || hostedArmedDown > 0 || legacyNames.length > 0 || !projects.bridgeLive || !!persistenceFailure || renameIntents.length > 0 || uploadRecoveryWarnings.length > 0 || stateRecoveryFailures.length > 0);
  return { ok: !degraded, degraded, uptimeSec: Math.round(process.uptime()), tmuxAvailable, sessions: hostSessions.size,
           legacySessions: legacyNames.length, legacyNames, legacyPolicy: "blocked", armed, gaveUp, hostedArmedDown, pc: _pcHealth,
           // How many times we have actually spawned tmux. Each one is a hard event-loop freeze, so
           // this is the rate to watch if the terminal feels stuttery — and it is what proves the
           // TTL cache is doing its job rather than being assumed to.
           legacyProbes: _legacyTmux.probes,
           projects,
           persistence: { ok: !persistenceFailure && !persistenceBlocked, detail: persistenceBlocked || persistenceFailure, blocked: !!persistenceBlocked },
           stateRecoveryFailures,
           pendingRenameIntents: renameIntents.length,
           uploadRecoveryWarnings,
           // State growth is observable BEFORE it is a problem: total bytes on disk plus a
           // size/count gauge per store, and plaintext exposure kept separate from the opaque
           // signed envelopes we hold in custody but never read.
           stateDirBytes: retention ? retention.stateDirBytes : null,
           retention,
           host: { connected: hostUp(), name: hostLabel, sessions: hostSessions.size, protocol: hostProtocol.protocol, caps: hostProtocol.caps, protocolOk: hostProtocolOk() },
           node: process.version, at: Date.now() };
}

app.get('/api/health', (req, res) => {
  res.json(healthSnapshot());
});


// --- app command queue: the owner (web) enqueues actions for the desktop app; the app polls + acks them.
// Today: "kill" a live agent session. Enqueue is owner-gated (the global auth middleware above); pull +
// ack are loopback-only (only the app, reaching in over its own SSH, can read the queue or run anything).
const COMMANDS_FILE = STATE_DIR + '/app-commands.json';
const COMMAND_LEASE_MS = Math.max(100, Number(process.env.MUX_COMMAND_LEASE_MS) || 120000);
const COMMAND_TERMINAL_RETENTION_MS = Math.max(
  24 * 60 * 60 * 1000,
  Number(process.env.MUX_COMMAND_TERMINAL_RETENTION_MS) || 90 * 24 * 60 * 60 * 1000,
);
const COMMAND_TERMINAL_LIMIT = Math.max(
  1000,
  Number(process.env.MUX_COMMAND_TERMINAL_LIMIT) || 10000,
);
const COMMAND_REPLAY_POLICY = new Map([
  ['kill', 'refused'],
  ['transcript', 'read-only'],
  ['transcriptfetch', 'read-only'],
  ['fetchfile', 'intent-fenced'],
  ['rename', 'idempotent'],
  ['setapptitle', 'idempotent'],
  ['addtocollection', 'idempotent'],
  ['startmux', 'intent-fenced'],
  ['startchat', 'intent-fenced'],
  ['cleartabhistory', 'idempotent'],
  ['settabcolor', 'idempotent'],
]);
let _commands = [];
function commandIntentId(value) {
  const raw = String(value || '').trim();
  return /^[A-Za-z0-9._-]{1,128}$/.test(raw) ? raw : '';
}
const STARTCHAT_ALLOWED_FIELDS = new Set([
  'type', 'intentId', 'muxName', 'title', 'tool', 'checkpointId', 'workspaceId', 'subfolder',
  'deckId', 'collectionId', 'collection', 'phrase',
]);
const STARTCHAT_MAX_TEXT = 200;
function startChatString(source, key) {
  if (!Object.prototype.hasOwnProperty.call(source, key)) return '';
  if (typeof source[key] !== 'string') return null;
  return source[key].trim().slice(0, STARTCHAT_MAX_TEXT);
}
function startChatIdentity(source, key) {
  if (!Object.prototype.hasOwnProperty.call(source, key)) return '';
  if (typeof source[key] !== 'string') return null;
  const value = source[key].trim();
  if (!value) return '';
  if (!opaqueIdentity(value) || value === '.' || value === '..' || /[\\/:]/.test(value)) return null;
  return value;
}
function startChatSubfolder(source) {
  if (!Object.prototype.hasOwnProperty.call(source, 'subfolder')) return '';
  if (typeof source.subfolder !== 'string') return null;
  const value = source.subfolder.trim();
  if (!value) return '';
  if (
    value === '.'
    || value === '..'
    || /[\\\/]/.test(value)
    || /[<>:"|?*\u0000-\u001f]/.test(value)
    || /[. ]$/.test(value)
    || /^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?$/i.test(value)
  ) return null;
  return value.slice(0, STARTCHAT_MAX_TEXT);
}
function normalizeStartChatFields(source) {
  if (!source || typeof source !== 'object' || Array.isArray(source)) return null;
  const muxName = Object.prototype.hasOwnProperty.call(source, 'muxName')
    ? (typeof source.muxName === 'string' ? strictMuxName(source.muxName) : null)
    : '';
  const tool = Object.prototype.hasOwnProperty.call(source, 'tool')
    ? (typeof source.tool === 'string' ? source.tool.trim().toLowerCase() : null)
    : '';
  const fields = {
    intentId: Object.prototype.hasOwnProperty.call(source, 'intentId')
      ? (typeof source.intentId === 'string' ? commandIntentId(source.intentId) : null)
      : '',
    muxName,
    title: startChatString(source, 'title'),
    tool,
    checkpointId: startChatIdentity(source, 'checkpointId'),
    workspaceId: startChatIdentity(source, 'workspaceId'),
    subfolder: startChatSubfolder(source),
    deckId: startChatIdentity(source, 'deckId'),
    collectionId: startChatIdentity(source, 'collectionId'),
    collection: startChatString(source, 'collection'),
    phrase: startChatString(source, 'phrase'),
  };
  return Object.values(fields).some(value => value === null) ? null : fields;
}
function startChatValidationError(fields) {
  if (!fields || !fields.intentId) return 'startchat requires a valid intent id';
  if (!fields.muxName) return 'startchat requires a valid mux session name';
  if (!fields.deckId) return 'startchat requires a valid deck identity';
  if (fields.collectionId && fields.collection)
    return 'startchat collectionId and collection are mutually exclusive';
  if (fields.checkpointId) {
    if (fields.tool || fields.workspaceId || fields.subfolder)
      return 'checkpoint startchat cannot include tool, workspaceId, or subfolder';
    return '';
  }
  if (!['claude', 'codex'].includes(fields.tool))
    return 'blank startchat requires tool claude or codex';
  if (!fields.workspaceId) return 'blank startchat requires workspaceId';
  return '';
}
function normalizeStartChatPayload(body) {
  const unknown = Object.keys(body).find(key => !STARTCHAT_ALLOWED_FIELDS.has(key));
  if (unknown) return { error: `startchat field is not allowed: ${unknown}` };
  const fields = normalizeStartChatFields(body);
  const error = startChatValidationError(fields);
  return error ? { error } : { fields };
}
function stableJson(value) {
  if (Array.isArray(value)) return '[' + value.map(stableJson).join(',') + ']';
  if (value && typeof value === 'object') {
    return '{' + Object.keys(value).sort().map(key => JSON.stringify(key) + ':' + stableJson(value[key])).join(',') + '}';
  }
  return JSON.stringify(value);
}
function commandFingerprint(command) {
  const payload = {
    type: String(command.type || ''), sessionId: String(command.sessionId || ''),
    tool: String(command.tool || ''), pid: Number(command.pid) || 0,
    uploadId: String(command.uploadId || ''), filename: String(command.filename || ''),
    title: String(command.title || ''), keep: !!command.keep, label: String(command.label || ''),
    muxName: String(command.muxName || ''), sessionName: String(command.sessionName || ''),
    insert: String(command.insert || ''), collection: String(command.collection || ''),
    collectionId: String(command.collectionId || ''), deckId: String(command.deckId || ''),
    deck: String(command.deck || ''), deckName: String(command.deckName || ''),
    checkpointId: String(command.checkpointId || ''), workspaceId: String(command.workspaceId || ''),
    subfolder: String(command.subfolder || ''), phrase: String(command.phrase || ''),
    takeover: !!command.takeover,
  };
  return crypto.createHash('sha256').update(stableJson(payload)).digest('hex');
}
// The client's proof of who authorized an operation. OPAQUE here on purpose: the relay bounds its size,
// stores it byte-stable, and hands it back on lease so the enforcing side can verify it. replayPolicy has
// always described an operation's SEMANTICS (dedup/replay safety); it has never described authorization,
// and a relay-minted lease token is not authority either. Read-only commands still need a principal.
const PRINCIPAL_AUTH_MAX_BYTES = 4096;
const COMMAND_REQUIRES_PRINCIPAL_AUTH = new Set(['transcriptfetch']);
function principalAuthEnvelope(value) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  let text;
  try { text = JSON.stringify(value); } catch { return null; }
  if (!text || text === '{}' || Buffer.byteLength(text, 'utf8') > PRINCIPAL_AUTH_MAX_BYTES) return null;
  return JSON.parse(text);
}
function commandOutcomeDetail(type, status, onPc = false) {
  if (status === 'pending' || status === 'leased') return '';
  if (type === 'fetchfile') {
    if (status === 'done') return 'downloaded to PC';
    return onPc ? 'downloaded to PC but prompt insertion failed' : 'file download failed';
  }
  const labels = {
    startmux: ['mux session started', 'PC bridge could not start mux session'],
    startchat: ['chat started', 'PC bridge could not start chat'],
    kill: ['session stopped', 'PC bridge could not stop session'],
    transcript: ['transcript opened', 'PC bridge could not open transcript'],
    transcriptfetch: ['transcript ready to read here', 'PC bridge could not fetch the transcript'],
    rename: ['session renamed', 'PC bridge could not rename session'],
    setapptitle: ['title updated', 'PC bridge could not update title'],
    addtocollection: ['collection updated', 'PC bridge could not update collection'],
    cleartabhistory: ['tab history cleared', 'PC bridge could not clear tab history'],
    settabcolor: ['tab color updated', 'PC bridge could not update tab color'],
  };
  const pair = labels[type] || ['command completed', 'PC bridge command failed'];
  return status === 'done' ? pair[0] : pair[1];
}
{
  const loadedCommands = durableJsonLoad(COMMANDS_FILE, [], Array.isArray);
  if (!Array.isArray(loadedCommands)) throw new Error('persisted app command queue must be an array');
  _commands = loadedCommands.map(c => {
    const status = ['pending', 'leased', 'done', 'failed'].includes(String(c.status))
      ? String(c.status)
      : 'failed';
    const type = String(c.type || '');
    const startChatFields = type === 'startchat'
      ? normalizeStartChatFields({ ...c, intentId: commandIntentId(c.intentId) || commandIntentId(c.id) })
      : null;
    if (type === 'startchat' && startChatValidationError(startChatFields)) return null;
    const command = {
    id: opaqueIdentity(c.id), intentId: commandIntentId(c.intentId) || commandIntentId(c.id),
    type,
    replayPolicy: type === 'startchat'
      ? COMMAND_REPLAY_POLICY.get(type)
      : String(c.replayPolicy || COMMAND_REPLAY_POLICY.get(type) || ''),
    sessionId: opaqueIdentity(c.sessionId),
    tool: ['claude', 'codex'].includes(String(c.tool || '').toLowerCase()) ? String(c.tool).toLowerCase() : '',
    pid: Number(c.pid) || 0,
    uploadId: /^u[a-z0-9]+-[a-z0-9]+$/i.test(String(c.uploadId || '')) ? String(c.uploadId) : '',
    filename: String(c.filename || '').replace(/\\/g, '/').split('/').pop().slice(0, 120),
    title: String(c.title || '').slice(0, 200), keep: !!c.keep,
    label: String(c.label || ''), muxName: strictMuxName(c.muxName), sessionName: strictMuxName(c.sessionName),
    insert: String(c.insert || ''), collection: String(c.collection || '').slice(0, 200),
    collectionId: String(c.collectionId || '').slice(0, 200), deckId: String(c.deckId || '').slice(0, 200),
    deck: String(c.deck || '').slice(0, 200), deckName: String(c.deckName || '').slice(0, 200),
    ...(type === 'startchat' ? {
      checkpointId: startChatFields.checkpointId,
      workspaceId: startChatFields.workspaceId,
      subfolder: startChatFields.subfolder,
      phrase: startChatFields.phrase,
      title: startChatFields.title,
      tool: startChatFields.tool,
      muxName: startChatFields.muxName,
      collection: startChatFields.collection,
      collectionId: startChatFields.collectionId,
      deckId: startChatFields.deckId,
    } : {}),
    takeover: !!c.takeover,
    principalAuth: principalAuthEnvelope(c.principalAuth),
    ts: Number(c.ts) || 0,
    status,
    detail: commandOutcomeDetail(
      type,
      status,
      !!c.onPc,
    ),
    doneAt: Number(c.doneAt) || 0,
    leaseOwner: status === 'leased' ? commandIntentId(c.leaseOwner) : '',
    leaseToken: status === 'leased' || status === 'done' || status === 'failed'
      ? commandIntentId(c.leaseToken)
      : '',
    leaseExpiresAt: status === 'leased' ? Number(c.leaseExpiresAt) || 0 : 0,
    attempt: Math.max(0, Number(c.attempt) || 0),
  };
    command.fingerprint = type === 'startchat'
      ? commandFingerprint(command)
      : (/^[a-f0-9]{64}$/.test(String(c.fingerprint || ''))
      ? String(c.fingerprint)
      : commandFingerprint(command));
    return command;
  }).filter(c => c && c.id && c.intentId);
}
function saveCommands(candidate = _commands) { writeJsonState(COMMANDS_FILE, candidate); }
function compactCommands(candidate, now = Date.now()) {
  const live = candidate.filter(command => command.status === 'pending' || command.status === 'leased');
  const terminal = candidate
    .filter(command => command.status === 'done' || command.status === 'failed')
    .filter(command => now - (Number(command.doneAt) || Number(command.ts) || 0) <= COMMAND_TERMINAL_RETENTION_MS)
    .sort((a, b) => (Number(b.doneAt) || Number(b.ts) || 0) - (Number(a.doneAt) || Number(a.ts) || 0))
    .slice(0, COMMAND_TERMINAL_LIMIT);
  const keep = new Set([...live, ...terminal]);
  return candidate.filter(command => keep.has(command));
}
function commitCommands(candidate) {
  candidate = compactCommands(candidate);
  try {
    saveCommands(candidate);
  } catch (error) {
    if (error.committed) _commands = candidate;
    throw error;
  }
  _commands = candidate;
}
_commands = compactCommands(_commands);
saveCommands(_commands);
let _cmdSeq = 0;
function enqueueAppCommand(b) {
  const id = 'c' + Date.now().toString(36) + '-' + (++_cmdSeq).toString(36);
  const intentId = commandIntentId(b.intentId) || id;
  const type = String(b.type || '');
  const replayPolicy = COMMAND_REPLAY_POLICY.get(type) || '';
  if (!replayPolicy) throw new Error('command type has no declared replay policy');
  const startChatFields = type === 'startchat' ? normalizeStartChatFields(b) : null;
  const cmd = { id, intentId, type, replayPolicy,
                sessionId: String(b.sessionId || ''), tool: String(b.tool || ''),
                pid: Number(b.pid) || 0, uploadId: String(b.uploadId || ''), filename: String(b.filename || ''),
                title: String(b.title || '').slice(0, 200), keep: !!b.keep, label: String(b.label || ''),
                muxName: String(b.muxName || ''), sessionName: String(b.sessionName || ''), insert: String(b.insert || ''),
                collection: String(b.collection || '').slice(0, 200), collectionId: String(b.collectionId || '').slice(0, 200),
                deckId: String(b.deckId || '').slice(0, 200), deck: String(b.deck || '').slice(0, 200),
                deckName: String(b.deckName || '').slice(0, 200), takeover: !!b.takeover,
                ...(type === 'startchat' ? {
                  checkpointId: startChatFields.checkpointId,
                  workspaceId: startChatFields.workspaceId,
                  subfolder: startChatFields.subfolder,
                  phrase: startChatFields.phrase,
                  title: startChatFields.title,
                  tool: startChatFields.tool,
                  muxName: startChatFields.muxName,
                  collection: startChatFields.collection,
                  collectionId: startChatFields.collectionId,
                  deckId: startChatFields.deckId,
                } : {}),
                principalAuth: principalAuthEnvelope(b.principalAuth),
                ts: Date.now(), status: 'pending', detail: '',
                doneAt: 0, leaseOwner: '', leaseToken: '', leaseExpiresAt: 0, attempt: 0 };
  if (COMMAND_REQUIRES_PRINCIPAL_AUTH.has(type) && !cmd.principalAuth) {
    const error = new Error('command type requires a principal auth envelope');
    error.principalAuthRequired = true;
    throw error;
  }
  cmd.fingerprint = commandFingerprint(cmd);
  const existing = _commands.find(command => command.intentId === intentId);
  if (existing) {
    if (existing.fingerprint !== cmd.fingerprint) {
      const error = new Error('intent id is already bound to a different command payload');
      error.intentConflict = true;
      throw error;
    }
    return { command: existing, deduplicated: true };
  }
  const candidate = [..._commands, cmd];
  commitCommands(candidate);
  // Wake long-poll lease waiters now that a fresh pending command exists. Deduplicated returns
  // above don't notify: their command was already visible before this call, so any waiter still
  // parked was parked because someone else holds its lease, and this call changed nothing.
  // setImmediate keeps waiter responses out of the enqueue caller's stack (its own response and
  // error handling come first).
  if (_leaseWaiters.length) setImmediate(notifyLeaseWaiters);
  return { command: cmd, deduplicated: false };
}
function waitForCommandResult(id, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  return new Promise(resolve => {
    const tick = () => {
      const c = _commands.find(x => x.id === id);
      if (c && c.status === 'done') return resolve({ ok: true, retryable: false, detail: c.detail || '' });
      if (c && c.status === 'failed') return resolve({ ok: false, retryable: false, detail: c.detail || '' });
      if (Date.now() >= deadline)
        return resolve({ ok: false, retryable: true, detail: 'timeout waiting for PC bridge ack' });
      setTimeout(tick, 200);
    };
    tick();
  });
}
app.post('/api/app-commands', (req, res) => {     // web (owner) enqueues
  const b = req.body || {};
  if (hasForbiddenRemoteField(b)) return res.status(400).json({ error: 'executable commands and local paths are forbidden' });
  if (!COMMAND_REPLAY_POLICY.has(b.type)) return res.status(400).json({ error: 'unsupported command' });
  if (b.type === 'startchat') {
    const normalized = normalizeStartChatPayload(b);
    if (normalized.error) return res.status(400).json({ error: normalized.error });
    Object.assign(b, normalized.fields);
  }
  const sessionId = b.sessionId ? opaqueIdentity(b.sessionId) : '';
  const muxName = b.muxName ? strictMuxName(b.muxName) : '';
  const sessionName = b.sessionName ? strictMuxName(b.sessionName) : '';
  const tool = String(b.tool || '').trim().toLowerCase();
  if (b.intentId && !commandIntentId(b.intentId))
    return res.status(400).json({ error: 'invalid intent id' });
  if (b.sessionId && !sessionId) return res.status(400).json({ error: 'invalid session identity' });
  if (b.muxName && !muxName) return res.status(400).json({ error: 'invalid mux session name' });
  if (b.sessionName && !sessionName) return res.status(400).json({ error: 'invalid mux session name' });
  if (tool && !['claude', 'codex'].includes(tool)) return res.status(400).json({ error: 'tool must be claude or codex' });
  b.sessionId = sessionId;
  b.muxName = muxName;
  b.sessionName = sessionName;
  b.tool = tool;
  b.replayPolicy = COMMAND_REPLAY_POLICY.get(b.type);
  if (b.type === 'kill' && !b.sessionId && !b.pid) return res.status(400).json({ error: 'sessionId or pid required' });
  if (b.type === 'transcript' && !b.sessionId) return res.status(400).json({ error: 'sessionId required' });
  if (b.type === 'transcriptfetch' && !b.sessionId) return res.status(400).json({ error: 'sessionId required' });
  if (b.type === 'transcriptfetch' && !principalAuthEnvelope(b.principalAuth))
    return res.status(400).json({ error: 'transcriptfetch requires a principal auth envelope' });
  if (b.type === 'fetchfile' && !b.uploadId) return res.status(400).json({ error: 'uploadId required' });
  if (b.type === 'fetchfile' && b.insert && !['path', 'element'].includes(String(b.insert).toLowerCase()))
    return res.status(400).json({ error: 'fetchfile insert must be path or element' });
  if (b.type === 'fetchfile' && b.insert && !(b.muxName || b.sessionName))
    return res.status(400).json({ error: 'fetchfile insertion requires muxName/sessionName' });
  if (b.type === 'fetchfile') {
    const uploadId = String(b.uploadId || '');
    if (!/^u[a-z0-9]+-[a-z0-9]+$/i.test(uploadId))
      return res.status(400).json({ error: 'invalid uploadId' });
    const upload = _uploads.find(item => item.id === uploadId);
    if (!upload) return res.status(404).json({ error: 'upload not found' });
    b.filename = upload.name;
    b.keep = !!upload.keep;
  }
  if (b.type === 'rename' && (!b.sessionId || !String(b.title || '').trim())) return res.status(400).json({ error: 'sessionId and title required' });
  if (b.type === 'setapptitle' && !b.sessionId) return res.status(400).json({ error: 'sessionId required' });   // empty title = clear the app override
  if (b.type === 'addtocollection' && (!(b.muxName || b.sessionName) || !(String(b.collection || '').trim() || String(b.collectionId || '').trim()))) return res.status(400).json({ error: 'muxName/sessionName and collection required' });
  if (b.type === 'addtocollection' && !appLive()) return res.status(409).json({ error: 'desktop app is not live; collection changes are disabled' });
  if (b.type === 'startmux' && !(b.muxName || b.sessionName)) return res.status(400).json({ error: 'muxName/sessionName required' });
  if (b.type === 'startmux' && !b.sessionId && !['claude', 'codex'].includes(String(b.tool || '').toLowerCase()))
    return res.status(400).json({ error: 'startmux requires sessionId or tool' });
  if (b.type === 'startmux') {
    const localOwner = ensureNoLocalOwnerForMuxName(b.muxName || b.sessionName, b.sessionId || '');
    if (!localOwner.ok) return res.status(409).json({ error: 'local copy is already running', detail: localOwner.detail });
  }
  let queued;
  try {
    queued = enqueueAppCommand(b);
  } catch (error) {
    if (error.intentConflict) return res.status(409).json({ error: error.message });
    if (error.principalAuthRequired) return res.status(400).json({ error: error.message });
    return failPersistence(res, error);
  }
  res.json({
    ok: true,
    id: queued.command.id,
    intentId: queued.command.intentId,
    status: queued.command.status,
    deduplicated: queued.deduplicated,
  });
});
// One lease pass over the queue: recover expired leases, then claim up to `limit` pending
// commands for `owner`. Throws whatever commitCommands throws (persistence), so each caller
// decides how to answer its own response.
function leaseAppCommands(owner, limit, leaseMs) {
  const now = Date.now();
  let changed = false;
  const candidate = _commands.map(command => {
    if (command.status !== 'leased' || command.leaseExpiresAt > now) return command;
    changed = true;
    return { ...command, status: 'pending', leaseOwner: '', leaseToken: '', leaseExpiresAt: 0 };
  });
  const leased = [];
  for (let index = 0; index < candidate.length && leased.length < limit; index++) {
    const command = candidate[index];
    if (command.status !== 'pending') continue;
    const updated = {
      ...command,
      status: 'leased',
      leaseOwner: owner,
      leaseToken: crypto.randomBytes(18).toString('base64url'),
      leaseExpiresAt: now + leaseMs,
      attempt: (Number(command.attempt) || 0) + 1,
    };
    candidate[index] = updated;
    leased.push(updated);
    changed = true;
  }
  if (changed) commitCommands(candidate);
  return leased;
}

// Long-poll waiters: lease requests that found an empty queue and asked (waitMs) to be held open
// until a command arrives. Each poll from the PC costs a full ssh handshake, so holding the answer
// here is what turns "poll every few seconds" into "answer the moment there is work". Waiters are
// woken only by a fresh enqueue; a lease that expires mid-wait is recovered lazily on the next
// lease pass, at worst one waitMs later — with waits capped at 25s against a 120s lease, that
// corner costs seconds on an already-failed consumer, so it stays lazy.
const LEASE_WAIT_MAX_MS = 25000;
const _leaseWaiters = [];

function notifyLeaseWaiters() {
  // FIFO fairness; stop as soon as a pass comes back empty — later waiters would see the same.
  while (_leaseWaiters.length) {
    const waiter = _leaseWaiters[0];
    let leased;
    try {
      leased = leaseAppCommands(waiter.owner, waiter.limit, waiter.leaseMs);
    } catch (error) {
      _leaseWaiters.shift();
      clearTimeout(waiter.timer);
      failPersistence(waiter.res, error);
      continue;
    }
    if (!leased.length) return;
    _leaseWaiters.shift();
    clearTimeout(waiter.timer);
    waiter.res.json(leased);
  }
}

function dropLeaseWaiter(waiter) {
  const index = _leaseWaiters.indexOf(waiter);
  if (index >= 0) _leaseWaiters.splice(index, 1);
  clearTimeout(waiter.timer);
}

app.post('/api/app-commands/lease', (req, res) => {
  if (!isTrustedLocal(req)) return res.status(403).json({ error: 'forbidden' });
  const owner = commandIntentId(req.body && req.body.owner);
  if (!owner) return res.status(400).json({ error: 'valid lease owner required' });
  const limit = Math.max(1, Math.min(16, Number(req.body && req.body.limit) || 8));
  const leaseMs = TEST_MODE && Number(req.body && req.body.leaseMs)
    ? Math.max(50, Math.min(COMMAND_LEASE_MS, Number(req.body.leaseMs)))
    : COMMAND_LEASE_MS;
  // waitMs opts into the long poll; absent/0 keeps the classic answer-now contract for old clients.
  const waitMs = Math.max(0, Math.min(LEASE_WAIT_MAX_MS, Number(req.body && req.body.waitMs) || 0));
  let leased;
  try {
    leased = leaseAppCommands(owner, limit, leaseMs);
  } catch (error) {
    return failPersistence(res, error);
  }
  if (leased.length || waitMs === 0) return res.json(leased);

  const waiter = { owner, limit, leaseMs, res, timer: null };
  waiter.timer = setTimeout(() => {
    dropLeaseWaiter(waiter);
    if (!res.writableEnded) res.json([]);
  }, waitMs);
  res.on('close', () => dropLeaseWaiter(waiter));   // client gone: never answer a dead socket
  _leaseWaiters.push(waiter);
});
app.get('/api/app-commands', (req, res) => {
  if (!isTrustedLocal(req)) return res.status(403).json({ error: 'forbidden' });
  return res.status(410).json({ error: 'unleased command pulls are disabled; use POST /api/app-commands/lease' });
});
app.post('/api/app-commands/:id/ack', (req, res) => {   // app (loopback) reports a result
  if (!isTrustedLocal(req)) return res.status(403).json({ error: 'forbidden' });
  const index = _commands.findIndex(x => x.id === req.params.id);
  if (index < 0) return res.status(404).json({ error: 'command not found' });
  const prior = _commands[index];
  const leaseToken = commandIntentId(req.body && req.body.leaseToken);
  const status = (req.body && req.body.ok) ? 'done' : 'failed';
  if (prior.status === 'done' || prior.status === 'failed') {
    if (!leaseToken || leaseToken !== prior.leaseToken || status !== prior.status)
      return res.status(409).json({ error: 'terminal command outcome is immutable' });
    return res.json({ ok: true, status: prior.status, deduplicated: true });
  }
  if (
    prior.status !== 'leased'
    || !leaseToken
    || leaseToken !== prior.leaseToken
    || prior.leaseExpiresAt <= Date.now()
  ) {
    return res.status(409).json({ error: 'lease token is missing, stale, or expired' });
  }
  const updated = {
    ...prior,
    status,
    detail: commandOutcomeDetail(prior.type, status, !!(req.body && req.body.onPc)),
    doneAt: Date.now(),
    leaseExpiresAt: 0,
  };
  if (updated.type === 'fetchfile' && updated.status === 'done' && updated.insert)
    updated.detail = 'downloaded to PC and inserted into the mux prompt';
  try {
    if (updated.type === 'fetchfile' && req.body && req.body.onPc === true && updated.uploadId) {
      const uploadIndex = _uploads.findIndex(x => x.id === updated.uploadId);
      if (uploadIndex >= 0) {
        const uploads = _uploads.slice();
        uploads[uploadIndex] = { ...uploads[uploadIndex], onPc: true };
        commitUploads(uploads);
      }
    }
    const candidate = _commands.slice();
    candidate[index] = updated;
    commitCommands(candidate);
  } catch (error) {
    return failPersistence(res, error);
  }
  res.json({ ok: true, status: updated.status, deduplicated: false });
});
app.get('/api/app-commands/:id', (req, res) => {  // web (owner) polls a command's outcome
  const c = _commands.find(x => x.id === req.params.id);
  if (!c) return res.status(404).json({ error: 'not found' });
  res.json({ id: c.id, status: c.status, detail: c.detail || '' });
});

// --- transcript page store: PC archive -> VPS (briefly) -> the owner's browser -------------------
// A `transcriptfetch` command is the ONLY thing that authorizes parking a chat's archived pages here.
// The app pushes them page-by-page with the scoped bridge credential; we keep exactly the newest fetch
// for that one explicitly requested session id and drop it on a bounded TTL. This is a courier, not a
// mirror: nothing is retained for a session nobody asked for, and the widening from live-screen bytes
// to archived history is deliberately fenced by the request that asked for it.
const TRANSCRIPTS_FILE = STATE_DIR + '/transcripts.json';
const TRANSCRIPT_SCHEMA_VERSION = 1;
const TRANSCRIPT_MAX_PAGES = 32;                                   // matches the app-side pager
const TRANSCRIPT_MAX_PAGE_BYTES = 256 * 1024;                      // app targets <=200KB; this is the fence
const TRANSCRIPT_MAX_SESSION_BYTES = TRANSCRIPT_MAX_PAGES * TRANSCRIPT_MAX_PAGE_BYTES;   // 8 MiB
const TRANSCRIPT_MAX_SESSIONS = 8;
// Floored at a minute in production so a real fetch has time to page in; tests may shorten it to prove
// the window actually closes (same escape hatch shape as the command lease duration).
const TRANSCRIPT_TTL_MS = Math.max(
  TEST_MODE ? 200 : 60000,
  Number(process.env.MUX_TRANSCRIPT_TTL_MS) || 15 * 60 * 1000,
);
const validTranscriptStore = value => (
  Array.isArray(value)
  && value.every(record => (
    record
    && typeof record === 'object'
    && !!opaqueIdentity(record.sessionId)
    && !!opaqueIdentity(record.fetchId)
    && Number.isFinite(Number(record.pages))
    && Number.isFinite(Number(record.expiresAt))
    && Array.isArray(record.pageList)
  ))
);
let _transcripts = [];
{
  const loaded = durableJsonLoad(TRANSCRIPTS_FILE, [], validTranscriptStore);
  if (!Array.isArray(loaded)) throw new Error('persisted transcript store must be an array');
  _transcripts = loaded.map(record => ({
    sessionId: opaqueIdentity(record.sessionId),
    fetchId: opaqueIdentity(record.fetchId),
    pages: Number(record.pages) || 0,
    bytes: Number(record.bytes) || 0,
    updatedAt: Number(record.updatedAt) || 0,
    expiresAt: Number(record.expiresAt) || 0,
    pageList: (Array.isArray(record.pageList) ? record.pageList : [])
      .filter(page => page && typeof page === 'object' && Number.isInteger(Number(page.page)))
      .map(page => ({
        page: Number(page.page),
        bytes: Number(page.bytes) || 0,
        messages: normalizeTranscriptMessages(page.messages),
      })),
  })).filter(record => record.sessionId && record.fetchId);
}
function normalizeTranscriptMessages(messages) {
  return (Array.isArray(messages) ? messages : []).map(message => {
    const kept = { role: String(message.role || ''), text: String(message.text || '') };
    if (message.ts !== undefined && message.ts !== null && Number.isFinite(Number(message.ts)))
      kept.ts = Number(message.ts);
    return kept;
  });
}
function liveTranscripts(now = Date.now()) {
  return _transcripts.filter(record => record.expiresAt > now);
}
function commitTranscripts(candidate) {
  writeJsonState(TRANSCRIPTS_FILE, candidate);
  _transcripts = candidate;
}
function transcriptPageProblem(body, sessionId) {
  if (!body || typeof body !== 'object' || Array.isArray(body)) return 'transcript page must be a JSON object';
  if (Number(body.schemaVersion) !== TRANSCRIPT_SCHEMA_VERSION) return 'unsupported transcript schema version';
  if (opaqueIdentity(body.sessionId) !== sessionId) return 'page session identity does not match the request';
  const pages = Number(body.pages);
  const page = Number(body.page);
  if (!Number.isInteger(pages) || pages < 1 || pages > TRANSCRIPT_MAX_PAGES)
    return 'pages must be an integer in 1..' + TRANSCRIPT_MAX_PAGES;
  if (!Number.isInteger(page) || page < 1 || page > pages) return 'page must be an integer in 1..pages';
  if (!Array.isArray(body.messages)) return 'messages must be an array';
  for (const message of body.messages) {
    if (!message || typeof message !== 'object' || Array.isArray(message)) return 'each message must be an object';
    if (typeof message.role !== 'string' || !message.role) return 'each message needs a role';
    if (typeof message.text !== 'string') return 'each message needs text';
    if (message.ts !== undefined && message.ts !== null && !Number.isFinite(Number(message.ts)))
      return 'message ts must be numeric';
  }
  return '';
}
// The fetch that asked for this session, if it is still inside the retention window. No request, no store.
function authorizingTranscriptFetch(sessionId, now = Date.now()) {
  return _commands
    .filter(command => command.type === 'transcriptfetch'
      && command.sessionId === sessionId
      && now - (Number(command.ts) || 0) <= TRANSCRIPT_TTL_MS)
    .sort((a, b) => (Number(b.ts) || 0) - (Number(a.ts) || 0))[0] || null;
}
app.post('/api/transcripts/:sessionId', (req, res) => {
  if (!TRANSCRIPT_BRIDGE_TOKEN) return res.status(503).json({ error: 'transcript bridge credential is not configured' });
  if (!transcriptBridgeTokenOk(req.headers[TRANSCRIPT_BRIDGE_HEADER]))
    return res.status(403).json({ error: 'scoped transcript bridge credential required' });
  const sessionId = opaqueIdentity(req.params.sessionId);
  if (!sessionId) return res.status(400).json({ error: 'invalid session identity' });
  const body = req.body;
  let bodyBytes = 0;
  try { bodyBytes = Buffer.byteLength(JSON.stringify(body === undefined ? null : body), 'utf8'); } catch { bodyBytes = Infinity; }
  if (bodyBytes > TRANSCRIPT_MAX_PAGE_BYTES) return res.status(413).json({ error: 'transcript page is too large' });
  const problem = transcriptPageProblem(body, sessionId);
  if (problem) return res.status(400).json({ error: problem });
  const now = Date.now();
  const authorizing = authorizingTranscriptFetch(sessionId, now);
  if (!authorizing) return res.status(409).json({ error: 'no live transcriptfetch request for this session' });
  const others = liveTranscripts(now).filter(record => record.sessionId !== sessionId);
  const prior = liveTranscripts(now).find(record => record.sessionId === sessionId);
  // A different authorizing fetch (or a re-page) means the old capture is stale — keep only the newest.
  const reuse = prior && prior.fetchId === authorizing.id && prior.pages === Number(body.pages);
  const stored = { page: Number(body.page), bytes: 0, messages: normalizeTranscriptMessages(body.messages) };
  stored.bytes = Buffer.byteLength(JSON.stringify(stored), 'utf8');
  const pageList = (reuse ? prior.pageList : []).filter(page => page.page !== stored.page).concat([stored])
    .sort((a, b) => a.page - b.page);
  const bytes = pageList.reduce((sum, page) => sum + (Number(page.bytes) || 0), 0);
  if (bytes > TRANSCRIPT_MAX_SESSION_BYTES)
    return res.status(413).json({ error: 'transcript exceeds the per-session byte budget' });
  const record = {
    sessionId,
    fetchId: authorizing.id,
    pages: Number(body.pages),
    bytes,
    updatedAt: now,
    expiresAt: now + TRANSCRIPT_TTL_MS,
    pageList,
  };
  const candidate = enforceTranscriptRetention(
    [...others, record]
      .sort((a, b) => (Number(b.updatedAt) || 0) - (Number(a.updatedAt) || 0))
      .slice(0, TRANSCRIPT_MAX_SESSIONS),
    now,
  );
  try {
    commitTranscripts(candidate);
  } catch (error) {
    return failPersistence(res, error);
  }
  res.json({
    ok: true,
    sessionId,
    page: stored.page,
    pages: record.pages,
    storedPages: record.pageList.map(page => page.page),
    bytes: record.bytes,
    expiresAt: record.expiresAt,
  });
});
app.get('/api/transcripts/:sessionId', (req, res) => {   // owner-gated by the global middleware
  const sessionId = opaqueIdentity(req.params.sessionId);
  if (!sessionId) return res.status(400).json({ error: 'invalid session identity' });
  const now = Date.now();
  const record = liveTranscripts(now).find(entry => entry.sessionId === sessionId);
  if (!record) return res.status(404).json({ error: 'no transcript pages for this session' });
  const available = record.pageList.map(page => page.page).sort((a, b) => a - b);
  const requested = req.query.page === undefined ? available[0] : Number(req.query.page);
  if (!Number.isInteger(requested)) return res.status(400).json({ error: 'page must be an integer' });
  const found = record.pageList.find(page => page.page === requested);
  if (!found) return res.status(404).json({ error: 'transcript page is not stored' });
  res.json({
    schemaVersion: TRANSCRIPT_SCHEMA_VERSION,
    sessionId,
    page: found.page,
    pages: record.pages,
    availablePages: available,
    expiresAt: record.expiresAt,
    messages: found.messages,
  });
});

// --- state retention: the global fence over every store above -------------------------------------
// Installed here because it prunes the transcript store and the command journal, so both must already
// exist. Two triggers: a pass at boot and one every 6h, plus inline enforcement on the accept path of
// a transcript push (below) so a burst of pushes can never outrun the timer. Nothing is deleted by
// this module directly — it hands back a candidate set that commits through the same writeJsonState
// discipline as every other mutation, and it stands down entirely while persistence is blocked.
_retention = createRetention({
  stateDir: STATE_DIR,
  transcripts: () => _transcripts,
  commands: () => _commands,
  commitTranscripts: candidate => commitTranscripts(candidate),
  commitCommands: candidate => commitCommands(candidate),
  blocked: () => !!persistenceBlocked,
  onError: (error, store) => { recordPersistenceFailure(error); console.error('[retention] ' + store + ': ' + error.message); },
});
// The push path is bounded by TRANSCRIPT_MAX_SESSIONS per its own leaf; this adds the global caps on
// top so an accepted page can never leave the store over budget even between sweeps.
function enforceTranscriptRetention(candidate, now = Date.now()) {
  if (!_retention) return candidate;
  return pruneTranscriptRetention(candidate, _retention.config, now).kept;
}
_retention.sweep();
_retention.start();

// --- file upload side-channel: phone -> VPS (stored here) -> the desktop app pulls it down to the PC
// (scp, via a `fetchfile` command) and points the model at the local path. Deliberately NOT routed
// through the terminal/tmux. Owner-gated by the global middleware; size-capped; pruned after an hour.
const UPLOADS_DIR = STATE_DIR + '/uploads';
fs.mkdirSync(UPLOADS_DIR, { recursive: true });
const UPLOADS_META = STATE_DIR + '/uploads-meta.json';
const uploadIdPattern = /^u[a-z0-9]+-[a-z0-9]+$/i;
const uploadTombstonePattern = /^(u[a-z0-9]+-[a-z0-9]+)\.deleting-[a-z0-9]+$/i;
const uploadDir = id => UPLOADS_DIR + '/' + String(id).replace(/[^A-Za-z0-9_.-]/g, '');
let uploadRecoveryWarnings = [];
const isSafeUploadName = name => (
  typeof name === 'string'
  && /^[A-Za-z0-9._-]{1,120}$/.test(name)
  && name !== '.'
  && name !== '..'
);
const validUploadMetadata = value => (
  Array.isArray(value)
  && value.every(upload => (
    upload
    && typeof upload === 'object'
    && uploadIdPattern.test(String(upload.id || ''))
    && isSafeUploadName(upload.name)
    && (!upload.session || !!strictMuxName(upload.session))
    && Number.isFinite(Number(upload.ts))
    && Number.isFinite(Number(upload.size))
  ))
);
let _uploads = [];
{
  const loadedUploads = durableJsonLoad(UPLOADS_META, [], validUploadMetadata);
  if (!Array.isArray(loadedUploads)) throw new Error('persisted upload metadata must be an array');
  _uploads = loadedUploads.map(u => ({
    id: String(u.id || ''), name: String(u.name || ''), session: String(u.session || ''),
    ts: Number(u.ts) || 0, size: Number(u.size) || 0, keep: !!u.keep, onPc: !!u.onPc,
  })).filter(u => uploadIdPattern.test(u.id));
}
function saveUploadsMeta(candidate = _uploads) { writeJsonState(UPLOADS_META, candidate); }
function commitUploads(candidate) {
  try {
    saveUploadsMeta(candidate);
  } catch (error) {
    if (error.committed) _uploads = candidate;
    throw error;
  }
  _uploads = candidate;
}
function reconcileUploadStorageOnBoot() {
  const metadataIds = new Set();
  for (const upload of _uploads) {
    if (metadataIds.has(upload.id))
      throw new Error('duplicate persisted upload id: ' + upload.id);
    metadataIds.add(upload.id);
  }

  const tombstones = new Map();
  for (const entry of fs.readdirSync(UPLOADS_DIR, { withFileTypes: true })) {
    if (!entry.isDirectory()) continue;
    const match = uploadTombstonePattern.exec(entry.name);
    if (!match) continue;
    const list = tombstones.get(match[1]) || [];
    list.push(entry.name);
    tombstones.set(match[1], list);
  }

  let changed = false;
  for (const [id, names] of tombstones) {
    names.sort().reverse();
    const original = uploadDir(id);
    if (metadataIds.has(id) && !fs.existsSync(original)) {
      fs.renameSync(UPLOADS_DIR + '/' + names.shift(), original);
      changed = true;
    }
    for (const name of names) {
      fs.rmSync(UPLOADS_DIR + '/' + name, { recursive: true, force: true });
      changed = true;
    }
  }

  for (const entry of fs.readdirSync(UPLOADS_DIR, { withFileTypes: true })) {
    if (!entry.isDirectory() || !uploadIdPattern.test(entry.name)) continue;
    if (!metadataIds.has(entry.name)) {
      fs.rmSync(UPLOADS_DIR + '/' + entry.name, { recursive: true, force: true });
      changed = true;
    }
  }

  const survivors = [];
  for (const upload of _uploads) {
    try {
      const directory = uploadDir(upload.id);
      const directoryStat = fs.lstatSync(directory);
      if (!directoryStat.isDirectory() || directoryStat.isSymbolicLink())
        throw new Error('storage is not a real directory');
      const file = directory + '/' + upload.name;
      const fileStat = fs.lstatSync(file);
      if (!fileStat.isFile() || fileStat.isSymbolicLink())
        throw new Error('bytes are missing or unsafe');
      survivors.push(upload);
    } catch (error) {
      const warning = `quarantined upload ${upload.id}: ${error.message}`;
      uploadRecoveryWarnings.push(warning);
      console.error('[uploads] ' + warning);
      try { fs.rmSync(uploadDir(upload.id), { recursive: true, force: true }); } catch {}
      changed = true;
    }
  }
  _uploads = survivors;
  if (changed) fsyncDirectory(UPLOADS_DIR);
}
reconcileUploadStorageOnBoot();
saveUploadsMeta(_uploads);
const isImage = n => /\.(png|jpe?g|gif|webp|bmp|svg|heic)$/i.test(n || '');
function dropUpload(u) {
  try { fs.rmSync(uploadDir(u.id), { recursive: true, force: true }); }
  catch (error) { console.error(`[uploads] deferred cleanup failed for ${u.id}: ${error.message}`); }
}
// "leave" (not kept) files are temporary: drop them on restart and after an hour. "keep" files persist.
function pruneUploads(all) {
  const cut = Date.now() - 60 * 60 * 1000;
  const survivors = [];
  const removed = [];
  for (const u of _uploads) {
    if (!u.keep && (all || (u.ts || 0) < cut)) removed.push(u); else survivors.push(u);
  }
  if (survivors.length !== _uploads.length) {
    commitUploads(survivors);
    for (const upload of removed) dropUpload(upload);
  }
}
pruneUploads(true);   // restart sweep: non-kept uploads disappear
let _upSeq = 0;
// Upload a file into the per-session workspace. keep=1 makes it persist; otherwise it's swept on restart.
app.post('/api/upload', express.raw({ type: '*/*', limit: '64mb' }), (req, res) => {
  const raw = req.body;
  if (!raw || !raw.length) return res.status(400).json({ error: 'empty body' });
  const candidateName = String(req.query.name || 'file').replace(/[^A-Za-z0-9._-]/g, '_').slice(0, 120);
  const safe = isSafeUploadName(candidateName) ? candidateName : 'file';
  const rawSession = String(req.query.session || '');
  const session = rawSession ? strictMuxName(rawSession) : '';
  if (rawSession && !session) return res.status(400).json({ error: 'invalid session name' });
  const keep = req.query.keep === '1';
  try {
    pruneUploads(false);
  } catch (error) {
    return failPersistence(res, error);
  }
  const id = 'u' + Date.now().toString(36) + '-' + (++_upSeq).toString(36);
  try {
    fs.mkdirSync(uploadDir(id), { recursive: false });
    fsyncDirectory(UPLOADS_DIR);
    writeBytesState(uploadDir(id) + '/' + safe, raw);
  } catch (error) {
    dropUpload({ id });
    return failPersistence(res, error);
  }
  const u = { id, name: safe, session, ts: Date.now(), size: raw.length, keep, onPc: false };
  const candidate = [..._uploads, u];
  try {
    commitUploads(candidate);
  } catch (error) {
    if (!error.committed) dropUpload(u);
    return failPersistence(res, error);
  }
  res.json({ ok: true, uploadId: id, filename: safe, size: raw.length, keep });
});
// The workspace: files uploaded for a session (newest first). Drives the file panel + thumbnails.
app.get('/api/uploads', (req, res) => {
  const rawSession = String(req.query.session || '');
  const session = rawSession ? strictMuxName(rawSession) : '';
  if (rawSession && !session) return res.status(400).json({ error: 'invalid session name' });
  const list = _uploads.filter(u => !session || u.session === session)
    .sort((a, b) => (b.ts || 0) - (a.ts || 0))
    .map(u => ({ id: u.id, name: u.name, size: u.size, keep: !!u.keep, ts: u.ts, image: isImage(u.name), onPc: !!u.onPc }));
  res.json(list);
});
app.get('/api/uploads/:id/raw', (req, res) => {
  const u = _uploads.find(x => x.id === req.params.id);
  if (!u) return res.status(404).end();
  res.sendFile(uploadDir(u.id) + '/' + u.name, {}, e => { if (e && !res.headersSent) res.status(404).end(); });
});
app.patch('/api/uploads/:id', withMutationIntent('upload.keep', req => ({
  id: String(req.params.id),
  keep: req.body && typeof req.body.keep === 'boolean' ? req.body.keep : null,
}), (req, res) => {
  const index = _uploads.findIndex(x => x.id === req.params.id);
  if (index < 0) return res.status(404).json({ error: 'not found' });
  if (req.body && typeof req.body.keep === 'boolean') {
    const candidate = _uploads.slice();
    candidate[index] = { ...candidate[index], keep: req.body.keep };
    try {
      commitUploads(candidate);
    } catch (error) {
      return failPersistence(res, error);
    }
  }
  res.json({ ok: true, keep: _uploads[index].keep });
}));
app.delete('/api/uploads/:id', withMutationIntent('upload.delete', req => ({
  id: String(req.params.id),
}), (req, res) => {
  const i = _uploads.findIndex(x => x.id === req.params.id);
  if (i >= 0) {
    const removed = _uploads[i];
    const candidate = _uploads.filter((_, index) => index !== i);
    const originalDir = uploadDir(removed.id);
    const tombstoneDir = originalDir + '.deleting-' + Date.now().toString(36);
    let staged = false;
    try {
      if (fs.existsSync(originalDir)) {
        fs.renameSync(originalDir, tombstoneDir);
        fsyncDirectory(UPLOADS_DIR);
        staged = true;
      }
      commitUploads(candidate);
    } catch (error) {
      if (error.committed) {
        if (staged) {
          try { fs.rmSync(tombstoneDir, { recursive: true, force: true }); } catch {}
        }
        return failPersistence(res, error);
      }
      if (staged) {
        try { fs.renameSync(tombstoneDir, originalDir); } catch (rollbackError) {
          console.error(`[uploads] delete rollback failed for ${removed.id}: ${rollbackError.message}`);
        }
      }
      return failPersistence(res, error);
    }
    if (staged) {
      try { fs.rmSync(tombstoneDir, { recursive: true, force: true }); }
      catch (error) { console.error(`[uploads] tombstone cleanup failed for ${removed.id}: ${error.message}`); }
    }
  }
  res.json({ ok: true });
}));

const server = http.createServer(app);
// noServer + manual routing: two path-bound WebSocketServers on one http server BOTH grab 'upgrade'
// and the non-matching one aborts the handshake with a 400 before the right one sees it.
const wss = new WebSocketServer({ noServer: true });
const wssHost = new WebSocketServer({ noServer: true });
let draining = false;              // set by the SIGTERM restart drain at the bottom of this file
server.on('upgrade', (req, socket, head) => {
  if (draining) { try { socket.destroy(); } catch {} return; }   // a restarting relay accepts no new links
  const p = (req.url || '').split('?')[0];
  if (p === '/ws') {
    // Reject cross-site WebSocket hijacking BEFORE the handshake — a foreign/absent-in-prod Origin
    // never gets a 101, so a hostile page in the owner's browser can't open a credentialed terminal socket.
    if (!wsOriginOk(req)) { try { socket.destroy(); } catch {} return; }
    wss.handleUpgrade(req, socket, head, ws => wss.emit('connection', ws, req));
  }
  else if (p === '/host') {
    // reject a bad /host token BEFORE completing the handshake (constant-time) â€” no 101, no 'open'
    let ok = false; try { ok = hostTokenOk(new URL(req.url, 'http://x').searchParams.get('token')); } catch {}
    if (!ok) { try { socket.destroy(); } catch {} return; }
    wssHost.handleUpgrade(req, socket, head, ws => wssHost.emit('connection', ws, req));
  } else socket.destroy();
});

// ---- the PC session host's inbound link (muxd dials US â€” no inbound port on the PC) ---------------
wssHost.on('connection', (ws, req) => {
  const u = new URL(req.url, 'http://x');
  if (!hostTokenOk(u.searchParams.get('token'))) { try { ws.close(1008, 'bad token'); } catch {} return; }
  try { req.socket.setNoDelay(true); } catch {}                       // low-latency: no Nagle on the host link
  let helloAccepted = false;
  console.log('[host] PC session host candidate connected');
  ws.on('message', raw => {
    let m; try { m = JSON.parse(raw.toString()); } catch { return; }
    // "exe" is a forbidden remote key, and agentTruth legitimately carries one — scanning the raw frame
    // would close the link on every hello a truth-capable muxd sends. See withoutAgentTruth().
    if (containsForbiddenRemoteKey(withoutAgentTruth(m))) {
      console.log('[host] rejected path/command-bearing protocol frame');
      try { ws.close(1008, 'host frame violated protocol'); } catch {}
      return;
    }
    if (m.t === 'hello') {
      const announced = announcedHostProtocol(m);
      const incoming = normalizeHostSessionList(m.sessions);
      if (!announced || !incoming) {
        console.log('[host] rejected incompatible or malformed hello');
        try { ws.close(1008, 'host hello violated protocol'); } catch {}
        return;
      }
      const prior = hostWs;
      hostWs = ws;
      helloAccepted = true;
      hostLabel = opaqueIdentity(m.host) || 'pc';
      hostProtocol = announced;
      hostSessions.clear();
      for (const [name, value] of incoming) hostSessions.set(name, value);
      console.log(`[host] hello from ${hostLabel} (${hostSessions.size} session(s), protocol ${hostProtocol.protocol})`);
      if (prior && prior !== ws) {
        try { prior.close(1000, 'replaced by validated host connection'); } catch {}
      }
      for (const [name, st] of sessions) {
        st.cur = null;
        clearScrollbackRequest(st);
        const waiter = [...st.sbWaiters].map(id => st.clients.get(id)).find(Boolean);
        if (waiter && hostSessions.has(name)) requestSessionScrollback(name, st, waiter);
      }
      for (const name of hostSessions.keys()) recompute(name);
      reconcileRenameIntentsFromHost();
      sweepAttentionEpisodes();
      const legacy = new Set(legacyTmuxNames());
      const twins = [...hostSessions.keys()].filter(name => legacy.has(name));
      if (twins.length) console.log(`[legacy] hosted/legacy name conflict(s): ${twins.join(', ')} - relay will refuse web attach/manage until cleaned manually`);
      return;
    }
    if (!helloAccepted || hostWs !== ws) {
      console.log('[host] rejected frame before validated hello');
      try { ws.close(1008, 'validated hello required'); } catch {}
      return;
    }
    if (m.t === 'sessions') {
      const incoming = normalizeHostSessionList(m.list);
      if (!incoming) {
        console.log('[host] rejected malformed session list');
        try { ws.close(1008, 'host session list violated protocol'); } catch {}
        return;
      }
      hostSessions.clear();
      for (const [n, v] of incoming) hostSessions.set(n, v);
      for (const [n, st] of sessions) {
        const pending = st.unpinnedLocalSize;
        const hosted = hostSessions.get(n);
        if (pending && hosted && (hosted.cols | 0) === pending.cols && (hosted.rows | 0) === pending.rows) {
          st.unpinnedLocalSize = null;
        }
      }
      reconcileRenameIntentsFromHost();
      sweepAttentionEpisodes();
    } else if (m.t === 'createResult') {
      const rid = String(m.rid || '');
      const pending = pendingHostCreates.get(rid);
      if (!pending) return;
      pendingHostCreates.delete(rid);
      const responseName = strictMuxName(m.s);
      if (responseName !== pending.expectedName) {
        pending.finish({ ok: false, created: false, detail: 'muxd acknowledgement identity did not match the request', session: null });
        return;
      }
      let session = null;
      if (m.ok && m.session) session = normalizeHostSession(m.session);
      if (m.ok && (!session || responseName !== strictMuxName(m.session && m.session.name))) {
        pending.finish({ ok: false, created: false, detail: 'muxd acknowledgement omitted a valid matching session', session: null });
        return;
      }
      if (session) hostSessions.set(responseName, session);
      pending.finish({
        ok: !!m.ok,
        created: !!m.created,
        status: m.ok ? 200 : (m.retryable ? 503 : 409),
        error: m.ok ? '' : (m.retryable ? 'muxd create outcome is uncertain' : 'muxd refused the create request'),
        retryable: !!m.retryable,
        detail: m.ok ? '' : String(m.detail || 'muxd refused the create request'),
        session,
      });
    } else if (m.t === 'o') {
      const n = strictMuxName(m.s); const h = hostSessions.get(n); if (h) h.lastOut = Date.now();
      const st = sessions.get(n); if (!st) return;
      const buf = Buffer.from(m.d || '', 'base64');
      for (const c of st.clients.values()) {
        if (!c.hosted || c.ws.readyState !== 1) continue;
        if (c.sbWait) {
          (c.q = c.q || []).push(buf); c.qBytes = (c.qBytes || 0) + buf.length;
          // Flood while still waiting for scrollback: relieve memory, but NEVER blank-and-drop.
          // The live stream itself repaints a TUI, so go live now â€” clear once (a fresh attach
          // starts clean) and replay what we buffered. wentLive means a late sb is dropped, but
          // only because real output is already painting the screen (never leaves it black).
          if (c.qBytes > 2000000 || c.q.length > 4000) {
            c.sbWait = false; c.wentLive = true;
            st.sbWaiters.delete(c.id);
            if (sendViewer(n, st, c, CLEAR_SCREEN, true)) {
              for (const q of c.q) if (!sendViewer(n, st, c, q, true)) break;
            }
            c.q = []; c.qBytes = 0;
          }
          continue;
        }
        c.wentLive = true;
        st.sbWaiters.delete(c.id);
        sendViewer(n, st, c, buf);
      }
    } else if (m.t === 'sb') {
      const n = strictMuxName(m.s); const st = sessions.get(n); if (!st) return;
      if (!st.sbInFlight || String(m.rid || '') !== st.sbRid) return;
      const buf = Buffer.from(m.d || '', 'base64');
      const waiters = [...st.sbWaiters];
      st.sbWaiters.clear();
      clearScrollbackRequest(st);
      for (const id of waiters) {
        const c = st.clients.get(id);
        // Deliver the replay unless live output has already painted (wentLive). Crucially this
        // fires EVEN IF the sbWait timeout already elapsed: the timeout no longer blanks the
        // screen, so a late sb is the only thing that paints an idle session. Dropping it here
        // (the old `!c.sbWait` guard) is exactly what left an idle terminal black.
        if (!c || !c.hosted || c.wentLive) continue;
        c.sbWait = false; c.wentLive = true;
        if (sendViewer(n, st, c, CLEAR_SCREEN, true) && sendViewer(n, st, c, buf, true)) {
          for (const q of (c.q || [])) if (!sendViewer(n, st, c, q, true)) break;
        }
        c.q = []; c.qBytes = 0;
      }
    } else if (m.t === 'lease') {
      // muxd decided; the relay only mirrors. The frame is cached and fanned out verbatim so every
      // viewer re-verifies muxd's signature itself — a relay that edits this string just breaks it.
      const n = strictMuxName(m.s);
      const raw = leaseConduit.cacheLeaseNotice(n, typeof m.f === 'string' ? m.f : '');
      if (raw) broadcastLeaseNotice(n, raw);
    } else if (m.t === 'err' && String(m.code || '').startsWith('principal-')) {
      const n = strictMuxName(m.s); const st = sessions.get(n); if (!st) return;
      const originKey = n + ':' + String(m.intentId || '');
      const origin = hostInputOrigins.get(originKey);
      const source = (origin && origin.clientId) || String(m.channelId || '');
      if (origin) clearTimeout(origin.timer);
      hostInputOrigins.delete(originKey);
      for (const c of st.clients.values()) {
        if (!c.hosted || c.ws.readyState !== 1) continue;
        if (source && c.id !== source) continue;
        try { c.ws.close(1008, String(m.code) + ': ' + String(m.m || 'input refused').slice(0, 90)); } catch {}
      }
    } else if (m.t === 'resync') {
      // muxd blew THIS session's ~1MiB egress budget and dropped its own backlog at a frame
      // boundary. The gap is unrecoverable from the stream, so repaint from truth: CLEAR +
      // scrollback replay for this session's viewers only — no other session lost a byte.
      const n = strictMuxName(m.s); const st = sessions.get(n); if (!st) return;
      clearScrollbackRequest(st);                 // supersede any in-flight (pre-gap) request
      let asked = false;
      for (const c of st.clients.values()) {
        if (!c.hosted || c.ws.readyState !== 1) continue;
        c.sbWait = true; c.wentLive = false; c.q = []; c.qBytes = 0;
        asked = requestSessionScrollback(n, st, c) || asked;
      }
      // Host unreachable: never strand viewers buffering forever — go live and let output repaint.
      if (!asked) for (const c of st.clients.values()) { c.sbWait = false; c.wentLive = true; }
    } else if (m.t === 'tailr') { const f = pendingTails.get(m.rid); if (f) { pendingTails.delete(m.rid); f(String(m.text || ''), String(m.sig || '')); }
    } else if (m.t === 'killed') {
      const n = strictMuxName(m.s);
      hostSessions.delete(n);
      leaseConduit.forgetSession(n);
      deleteSessionViewerState(n, 'session ended');
    }
  });
  const ka = setInterval(() => { if (ws.readyState === 1) { try { ws.ping(); } catch {} } }, 20000);
  ws.on('close', () => {
    clearInterval(ka);
    if (hostWs === ws) {
      hostWs = null; hostProtocol = { protocol: 0, caps: [] };
      for (const st of sessions.values()) {
        clearScrollbackRequest(st);
      }
      for (const [rid, pending] of pendingHostCreates) {
        pendingHostCreates.delete(rid);
        pending.finish({ ok: false, created: false, detail: 'PC mux host disconnected before acknowledgement', session: null });
      }
      console.log('[host] PC session host disconnected');
    }
  });
});

// ---- shared window sizing + DEVICE-IDENTITY PINNING (multi-client mirror, ONE stable size) --------
// One shared size per session (tmux can't per-client-size a shared window; we hold every viewer at the
// server-chosen size and each PANs if it's bigger than their screen). PIN = "prefer THIS device": the
// pinned device's viewport drives the size for EVERYONE â€” last-pinner-wins â€” and it's keyed to a
// persistent deviceId (localStorage), so a Wi-Fi blip / reconnect / relay restart does NOT lose the pin
// (the old code pinned a connection id â†’ gone on every reconnect). No pin = auto over the RECENTLY-ACTIVE
// viewers only, so a backgrounded desktop tab in another room can't force your phone to pan forever.
const PINS_FILE = STATE_DIR + '/pins.json';
let pins = new Map();   // session -> { deviceId, label, cols, rows, at }
{
  const loadedPins = durableJsonLoad(
    PINS_FILE,
    [],
    value => Array.isArray(value) && value.every(entry => Array.isArray(entry) && entry.length === 2),
  );
  if (!Array.isArray(loadedPins)) throw new Error('persisted pin state must be an entry array');
  pins = new Map(loadedPins);
}
let _pinsDirty = false;
function savePins(candidate = pins) {
  try {
    writeJsonState(PINS_FILE, [...candidate]);
  } catch (error) {
    if (error.committed) {
      pins = candidate;
      _pinsDirty = false;
    }
    throw error;
  }
  pins = candidate;
  _pinsDirty = false;
}
const RENAME_INTENTS_FILE = STATE_DIR + '/rename-intents.json';
let renameIntents = durableJsonLoad(
  RENAME_INTENTS_FILE,
  [],
  value => Array.isArray(value) && value.every(intent => (
    intent
    && typeof intent === 'object'
    && /^[A-Za-z0-9._-]{1,128}$/.test(String(intent.id || ''))
    && !!strictMuxName(intent.from)
    && !!strictMuxName(intent.to)
    && intent.from !== intent.to
    && typeof intent.fromHadPin === 'boolean'
    && typeof intent.toHadPin === 'boolean'
  )),
);
function saveRenameIntents(candidate = renameIntents) {
  try {
    writeJsonState(RENAME_INTENTS_FILE, candidate);
  } catch (error) {
    if (error.committed) renameIntents = candidate;
    throw error;
  }
  renameIntents = candidate;
}
function beginRenameIntent(from, to) {
  if (renameIntents.some(intent => (
    intent.from === from || intent.to === from || intent.from === to || intent.to === to
  ))) return null;
  const intent = {
    id: 'r' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2, 10),
    from,
    to,
    fromHadPin: pins.has(from),
    fromPin: pins.has(from) ? pins.get(from) : null,
    toHadPin: pins.has(to),
    toPin: pins.has(to) ? pins.get(to) : null,
    createdAt: Date.now(),
  };
  saveRenameIntents([...renameIntents, intent]);
  return intent;
}
function applyRenameIntentPins(intent, renamed) {
  if (!intent.fromHadPin && !intent.toHadPin) return;
  const candidate = new Map(pins);
  candidate.delete(intent.from);
  candidate.delete(intent.to);
  if (renamed) {
    if (intent.fromHadPin) candidate.set(intent.to, intent.fromPin);
    else if (intent.toHadPin) candidate.set(intent.to, intent.toPin);
  } else {
    if (intent.fromHadPin) candidate.set(intent.from, intent.fromPin);
    if (intent.toHadPin) candidate.set(intent.to, intent.toPin);
  }
  savePins(candidate);
}
function completeRenameIntent(id) {
  const candidate = renameIntents.filter(intent => intent.id !== id);
  if (candidate.length === renameIntents.length) return;
  saveRenameIntents(candidate);
}
function reconcileRenameIntents() {
  for (const intent of [...renameIntents]) {
    const oldExists = hostSessions.has(intent.from);
    const newExists = hostSessions.has(intent.to);
    if (oldExists === newExists) continue;
    applyRenameIntentPins(intent, newExists);
    completeRenameIntent(intent.id);
  }
}
function reconcileRenameIntentsFromHost() {
  try {
    reconcileRenameIntents();
  } catch (error) {
    persistenceFailure = String(error && error.message || error);
    if (!error.committed) persistenceBlocked = persistenceFailure;
    console.error('[persistence] rename-intent reconciliation failed: ' + persistenceFailure);
  }
}
savePins(pins);
saveRenameIntents(renameIntents);
const _pinRetryTimer = setInterval(() => {
  if (!_pinsDirty) return;
  try { savePins(new Map(pins)); }
  catch (error) {
    persistenceFailure = String(error && error.message || error);
    console.error('[persistence] pin retry failed: ' + persistenceFailure);
  }
}, 20000);
const ACTIVE_MS = +process.env.MUX_ACTIVE_MS || 180000;   // "recently active" window that auto-size considers (3 min; env-overridable for tests)
const PIN_HOST_SYNC_GRACE_MS = +process.env.MUX_PIN_HOST_SYNC_GRACE_MS || 5000;
const VIEWER_HIGH_WATER_BYTES = Math.max(1024, +process.env.MUX_VIEWER_HIGH_WATER_BYTES || 4 * 1024 * 1024);
const VIEWER_REPLAY_BURST_BYTES = HOST_SB_BYTES + 2000000 + CLEAR_SCREEN.length;

const sessions = new Map(); // name -> { clients: Map<id,Client>, cur, unpinnedLocalSize, sbInFlight, sbRid, sbWaiters, sbRequestTimer }
let _cid = 0;
let _scrollbackRequestSeq = 0;
function sessionState(name) {
  let st = sessions.get(name);
  if (!st) {
    st = {
      clients: new Map(), cur: null, unpinnedLocalSize: null, sbInFlight: false, sbRid: '',
      sbWaiters: new Set(), sbRequestTimer: null,
    };
    sessions.set(name, st);
  }
  return st;
}
function deleteEmptySessionState(name, st) {
  if (st.clients.size === 0 && sessions.get(name) === st) {
    clearScrollbackRequest(st);
    sessions.delete(name);
  }
}
function clearPinForDisconnectedDevice(name, st, client) {
  const deviceId = client.deviceId || ('sock-' + client.id);
  let pin = pins.get(name);
  if (!pin || pin.deviceId !== deviceId) return;
  const stillConnected = [...st.clients.values()].some(
    other => (other.deviceId || ('sock-' + other.id)) === deviceId,
  );
  if (stillConnected) return;
  const candidate = new Map(pins);
  candidate.delete(name);
  commitBestEffortPinCleanup(candidate, 'disconnected pin cleanup');
}
function commitBestEffortPinCleanup(candidate, context) {
  try {
    savePins(candidate);
  } catch (error) {
    pins = candidate;
    _pinsDirty = true;
    persistenceFailure = String(error && error.message || error);
    console.error(`[persistence] ${context} retry scheduled: ${persistenceFailure}`);
  }
}
function removeViewer(name, st, client) {
  if (client.removed) return;
  client.removed = true;
  if (client.ka) clearInterval(client.ka);
  if (client.sbTimer) clearTimeout(client.sbTimer);
  st.clients.delete(client.id);
  st.sbWaiters.delete(client.id);
  clearPinForDisconnectedDevice(name, st, client);
  deleteEmptySessionState(name, st);
}
function deleteSessionViewerState(name, reason) {
  const st = sessions.get(name);
  if (!st) return;
  for (const client of [...st.clients.values()]) {
    removeViewer(name, st, client);
    try { client.ws.close(1013, reason); } catch {}
  }
  sessions.delete(name);
}
function viewerBytes(data) {
  return Buffer.isBuffer(data) ? data.length : Buffer.byteLength(String(data));
}
function sendViewer(name, st, client, data, replayBurst = false) {
  if (client.removed || client.ws.readyState !== 1) return false;
  const limit = VIEWER_HIGH_WATER_BYTES + (replayBurst ? VIEWER_REPLAY_BURST_BYTES : 0);
  if (client.ws.bufferedAmount + viewerBytes(data) > limit) {
    removeViewer(name, st, client);
    try { client.ws.terminate(); } catch {}
    return false;
  }
  try {
    client.ws.send(data);
    return true;
  } catch {
    removeViewer(name, st, client);
    try { client.ws.terminate(); } catch {}
    return false;
  }
}
function clearScrollbackRequest(st) {
  if (st.sbRequestTimer) clearTimeout(st.sbRequestTimer);
  st.sbRequestTimer = null;
  st.sbInFlight = false;
  st.sbRid = '';
}
function requestSessionScrollback(name, st, client) {
  st.sbWaiters.add(client.id);
  if (st.sbInFlight) return true;
  st.sbInFlight = true;
  st.sbRid = 'sb' + Date.now().toString(36) + '-' + (++_scrollbackRequestSeq).toString(36);
  const rid = st.sbRid;
  if (sendHost({ t: 'sb', s: name, rid, max: HOST_SB_BYTES })) {
    st.sbRequestTimer = setTimeout(() => {
      if (sessions.get(name) !== st || !st.sbInFlight || st.sbRid !== rid) return;
      clearScrollbackRequest(st);
      const waiter = [...st.sbWaiters].map(id => st.clients.get(id)).find(Boolean);
      if (waiter && hostSessions.has(name)) requestSessionScrollback(name, st, waiter);
    }, HOST_SB_REQUEST_TIMEOUT_MS);
    return true;
  }
  clearScrollbackRequest(st);
  st.sbWaiters.delete(client.id);
  return false;
}
function isActive(c) { return c.visible !== false || (Date.now() - (c.lastActive || c.connAt || 0) < ACTIVE_MS); }
function widest(list) { let b = list[0]; for (const c of list) if (c.vcols > b.vcols || (c.vcols === b.vcols && c.vrows > b.vrows)) b = c; return b; }
function commonFit(list) {
  let cols = list[0].vcols, rows = list[0].vrows;
  for (const c of list) {
    cols = Math.min(cols, c.vcols);
    rows = Math.min(rows, c.vrows);
  }
  return { vcols: cols, vrows: rows };
}
function targetSize(st, name) {
  const all = [...st.clients.values()].filter(c => c.vcols > 1 && c.vrows > 1);
  let pin = pins.get(name);
  if (pin) {
    const onDev = all.filter(c => (c.deviceId || ('sock-' + c.id)) === pin.deviceId);
    if (onDev.length) {
      const c = widest(onDev);
      if (c.vcols !== pin.cols || c.vrows !== pin.rows) {
        pin = { ...pin, cols: c.vcols, rows: c.vrows };
        pins.set(name, pin);
        _pinsDirty = true;
      }
      return { cols: c.vcols, rows: c.vrows, pin };
    }
    const candidate = new Map(pins);
    candidate.delete(name);
    commitBestEffortPinCleanup(candidate, 'stale pin cleanup');
  }
  const hosted = hostSessions.get(name);
  const localOwned = hosted && hosted.alive !== false
    && ((hosted.localViewers | 0) > 0 || !!hosted.owner);
  if (localOwned && (hosted.cols | 0) > 1 && (hosted.rows | 0) > 1) {
    const pending = st.unpinnedLocalSize;
    if (pending && pending.until > Date.now()) {
      return { cols: pending.cols, rows: pending.rows, pin: null, hostedSize: true };
    }
    st.unpinnedLocalSize = null;
    // Without an explicit pin, an attached PC terminal owns the PTY size. A deliberate pin
    // outranks it so every connected viewer, including the PC terminal, follows the chosen device.
    return { cols: hosted.cols | 0, rows: hosted.rows | 0, pin: null, hostedSize: true };
  }
  if (!all.length) return null;
  const active = all.filter(isActive);
  // One PTY can paint only one grid. Use the per-axis minimum across active viewers so every
  // active screen can show the complete frame; larger viewports letterbox on either axis.
  const c = commonFit(active.length ? active : all);
  return { cols: c.vcols, rows: c.vrows, pin: null };
}
function recompute(name) {
  const st = sessions.get(name); if (!st) return;
  const sz = targetSize(st, name); if (!sz) return;
  const cols = clampTermDimension(sz.cols, 2, MAX_TERM_COLS);
  const rows = clampTermDimension(sz.rows, 2, MAX_TERM_ROWS);
  for (const c of st.clients.values()) { if (c.term) try { c.term.resize(cols, rows); } catch {} }
  // Hosted sessions follow the attached PC terminal unless a human explicitly pins a device.
  // A pin (or a headless hosted session) is forwarded to muxd so the one shared PTY really resizes.
  if ([...st.clients.values()].some(c => c.hosted) && !sz.hostedSize) {
    if (!st.cur || st.cur.cols !== cols || st.cur.rows !== rows) { st.cur = { cols, rows }; sendHost({ t: 'resize', s: name, cols, rows }); }
  }
  const clients = [...st.clients.values()].map(c => ({ id: c.id, w: c.vcols, h: c.vrows, label: c.label || '', dev: c.deviceId || '', active: isActive(c) }));
  const pinned = !!sz.pin, pinLabel = pinned ? (sz.pin.label || 'a device') : '';
  for (const c of st.clients.values()) {
    if (c.ws.readyState !== 1) continue;
    const mine = pinned && sz.pin.deviceId === (c.deviceId || ('sock-' + c.id));
    const mode = pinned ? 'pinned' : 'auto';
    const modeLabel = pinned ? `ðŸ“Œ ${mine ? 'this device' : pinLabel} Â· ${cols}Ã—${rows}` : `auto Â· ${cols}Ã—${rows}`;
    sendViewer(name, st, c, 'd' + JSON.stringify({ cols, rows, mode, local: !!sz.hostedSize, pinLabel, mine, modeLabel, me: c.id, clients }));
  }
}
function pinToDevice(name, client, on) {
  const candidate = new Map(pins);
  const previous = pins.get(name);
  if (on) candidate.set(name, { deviceId: client.deviceId || ('sock-' + client.id), label: client.label || 'this device', cols: client.vcols, rows: client.vrows, at: Date.now() });
  else candidate.delete(name);
  try {
    savePins(candidate);
  } catch (error) {
    persistenceFailure = String(error && error.message || error);
    if (!error.committed) persistenceBlocked = persistenceFailure;
    console.error('[persistence] pin change failed: ' + persistenceFailure);
    if (!error.committed) {
      recompute(name);
      return false;
    }
  }
  const st = sessions.get(name);
  if (st) {
    st.unpinnedLocalSize = !on && previous
      ? { cols: previous.cols | 0, rows: previous.rows | 0, until: Date.now() + PIN_HOST_SYNC_GRACE_MS }
      : null;
  }
  recompute(name);
  return true;
}
function pinToDeviceId(name, deviceId) {   // long-press: pin to ANY listed device
  const st = sessions.get(name); if (!st) return;
  const c = [...st.clients.values()].find(x => (x.deviceId || ('sock-' + x.id)) === deviceId);
  if (c) pinToDevice(name, c, true);
}
function cycleMode(name, client) {   // the size chip / legacy 's': toggle pin-to-ME â†” auto (last-pinner-wins)
  const pin = pins.get(name);
  const mine = pin && client && pin.deviceId === (client.deviceId || ('sock-' + client.id));
  pinToDevice(name, client, !mine);
}
function applyActivity(client, o) {
  if (o && typeof o.vis === 'boolean') client.visible = o.vis;
  if (o && o.act) client.lastActive = Date.now();
}
// Fan a muxd lease notice to every viewer of a session, unmodified. Deliberately NOT routed through
// sendViewer: that path is byte-stream backpressure for terminal output, and a control frame the
// client must verify may not be coalesced, truncated, or dropped into a burst queue.
function broadcastLeaseNotice(name, raw) {
  const st = sessions.get(name);
  if (!st) return;
  for (const c of st.clients.values()) {
    if (!c || !c.ws || c.ws.readyState !== 1) continue;
    try { c.ws.send('L' + raw); } catch {}
  }
}
// Shared sizing/pin/activity message handler for BOTH paths ('i' input stays with each caller).
function handleClientMsg(name, client, s) {
  const t = s[0];
  if (t === 'v' || t === 'r') {
    try {
      const o = JSON.parse(s.slice(1));
      if (o.cols != null) client.vcols = clampTermDimension(o.cols, client.vcols, MAX_TERM_COLS);
      if (o.rows != null) client.vrows = clampTermDimension(o.rows, client.vrows, MAX_TERM_ROWS);
      applyActivity(client, o);
    } catch {}
    recompute(name);
    return true;
  }
  if (t === 'h') { try { applyActivity(client, JSON.parse(s.slice(1))); } catch {} return true; }
  if (t === 'P') { const arg = s.slice(1); if (arg && arg[0] === '#') pinToDeviceId(name, arg.slice(1)); else pinToDevice(name, client, arg !== '0'); return true; }
  if (t === 's') { cycleMode(name, client); return true; }
  return false;
}

wss.on('connection', async (ws, req) => {
  // Terminal attach is owner-only; loopback is NOT a credential. testModeLocalTrust() is the same
  // TEST_MODE+loopback exemption the HTTP gate uses and is false in any deploy — see its definition.
  if (!testModeLocalTrust(req) && !(await isOwner(cookieVal(req, HL_COOKIE)))) { try { ws.close(1008, 'unauthorized'); } catch {} return; }
  try { req.socket.setNoDelay(true); } catch {}                       // low-latency keystrokes: no Nagle on the viewer link
  const u = new URL(req.url, 'http://x');
  const name = strictMuxName(u.searchParams.get('session'));
  if (!name) return ws.close();
  const vcols = clampTermDimension(u.searchParams.get('cols'), 100, MAX_TERM_COLS);
  const vrows = clampTermDimension(u.searchParams.get('rows'), 30, MAX_TERM_ROWS);
  const deviceId = (u.searchParams.get('dev') || '').replace(/[^A-Za-z0-9_-]/g, '').slice(0, 40);   // persistent device identity for pinning
  const label = (u.searchParams.get('label') || '').replace(/[^\w .Â·/+-]/g, '').slice(0, 32) || 'device';

  // ---- PC-HOSTED attach: bridge this viewer to muxd (no tmux, no ssh â€” the session lives on the PC).
  // Unknown web tabs create an empty PC-local muxd shell. That preserves the old pleasant multiplex flow
  // while keeping ownership local to the PC instead of the VPS.
  const legacyBlocked = tmuxHas(name);
  if (legacyBlocked) {
    try { ws.close(1013, legacyDetail(name)); } catch {}
    return;
  }
  if (hostUp() && (hostSessions.has(name) || !legacyBlocked)) {
    let known = hostSessions.get(name);
    if (known && !hostProtocolOk()) {
      try { ws.close(1013, hostProtocolDetail()); } catch {}
      return;
    }
    if (known && known.alive === false) {
      try { ws.close(1013, 'Dormant mux session: relaunch it from Projects or run mux ' + name + ' locally.'); } catch {}
      return;
    }
    if (!known) {
      try { ws.close(1013, 'Mux session does not exist. Create it explicitly before attaching.'); } catch {}
      return;
    }
    const id = 'c' + (++_cid);
    const st = sessionState(name);
    const client = { id, ws, term: null, hosted: true, vcols, vrows, sbWait: true, wentLive: false, sbTok: ++_cid, q: [], qBytes: 0, deviceId, label, visible: true, lastActive: Date.now(), connAt: Date.now() };
    st.clients.set(id, client);
    if (!requestSessionScrollback(name, st, client)) {                   // one snapshot shared by every concurrent waiter
      removeViewer(name, st, client);
      try { ws.close(1013, 'PC mux host offline'); } catch {}
      return;
    }
    // Scrollback slow/large: relieve the wait WITHOUT blanking the screen. If live output was
    // buffered, flow it now (a TUI repaints). If the session is idle (nothing buffered), leave the
    // screen as-is and keep waiting â€” wentLive stays false so the sb reply (or the next live byte)
    // still paints it. Blanking here and then dropping the late sb was the black-screen bug.
    client.sbTimer = setTimeout(() => {
      if (!client.sbWait) return;
      client.sbWait = false;
      if (client.q && client.q.length) {
        client.wentLive = true;
        st.sbWaiters.delete(client.id);
        if (sendViewer(name, st, client, CLEAR_SCREEN, true)) {
          for (const q of client.q) if (!sendViewer(name, st, client, q, true)) break;
        }
        client.q = []; client.qBytes = 0;
      }
    }, HOST_SB_WAIT_MS);
    recompute(name);
    // A viewer that attaches mid-lease must not be told "you may type" by silence. Replay muxd's
    // last notice verbatim; if the relay has none, it says nothing rather than inventing a state.
    {
      const pending = leaseConduit.cachedNotice(name);
      if (pending) { try { ws.send('L' + pending); } catch {} }
    }
    ws.on('message', m => {
      const s = m.toString();
      if (s[0] === 'i') {
        rememberHostInputOrigin(name + ':', client.id);
        if (!sendHost({ t: 'i', s: name, channelId: client.id,
                        d: Buffer.from(s.slice(1), 'utf8').toString('base64') })) {
          try { ws.close(1013, 'PC mux host offline'); } catch {}
          return;
        }
        client.lastActive = Date.now(); return;
      }
      // 'I' signed input, 'L' signed lease op. The relay forwards the client's exact bytes: it has
      // no key to re-sign with, so any edit here would invalidate the proof rather than forge it.
      if (s[0] === 'I' || s[0] === 'L') {
        const raw = s.slice(1);
        const out = s[0] === 'I'
          ? (leaseConduit.forwardSignedInput(name, raw) || leaseConduit.forwardDeliberateSend(name, raw))
          : leaseConduit.forwardSignedLeaseOp(name, raw);
        if (!out) return;                                   // unproven frame: dropped, never repaired
        if (out.t === 'i') {
          const intentId = String(out.auth && out.auth.intentId || '');
          rememberHostInputOrigin(name + ':' + intentId, client.id);
        }
        if (out.t === 'i' && !hostSupportsCap('inputDurable')) {
          const bodyB64 = out.auth && out.auth.bodyB64;
          if (!bodyB64) return;
          delete out.auth;
          out.d = bodyB64;
        }
        if (!sendHost(out)) { try { ws.close(1013, 'PC mux host offline'); } catch {} return; }
        client.lastActive = Date.now(); return;
      }
      handleClientMsg(name, client, s);
    });
    client.ka = setInterval(() => { if (ws.readyState === 1) { try { ws.ping(); } catch {} } }, 25000);
    ws.on('close', () => { removeViewer(name, st, client); recompute(name); });
    return;
  }

  try { ws.close(1013, 'PC mux host offline'); } catch {}
});

const PORT = +process.env.PORT || 7682;
// 0.0.0.0: the PC's muxd dials us directly over the LAN (ws://<vps>:7682/host, token-gated; ufw scopes
// the port to the LAN). Loopback-trust semantics are unchanged â€” a LAN caller is NOT trusted-local and
// still hits the hl-auth owner gate for everything except /host-with-token.
server.listen(PORT, '0.0.0.0', () => console.log('multiplex-app on 0.0.0.0:' + PORT));

// --- ops alerts: push on degraded-health edge transitions (r.1.17) ---
// Secret (ntfy topic URL) lives in /etc/multiplex-app.env as MUX_ALERT_NTFY_URL.
// The topic IS the credential — never committed, never logged.
if (process.env.MUX_ALERT_NTFY_URL) {
  startHealthAlerts({
    notifier: createNotifier({ url: process.env.MUX_ALERT_NTFY_URL }),
    getHealth: healthSnapshot,
  });
}

// ---- RESTART DRAIN: a deploy must be invisible, not a black hole --------------------------------
// A deploy sends SIGTERM. With no handler node dies mid-frame: every socket is reset, the browser sees
// an abnormal 1006 and walks its exponential backoff, and a live terminal looks dead for ~10s. Instead
// we drain: refuse new upgrades, flush the only in-memory-dirty durable state, close every viewer with
// 1012 "restarting" (index.html maps that to a 500ms reconnect fuse + a toast), and close the muxd host
// link LAST so the PC never learns the viewers are gone before they actually are. Everything is torn
// down so the loop empties on its own; the 5s timer is an unref'd backstop, not the expected path.
function flushDurableStateForRestart() {
  // Every acknowledged mutation already committed through durable-state.js before it was published in
  // memory — pins are the one value allowed to lag behind (coalesced by a 20s retry timer), so they are
  // the only thing a restart can actually lose.
  if (!_pinsDirty) return;
  try { savePins(new Map(pins)); }
  catch (error) { console.error('[drain] pin flush failed: ' + String(error && error.message || error)); }
}
function drainForRestart(signal) {
  if (draining) return;
  draining = true;
  setTimeout(() => process.exit(0), 5000).unref();   // unref'd: never hold the loop open just to die
  console.log(`[drain] ${signal}: refusing upgrades, closing viewers with 1012 restarting`);

  try { server.close(); } catch {}                   // stop listening; already-upgraded sockets survive
  try { server.closeIdleConnections(); } catch {}    // idle keep-alive HTTP would otherwise pin the loop
  flushDurableStateForRestart();
  clearInterval(_pinRetryTimer);
  if (_probeBootTimer) clearTimeout(_probeBootTimer);
  if (_probeTimer) clearInterval(_probeTimer);

  const viewers = new Set(wss.clients);
  for (const st of sessions.values()) {
    if (st.sbRequestTimer) { clearTimeout(st.sbRequestTimer); st.sbRequestTimer = null; }
    for (const client of st.clients.values()) {
      if (client.ka) { clearInterval(client.ka); client.ka = null; }
      viewers.add(client.ws);
    }
  }
  for (const ws of viewers) { try { ws.close(1012, 'restarting'); } catch {} }

  const host = hostWs;
  for (const ws of wssHost.clients) if (ws !== host) { try { ws.close(1012, 'restarting'); } catch {} }
  if (host) { try { host.close(1012, 'restarting'); } catch {} }   // host link closed last, always

  // grace for those close frames to reach the wire, then force the rest down so the loop can empty
  setTimeout(() => {
    for (const ws of viewers) { try { ws.terminate(); } catch {} }
    for (const ws of wssHost.clients) { try { ws.terminate(); } catch {} }
    try { wss.close(); } catch {}
    try { wssHost.close(); } catch {}
  }, 750).unref();
}
process.on('SIGTERM', () => drainForRestart('SIGTERM'));
// Windows cannot deliver SIGTERM to a running process at all — libuv turns kill('SIGTERM') into
// TerminateProcess, so the handler above provably never runs there (even self-signalling just dies).
// Production is Linux, but the dev machine is not, and a drain nobody can test is a drain that rots.
// So: a second door, opened only under MUX_TEST_MODE and only over an IPC channel the parent had to
// create at spawn time — in a deploy there is no channel and no test mode, so it cannot be reached.
if (TEST_MODE && process.send) {
  process.on('message', m => {
    if (!m || m.t !== 'drain') return;
    try { process.channel.unref(); } catch {}   // else the IPC handle alone keeps the loop alive forever
    drainForRestart('SIGTERM');
  });
}
