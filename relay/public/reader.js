// Reader view for parked transcript pages. Deliberately a SEPARATE page from index.html: the terminal
// page is a live byte pipe, this one is a document. Nothing here touches xterm, the websocket, or the
// session list.
//
// TRUST: the relay is a courier for these pages, not an authority. Two rules follow and both are load-bearing:
//   1. Page CONTENT is plaintext relay exposure by accepted decision — we render it, we never claim it is
//      attested.
//   2. Any SUCCESS/terminal statement shown to a human must come from a bridge-signed response. The only
//      such signal the web can see is a `transcriptfetch` command reaching a terminal status, which the PC
//      bridge sets with the host credential. A 200 from the enqueue means REQUESTED, never DONE, and this
//      file never upgrades one to the other.
(function installMuxReader(global) {
  'use strict';

  const READER = {};

  // --- injectable seams (the verifier swaps these; the browser uses the globals) ---------------------
  READER.base = null;              // null => derive from location.pathname
  READER.fetch = null;             // null => global.fetch
  READER.postIntent = null;        // null => global.postIntent (intent-journal.js)
  READER.sleep = null;             // null => real setTimeout
  READER.now = null;               // null => Date.now

  const theFetch = () => READER.fetch || global.fetch;
  const thePost = () => READER.postIntent || global.postIntent;
  const now = () => (READER.now ? READER.now() : Date.now());
  const sleep = ms => (READER.sleep ? READER.sleep(ms) : new Promise(r => setTimeout(r, ms)));

  function apiBase() {
    if (typeof READER.base === 'string') return READER.base;
    const loc = global.location;
    return loc ? String(loc.pathname || '').replace(/\/[^/]*$/, '') : '';
  }
  READER.apiBase = apiBase;

  // --- roles -----------------------------------------------------------------------------------------
  const ROLE_LABEL = {
    user: 'you', assistant: 'agent', tool: 'tool call', system: 'system', other: 'note',
  };
  function normalizeRole(role) {
    const r = String(role == null ? '' : role).trim().toLowerCase();
    if (r === 'human') return 'user';
    if (r === 'ai' || r === 'model' || r === 'agent' || r === 'claude' || r === 'codex') return 'assistant';
    if (r === 'tool' || r === 'tool_use' || r === 'tool_result' || r === 'function' || r === 'function_call') return 'tool';
    if (r === 'user' || r === 'assistant' || r === 'system') return r;
    return 'other';
  }
  const roleClass = role => 'role-' + normalizeRole(role);
  const isToolMessage = message => normalizeRole(message && message.role) === 'tool';
  READER.normalizeRole = normalizeRole;
  READER.roleClass = roleClass;
  READER.isToolMessage = isToolMessage;

  // --- ordering: newest LAST, the way a chat reads -----------------------------------------------------
  // Page number descends (page 1 is newest); inside a page, ts breaks ties when present and
  // array order holds otherwise, so a page missing timestamps is never shuffled.
  function orderedMessages(pages) {
    const list = Array.isArray(pages) ? pages : [];
    const flat = [];
    list.forEach((page, pageIndex) => {
      const number = Number.isFinite(Number(page && page.page)) ? Number(page.page) : pageIndex;
      const messages = Array.isArray(page && page.messages) ? page.messages : [];
      messages.forEach((message, index) => {
        const ts = Number(message && message.ts);
        flat.push({ message, page: number, index, ts: Number.isFinite(ts) ? ts : null });
      });
    });
    flat.sort((a, b) => {
      if (a.page !== b.page) return b.page - a.page;
      if (a.ts !== null && b.ts !== null && a.ts !== b.ts) return a.ts - b.ts;
      return a.index - b.index;
    });
    return flat.map(entry => entry.message);
  }
  READER.orderedMessages = orderedMessages;

  // --- rendering ---------------------------------------------------------------------------------------
  const OFFLINE_TEXT =
    'Your PC is not reachable right now, so a fresh transcript cannot be fetched. '
    + 'Wake the PC / start MuxdSessionHost, then press Refresh. '
    + 'Anything already fetched stays readable below until it expires.';
  const EMPTY_TEXT =
    'No transcript is parked for this chat yet. Press Refresh to ask your PC for it — '
    + 'it is fetched on demand and dropped again after a few minutes.';
  const EXPIRED_TEXT =
    'This transcript has expired off the relay. Press Refresh to fetch it again.';
  READER.OFFLINE_TEXT = OFFLINE_TEXT;
  READER.EMPTY_TEXT = EMPTY_TEXT;

  const firstLine = text => {
    const line = String(text || '').split('\n').find(part => part.trim()) || '';
    return line.trim().slice(0, 80);
  };
  function stamp(ts) {
    const value = Number(ts);
    if (!Number.isFinite(value) || value <= 0) return '';
    try { return new Date(value).toLocaleString(); } catch (error) { return ''; }
  }

  function clear(mount) { mount.textContent = ''; }

  function note(doc, kind, text) {
    const node = doc.createElement('div');
    node.className = 'fallback ' + kind;
    node.textContent = text;
    return node;
  }

  // A tool call renders as <details> so it is collapsed by default — the whole point of the reader is
  // that the human reads the conversation, not the machinery, unless they ask for it.
  function renderMessage(doc, message) {
    const role = normalizeRole(message && message.role);
    const tool = role === 'tool';
    const text = String((message && message.text) == null ? '' : message.text);
    const node = doc.createElement(tool ? 'details' : 'article');
    node.className = 'msg ' + roleClass(role) + (tool ? ' tool-call' : '');
    node.setAttribute('data-role', String((message && message.role) || role));

    const head = doc.createElement(tool ? 'summary' : 'div');
    head.className = 'who';
    head.textContent = ROLE_LABEL[role] + (tool && firstLine(text) ? ' · ' + firstLine(text) : '');
    node.appendChild(head);

    const when = stamp(message && message.ts);
    if (when) {
      const time = doc.createElement('div');
      time.className = 'when';
      time.textContent = when;
      node.appendChild(time);
    }

    const body = doc.createElement('div');
    body.className = 'body';
    body.textContent = text;            // textContent, never innerHTML: transcript text is untrusted input
    node.appendChild(body);
    return node;
  }
  READER.renderMessage = renderMessage;

  function renderTranscript(doc, mount, pages) {
    clear(mount);
    const messages = orderedMessages(pages);
    if (!messages.length) {
      mount.appendChild(note(doc, 'empty', EMPTY_TEXT));
      return { count: 0, empty: true };
    }
    messages.forEach(message => mount.appendChild(renderMessage(doc, message)));
    return { count: messages.length, empty: false };
  }
  READER.renderTranscript = renderTranscript;

  function renderOffline(doc, mount, health) {
    clear(mount);
    const node = note(doc, 'offline', OFFLINE_TEXT);
    mount.appendChild(node);
    const age = health && Number(health.runningAgeMs);
    if (Number.isFinite(age) && age > 0) {
      const detail = doc.createElement('div');
      detail.className = 'fallback-detail';
      detail.textContent = 'Last seen ' + Math.round(age / 1000) + 's ago.';
      mount.appendChild(detail);
    }
    return { count: 0, offline: true };
  }
  READER.renderOffline = renderOffline;

  // Single entry point the page (and the verifier) uses: a view object decides which surface renders.
  function render(doc, mount, view) {
    const v = view || {};
    if (v.offline) return renderOffline(doc, mount, v.health);
    if (v.expired) { clear(mount); mount.appendChild(note(doc, 'expired', EXPIRED_TEXT)); return { count: 0, expired: true }; }
    return renderTranscript(doc, mount, v.pages || []);
  }
  READER.render = render;

  // --- transport ---------------------------------------------------------------------------------------
  const JSON_HEADERS = { Accept: 'application/json' };

  async function readHealth() {
    try {
      const response = await theFetch()(apiBase() + '/api/projects', { headers: JSON_HEADERS });
      if (!response || !response.ok) return { bridgeLive: false, unreachable: true };
      const body = await response.json();
      return {
        bridgeLive: !!(body && (body.bridgeLive ?? body.live)),
        appLive: !!(body && body.appLive),
        runningAgeMs: body && body.runningSyncedAt ? now() - Number(body.runningSyncedAt) : null,
      };
    } catch (error) {
      return { bridgeLive: false, unreachable: true, error: String((error && error.message) || error) };
    }
  }
  READER.readHealth = readHealth;

  async function loadPages(sessionId) {
    const url = apiBase() + '/api/transcripts/' + encodeURIComponent(sessionId);
    const first = await theFetch()(url, { headers: JSON_HEADERS });
    if (first && first.status === 404) return { pages: [], expired: true };
    if (!first || !first.ok) throw new Error('transcript HTTP ' + (first && first.status));
    const head = await first.json();
    const pages = [head];
    const rest = (Array.isArray(head.availablePages) ? head.availablePages : []).filter(p => p !== head.page);
    for (const page of rest) {
      const response = await theFetch()(url + '?page=' + encodeURIComponent(page), { headers: JSON_HEADERS });
      if (!response || !response.ok) throw new Error('transcript page ' + page + ' HTTP ' + (response && response.status));
      pages.push(await response.json());
    }
    return { pages, sessionId: head.sessionId, totalPages: head.pages, expiresAt: head.expiresAt };
  }
  READER.loadPages = loadPages;

  // Never enqueue an archive read without authorization from the trusted origin.
  // An absent mint endpoint is a refusal, not permission to fabricate an envelope.
  let mintUnavailable = false;
  async function mintPrincipalAuth(sessionId) {
    if (!mintUnavailable) {
      try {
        const response = await theFetch()(apiBase() + '/api/principal-auth', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
          body: JSON.stringify({ intent: 'archive.read', sessionId }),
        });
        if (response && response.ok) {
          const body = await response.json();
          if (body && typeof body === 'object' && Object.keys(body).length) return body;
        }
        if (response && response.status === 404) mintUnavailable = true;
      } catch (error) { mintUnavailable = true; }
    }
    return null;
  }
  READER.mintPrincipalAuth = mintPrincipalAuth;

  // Terminal truth ONLY. `null` means we never saw a bridge-signed outcome — the caller must not say "done".
  // The durable intent id is preferred: an accepted transcriptfetch is journaled, so by-intent polling
  // still resolves the SAME command after a reload, when the in-memory command id is gone. Falling back
  // to the command id keeps the reader usable against a relay that has not learned the by-intent route.
  async function pollCommand(id, intentId, timeoutMs, intervalMs) {
    if (intentId && typeof global.pollIntent === 'function') {
      const record = await global.pollIntent(intentId, {
        base: apiBase(), fetch: theFetch(), timeoutMs: Number(timeoutMs) || 90000,
        intervalMs: Number(intervalMs) || 1200, now, sleep,
      });
      return record && (record.status === 'done' || record.status === 'failed') ? record : null;
    }
    const started = now();
    const limit = Number(timeoutMs) || 90000;
    const step = Number(intervalMs) || 1200;
    while (now() - started < limit) {
      await sleep(step);
      let record = null;
      try {
        const response = await theFetch()(apiBase() + '/api/app-commands/' + encodeURIComponent(id), { headers: JSON_HEADERS });
        if (response && response.ok) record = await response.json();
      } catch (error) { record = null; }
      if (record && (record.status === 'done' || record.status === 'failed')) return record;
    }
    return null;
  }
  READER.pollCommand = pollCommand;

  // On a reload, intent-journal.js is loaded before reader.js. Resolve every accepted transcript command
  // before rendering a new fetch request, so a completed command remains pollable by its stable intentId.
  READER.recoverPending = async function () {
    if (typeof global.recoverPendingIntents !== 'function') return [];
    return global.recoverPendingIntents({ base: apiBase(), fetch: theFetch() });
  };
  READER.recoverPending();

  async function refresh(sessionId, options) {
    const opts = options || {};
    if (!sessionId) return { ok: false, error: 'no session id' };
    const health = await readHealth();
    if (!health.bridgeLive) return { ok: false, offline: true, health };   // no enqueue while the bridge is dark

    const principalAuth = await mintPrincipalAuth(sessionId);
    if (!principalAuth) return { ok: false, error: 'Transcript authorization unavailable; no fetch was requested.' };
    let response;
    try {
      response = await thePost()(apiBase() + '/api/app-commands',
        { type: 'transcriptfetch', sessionId: String(sessionId), principalAuth },
        'transcript-read');
    } catch (error) {
      return { ok: false, error: String((error && error.message) || error) };
    }
    if (!response || !response.ok) {
      let detail = '';
      try { detail = String(((await response.json()) || {}).error || ''); } catch (error) { detail = ''; }
      return { ok: false, status: response && response.status, error: detail || ('enqueue HTTP ' + (response && response.status)) };
    }
    const queued = await response.json();

    const settled = await pollCommand(queued && queued.id, queued && queued.intentId, opts.timeoutMs, opts.intervalMs);
    if (!settled) return { ok: false, requested: true, pending: true, id: queued && queued.id };
    if (settled.status !== 'done') return { ok: false, requested: true, failed: true, detail: String(settled.detail || '') };

    const loaded = await loadPages(sessionId);
    return { ok: true, verified: true, detail: String(settled.detail || ''), ...loaded };
  }
  READER.refresh = refresh;

  global.MuxReader = READER;
  if (typeof module === 'object' && module && module.exports) module.exports = READER;
})(typeof globalThis === 'object' ? globalThis : this);
