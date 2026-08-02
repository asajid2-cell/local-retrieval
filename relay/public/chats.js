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
      loading: false, error: '', generation: 0,
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
        var title = text(doc.createElement('span'), chat.title || chat.id);
        title.className = 'title';
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
        stateBox.innerHTML = '<strong>PC archive unavailable</strong>The discovery endpoint could not be reached. Check the PC server and /remote/ tunnel.';
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

  global.MuxChats = {
    queryParams: queryParams,
    statusFor: statusFor,
    createController: createController,
    install: install,
  };

  if (global.document) {
    if (global.document.readyState === 'loading') {
      global.document.addEventListener('DOMContentLoaded', function () { install(); }, { once: true });
    } else {
      install();
    }
  }
})(globalThis);
