// FLEET GLANCE — "what is every session doing right now", answered without attaching to any of them.
// Pure rendering over one GET /api/fleet payload: no fetch here except fetchFleet(), no DOM API beyond
// assigning innerHTML, so the whole render path is unit-testable by handing renderFleet a {innerHTML:''}
// stand-in. Snippets are terminal truth signed by muxd; this file only ever renders them as TEXT
// (escaped, never parsed as markup) and re-clamps the byte cap the server already applied, because a
// glance screen that can be made to lie by terminal output is worse than no glance screen.
(function installFleet(global) {
  'use strict';

  var SNIPPET_LINES = 2;
  var SNIPPET_BYTES = 2048;

  function esc(value) {
    return String(value == null ? '' : value)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  // Terminal bytes are not plain text: strip CSI/OSC escapes and raw control codes so a session
  // painting a TUI cannot smear cursor moves across the glance list.
  function cleanTerminalText(value) {
    return String(value == null ? '' : value)
      .replace(/\x1b\][^]*?(?:\x07|\x1b\\)/g, '')
      .replace(/\x1b\[[0-9;?]*[ -\/]*[@-~]/g, '')
      .replace(/\x1b[@-Z\\_]/g, '')
      .replace(/\r/g, '')
      .replace(/[\x00-\x08\x0b-\x1f\x7f]/g, '');
  }

  function snippetText(row) {
    var raw = cleanTerminalText(row && row.snippet).replace(/\s+$/, '');
    if (!raw) return '';
    var lines = raw.split('\n').slice(-SNIPPET_LINES);
    var text = lines.join('\n');
    return text.length > SNIPPET_BYTES ? text.slice(-SNIPPET_BYTES) : text;
  }

  // Exactly the `state` vocabulary relay/server.js's attentionStatusForHosted() puts on a fleet row:
  // green, yellow, red, white, detached, dormant. Only one of these becomes a class; anything else
  // collapses to "unknown" rather than injecting an attacker-chosen token into the class attribute.
  var STATES = ['green', 'yellow', 'red', 'white', 'detached', 'dormant'];
  function stateClass(row) {
    var state = String((row && row.state) || '').toLowerCase();
    return 'fleet-state-' + (STATES.indexOf(state) >= 0 ? state : 'unknown');
  }

  function ageText(ms) {
    var n = Number(ms);
    if (!isFinite(n) || n < 0) return '';
    var s = Math.floor(n / 1000);
    if (s < 5) return 'now';
    if (s < 60) return s + 's ago';
    var m = Math.floor(s / 60);
    if (m < 60) return m + 'm ago';
    var h = Math.floor(m / 60);
    if (h < 48) return h + 'h ago';
    return Math.floor(h / 24) + 'd ago';
  }

  function labelFor(row) {
    return String((row && row.agentLabel) || (row && row.agentState) || (row && row.kind) || '');
  }

  function fleetRowHtml(row) {
    row = row || {};
    var name = String(row.name || '');
    var snippet = snippetText(row);
    var age = ageText(row.lastOutAgeMs);
    var badges = '';
    if (row.autoheal) badges += '<span class="fleet-badge fleet-autoheal" title="autoheal armed">autoheal</span>';
    if (row.snippetDegraded) badges += '<span class="fleet-badge fleet-degraded" title="host did not answer; showing last known state">stale</span>';
    if (row.snippetSig) badges += '<span class="fleet-badge fleet-signed" data-fleet-sig="' + esc(row.snippetSig) + '" title="muxd-signed tail">signed</span>';
    return '<button type="button" class="fleet-row ' + stateClass(row) + '"'
      + ' data-fleet-name="' + esc(name) + '" aria-label="Attach to ' + esc(name) + '">'
      + '<span class="fleet-head">'
      + '<span class="fleet-chip" aria-hidden="true"></span>'
      + '<span class="fleet-name">' + esc(name) + '</span>'
      + '<span class="fleet-label">' + esc(labelFor(row)) + '</span>'
      + '<span class="fleet-age">' + esc(age) + '</span>'
      + '</span>'
      + (badges ? '<span class="fleet-badges">' + badges + '</span>' : '')
      + '<pre class="fleet-snippet">' + esc(snippet) + '</pre>'
      + '</button>';
  }

  function emptyHtml(data) {
    if (data && data.hostUp === false) {
      return '<div class="fleet-empty">PC host offline — no sessions to glance at.</div>';
    }
    return '<div class="fleet-empty">No sessions yet.</div>';
  }

  function renderFleet(el, data) {
    if (!el) return 0;
    var rows = (data && Array.isArray(data.sessions)) ? data.sessions
      : (Array.isArray(data) ? data : []);
    el.innerHTML = rows.length ? rows.map(fleetRowHtml).join('') : emptyHtml(data);
    return rows.length;
  }

  function fetchFleet(base) {
    var prefix = String(base == null ? '' : base);
    return fetch(prefix + '/api/fleet', { headers: { 'Content-Type': 'application/json' } })
      .then(function (res) {
        if (!res.ok) throw new Error('fleet ' + res.status);
        return res.json();
      });
  }

  // Tap-to-attach: delegated so a re-render never leaves stale listeners behind.
  function attachFleet(el, onAttach) {
    if (!el || !el.addEventListener) return;
    el.addEventListener('click', function (ev) {
      var node = ev.target;
      while (node && node !== el && !(node.dataset && node.dataset.fleetName)) node = node.parentNode;
      if (!node || node === el) return;
      var name = node.dataset.fleetName;
      if (name) onAttach(name);
    });
  }

  global.MuxFleet = {
    SNIPPET_LINES: SNIPPET_LINES,
    SNIPPET_BYTES: SNIPPET_BYTES,
    esc: esc,
    cleanTerminalText: cleanTerminalText,
    snippetText: snippetText,
    STATES: STATES,
    stateClass: stateClass,
    ageText: ageText,
    fleetRowHtml: fleetRowHtml,
    renderFleet: renderFleet,
    fetchFleet: fetchFleet,
    attachFleet: attachFleet,
  };
})(typeof globalThis !== 'undefined' ? globalThis : this);
