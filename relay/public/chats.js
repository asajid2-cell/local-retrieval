(function installMuxChats(global) {
  'use strict';

  // Same origin as this page: nginx mounts the PC's read-only discovery API under /multiplex/ rather
  // than publishing the whole PC server at /remote/ (which also carries launch and co-pilot routes).
  // Unauthenticated calls come back as a JSON 401 from the edge gate, never an HTML login page.
  var DEFAULT_API = '/multiplex/pc/api/discovery';
  var PAGE_SIZE = 40;

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
    if (f.showHidden) params.set('showHidden', 'true');
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
        agent: '', date: '', minUserMessages: 0, showHidden: false,
        project: '', sort: 'recent',
      },
      rows: [], facets: { tags: [], phrases: [], projects: [], hidden: 0 },
      offset: 0, limit: d.limit || PAGE_SIZE, total: 0, hasMore: false,
      loading: false, error: '', coverage: null, generation: 0,
    };

    async function getJson(path, params) {
      var url = apiBase + path;
      var qs = params && params.toString();
      if (qs) url += '?' + qs;
      var response = await fetchFn(url, { headers: { Accept: 'application/json' } });
      if (!response || !response.ok) throw new Error('HTTP ' + ((response && response.status) || 0));
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
      return load(true);
    }

    function page(delta) {
      state.offset = Math.max(0, state.offset + delta * state.limit);
      return load(false);
    }

    return { state: state, load: load, cycleTag: cycleTag, phrase: phrase, page: page };
  }

  var START_SAFE_MUX = /^[A-Za-z0-9_.-]{1,48}$/;
  var START_INVALID_SUBFOLDER = /[\\\/:*?"<>|]/;
  var START_DEFAULT_POLL_INTERVAL = 800;
  var START_DEFAULT_POLL_TIMEOUT = 60000;

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
        tool: '', workspaceId: '', subfolder: '', phrase: '',
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
        tool: '', workspaceId: '', subfolder: '', phrase: '',
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
        workspaceId: checkpoint ? '' : capText(f.workspaceId, 200),
        subfolder: checkpoint ? '' : capText(f.subfolder, 200),
        deckId: capText(f.deckId, 200),
        collectionId: capText(f.collectionId, 200),
        collection: capText(f.collection, 200),
        phrase: capText(f.phrase, 200),
      };
      delete payload.intentId;
      return payload;
    }

    function validate() {
      var f = state.form;
      var name = capText(f.name, 200);
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
            detail: 'Still waiting on the PC. The start request remains queued.',
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
      base: '',
      fetch: h.fetch || global.fetch.bind(global),
      postIntent: h.postIntent || global.postIntent,
      pollTimeoutMs: 60000,
      offlineTimeoutMs: 4000,
    });
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

    async function resume(chat, article, button) {
      if (!resumePicker) {
        rowStatus(article, 'Resume controls are unavailable on this page.', 'bad');
        return;
      }
      button.disabled = true;
      rowStatus(article, 'Queuing resume...', '');
      var outcome;
      try { outcome = await resumePicker.resume(chat); }
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

    function render() {
      var view = statusFor(controller.state);
      status.className = 'status ' + view.tone;
      text(status, view.text);
      renderFacets();
      renderRows();
      renderPager();

      stateBox.hidden = !controller.state.loading && !controller.state.error && controller.state.total > 0;
      if (controller.state.loading) {
        stateBox.innerHTML = '<strong>Loading chats</strong>Reading the archive directly from your PC.';
      } else if (controller.state.error) {
        stateBox.innerHTML = '<strong>PC archive unavailable</strong>Your PC did not answer. Check that the archive server is running there and that the reverse tunnel is up.';
      } else if (!controller.state.total) {
        stateBox.innerHTML = '<strong>No matching chats</strong>Clear a filter or show hidden one-off chats.';
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

    var form = $('#searchform');
    if (form) form.onsubmit = function (event) {
      event.preventDefault();
      controller.state.filters.q = ($('#query') && $('#query').value) || '';
      controller.load(true);
    };
    bindSelect('#agent', 'agent');
    bindSelect('#sort', 'sort');
    bindSelect('#date', 'date');
    bindSelect('#minimum', 'minUserMessages', function (value) { return Number(value) || 0; });
    bindSelect('#project', 'project');
    var hidden = $('#hidden');
    if (hidden) hidden.onchange = function () {
      controller.state.filters.showHidden = hidden.checked;
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
      if (tool) { tool.value = s.form.tool; tool.disabled = checkpoint || s.submitting; }
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
