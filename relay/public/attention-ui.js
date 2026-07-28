// Needs-attention chip + per-session notification mute.
//
// The coloured tab dot is a 6px hint you have to already know how to read; a session that is actually
// WAITING FOR YOU deserves a word. This module owns both halves of that: the chip text/tooltip, and
// the mute toggle that stops the phone push for one session without silencing the rest.
//
// It lives outside index.html so every branch is unit-testable without a browser — everything here is
// pure except sendNotifyToggle, which is the single fetch and takes an injectable fetch impl.
(function installMuxAttentionUi(global) {
  'use strict';

  // The client NEVER sees the server's raw needsAttention flag for its chip decision: the relay
  // pre-reduces attention into s.state (green working / yellow waiting / red stopped / white shell /
  // dormant / detached). Keying off state is what keeps the browser and the phone push agreeing about
  // who is waiting — both descend from attentionStatusForHosted() in server.js.
  const CHIPS = {
    yellow: { text: 'NEEDS YOU', cls: 'attnchip', title: 'Waiting for you — this agent stopped to ask something' },
    red: { text: 'STOPPED', cls: 'attnchip stopped', title: 'Agent stopped or hit a blocker' },
  };

  function escapeAttr(text) {
    return String(text)
      .replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')
      .replaceAll('"', '&quot;').replaceAll("'", '&#39;');
  }

  // null unless this session is in an attention state. Muting changes how the chip READS (it is still
  // shown — muting silences your phone, it does not hide the fact that the agent is waiting).
  function attentionChip(s) {
    const chip = CHIPS[s && s.state];
    if (!chip) return null;
    const muted = !!(s && s.notifyMuted);
    return {
      text: chip.text,
      cls: chip.cls + (muted ? ' muted' : ''),
      title: chip.title + (muted ? ' · phone notifications muted for this session' : ''),
      muted,
    };
  }

  function attentionChipHtml(s) {
    const chip = attentionChip(s);
    if (!chip) return '';
    return '<span class="' + chip.cls + '" title="' + escapeAttr(chip.title) + '">'
      + (chip.muted ? '\u{1F515} ' : '') + chip.text + '</span>';
  }

  function muteToggleLabel(s) {
    return (s && s.notifyMuted) ? '\u{1F515} Notifications: MUTED' : '\u{1F514} Notifications: on';
  }

  function muteToggleTitle(s) {
    return (s && s.notifyMuted)
      ? 'This tab never pushes to your phone when it needs you. Tap to unmute — the next episode buzzes.'
      : 'Tap to mute phone pushes for this tab only. Other sessions keep buzzing.';
  }

  // POST the toggle. `on` is notifications-ENABLED, matching the server contract ({on:boolean}), so
  // unmute is on:true. Returns the parsed body; throws on a non-2xx so the caller can flash the error.
  async function sendNotifyToggle(base, name, on, fetchImpl) {
    const doFetch = fetchImpl || global.fetch;
    const response = await doFetch(
      String(base || '') + '/api/sessions/' + encodeURIComponent(name) + '/notify',
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ on: !!on }),
      },
    );
    if (!response.ok) throw new Error('HTTP ' + response.status);
    return await response.json();
  }

  // Write the confirmed state back into the cached session list so the next renderTabs() shows it
  // without waiting for the poll. Returns the number of rows updated (0 = the row is already gone).
  function applyNotifyState(list, name, muted) {
    let updated = 0;
    for (const row of (Array.isArray(list) ? list : [])) {
      if (row && row.name === name) { row.notifyMuted = !!muted; updated++; }
    }
    return updated;
  }

  global.MuxAttentionUi = {
    attentionChip, attentionChipHtml, muteToggleLabel, muteToggleTitle, sendNotifyToggle, applyNotifyState,
  };
})(globalThis);
