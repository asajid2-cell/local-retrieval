(function installMuxResumePicker(global) {
  'use strict';

  // Resume-from-archive picker (phone). Reads the chat catalogue the desktop bridge pushed
  // (GET /api/archive-index), filters it in the browser, and on a tap asks the PC to bring that chat
  // back with the EXISTING intent-fenced `startmux` app-command.
  //
  // AUTHORITY: nothing in this file authorizes anything. `startmux` is authorized only by the signed
  // session.create principal proof the desktop bridge mints and muxd verifies; the relay carries that
  // proof through unchanged. The intentId this file rides on has exactly one meaning — collapsing a
  // double-tap into one resume. It is a dedupe key, never a capability.
  //
  // The picker also never sends a command line, an exe, or a local path: the row it holds is opaque
  // ids plus display text, and the payload is {type, sessionId, tool, muxName} and nothing else.

  var DEFAULTS = { pollIntervalMs: 1200, pollTimeoutMs: 60000, offlineTimeoutMs: 4000 };

  // Mirrors the relay's strictMuxName (server.js:118): [A-Za-z0-9_.-], 48 chars, and the sanitized
  // form must EQUAL the raw — the relay refuses a stray character rather than mangling it.
  var SAFE_MUX = /^[A-Za-z0-9_.-]{1,48}$/;

  function sanitizeMuxName(value) {
    return String(value == null ? '' : value).replace(/[^A-Za-z0-9_.-]/g, '').slice(0, 48);
  }

  // A pushed muxName that did not survive the relay's normalization arrives as '' — deriving a valid
  // one from the opaque id keeps that chat resumable instead of dead-ending the tap on a name the user
  // never chose and cannot see.
  function muxNameFor(chat) {
    var c = chat || {};
    var pushed = String(c.muxName == null ? '' : c.muxName).trim();
    if (SAFE_MUX.test(pushed)) return pushed;
    // The 48-char ceiling is a length rule, not a verdict on the name: a long-but-clean name is the
    // user's own, so trim it rather than swap in a derived one they never chose and cannot recognize.
    if (/^[A-Za-z0-9_.-]+$/.test(pushed)) return pushed.slice(0, 48);
    var derived = sanitizeMuxName((c.tool || 'chat') + '-' + (c.id || ''));
    // A derived name of bare separators ('-') would resume a tab the user cannot recognize and ask the
    // PC for a session literally named '-'; without a letter or digit there is no name to resume.
    if (!/[A-Za-z0-9]/.test(derived)) return '';
    return SAFE_MUX.test(derived) ? derived : '';
  }

  // The relay only accepts claude|codex; anything else must travel as '' so the row still resumes on
  // its sessionId instead of being refused with a 400 the user cannot act on.
  function toolFor(chat) {
    var tool = String((chat && chat.tool) || '').trim().toLowerCase();
    return tool === 'claude' || tool === 'codex' ? tool : '';
  }

  function haystack(chat) {
    var c = chat || {};
    return [c.title, c.tool, c.cwd, c.workspaceLabel, c.muxName]
      .map(function (v) { return String(v == null ? '' : v); })
      .join(' ')
      .toLowerCase();
  }

  // Every whitespace-separated token must appear in the row: "cortex claude" narrows, it never widens.
  // Filtering is client-side on purpose — the whole index is already in hand, so typing costs no round
  // trip and the search string never leaves the phone.
  function filterChats(chats, query) {
    var rows = Array.isArray(chats) ? chats.slice() : [];
    var tokens = String(query == null ? '' : query).toLowerCase().split(/\s+/).filter(Boolean);
    if (!tokens.length) return rows;
    return rows.filter(function (chat) {
      var hay = haystack(chat);
      return tokens.every(function (token) { return hay.indexOf(token) >= 0; });
    });
  }

  // Freshness follows the projection's doctrine: rows survive a relay restart, the claim that the app
  // is ANSWERING does not. Every branch produces a visible line — the picker is never silent about why
  // a resume might not happen now.
  function freshness(state) {
    var s = state || {};
    var count = Array.isArray(s.chats) ? s.chats.length : 0;
    if (s.error) return { state: 'error', live: false, label: s.error };
    if (!s.loaded) return { state: 'loading', live: false, label: 'Loading your chat archive…' };
    if (!count) return {
      state: 'empty', live: false,
      label: 'No resumable chats yet — open the desktop app so it can push your archive.',
    };
    if (s.appLive === true) return {
      state: 'live', live: true,
      label: '● desktop app live — a resume starts now',
    };
    return {
      state: 'offline', live: false,
      label: '○ app offline — a resume will queue until the desktop app is next open',
    };
  }

  function createPicker(deps) {
    var d = deps || {};
    var base = d.base || '';
    var fetchFn = d.fetch || (global.fetch ? global.fetch.bind(global) : null);
    var postIntent = d.postIntent || global.postIntent;
    var sleep = d.sleep || function (ms) { return new Promise(function (r) { setTimeout(r, ms); }); };
    var now = d.now || function () { return Date.now(); };
    var pollIntervalMs = d.pollIntervalMs || DEFAULTS.pollIntervalMs;
    var pollTimeoutMs = d.pollTimeoutMs || DEFAULTS.pollTimeoutMs;
    var offlineTimeoutMs = d.offlineTimeoutMs || DEFAULTS.offlineTimeoutMs;

    var state = {
      chats: [], query: '', host: '', updatedAt: 0, ageMs: null,
      appLive: false, loaded: false, error: '',
    };

    async function load() {
      var res;
      try {
        res = await fetchFn(base + '/api/archive-index');
      } catch (error) {
        state.appLive = false;
        state.error = 'could not reach the relay: ' + ((error && error.message) || error);
        return state;
      }
      if (!res || !res.ok) {
        state.appLive = false;
        state.error = 'could not load your chat archive (HTTP ' + ((res && res.status) || 0) + ')';
        return state;
      }
      var body = (await res.json()) || {};
      // `resumable:false` is the app saying it could not build a trusted resume for that chat; the
      // picker must not offer it, because tapping it would be a promise the PC cannot keep.
      state.chats = (Array.isArray(body.chats) ? body.chats : [])
        .filter(function (c) { return c && c.id && c.resumable !== false; });
      state.host = String(body.host || '');
      state.updatedAt = Number(body.updatedAt) || 0;
      state.ageMs = body.ageMs == null ? null : Number(body.ageMs);
      state.appLive = body.appLive === true;
      state.loaded = true;
      state.error = '';
      return state;
    }

    function visible() { return filterChats(state.chats, state.query); }
    function setQuery(query) { state.query = String(query == null ? '' : query); return visible(); }

    // The relay answers a refused startmux with {error, detail} — the detail is the sentence that
    // actually tells the user what to do ("local copy is already running" + which chat holds it), so
    // it must reach the screen rather than being flattened into a status code.
    async function refusalText(res) {
      var body = null;
      try { body = await res.json(); } catch (error) { body = null; }
      var parts = [];
      if (body && body.error) parts.push(String(body.error));
      if (body && body.detail) parts.push(String(body.detail));
      if (!parts.length) parts.push('HTTP ' + ((res && res.status) || 0));
      return parts.join(' — ');
    }

    async function poll(id, deadlineMs) {
      var until = now() + deadlineMs;
      var last = { status: 'pending', detail: '' };
      while (now() < until) {
        await sleep(pollIntervalMs);
        try {
          var res = await fetchFn(base + '/api/app-commands/' + encodeURIComponent(id));
          if (res && res.ok) {
            var body = (await res.json()) || {};
            last = { status: String(body.status || ''), detail: String(body.detail || '') };
            if (last.status === 'done' || last.status === 'failed') return last;
          }
        } catch (error) { /* a transient poll miss is not an outcome; keep asking until the deadline */ }
      }
      return last;
    }

    // One tap. Deliberately NOT guarded by an in-flight lock: a double-tap must reach the relay twice
    // and be collapsed THERE, because the intent journal — not the button's disabled state — is what
    // makes this idempotent across a reload, a flaky retry, or a second phone.
    async function resume(chat) {
      var muxName = muxNameFor(chat);
      if (!muxName) return { state: 'failed', muxName: '', detail: 'this chat has no resumable session name' };
      var payload = {
        type: 'startmux',
        sessionId: String((chat && chat.id) || ''),
        tool: toolFor(chat),
        muxName: muxName,
      };
      var res;
      try {
        res = await postIntent(base + '/api/app-commands', payload, 'resume');
      } catch (error) {
        return { state: 'failed', muxName: muxName, detail: 'could not queue the resume: ' + ((error && error.message) || error) };
      }
      if (!res.ok) {
        return { state: 'failed', muxName: muxName, status: res.status, detail: await refusalText(res) };
      }
      var queued = (await res.json()) || {};
      // Offline gets a short deadline so the UI says "queued" quickly instead of spinning for a minute
      // at a PC that is not listening. The command itself stays queued either way.
      var outcome = await poll(queued.id, state.appLive ? pollTimeoutMs : offlineTimeoutMs);
      var result = {
        muxName: muxName,
        id: queued.id,
        intentId: queued.intentId || '',
        deduplicated: queued.deduplicated === true,
        status: outcome.status,
        detail: outcome.detail,
      };
      if (outcome.status === 'done') {
        result.state = 'done';
        result.detail = outcome.detail || 'mux session started';
        if (typeof d.loadSessions === 'function') {
          // Refresh the tab strip AND select the freshly hosted tab — the resume is only finished when
          // the user is looking at the session, not when the command says done.
          try { await d.loadSessions(muxName); } catch (error) { /* the tab poll retries on its own */ }
        }
        return result;
      }
      if (outcome.status === 'failed') {
        result.state = 'failed';
        result.detail = outcome.detail || 'the PC refused the resume';
        return result;
      }
      result.state = 'queued';
      result.detail = state.appLive
        ? 'still waiting on the PC — the resume stays queued and starts when the app answers'
        : 'queued — the desktop app is offline; this chat resumes when it is next open';
      return result;
    }

    return {
      state: state,
      load: load,
      visible: visible,
      setQuery: setQuery,
      resume: resume,
      freshness: function () { return freshness(state); },
    };
  }

  // The only DOM-touching part. Everything above is pure or dependency-injected so the logic can be
  // driven headless; this half is asserted structurally.
  function install(host) {
    var h = host || {};
    var doc = h.document || global.document;
    if (!doc) return null;
    var $ = h.$ || function (sel) { return doc.querySelector(sel); };
    var dlg = $('#resumedlg');
    var list = $('#resumelist');
    var box = $('#resumeq');
    if (!dlg || !list || !box) return null;
    var liveEl = $('#resumelive');
    var statusEl = $('#resumestatus');

    var picker = createPicker({
      base: h.base || '',
      fetch: h.fetch || (global.fetch ? global.fetch.bind(global) : null),
      postIntent: h.postIntent || global.postIntent,
      loadSessions: h.loadSessions,
    });

    function setStatus(message, tone) {
      if (!statusEl) return;
      statusEl.textContent = message || '';
      statusEl.className = 'resumestatus' + (tone ? ' ' + tone : '');
    }

    function render() {
      var fresh = picker.freshness();
      if (liveEl) { liveEl.textContent = fresh.label; liveEl.className = 'resumelive ' + fresh.state; }
      var rows = picker.visible();
      list.innerHTML = '';
      if (!rows.length) {
        var empty = doc.createElement('p');
        empty.className = 'hint';
        empty.textContent = picker.state.chats.length
          ? 'No chat matches “' + picker.state.query + '”.'
          : 'Nothing to resume yet.';
        list.appendChild(empty);
        return;
      }
      rows.forEach(function (chat) {
        var row = doc.createElement('button');
        row.type = 'button';
        row.className = 'act wide resumerow';
        row.dataset.chatId = chat.id;
        var title = doc.createElement('span');
        title.className = 'resumetitle';
        title.textContent = chat.title || chat.id;
        var meta = doc.createElement('span');
        meta.className = 'resumemeta';
        meta.textContent = [chat.tool, chat.workspaceLabel || chat.cwd].filter(Boolean).join(' · ');
        row.appendChild(title);
        row.appendChild(meta);
        row.onclick = function () { tap(chat, row); };
        list.appendChild(row);
      });
    }

    async function tap(chat, row) {
      if (row) row.classList.add('busy');
      setStatus('Resuming “' + (chat.title || chat.id) + '”…', 'busy');
      var outcome;
      try { outcome = await picker.resume(chat); }
      catch (error) { outcome = { state: 'failed', detail: (error && error.message) || String(error) }; }
      if (row) row.classList.remove('busy');
      if (outcome.state === 'done') {
        setStatus('Resumed as “' + outcome.muxName + '”', 'ok');
        if (typeof h.flash === 'function') h.flash('Resumed “' + (chat.title || chat.id) + '”');
        if (dlg.open) dlg.close();
        return outcome;
      }
      // Queued and failed both get a sentence. Silence would read as "nothing happened" when in fact
      // a command is sitting on the relay waiting for the PC.
      setStatus(
        (outcome.state === 'queued' ? 'Queued: ' : 'Could not resume: ') + (outcome.detail || 'no detail from the PC'),
        outcome.state === 'queued' ? 'warn' : 'bad'
      );
      return outcome;
    }

    async function open() {
      if (typeof h.openDialog === 'function') h.openDialog('#resumedlg'); else dlg.showModal();
      box.value = picker.state.query;
      setStatus('', '');
      render();
      await picker.load();
      render();
    }

    box.addEventListener('input', function () { picker.setQuery(box.value); render(); });
    var closeBtn = $('#resumeclose');
    if (closeBtn) closeBtn.onclick = function () { dlg.close(); };
    var openBtn = $('#resumebtn');
    if (openBtn) openBtn.onclick = open;
    return { picker: picker, open: open, render: render, tap: tap };
  }

  global.MuxResumePicker = {
    filterChats: filterChats,
    muxNameFor: muxNameFor,
    toolFor: toolFor,
    freshness: freshness,
    createPicker: createPicker,
    install: install,
  };
})(globalThis);
