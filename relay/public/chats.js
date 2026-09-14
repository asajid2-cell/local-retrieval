(function installMuxChats(global) {
  'use strict';

  // Same origin as this page: nginx mounts the PC's read-only discovery API under /multiplex/ rather
  // than publishing the whole PC server at /remote/ (which also carries launch and co-pilot routes).
  // Unauthenticated calls come back as a JSON 401 from the edge gate, never an HTML login page.
  var DEFAULT_API = '/multiplex/pc/api/discovery';
  var PAGE_SIZE = 40;

  // The desktop app filters the list as you type on a 150 ms trailing debounce
  // (TrailingDebouncer.DefaultDelay), and reserves the whole-transcript scan for Enter because it reads
  // every transcript file. The browser used to filter only on submit, so pressing keys did nothing here
  // and the two surfaces felt like different products. These numbers keep them the same shape.
  var LIVE_FILTER_MS = 150;
  var DEEP_SEARCH_MIN = 8;
  var DEEP_SEARCH_LIMIT = 40;

  function cleanSet(values) {
    return Array.from(new Set(Array.from(values || []).map(function (v) {
      return String(v == null ? '' : v).trim();
    }).filter(Boolean)));
  }

  function queryParams(filters, offset, limit) {
    var f = filters || {};
    var params = new URLSearchParams();
    function put(name, value) { if (value !== '' && value != null && value !== false) params.set(name, String(value)); }
    put('q', String(f.q || '').trim());
    put('include', cleanSet(f.include).join(','));
    put('exclude', cleanSet(f.exclude).join(','));
    put('match', f.matchAll ? 'all' : 'any');
    put('agent', f.agent || '');
    put('date', f.date || '');
    put('minUserMessages', Number(f.minUserMessages) || 0);
    put('archived', f.archived || 'active');
    if (f.showHidden) params.set('showHidden', 'true');
    if (f.showAutomationWorkers) params.set('showAutomationWorkers', 'true');
    put('project', f.project || '');
    put('sort', f.sort || 'recent');
    if (offset != null) params.set('offset', String(Math.max(0, Number(offset) || 0)));
    if (limit != null) params.set('limit', String(Math.max(1, Number(limit) || PAGE_SIZE)));
    return params;
  }

  function statusFor(state) {
    var s = state || {};
    if (s.error) return { tone: 'error', text: 'PC archive unavailable: ' + s.error };
    if (s.loading) return { tone: 'loading', text: 'Loading chats from your PC...' };
    if (s.coverage && s.coverage.complete === false) {
      return {
        tone: 'loading',
        text: s.coverage.message || (
          'Search is partial: ' + (Number(s.coverage.indexedFiles) || 0)
          + ' of ' + (Number(s.coverage.totalFiles) || 0) + ' transcript files indexed.'
        ),
      };
    }
    if (!s.total) return { tone: 'online', text: 'PC archive connected. No chats match these filters.' };
    return { tone: 'online', text: 'PC archive connected. ' + s.total + ' matching chat' + (s.total === 1 ? '' : 's') + '.' };
  }

  function formatDate(value) {
    var date = new Date(value || '');
    if (!Number.isFinite(date.getTime())) return '';
    return new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', year: 'numeric' }).format(date);
  }

  function createController(deps) {
    var d = deps || {};
    var fetchFn = d.fetch || (global.fetch && global.fetch.bind(global));
    var apiBase = d.apiBase || global.MUX_DISCOVERY_BASE || DEFAULT_API;
    var state = {
      filters: {
        q: '', include: new Set(), exclude: new Set(), matchAll: false,
        agent: '', date: '', minUserMessages: 0, showHidden: false, showAutomationWorkers: false,
        archived: 'active', project: '', sort: 'recent',
      },
      rows: [], facets: { tags: [], phrases: [], projects: [], hidden: 0 },
      offset: 0, limit: d.limit || PAGE_SIZE, total: 0, hasMore: false,
      loading: false, error: '', coverage: null, generation: 0,
      // The full-transcript scan is a SEPARATE, explicitly requested result set. It is never folded into
      // `rows` because the two answer different questions ("what matches the filters" vs "what else
      // contains this phrase"), and merging them would make `total` and the pager lie.
      deep: { query: '', count: 0, rows: [], ran: true, loading: false, error: '', tooShort: false, unsupported: false },
      deepGeneration: 0,
    };

    async function getJson(path, params) {
      var url = apiBase + path;
      var qs = params && params.toString();
      if (qs) url += '?' + qs;
      var response = await fetchFn(url, { headers: { Accept: 'application/json' } });
      if (!response || !response.ok) {
        var failure = new Error('HTTP ' + ((response && response.status) || 0));
        // Keep the status: a PC build that predates a route answers 404, and that has to be told apart
        // from "the search ran and found nothing".
        failure.status = (response && response.status) || 0;
        throw failure;
      }
      return (await response.json()) || {};
    }

    async function load(reset) {
      if (reset) state.offset = 0;
      var generation = ++state.generation;
      state.loading = true;
      state.error = '';
      if (d.onChange) d.onChange(state);
      try {
        var params = queryParams(state.filters, state.offset, state.limit);
        var facetParams = queryParams(state.filters, null, null);
        var values = await Promise.all([
          getJson('/chats', params),
          getJson('/facets', facetParams),
        ]);
        if (generation !== state.generation) return state;
        var page = values[0];
        var facets = values[1];
        state.rows = Array.isArray(page.rows) ? page.rows : [];
        state.offset = Number(page.offset) || 0;
        state.limit = Number(page.limit) || state.limit;
        state.total = Number(page.total) || 0;
        state.hasMore = page.hasMore === true;
        state.coverage = page.coverage || facets.coverage || null;
        state.facets = {
          tags: Array.isArray(facets.tags) ? facets.tags : [],
          phrases: Array.isArray(facets.phrases) ? facets.phrases : [],
          projects: Array.isArray(facets.projects) ? facets.projects : [],
          hidden: Number(facets.hidden) || 0,
        };
      } catch (error) {
        if (generation !== state.generation) return state;
        state.rows = [];
        state.total = 0;
        state.hasMore = false;
        state.coverage = null;
        state.error = (error && error.message) || String(error);
      } finally {
        if (generation === state.generation) {
          state.loading = false;
          if (d.onChange) d.onChange(state);
        }
      }
      return state;
    }

    function cycleTag(tag) {
      var value = String(tag || '');
      if (state.filters.include.has(value)) {
        state.filters.include.delete(value);
        state.filters.exclude.add(value);
      } else if (state.filters.exclude.has(value)) {
        state.filters.exclude.delete(value);
      } else {
        state.filters.include.add(value);
      }
      return load(true);
    }

    function phrase(value) {
      state.filters.q = '[' + String(value || '').trim() + ']';
      clearDeep();
      return load(true);
    }

    // Enter on the search box: the phone-side equivalent of the desktop app's whole-transcript scan.
    // Every failure mode is named, because "no matches" and "the scan never ran" are different answers.
    async function deepSearch(query) {
      var q = String(query == null ? '' : query).trim();
      var generation = ++state.deepGeneration;
      state.deep = { query: q, count: 0, rows: [], ran: true, loading: false, error: '', tooShort: false, unsupported: false };
      if (q.length < DEEP_SEARCH_MIN) {
        state.deep.tooShort = q.length > 0;
        if (d.onChange) d.onChange(state);
        return state;
      }
      state.deep.loading = true;
      if (d.onChange) d.onChange(state);
      try {
        var params = new URLSearchParams();
        params.set('q', q);
        params.set('limit', String(DEEP_SEARCH_LIMIT));
        if (state.filters.showHidden) params.set('showHidden', 'true');
        var body = await getJson('/search', params);
        if (generation !== state.deepGeneration) return state;
        state.deep.rows = Array.isArray(body.rows) ? body.rows : [];
        state.deep.count = Number(body.count) || 0;
        state.deep.ran = body.ran === true;
      } catch (error) {
        if (generation !== state.deepGeneration) return state;
        var status = Number(error && error.status) || 0;
        state.deep.unsupported = status === 404 || status === 405 || status === 501;
        state.deep.error = (error && error.message) || String(error);
      } finally {
        if (generation === state.deepGeneration) {
          state.deep.loading = false;
          if (d.onChange) d.onChange(state);
        }
      }
      return state;
    }

    // Typing again means the phrase those results belong to is gone. Dropping them here (rather than
    // leaving a stale section under a different query) is why the deep pass bumps a generation of its own.
    function clearDeep() {
      var deep = state.deep;
      if (!deep.query && !deep.loading && !deep.rows.length && !deep.error && !deep.tooShort) return state;
      state.deepGeneration += 1;
      state.deep = { query: '', count: 0, rows: [], ran: true, loading: false, error: '', tooShort: false, unsupported: false };
      if (d.onChange) d.onChange(state);
      return state;
    }

    function page(delta) {
      state.offset = Math.max(0, state.offset + delta * state.limit);
      return load(false);
    }

    return { state: state, load: load, cycleTag: cycleTag, phrase: phrase, page: page, deepSearch: deepSearch, clearDeep: clearDeep };
  }

  var START_SAFE_MUX = /^[A-Za-z0-9_.-]{1,48}$/;
  var START_INVALID_SUBFOLDER = /[\\\/:*?"<>|]/;
  var START_DEFAULT_POLL_INTERVAL = 800;
  var START_DEFAULT_POLL_TIMEOUT = 60000;

  // Metadata mutations are explicit desired-state commands. The browser never changes the row
  // optimistically: the authoritative discovery projection is reloaded only after a terminal ACK.
  var MANAGEMENT_POLL_INTERVAL = 800;
  var MANAGEMENT_POLL_TIMEOUT = 60000;
  var OPAQUE_ID = /^[A-Za-z0-9._-]+$/;
  function managementId(value) {
    var id = String(value == null ? '' : value).trim();
    return id && OPAQUE_ID.test(id) ? id : '';
  }
  function managementPayload(type, chat, value) {
    var row = chat || {};
    var sessionId = managementId(row.id);
    var revision = managementId(row.revision);
    var collectionId = managementId(value && value.collectionId);
    var collectionRevision = managementId(value && value.collectionRevision);
    var branchTypes = ['checkpointcreate', 'branchcreate', 'checkpointrename', 'checkpointdelete', 'checkpointspawn'];
    var supported = ['reclaim', 'setfavorite', 'setapptitle', 'archive', 'settag', 'setphrases', 'addtocollection', 'removefromcollection'].concat(branchTypes);
    if (branchTypes.indexOf(type) >= 0) {
      var source = type === 'checkpointcreate' || type === 'branchcreate';
      var object = source ? row : ((value && value.snapshot) || value || row);
      var objectId = managementId(object && object.id != null ? object.id : object && object.snapshotId);
      var objectRevision = managementId(object && object.revision != null ? object.revision : object && object.expectedRevision);
      if (!objectId) return { error: source ? 'This chat has no safe identity.' : 'This checkpoint has no safe snapshot identity.' };
      if (!objectRevision) return { error: source ? 'This chat has no authoritative revision.' : 'This checkpoint has no authoritative revision.' };
      var branchPayload = { type: type, expectedRevision: objectRevision };
      if (source) {
        branchPayload.sessionId = objectId;
        var branchTool = String(row.tool || '').trim().toLowerCase();
        if (branchTool !== 'claude' && branchTool !== 'codex') return { error: 'This chat has no supported tool.' };
        branchPayload.tool = branchTool;
      } else {
        branchPayload.snapshotId = objectId;
      }
      if (type === 'checkpointcreate' || type === 'checkpointrename') {
        if (!value || typeof value.name !== 'string' || !value.name.trim()) return { error: 'Checkpoint name must not be empty.' };
        branchPayload.name = value.name.trim();
      }
      return { payload: branchPayload };
    }
    if (type === 'addtocollection' || type === 'removefromcollection') {
      if (!collectionId || !collectionRevision) return { error: 'Choose a current collection with an authoritative revision.' };
      if (!sessionId) return { error: 'This result has no safe chat identity.' };
      var membershipPayload = { type: type, sessionId: sessionId, collectionId: collectionId, expectedCollectionRevision: collectionRevision };
      if (row.tool === 'claude' || row.tool === 'codex') membershipPayload.tool = String(row.tool).toLowerCase();
      return { payload: membershipPayload };
    }
    if (supported.indexOf(type) < 0) return { error: 'Unsupported chat action.' };
    if (!sessionId) return { error: 'This result has no safe chat identity.' };
    if (!revision) return { error: 'This result has no authoritative revision.' };
    var payload = { type: type, sessionId: sessionId, expectedRevision: revision };
    if (type === 'reclaim') {
      if (value !== true) return { error: 'Explicit cleanup confirmation is required.' };
      if (row.tool !== 'claude' && row.tool !== 'codex') return { error: 'An authoritative chat tool is required.' };
      payload.confirmed = true;
    } else if (type === 'setfavorite') {
      if (typeof value !== 'boolean') return { error: 'Favorite state must be explicit.' };
      payload.favorite = value;
    } else if (type === 'archive') {
      if (typeof value !== 'boolean') return { error: 'Archive state must be explicit.' };
      payload.archived = value;
    } else if (type === 'setapptitle') {
      if (typeof value !== 'string') return { error: 'App title must be a string.' };
      payload.title = value.trim();
    } else if (type === 'setphrases') {
      if (!Array.isArray(value) || value.some(function (item) { return typeof item !== 'string'; }))
        return { error: 'Phrases must be an array of strings.' };
      payload.phrases = cleanSet(value);
    } else {
      if (!value || typeof value !== 'object' || typeof value.tag !== 'string' || typeof value.enabled !== 'boolean')
        return { error: 'Tag and enabled state are required.' };
      payload.tag = value.tag.trim();
      payload.enabled = value.enabled;
      if (!payload.tag) return { error: 'Tag must not be empty.' };
    }
    var tool = String(row.tool || '').trim().toLowerCase();
    if (tool === 'claude' || tool === 'codex') payload.tool = tool;
    return { payload: payload };
  }
  function managementErrorText(response) {
    return Promise.resolve().then(async function () {
      var body = null;
      try { body = response && await response.json(); } catch (error) { body = null; }
      return [body && body.error, body && body.detail].filter(Boolean).join(' - ')
        || 'HTTP ' + ((response && response.status) || 0);
    });
  }
  function setManagementBusy(dialog, busy) {
    if (!dialog) return;
    dialog.dataset.managementBusy = busy ? 'true' : 'false';
    Array.from(dialog.querySelectorAll ? dialog.querySelectorAll('button, select, input') : []).forEach(function (control) {
      control.disabled = !!busy;
    });
  }
  function createManagement(deps) {
    var d = deps || {};
    var fetchFn = d.fetch || (global.fetch && global.fetch.bind(global));
    var postIntent = d.postIntent || global.postIntent;
    var base = d.base || '';
    var sleep = d.sleep || function (ms) { return new Promise(function (resolve) { setTimeout(resolve, ms); }); };
    var now = d.now || function () { return Date.now(); };
    var interval = Number(d.pollIntervalMs) || MANAGEMENT_POLL_INTERVAL;
    var timeout = Number(d.pollTimeoutMs) || MANAGEMENT_POLL_TIMEOUT;
    var state = { submitting: false, promise: null, key: null, lastOutcome: null };
    function notifyBusy(value) {
      state.submitting = value;
      if (typeof d.onBusy === 'function') d.onBusy(value);
    }
    function managementKey(payload) {
      return JSON.stringify(payload);
    }
    async function poll(id) {
      var deadline = now() + timeout;
      var latest = { status: 'pending', detail: '' };
      while (now() < deadline) {
        await sleep(interval);
        try {
          var response = await fetchFn(base + '/api/app-commands/' + encodeURIComponent(id), { headers: { Accept: 'application/json' } });
          if (response && response.ok) {
            var body = await response.json() || {};
            latest = { status: String(body.status || ''), detail: String(body.detail || '') };
            if (latest.status === 'done' || latest.status === 'failed') return latest;
          }
        } catch (error) { /* remain pending until the bounded deadline */ }
      }
      return { status: 'uncertain', detail: 'No authoritative ACK arrived before the deadline.' };
    }
    async function run(type, chat, value) {
      var checked = managementPayload(type, chat, value);
      if (checked.error) return { state: 'refused', detail: checked.error };
      var key = managementKey(checked.payload);
      if (state.promise) {
        if (state.key === key) return state.promise;
        return { state: 'refused', detail: 'Another metadata change is still pending. Wait for its refreshed result before trying again.' };
      }
      notifyBusy(true);
      state.key = key;
      var promise = (async function () {
        try {
          if (typeof postIntent !== 'function') throw new Error('intent transport is unavailable');
          var response = await postIntent(base + '/api/app-commands', checked.payload, 'chat-' + type);
          if (!response || !response.ok) return { state: response && response.status >= 400 && response.status < 500 ? 'refused' : 'uncertain', detail: await managementErrorText(response) };
          var queued = await response.json() || {};
          var result = await poll(queued.id);
          if (result.status === 'done') return { state: 'done', id: queued.id, intentId: queued.intentId || '', detail: result.detail || 'Favorite updated on the PC.' };
          if (result.status === 'failed') return { state: 'refused', id: queued.id, intentId: queued.intentId || '', detail: result.detail || 'The PC refused the favorite change.' };
          return { state: 'uncertain', id: queued.id, intentId: queued.intentId || '', detail: result.detail };
        } catch (error) { return { state: 'uncertain', detail: (error && error.message) || String(error) }; }
        finally {
          notifyBusy(false);
          if (state.promise === promise) {
            state.promise = null;
            state.key = null;
          }
        }
      })();
      state.promise = promise;
      return promise;
    }
    return { state: state, run: run, payload: managementPayload };
  }

  function managementStatus(outcome) {
    if (!outcome) return { tone: 'bad', text: 'No authoritative result from the PC.' };
    if (outcome.state === 'done') return { tone: 'ok', text: outcome.detail || 'Favorite updated on the PC.' };
    if (outcome.state === 'uncertain') return { tone: 'warn', text: 'Uncertain: ' + (outcome.detail || 'refresh to reconcile.') };
    if (outcome.state === 'refused') return { tone: 'bad', text: 'Refused: ' + (outcome.detail || 'the PC rejected the change.') };
    return { tone: 'bad', text: outcome.detail || 'The PC reported a failure.' };
  }

  function capText(value, limit) {
    return String(value == null ? '' : value).trim().slice(0, limit);
  }

  function rowId(row) {
    return row && row.id != null ? String(row.id).trim() : '';
  }

  function hasRow(rows, id) {
    var value = String(id || '');
    return (Array.isArray(rows) ? rows : []).some(function (row) { return rowId(row) === value; });
  }

  function startRows(value) {
    return Array.isArray(value) ? value.filter(function (row) { return rowId(row); }) : [];
  }

  function startErrorText(response) {
    return Promise.resolve().then(async function () {
      var body = null;
      try { body = response && await response.json(); } catch (error) { body = null; }
      var parts = [];
      if (body && body.error) parts.push(String(body.error));
      if (body && body.detail) parts.push(String(body.detail));
      return parts.join(' - ') || 'HTTP ' + ((response && response.status) || 0);
    });
  }

  function createStartChat(deps) {
    var d = deps || {};
    var fetchFn = d.fetch || (global.fetch && global.fetch.bind(global));
    var postIntent = d.postIntent || global.postIntent;
    var base = d.base || '';
    var discoveryBase = d.discoveryBase || d.apiBase || DEFAULT_API;
    var sleep = d.sleep || function (ms) { return new Promise(function (resolve) { setTimeout(resolve, ms); }); };
    var now = d.now || function () { return Date.now(); };
    var pollIntervalMs = Number(d.pollIntervalMs) || START_DEFAULT_POLL_INTERVAL;
    var pollTimeoutMs = Number(d.pollTimeoutMs) || START_DEFAULT_POLL_TIMEOUT;
    var navigate = d.navigate || function (muxName) {
      if (global.location) global.location.href = './?s=' + encodeURIComponent(muxName);
    };
    var state = {
      options: { decks: [], collections: [], checkpoints: [], workspaces: [], activeDeckId: '' },
      form: {
        name: '', deckId: '', collectionId: '', collection: '', checkpointId: '',
        tool: '', workspaceId: '', subfolder: '', phrase: '', launchMode: '', handoffFromId: '',
      },
      loading: false, collectionsLoading: false, submitting: false,
      error: '', lastOutcome: null, lastPayload: '',
      generation: 0, collectionGeneration: 0, submitPromise: null,
    };

    async function getJson(path, params) {
      if (typeof fetchFn !== 'function') throw new Error('browser fetch is unavailable');
      var url = discoveryBase + path;
      var query = params && new URLSearchParams(params).toString();
      if (query) url += '?' + query;
      var response = await fetchFn(url, { headers: { Accept: 'application/json' } });
      if (!response || !response.ok) throw new Error('HTTP ' + ((response && response.status) || 0));
      return (await response.json()) || {};
    }

    function resetForm() {
      state.form = {
        name: '', deckId: '', collectionId: '', collection: '', checkpointId: '',
        tool: '', workspaceId: '', subfolder: '', phrase: '', launchMode: '', handoffFromId: '',
      };
      state.error = '';
      state.lastOutcome = null;
      state.lastPayload = '';
    }

    async function loadCollections(deckId) {
      var selected = String(deckId || '').trim();
      state.form.deckId = selected;
      state.form.collectionId = '';
      state.form.collection = '';
      state.options.collections = [];
      state.options.activeDeckId = selected;
      state.collectionsLoading = true;
      var generation = ++state.collectionGeneration;
      try {
        var body = await getJson('/start/collections', { deckId: selected });
        if (generation !== state.collectionGeneration) return state;
        state.options.collections = startRows(body.rows);
        state.error = '';
      } catch (error) {
        if (generation !== state.collectionGeneration) return state;
        state.options.collections = [];
        state.error = 'Could not load collections: ' + ((error && error.message) || error);
      } finally {
        if (generation === state.collectionGeneration) state.collectionsLoading = false;
      }
      return state;
    }

    async function load() {
      var generation = ++state.generation;
      state.loading = true;
      state.error = '';
      try {
        var values = await Promise.all([
          getJson('/start/decks'),
          getJson('/start/checkpoints'),
          getJson('/start/workspaces'),
        ]);
        if (generation !== state.generation) return state;
        state.options.decks = startRows(values[0].rows);
        state.options.checkpoints = startRows(values[1].rows);
        state.options.workspaces = startRows(values[2].rows);
        var active = String(values[0].activeDeckId || '');
        if (!hasRow(state.options.decks, active)) active = rowId(state.options.decks[0]) || 'main';
        state.options.activeDeckId = active;
        state.form.deckId = active;
        state.form.collectionId = '';
        state.form.collection = '';
        var collections = await getJson('/start/collections', { deckId: active });
        if (generation !== state.generation) return state;
        state.options.collections = startRows(collections.rows);
        state.options.activeDeckId = active;
      } catch (error) {
        if (generation !== state.generation) return state;
        state.options.decks = [];
        state.options.collections = [];
        state.options.checkpoints = [];
        state.options.workspaces = [];
        state.error = 'Could not load start options: ' + ((error && error.message) || error);
      } finally {
        if (generation === state.generation) {
          state.loading = false;
          state.collectionsLoading = false;
        }
      }
      return state;
    }

    function setField(name, value) {
      var next = String(value == null ? '' : value);
      if (name === 'collectionId') {
        state.form.collectionId = next;
        if (next) state.form.collection = '';
      } else if (name === 'collection') {
        state.form.collection = capText(next, 200);
        if (state.form.collection) state.form.collectionId = '';
      } else if (Object.prototype.hasOwnProperty.call(state.form, name)) {
        state.form[name] = next;
      }
      state.error = '';
      return state.form[name];
    }

    function setCheckpoint(value) {
      state.form.checkpointId = String(value == null ? '' : value);
      if (state.form.checkpointId) {
        state.form.tool = '';
        state.form.workspaceId = '';
        state.form.subfolder = '';
      }
      state.error = '';
      return state.form.checkpointId;
    }

    function payloadFor(form) {
      var f = form || state.form;
      var checkpoint = capText(f.checkpointId, 200);
      var payload = {
        type: 'startchat',
        intentId: undefined,
        muxName: capText(f.name, 48),
        title: capText(f.name, 200),
        tool: checkpoint ? '' : capText(f.tool, 16).toLowerCase(),
        checkpointId: checkpoint,
        checkpointRevision: checkpoint ? String((state.options.checkpoints.find(function (row) { return row.id === checkpoint; }) || {}).revision || '') : '',
        collectionRevision: f.collectionId ? String((state.options.collections.find(function (row) { return row.id === f.collectionId; }) || {}).managementRevision || '') : '',
        workspaceId: checkpoint ? '' : capText(f.workspaceId, 200),
        subfolder: checkpoint ? '' : capText(f.subfolder, 200),
        deckId: capText(f.deckId, 200),
        collectionId: capText(f.collectionId, 200),
        collection: capText(f.collection, 200),
        phrase: capText(f.phrase, 200),
      };
      delete payload.intentId;
      if (f.handoffFromId) { payload.handoffFromId = f.handoffFromId; payload.launchMode = 'gateway'; }
      return payload;
    }

    function validate() {
      var f = state.form;
      var name = capText(f.name, 200);
      if (f.handoffFromId && (f.tool !== 'claude' || f.checkpointId)) return { error: 'Gateway handoff requires a new Claude chat, not a checkpoint or Codex resume.' };
      if (!START_SAFE_MUX.test(name)) return { error: 'Chat name must use 1-48 letters, numbers, ".", "_" or "-".' };
      if (!hasRow(state.options.decks, f.deckId)) return { error: 'Choose a current deck.' };
      if (f.collectionId && !hasRow(state.options.collections, f.collectionId))
        return { error: 'Choose a collection from the selected deck, or enter a new name.' };
      if (f.collectionId && capText(f.collection, 200))
        return { error: 'Choose either an existing collection or a new collection name.' };
      if (f.checkpointId) {
        if (!hasRow(state.options.checkpoints, f.checkpointId)) return { error: 'Choose a current checkpoint.' };
      } else {
        if (f.tool !== 'codex' && f.tool !== 'claude') return { error: 'Choose Codex or Claude for a blank chat.' };
        if (!hasRow(state.options.workspaces, f.workspaceId)) return { error: 'Choose a current workspace.' };
        var subfolder = capText(f.subfolder, 200);
        if (subfolder === '.' || subfolder === '..' || START_INVALID_SUBFOLDER.test(subfolder))
          return { error: 'New subfolder must be one local folder name, without path separators.' };
        if (subfolder.indexOf('/') >= 0 || subfolder.indexOf('\\') >= 0)
          return { error: 'New subfolder must be one local folder name, without path separators.' };
      }
      return { payload: payloadFor({ ...f, name: name }) };
    }

    async function poll(id) {
      var deadline = now() + pollTimeoutMs;
      var latest = { status: 'pending', detail: '' };
      while (now() < deadline) {
        await sleep(pollIntervalMs);
        try {
          var response = await fetchFn(base + '/api/app-commands/' + encodeURIComponent(id), {
            headers: { Accept: 'application/json' },
          });
          if (response && response.ok) {
            var body = (await response.json()) || {};
            latest = { status: String(body.status || ''), detail: String(body.detail || '') };
            if (latest.status === 'done' || latest.status === 'failed') return latest;
          }
        } catch (error) { /* a transient poll miss remains pending until the deadline */ }
      }
      return latest;
    }

    async function submit() {
      if (state.submitPromise) return state.submitPromise;
      var checked = validate();
      if (checked.error) {
        state.error = checked.error;
        state.lastOutcome = { state: 'invalid', detail: checked.error };
        return state.lastOutcome;
      }
      var payload = checked.payload;
      var payloadKey = JSON.stringify(payload);
      state.submitting = true;
      state.error = '';
      state.submitPromise = (async function () {
        try {
          if (state.lastOutcome && state.lastOutcome.state === 'queued'
              && state.lastOutcome.id && state.lastPayload === payloadKey) {
            var retried = await poll(state.lastOutcome.id);
            if (retried.status === 'done') {
              state.lastOutcome = {
                state: 'done', id: state.lastOutcome.id, intentId: state.lastOutcome.intentId || '',
                muxName: payload.muxName, detail: retried.detail || 'mux session started',
              };
              await navigate(payload.muxName);
              return state.lastOutcome;
            }
            if (retried.status === 'failed') {
              state.lastOutcome = {
                state: 'failed', id: state.lastOutcome.id,
                detail: retried.detail || 'The PC refused the new chat.',
              };
              state.error = state.lastOutcome.detail;
              return state.lastOutcome;
            }
            state.error = state.lastOutcome.detail;
            return state.lastOutcome;
          }

          state.lastOutcome = null;
          if (typeof postIntent !== 'function') throw new Error('intent transport is unavailable');
          var response = await postIntent(base + '/api/app-commands', payload, 'startchat');
          if (!response || !response.ok) {
            var refusal = await startErrorText(response);
            state.lastOutcome = { state: 'failed', status: response && response.status, detail: refusal };
            state.error = refusal;
            return state.lastOutcome;
          }
          var queued = (await response.json()) || {};
          var result = await poll(queued.id);
          if (result.status === 'done') {
            state.lastOutcome = {
              state: 'done', id: queued.id, intentId: queued.intentId || '',
              muxName: payload.muxName, detail: result.detail || 'mux session started',
            };
            await navigate(payload.muxName);
            return state.lastOutcome;
          }
          if (result.status === 'failed') {
            state.lastOutcome = { state: 'failed', id: queued.id, detail: result.detail || 'The PC refused the new chat.' };
            state.error = state.lastOutcome.detail;
            return state.lastOutcome;
          }
          state.lastOutcome = {
            state: 'queued', id: queued.id, intentId: queued.intentId || '',
            muxName: payload.muxName,
            detail: result.detail || 'Still waiting on the PC. The start request remains queued.',
          };
          state.lastPayload = payloadKey;
          state.error = state.lastOutcome.detail;
          return state.lastOutcome;
        } catch (error) {
          state.lastOutcome = { state: 'failed', detail: 'Could not start the chat: ' + ((error && error.message) || error) };
          state.error = state.lastOutcome.detail;
          return state.lastOutcome;
        } finally {
          state.submitting = false;
          state.submitPromise = null;
        }
      })();
      return state.submitPromise;
    }

    return {
      state: state,
      load: load,
      loadCollections: loadCollections,
      resetForm: resetForm,
      setField: setField,
      setCheckpoint: setCheckpoint,
      payloadFor: payloadFor,
      validate: validate,
      poll: poll,
      submit: submit,
    };
  }

  function install(host) {
    var h = host || {};
    var doc = h.document || global.document;
    if (!doc) return null;
    var $ = h.$ || function (selector) { return doc.querySelector(selector); };
    var list = $('#chatlist');
    var status = $('#status');
    var stateBox = $('#state');
    if (!list || !status || !stateBox) return null;

    var resumePicker = global.MuxResumePicker && global.MuxResumePicker.createPicker({
      base: h.base || '',
      fetch: h.fetch || global.fetch.bind(global),
      postIntent: h.postIntent || global.postIntent,
      pollTimeoutMs: 60000,
      offlineTimeoutMs: 4000,
    });
    var management = createManagement({
      base: h.base || '',
      fetch: h.fetch || global.fetch.bind(global),
      postIntent: h.postIntent || global.postIntent,
      sleep: h.managementSleep,
      now: h.managementNow,
      pollIntervalMs: h.managementPollIntervalMs,
      pollTimeoutMs: h.managementPollTimeoutMs,
      onBusy: function (busy) {
        var dialogs = doc.querySelectorAll ? doc.querySelectorAll('dialog.metadata-dialog, dialog.checkpoint-dialog') : [];
        Array.from(dialogs || []).forEach(function (dialog) {
          setManagementBusy(dialog, busy);
        });
      },
    });
    async function mutate(chat, article, button, type, value, successText) {
      button.disabled = true;
      rowStatus(article, 'Queuing metadata change...', 'warn');
      try {
        var outcome = await management.run(type, chat, value);
        var view = managementStatus(outcome);
        if (outcome.state === 'done') {
          await controller.load(true);
          view = { tone: 'ok', text: successText };
        }
        rowStatus(article, view.text, view.tone);
        return outcome;
      } catch (error) {
        var detail = (error && error.message) || String(error);
        rowStatus(article, 'Metadata change failed: ' + detail, 'bad');
        return { state: 'uncertain', detail: detail };
      } finally {
        button.disabled = false;
      }
    }
    async function favorite(chat, article, button) {
      return mutate(chat, article, button, 'setfavorite', chat.pinned !== true, 'Favorite updated on the PC.');
    }
    function controllerApi(path) { return (h.apiBase || global.MUX_DISCOVERY_BASE || DEFAULT_API) + path; }
    var checkpointRows = [];
    var checkpointLoading = false;
    async function refreshCheckpoints() {
      checkpointLoading = true;
      try {
        var response = await (h.fetch || global.fetch.bind(global))(controllerApi('/start/checkpoints'), { headers: { Accept: 'application/json' } });
        if (!response || !response.ok) throw new Error('HTTP ' + ((response && response.status) || 0));
        var body = await response.json();
        checkpointRows = startRows(body.rows);
      } catch (error) {
        checkpointRows = [];
        throw error;
      } finally { checkpointLoading = false; }
    }
    function checkpointLabel(row) {
      return [row.label || row.id, row.tool, row.revision].filter(Boolean).join(' - ');
    }
    function checkpointDialog(chat, article) {
      var dialog = doc.createElement('dialog'); dialog.className = 'checkpoint-dialog';
      var form = doc.createElement('form'); form.method = 'dialog'; form.className = 'startform';
      var heading = doc.createElement('h2'); text(heading, 'Manage checkpoints'); form.appendChild(heading);
      var note = doc.createElement('p'); note.className = 'startnote'; text(note, 'Checkpoint actions use the reviewed snapshot revision. Delete removes only that snapshot; the source chat and any branches stay.'); form.appendChild(note);
      var filter = doc.createElement('select');
      var own = doc.createElement('option'); own.value = 'own'; text(own, 'Checkpoints for this chat'); filter.appendChild(own);
      var all = doc.createElement('option'); all.value = 'all'; text(all, 'Show all checkpoints'); filter.appendChild(all); form.appendChild(filter);
      var listbox = doc.createElement('div'); listbox.className = 'rowchips'; form.appendChild(listbox);
      var errorBox = doc.createElement('p'); errorBox.className = 'rowstatus bad'; errorBox.hidden = true; errorBox.setAttribute('role', 'alert'); form.appendChild(errorBox);
      var actions = doc.createElement('div'); actions.className = 'startactions';
      var close = doc.createElement('button'); close.type = 'button'; text(close, 'Close'); actions.appendChild(close);
      var create = doc.createElement('button'); create.type = 'button'; create.className = 'primary'; text(create, 'Create checkpoint'); actions.appendChild(create); form.appendChild(actions);
      dialog.appendChild(form); (doc.body || list).appendChild(dialog);
      function showError(message) { errorBox.hidden = false; text(errorBox, message); }
      function currentRows() { return checkpointRows.filter(function (row) { return filter.value === 'all' || String(row.sourceSessionId || '') === rowId(chat); }); }
      function renderCheckpoints() {
        clear(listbox);
        currentRows().forEach(function (snapshot) {
          var item = doc.createElement('div'); item.className = 'rowchips';
          var label = doc.createElement('span'); text(label, checkpointLabel(snapshot)); item.appendChild(label);
          function action(type, caption) {
            var button = doc.createElement('button'); button.type = 'button'; button.className = 'metadata'; text(button, caption); button.disabled = management.state.submitting;
            button.onclick = async function () {
              if (management.state.submitting) return;
              if (type === 'checkpointdelete' && !global.confirm('Delete this snapshot only? The source chat and branches will remain.')) return;
              var name = '';
              if (type === 'checkpointrename') { name = global.prompt('Checkpoint name', snapshot.label || ''); if (name == null) return; }
              var value = type === 'checkpointrename' ? { snapshot: snapshot, name: name } : { snapshot: snapshot };
              var outcome = await mutate(snapshot, item, button, type, value, 'Checkpoint updated on the PC.');
              if (outcome.state === 'done') { await refreshCheckpoints(); renderCheckpoints(); }
              else showError(managementStatus(outcome).text);
            };
            item.appendChild(button);
          }
          action('checkpointrename', 'Rename'); action('checkpointspawn', 'Spawn saved chat'); action('checkpointdelete', 'Delete snapshot');
          listbox.appendChild(item);
        });
        if (!currentRows().length) { var empty = doc.createElement('span'); text(empty, checkpointLoading ? 'Loading checkpoints...' : 'No checkpoints found.'); listbox.appendChild(empty); }
      }
      filter.onchange = renderCheckpoints;
      create.onclick = async function () {
        var name = global.prompt('Checkpoint name'); if (name == null) return;
        var outcome = await mutate(chat, form, create, 'checkpointcreate', { name: name }, 'Checkpoint created on the PC.');
        if (outcome.state === 'done') { await refreshCheckpoints(); renderCheckpoints(); } else showError(managementStatus(outcome).text);
      };
      close.onclick = function () { if (dialog.close) dialog.close(); dialog.remove && dialog.remove(); };
      setManagementBusy(dialog, management.state.submitting);
      refreshCheckpoints().then(renderCheckpoints).catch(function (error) { showError('Could not load checkpoints: ' + ((error && error.message) || error)); renderCheckpoints(); });
      if (dialog.showModal) dialog.showModal();
    }
    function metadataDialog(chat, article) {
      var dialog = doc.createElement('dialog');
      dialog.className = 'metadata-dialog';
      var form = doc.createElement('form');
      form.method = 'dialog';
      var title = doc.createElement('h2'); text(title, 'Edit chat metadata'); form.appendChild(title);
      function field(label, value, name) {
        var wrap = doc.createElement('label'); wrap.className = 'field';
        var caption = doc.createElement('span'); text(caption, label); wrap.appendChild(caption);
        var input = doc.createElement('input'); input.name = name; input.value = value; wrap.appendChild(input); form.appendChild(wrap); return input;
      }
      var appTitle = chat.customTitle !== undefined ? chat.customTitle : chat.appTitle;
      if (appTitle === undefined) appTitle = chat.title || '';
      var titleInput = field('App title (blank clears)', appTitle || '', 'title');
      var tagInput = field('Tag to add or remove', '', 'tag');
      var phrasesInput = field('Phrases (comma separated, blank clears)', (chat.phrases || []).join(', '), 'phrases');
      var dialogError = doc.createElement('div'); dialogError.className = 'rowstatus bad'; dialogError.hidden = true; dialogError.setAttribute('role', 'alert'); form.appendChild(dialogError);
      var collectionField = doc.createElement('label'); collectionField.className = 'field';
      var collectionCaption = doc.createElement('span'); text(collectionCaption, 'Existing collection'); collectionField.appendChild(collectionCaption);
      var collectionSelect = doc.createElement('select'); collectionSelect.disabled = true; collectionField.appendChild(collectionSelect); form.appendChild(collectionField);
      var collectionActions = doc.createElement('div'); collectionActions.className = 'startactions';
      var addCollection = doc.createElement('button'); addCollection.type = 'button'; text(addCollection, 'Add to collection'); addCollection.disabled = true;
      var removeCollection = doc.createElement('button'); removeCollection.type = 'button'; text(removeCollection, 'Remove from collection'); removeCollection.disabled = true;
      collectionActions.appendChild(addCollection); collectionActions.appendChild(removeCollection); form.appendChild(collectionActions);
      var collectionRows = [];
      async function loadCollectionPicker() {
        try {
          var decksResponse = await (h.fetch || global.fetch.bind(global))(controllerApi('/start/decks'), { headers: { Accept: 'application/json' } });
          var decks = await decksResponse.json();
          var deckId = String(decks.activeDeckId || (decks.rows && decks.rows[0] && decks.rows[0].id) || 'main');
          var rowsResponse = await (h.fetch || global.fetch.bind(global))(controllerApi('/start/collections?deckId=' + encodeURIComponent(deckId)), { headers: { Accept: 'application/json' } });
          var body = await rowsResponse.json(); collectionRows = startRows(body.rows);
          clear(collectionSelect); collectionRows.forEach(function (row) { var option = doc.createElement('option'); option.value = row.id; text(option, row.label); collectionSelect.appendChild(option); });
          collectionSelect.disabled = collectionRows.length === 0; addCollection.disabled = collectionRows.length === 0; removeCollection.disabled = collectionRows.length === 0;
        } catch (error) { showDialogError('Could not load existing collections: ' + ((error && error.message) || error)); }
      }
      function controllerApi(path) { return (h.apiBase || global.MUX_DISCOVERY_BASE || DEFAULT_API) + path; }
      async function collectionMutation(type) {
        var picked = collectionRows.filter(function (row) { return rowId(row) === collectionSelect.value; })[0];
        if (!picked) return;
        var current = freshRow(); if (!current) { showDialogError('Refresh the chat before changing collections.'); return; }
        var outcome = await mutate(current, article, type === 'addtocollection' ? addCollection : removeCollection, type, { collectionId: picked.id, collectionRevision: picked.revision }, 'Collection membership updated on the PC.');
        if (outcome.state !== 'done') showDialogError(managementStatus(outcome).text);
        else await loadCollectionPicker();
      }
      addCollection.onclick = function () { return collectionMutation('addtocollection'); };
      removeCollection.onclick = function () { return collectionMutation('removefromcollection'); };
      function showDialogError(message) { dialogError.hidden = false; text(dialogError, message); }
      loadCollectionPicker();
      function freshRow() { return controller.state.rows.filter(function (row) { return rowId(row) === rowId(chat); })[0]; }
      async function apply(type, value, successText) {
        var current = freshRow();
        if (!current) throw new Error('The chat disappeared while saving metadata. Refresh and try again.');
        var outcome = await mutate(current, article, save, type, value, successText);
        if (outcome.state !== 'done') throw new Error(managementStatus(outcome).text);
        current = freshRow();
        if (!current && type !== 'archive') throw new Error('The PC accepted the change but the refreshed chat row is unavailable.');
        return current;
      }
      var archive = doc.createElement('label'); archive.className = 'check';
      var archiveInput = doc.createElement('input'); archiveInput.type = 'checkbox'; archiveInput.checked = chat.archived === true;
      archive.appendChild(archiveInput); var archiveText = doc.createElement('span'); text(archiveText, 'Archived'); archive.appendChild(archiveText); form.appendChild(archive);
      var actions = doc.createElement('div'); actions.className = 'startactions';
      var cancel = doc.createElement('button'); cancel.type = 'button'; text(cancel, 'Cancel'); actions.appendChild(cancel);
      var save = doc.createElement('button'); save.type = 'submit'; save.className = 'primary'; text(save, 'Save metadata'); actions.appendChild(save); form.appendChild(actions);
      dialog.appendChild(form); (doc.body || list).appendChild(dialog);
      cancel.onclick = function () { if (dialog.close) dialog.close(); dialog.remove && dialog.remove(); };
      form.onsubmit = async function (event) {
        event.preventDefault();
        if (save.disabled) return;
        save.disabled = true;
        try {
          var current = freshRow();
          if (!current) throw new Error('The chat is no longer available. Refresh and try again.');
          var desiredTitle = titleInput.value.trim();
          var currentTitle = (current.customTitle !== undefined ? current.customTitle : current.appTitle);
          if (currentTitle === undefined) currentTitle = current.title || '';
          if (desiredTitle !== String(currentTitle || '').trim()) current = await apply('setapptitle', desiredTitle, 'App title updated on the PC.');
          var desiredPhrases = phrasesInput.value.split(',').map(function (v) { return v.trim(); }).filter(Boolean);
          if (JSON.stringify(desiredPhrases) !== JSON.stringify(current.phrases || [])) current = await apply('setphrases', desiredPhrases, 'Phrases updated on the PC.');
          var tagged = tagInput.value.trim();
          if (tagged) {
            var enabled = !(current.tags || []).some(function (tag) { return tag.toLowerCase() === tagged.toLowerCase(); });
            current = await apply('settag', { tag: tagged, enabled: enabled }, 'Tag updated on the PC.');
          }
          if (archiveInput.checked !== (current.archived === true)) await apply('archive', archiveInput.checked, 'Archive state updated on the PC.');
          if (dialog.close) dialog.close();
          dialog.remove && dialog.remove();
        } catch (error) {
          showDialogError((error && error.message) || String(error));
          save.disabled = false;
        }
      };
      if (dialog.showModal) dialog.showModal();
    }
    if (resumePicker) {
      // Discovery proves the PC archive endpoint answered, not that the desktop app command poller is
      // currently open. Keep the short queue deadline and report a queued outcome honestly.
      resumePicker.state.loaded = true;
      resumePicker.state.appLive = false;
    }

    var controller = createController({
      fetch: h.fetch || global.fetch.bind(global),
      apiBase: h.apiBase,
      onChange: render,
    });

    function text(node, value) { node.textContent = value == null ? '' : String(value); return node; }
    function clear(node) { while (node.firstChild) node.removeChild(node.firstChild); }

    function chip(label, count, className, onClick) {
      var button = doc.createElement('button');
      button.type = 'button';
      button.className = 'chip' + (className ? ' ' + className : '');
      text(button, label);
      if (count != null) {
        var amount = doc.createElement('span');
        amount.className = 'count';
        text(amount, count);
        button.appendChild(amount);
      }
      button.onclick = onClick;
      return button;
    }

    function renderFacets() {
      var tags = $('#tagchips');
      var phrases = $('#phrasechips');
      var project = $('#project');
      if (tags) {
        clear(tags);
        var tagFacets = controller.state.facets.tags.slice();
        controller.state.filters.include.forEach(function (name) {
          if (!tagFacets.some(function (facet) { return String(facet.value).toLowerCase() === name.toLowerCase(); }))
            tagFacets.push({ value: name, count: 0 });
        });
        controller.state.filters.exclude.forEach(function (name) {
          if (!tagFacets.some(function (facet) { return String(facet.value).toLowerCase() === name.toLowerCase(); }))
            tagFacets.push({ value: name, count: 0 });
        });
        tagFacets.forEach(function (facet) {
          var name = String(facet.value || '');
          var mode = controller.state.filters.include.has(name) ? 'include'
            : controller.state.filters.exclude.has(name) ? 'exclude' : '';
          var button = chip(name, facet.count, mode, function () { controller.cycleTag(name); });
          button.dataset.tag = name;
          button.dataset.state = mode || 'none';
          button.setAttribute('aria-label', name + ': ' + (mode || 'not filtered'));
          tags.appendChild(button);
        });
      }
      if (phrases) {
        clear(phrases);
        controller.state.facets.phrases.slice(0, 24).forEach(function (facet) {
          phrases.appendChild(chip('[' + facet.value + ']', facet.count, 'phrase', function () {
            var q = $('#query');
            if (q) q.value = '[' + facet.value + ']';
            controller.phrase(facet.value);
          }));
        });
      }
      if (project) {
        var selected = controller.state.filters.project;
        clear(project);
        var all = doc.createElement('option');
        all.value = '';
        text(all, 'All projects');
        project.appendChild(all);
        controller.state.facets.projects.forEach(function (facet) {
          var option = doc.createElement('option');
          option.value = facet.id;
          text(option, facet.label + ' (' + facet.count + ')');
          project.appendChild(option);
        });
        if (selected && !controller.state.facets.projects.some(function (facet) { return facet.id === selected; })) {
          var missing = doc.createElement('option');
          missing.value = selected;
          text(missing, selected + ' (0)');
          project.appendChild(missing);
        }
        project.value = selected;
      }
    }

    function rowStatus(row, message, tone) {
      var target = row.querySelector('.rowstatus');
      if (!target) return;
      target.hidden = !message;
      target.className = 'rowstatus' + (tone ? ' ' + tone : '');
      text(target, message);
    }

    async function resume(chat, article, button, launchMode) {
      if (!resumePicker) {
        rowStatus(article, 'Resume controls are unavailable on this page.', 'bad');
        return;
      }
      button.disabled = true;
      rowStatus(article, 'Queuing resume...', '');
      var outcome;
      try { outcome = await resumePicker.resume(chat, launchMode); }
      catch (error) { outcome = { state: 'failed', detail: (error && error.message) || String(error) }; }
      button.disabled = chat.resumable === false;
      if (outcome.state === 'done') rowStatus(article, 'Resumed as ' + outcome.muxName + '.', 'ok');
      else if (outcome.state === 'queued') rowStatus(article, outcome.detail || 'Queued for the desktop app.', 'warn');
      else rowStatus(article, outcome.detail || 'The PC refused this resume.', 'bad');
    }

    function renderRows() {
      clear(list);
      controller.state.rows.forEach(function (chat) {
        var article = doc.createElement('article');
        article.className = 'chat';
        article.dataset.chatId = chat.id;

        var main = doc.createElement('div');
        main.className = 'chatmain';
        var titleLine = doc.createElement('div');
        titleLine.className = 'titleline';
        var tool = text(doc.createElement('span'), chat.tool || 'chat');
        tool.className = 'tool';
        var title = text(
          doc.createElement(chat.navigable === false ? 'span' : 'a'),
          chat.title || chat.id,
        );
        title.className = 'title';
        if (chat.navigable !== false) {
          title.href = './reader.html?session=' + encodeURIComponent(chat.id)
            + '&title=' + encodeURIComponent(chat.title || chat.id);
        }
        titleLine.appendChild(tool);
        titleLine.appendChild(title);
        if (chat.archived) {
          var archived = text(doc.createElement('span'), 'Archived');
          archived.className = 'pin';
          archived.title = 'Archived chat';
          titleLine.appendChild(archived);
        }
        if (chat.pinned) {
          var pin = text(doc.createElement('span'), '*');
          pin.className = 'pin';
          pin.title = 'Pinned';
          titleLine.appendChild(pin);
        }
        main.appendChild(titleLine);
        var meta = doc.createElement('div');
        meta.className = 'meta';
        text(meta, [chat.workspaceLabel, formatDate(chat.updatedAt), (Number(chat.userMsgCount) || 0) + ' your messages'].filter(Boolean).join(' - '));
        main.appendChild(meta);
        if (chat.snippet) {
          var snippet = text(doc.createElement('p'), chat.snippet);
          snippet.className = 'snippet';
          main.appendChild(snippet);
        }

        var rowChips = doc.createElement('div');
        rowChips.className = 'rowchips';
        (Array.isArray(chat.tags) ? chat.tags : []).forEach(function (tag) {
          rowChips.appendChild(chip(tag, null, '', function () { controller.cycleTag(tag); }));
        });
        (Array.isArray(chat.phrases) ? chat.phrases : []).forEach(function (phrase) {
          rowChips.appendChild(chip('[' + phrase + ']', null, 'phrase', function () {
            var q = $('#query');
            if (q) q.value = '[' + phrase + ']';
            controller.phrase(phrase);
          }));
        });
        if (rowChips.children.length) main.appendChild(rowChips);
        article.appendChild(main);

        var resumeButton = text(doc.createElement('button'), 'Resume');
        resumeButton.type = 'button';
        resumeButton.className = 'resume';
        resumeButton.disabled = chat.resumable === false;
        resumeButton.title = chat.resumable === false ? 'This chat cannot be resumed safely' : 'Resume this chat';
        resumeButton.onclick = function () { return resume(chat, article, resumeButton); };
        article.appendChild(resumeButton);
        if (chat.tool === 'claude') {
          var gatewayButton = text(doc.createElement('button'), 'Resume as Gateway');
          gatewayButton.type = 'button'; gatewayButton.className = 'resume'; gatewayButton.disabled = chat.resumable === false;
          gatewayButton.onclick = function () { return resume(chat, article, gatewayButton, 'gateway'); };
          article.appendChild(gatewayButton);
        }

        if (chat.tool === 'codex') {
          var handoffButton = text(doc.createElement('button'), 'Continue in Gateway');
          handoffButton.type = 'button'; handoffButton.className = 'metadata';
          handoffButton.onclick = async function () {
            handoffButton.disabled = true;
            try {
              var response = await global.fetch(controllerApi('/copy'), { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sessionId: chat.id, tool: 'codex', mode: 'resume' }) });
              if (!response.ok) throw new Error('Could not retrieve handoff prompt');
              var value = await response.json();
              if (value.sessionId !== chat.id || value.tool !== 'codex' || value.mode !== 'resume' || typeof value.payload !== 'string' || !value.payload) throw new Error('Handoff prompt identity did not match');
              await global.navigator.clipboard.writeText(value.payload);
              if (!global.confirm('The handoff prompt is copied. Create a new Gateway chat, then paste it there? The original Codex chat is unchanged.')) return;
              doc.dispatchEvent(new global.CustomEvent('mux-gateway-handoff', { detail: { sessionId: chat.id } }));
            } catch (error) { rowStatus(article, error.message || 'Handoff preparation failed', 'bad'); }
            finally { handoffButton.disabled = false; }
          };
          article.appendChild(handoffButton);
        }

        var favoriteButton = text(doc.createElement('button'), chat.pinned ? 'Unfavorite' : 'Favorite');
        favoriteButton.type = 'button';
        favoriteButton.className = 'favorite';
        favoriteButton.dataset.action = 'setfavorite';
        favoriteButton.title = 'Set the desired favorite state on the PC';
        favoriteButton.onclick = function () { return favorite(chat, article, favoriteButton); };
        article.appendChild(favoriteButton);

        var metadataButton = text(doc.createElement('button'), 'Metadata');
        metadataButton.type = 'button';
        metadataButton.className = 'metadata';
        metadataButton.dataset.action = 'metadata';
        metadataButton.title = 'Edit title, archive state, tags, and phrases';
        metadataButton.onclick = function () { metadataDialog(chat, article); };
        article.appendChild(metadataButton);

        var copyOptions = [['resume', 'Copy resume prompt'], ['command', 'Copy resume command'], ['path', 'Copy source path'], ['paths', 'Copy paths'], ['code', 'Copy code']];
        if (chat.tool === 'claude') copyOptions.push(['gateway', 'Copy Gateway command']);
        var copyMenu = doc.createElement('details');
        copyMenu.appendChild(text(doc.createElement('summary'), 'Copy'));
        copyOptions.forEach(function (option) {
          var copyButton = text(doc.createElement('button'), option[1]); copyButton.type = 'button';
          copyButton.onclick = async function () {
            copyButton.disabled = true;
            try {
              var mode = option[0] === 'gateway' ? 'command' : option[0];
              var response = await global.fetch(controllerApi('/copy'), { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sessionId: chat.id, tool: chat.tool, mode: mode, launchMode: option[0] === 'gateway' ? 'gateway' : undefined }) });
              if (!response.ok) {
                var refusal = await response.json().catch(function () { return null; });
                throw new Error(refusal && typeof refusal.error === 'string' && refusal.error
                  ? refusal.error : 'PC refused copy (HTTP ' + response.status + ')');
              }
              var value = await response.json();
              if (value.sessionId !== chat.id || value.tool !== chat.tool || value.mode !== mode || typeof value.payload !== 'string') throw new Error('Copy response identity did not match');
              if (!global.navigator.clipboard || !global.navigator.clipboard.writeText) throw new Error('Clipboard access unavailable; use a secure browser connection');
              await global.navigator.clipboard.writeText(value.payload);
              rowStatus(article, option[1].replace('Copy ', 'Copied ') + '.', 'ok');
            } catch (error) { rowStatus(article, error.message || 'Copy failed', 'bad'); }
            finally { copyButton.disabled = false; }
          };
          copyMenu.appendChild(copyButton);
        });
        article.appendChild(copyMenu);

        function chatBranchAction(type, caption, name) {
          var button = text(doc.createElement('button'), caption);
          button.type = 'button'; button.className = 'metadata'; button.dataset.action = type;
          button.onclick = async function () {
            if (management.state.submitting) return;
            var value = type === 'checkpointcreate' ? { name: name || global.prompt('Checkpoint name') } : {};
            if (type === 'checkpointcreate' && value.name == null) return;
            var outcome = await mutate(chat, article, button, type, value, caption + ' completed on the PC.');
            if (outcome.state === 'done') { await refreshCheckpoints(); }
          };
          article.appendChild(button);
        }
        chatBranchAction('checkpointcreate', 'Create checkpoint');
        chatBranchAction('branchcreate', 'Branch chat');
        var reclaimButton = text(doc.createElement('button'), 'Reclaim');
        reclaimButton.type = 'button'; reclaimButton.className = 'metadata'; reclaimButton.dataset.action = 'reclaim';
        reclaimButton.title = 'Stop this chat’s processes and release ownership without restarting or deleting its transcript';
        reclaimButton.onclick = async function () {
          if (management.state.submitting) return;
          if (!global.confirm('Reclaim this chat? This stops its running processes and releases ownership. It does not restart the chat or delete its transcript.')) return;
          await mutate(chat, article, reclaimButton, 'reclaim', true, 'Cleanup verified on the PC. No chat was restarted.');
        };
        article.appendChild(reclaimButton);
        var manageButton = text(doc.createElement('button'), 'Manage checkpoints');
        manageButton.type = 'button'; manageButton.className = 'metadata'; manageButton.dataset.action = 'checkpoints';
        manageButton.onclick = function () { checkpointDialog(chat, article); };
        article.appendChild(manageButton);

        var outcome = doc.createElement('p');
        outcome.className = 'rowstatus';
        outcome.hidden = true;
        outcome.setAttribute('aria-live', 'polite');
        article.appendChild(outcome);
        list.appendChild(article);
      });
    }

    function renderPager() {
      var pager = $('#pager');
      if (!pager) return;
      pager.hidden = controller.state.loading || controller.state.total <= controller.state.limit;
      var current = Math.floor(controller.state.offset / controller.state.limit) + 1;
      var pages = Math.max(1, Math.ceil(controller.state.total / controller.state.limit));
      text($('#pageinfo'), current + ' / ' + pages);
      $('#prev').disabled = controller.state.offset <= 0;
      $('#next').disabled = !controller.state.hasMore;
    }

    function deepRow(chat) {
      var article = doc.createElement('article');
      article.className = 'chat deep';
      article.dataset.chatId = chat.id;
      var main = doc.createElement('div');
      main.className = 'chatmain';
      var titleLine = doc.createElement('div');
      titleLine.className = 'titleline';
      var tool = text(doc.createElement('span'), chat.tool || 'chat');
      tool.className = 'tool';
      var title = text(doc.createElement(chat.navigable === false ? 'span' : 'a'), chat.title || chat.id);
      title.className = 'title';
      if (chat.navigable !== false) {
        title.href = './reader.html?session=' + encodeURIComponent(chat.id)
          + '&title=' + encodeURIComponent(chat.title || chat.id);
      }
      titleLine.appendChild(tool);
      titleLine.appendChild(title);
      main.appendChild(titleLine);
      if (chat.snippet) {
        var snippet = text(doc.createElement('p'), chat.snippet);
        snippet.className = 'snippet';
        main.appendChild(snippet);
      }
      var meta = doc.createElement('div');
      meta.className = 'meta';
      text(meta, [
        chat.provenance ? 'matched in ' + chat.provenance : '',
        chat.matchedTerms ? 'words: ' + chat.matchedTerms : '',
        formatDate(chat.updatedAt),
      ].filter(Boolean).join(' - '));
      main.appendChild(meta);
      article.appendChild(main);
      return article;
    }

    // Returns how many transcript-only rows it put on screen, so the empty state above cannot claim
    // "no matching chats" in the same breath as a list of matched chats.
    function renderDeepResults() {
      var section = $('#deepresults');
      var heading = $('#deepheading');
      var deepList = $('#deeplist');
      if (!section || !heading || !deepList) return 0;
      var deep = controller.state.deep;
      var visible = !!(deep.query || deep.loading || deep.error || deep.tooShort || deep.unsupported);
      section.hidden = !visible;
      if (!visible) { clear(deepList); text(heading, ''); return 0; }

      if (deep.tooShort) {
        text(heading, 'Full-transcript search needs at least ' + DEEP_SEARCH_MIN + ' characters.');
        clear(deepList);
        return 0;
      }
      if (deep.unsupported) {
        // An older PC build simply does not have the route. Saying "no matches" here would be a lie.
        text(heading, 'This PC build cannot search inside transcripts. Update the archive app on your PC.');
        clear(deepList);
        return 0;
      }
      if (deep.loading) {
        text(heading, 'Scanning full transcripts for "' + deep.query + '"...');
        clear(deepList);
        return 0;
      }
      if (deep.error) {
        text(heading, 'Full-transcript search failed: ' + deep.error);
        clear(deepList);
        return 0;
      }
      if (!deep.ran) {
        text(heading, 'No full-transcript scan ran - "' + deep.query + '" has no words to search for.');
        clear(deepList);
        return 0;
      }

      // Only chats the filtered list did NOT already show: the appends below are the point of the scan,
      // and repeating a row the user can already see above would just look like a duplicate.
      var listed = {};
      controller.state.rows.forEach(function (row) { listed[rowId(row)] = true; });
      var extras = deep.rows.filter(function (row) { return !listed[rowId(row)]; });
      if (extras.length) {
        text(heading, extras.length + ' more chat' + (extras.length === 1 ? '' : 's')
          + ' matched "' + deep.query + '" inside the transcript, beyond titles and metadata.');
      } else if (deep.count) {
        text(heading, 'Every full-transcript match for "' + deep.query + '" is already listed above.');
      } else {
        text(heading, 'No chat contains "' + deep.query + '" in its transcript.');
      }
      clear(deepList);
      extras.forEach(function (chat) { deepList.appendChild(deepRow(chat)); });
      return extras.length;
    }

    function render() {
      var view = statusFor(controller.state);
      status.className = 'status ' + view.tone;
      text(status, view.text);
      renderFacets();
      renderRows();
      var deepExtras = renderDeepResults();
      renderPager();

      stateBox.hidden = !controller.state.loading && !controller.state.error && controller.state.total > 0;
      if (controller.state.loading) {
        stateBox.innerHTML = '<strong>Loading chats</strong>Reading the archive directly from your PC.';
      } else if (controller.state.error) {
        stateBox.innerHTML = '<strong>PC archive unavailable</strong>Your PC did not answer. Check that the archive server is running there and that the reverse tunnel is up.';
      } else if (!controller.state.total && deepExtras) {
        stateBox.innerHTML = '<strong>No chats match these filters</strong>The full-transcript scan below found chats containing your phrase.';
      } else if (!controller.state.total) {
        stateBox.innerHTML = '<strong>No matching chats</strong>Clear a filter or reveal hidden one-off or automation chats.';
      }
    }

    function bindSelect(id, key, parse) {
      var element = $(id);
      if (!element) return;
      element.onchange = function () {
        controller.state.filters[key] = parse ? parse(element.value) : element.value;
        controller.load(true);
      };
    }

    var liveTimer = 0;
    function queryValue() { return ($('#query') && $('#query').value) || ''; }
    // The filter runs 150 ms after you stop typing, exactly like the app's trailing debounce: a five-key
    // burst costs ONE discovery pass. A newer keystroke supersedes the response through the controller's
    // own generation guard, so a slow answer can never overwrite a fresher one.
    function runLiveFilter() {
      liveTimer = 0;
      var value = queryValue();
      if (value === controller.state.filters.q) return;   // `input` and `search` can both fire for one clear
      controller.state.filters.q = value;
      if (value !== controller.state.deep.query) controller.clearDeep();
      controller.load(true);
    }
    function cancelLiveFilter() {
      if (liveTimer) { clearTimeout(liveTimer); liveTimer = 0; }
    }
    var queryBox = $('#query');
    if (queryBox) {
      queryBox.oninput = function () {
        cancelLiveFilter();
        liveTimer = setTimeout(runLiveFilter, LIVE_FILTER_MS);
      };
      // Clearing a type=search box fires `search`, not `input`, in most browsers; reset the list at once.
      queryBox.onsearch = function () { cancelLiveFilter(); runLiveFilter(); };
    }
    var form = $('#searchform');
    if (form) form.onsubmit = function (event) {
      event.preventDefault();
      cancelLiveFilter();
      var value = queryValue();
      var changed = value !== controller.state.filters.q;
      controller.state.filters.q = value;
      var reload = changed ? controller.load(true) : Promise.resolve(controller.state);
      // Enter = filter AND scan whole transcripts, the same pairing the desktop search box has. They run
      // together because the scan reads files and does not depend on the filtered list.
      return Promise.all([reload, controller.deepSearch(value)]);
    };
    bindSelect('#agent', 'agent');
    bindSelect('#sort', 'sort');
    bindSelect('#date', 'date');
    bindSelect('#minimum', 'minUserMessages', function (value) { return Number(value) || 0; });
    bindSelect('#project', 'project');
    bindSelect('#archived', 'archived');
    var matchAll = $('#matchall');
    if (matchAll) matchAll.onchange = function () {
      controller.state.filters.matchAll = matchAll.checked;
      controller.load(true);
    };
    var hidden = $('#hidden');
    if (hidden) hidden.onchange = function () {
      controller.state.filters.showHidden = hidden.checked;
      controller.load(true);
    };
    var automation = $('#automation');
    if (automation) automation.onchange = function () {
      controller.state.filters.showAutomationWorkers = automation.checked;
      controller.load(true);
    };
    $('#prev').onclick = function () { controller.page(-1); };
    $('#next').onclick = function () { controller.page(1); };

    render();
    controller.load(true);
    return { controller: controller, render: render };
  }

  function installStartChat(host) {
    var h = host || {};
    var doc = h.document || global.document;
    if (!doc) return null;
    var $ = h.$ || function (selector) { return doc.querySelector(selector); };
    var dlg = $('#startdlg');
    var form = $('#startform');
    if (!dlg || !form) return null;
    var statusEl = $('#startstatus');
    var start = createStartChat({
      base: h.base || '',
      discoveryBase: h.apiBase || global.MUX_DISCOVERY_BASE || DEFAULT_API,
      fetch: h.fetch || (global.fetch && global.fetch.bind(global)),
      postIntent: h.postIntent || global.postIntent,
      navigate: h.navigate,
      sleep: h.sleep,
      now: h.now,
      pollIntervalMs: h.pollIntervalMs,
      pollTimeoutMs: h.pollTimeoutMs,
    });

    function text(node, value) { if (node) node.textContent = value == null ? '' : String(value); return node; }
    function clear(node) { if (!node) return; while (node.firstChild) node.removeChild(node.firstChild); }
    function option(label, value) {
      var node = doc.createElement('option');
      node.value = value;
      text(node, label);
      return node;
    }
    function fill(select, placeholder, rows, selected, labelFor) {
      if (!select) return;
      clear(select);
      select.appendChild(option(placeholder, ''));
      (rows || []).forEach(function (row) {
        var id = rowId(row);
        var label = labelFor ? labelFor(row) : (row.label || id);
        select.appendChild(option(label, id));
      });
      select.value = selected || '';
    }
    function setStatus(message, tone) {
      if (!statusEl) return;
      statusEl.hidden = !message;
      statusEl.className = 'startstatus' + (tone ? ' ' + tone : '');
      text(statusEl, message);
    }
    function renderOptions() {
      var s = start.state;
      fill($('#startdeck'), s.loading ? 'Loading decks...' : 'Choose a deck',
        s.options.decks, s.form.deckId, function (row) { return row.label || row.id; });
      fill($('#startcollection'), s.collectionsLoading ? 'Loading collections...' : 'No collection',
        s.options.collections, s.form.collectionId, function (row) { return row.label || row.id; });
      fill($('#startcheckpoint'), 'Blank chat', s.options.checkpoints, s.form.checkpointId, function (row) {
        var detail = [row.sourceTitle, row.tool, row.workspaceLabel].filter(Boolean).join(' - ');
        return detail ? (row.label || row.id) + ' - ' + detail : (row.label || row.id);
      });
      fill($('#startworkspace'), 'Choose a workspace', s.options.workspaces, s.form.workspaceId,
        function (row) { return row.label || row.id; });
    }
    function renderControls() {
      var s = start.state;
      var checkpoint = !!s.form.checkpointId;
      var tool = $('#starttool');
      var workspace = $('#startworkspace');
      var subfolder = $('#startsubfolder');
      if (tool) { tool.value = s.form.tool; tool.disabled = checkpoint || !!s.form.handoffFromId || s.submitting; }
      if (workspace) { workspace.value = s.form.workspaceId; workspace.disabled = checkpoint || s.submitting; }
      if (subfolder) { subfolder.value = s.form.subfolder; subfolder.disabled = checkpoint || s.submitting; }
      var name = $('#startname');
      var phrase = $('#startphrase');
      var newCollection = $('#startcollectionnew');
      if (name) { name.value = s.form.name; name.disabled = s.submitting; }
      if (phrase) { phrase.value = s.form.phrase; phrase.disabled = s.submitting; }
      if (newCollection) { newCollection.value = s.form.collection; newCollection.disabled = s.submitting; }
      var submitButton = $('#startsubmit');
      if (submitButton) submitButton.disabled = s.loading || s.collectionsLoading || s.submitting;
      var note = $('#startcheckpointnote');
      if (note) note.hidden = !checkpoint;
      if (s.loading) setStatus('Loading start options from your PC...', 'warn');
      else if (s.submitting) setStatus('Starting the chat and waiting for the PC...', 'warn');
      else if (s.error) setStatus(s.error, s.lastOutcome && s.lastOutcome.state === 'queued' ? 'warn' : 'bad');
      else if (s.lastOutcome && s.lastOutcome.state === 'done') setStatus(s.lastOutcome.detail, 'ok');
      else if (s.form.handoffFromId) setStatus('New Gateway handoff: choose its name and workspace. Paste the copied prompt after it opens; the Codex source stays unchanged.', 'warn');
      else setStatus('', '');
    }
    function render() { renderOptions(); renderControls(); }
    function setPageInert(inert) {
      if (!doc.body || !doc.body.children) return;
      Array.from(doc.body.children).forEach(function (node) {
        if (node === dlg || String(node.tagName || '').toUpperCase() === 'SCRIPT') return;
        node.inert = !!inert;
      });
    }
    function open() {
      start.resetForm();
      render();
      if (typeof h.openDialog === 'function') h.openDialog('#startdlg');
      else if (typeof dlg.showModal === 'function') dlg.showModal();
      setPageInert(true);
      start.load().then(render);
      return start.state;
    }
    doc.addEventListener('mux-gateway-handoff', function (event) {
      open();
      start.setField('handoffFromId', event.detail.sessionId);
      start.setField('launchMode', 'gateway');
      start.setField('tool', 'claude');
      render();
    });
    function close() {
      if (typeof dlg.close === 'function') dlg.close();
      setPageInert(false);
    }
    function bindValue(id, field, cap) {
      var node = $(id);
      if (!node) return;
      node.oninput = node.onchange = function () {
        start.setField(field, cap ? cap(node.value, cap) : node.value);
        if (field === 'collection' || field === 'collectionId') renderOptions();
        renderControls();
      };
    }

    bindValue('#startname', 'name');
    bindValue('#starttool', 'tool');
    bindValue('#startworkspace', 'workspaceId');
    bindValue('#startsubfolder', 'subfolder');
    bindValue('#startphrase', 'phrase');
    bindValue('#startcollectionnew', 'collection');
    var deck = $('#startdeck');
    if (deck) deck.onchange = function () {
      start.loadCollections(deck.value).then(render);
      render();
    };
    var collection = $('#startcollection');
    if (collection) collection.onchange = function () {
      start.setField('collectionId', collection.value);
      renderControls();
    };
    var checkpoint = $('#startcheckpoint');
    if (checkpoint) checkpoint.onchange = function () {
      start.setCheckpoint(checkpoint.value);
      renderControls();
    };
    var submitButton = $('#startsubmit');
    form.onsubmit = async function (event) {
      if (event && event.preventDefault) event.preventDefault();
      var outcome = await start.submit();
      renderControls();
      if (outcome.state === 'done' && dlg.open) close();
      return outcome;
    };
    var closeButton = $('#startclose');
    if (closeButton) closeButton.onclick = close;
    var cancelButton = $('#startcancel');
    if (cancelButton) cancelButton.onclick = close;
    var openButton = $('#startchatbtn');
    if (openButton) openButton.onclick = open;
    if (typeof dlg.addEventListener === 'function')
      dlg.addEventListener('close', function () { setPageInert(false); });
    render();
    return { controller: start, open: open, close: close, render: render, submit: start.submit };
  }

  global.MuxChats = {
    queryParams: queryParams,
    statusFor: statusFor,
    createController: createController,
    createStartChat: createStartChat,
    createManagement: createManagement,
    managementPayload: managementPayload,
    setManagementBusy: setManagementBusy,
    installStartChat: installStartChat,
    install: install,
  };

  if (global.document) {
    if (global.document.readyState === 'loading') {
      global.document.addEventListener('DOMContentLoaded', function () { installStartChat(); install(); }, { once: true });
    } else {
      installStartChat();
      install();
    }
  }
})(globalThis);
