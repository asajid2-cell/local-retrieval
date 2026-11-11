(function installMuxThreads(global) {
  'use strict';

  // A phone render of the SAME teemux output the terminal already holds.
  //
  // This is not a second data plane and it invents no metadata: it reads the live buffer rows that the
  // grid-ownership work already gives us (real bytes, real wrap flags) and decides which rows are one
  // message. Everything it keys on is IN THE TEXT - the agent's own glyphs - because the pty stream carries
  // nothing else and the project rule is "no in-band element metadata".
  //
  // The vocabulary was measured from a live agent tab (probe_thread_markers.cjs, tab-5bec, 2026-11-11):
  //   indent 0 + one of these glyphs  -> a message STARTS
  //     ❯ ⏵ ›   the user's prompt line (its body follows at indent 2)
  //     ⏺ ●     assistant text, a tool call, or a status note
  //     ⏎ ⎿     a tool result block
  //     ✻ ✳ ∗   a run summary ("Cooked for 46m")
  //   indent 2   the body of the message above (paragraphs, list items, quotes)
  //   indent 5+  tool detail: the sub-lines of a tool result
  //   ─ ━         a separator row: never a message, never body
  //
  // A message keeps absorbing rows until the next indent-0 glyph row. Blank rows belong to whatever is
  // open, so a message's paragraph spacing survives. When the buffer is the ALTERNATE screen a full-screen
  // TUI owns every cell and rows are not messages at all, so nothing is grouped and the caller is told why.

  var LEAD = {
    user:     ['❯', '⏵', '›'],
    assistant:['⏺', '●', '○'],
    tool:     ['⏎', '⎿', '└'],
    status:   ['✻', '✳'],
  };
  var RULE = ['─', '━', '═', '▁', '▔'];
  // The tool leaders are the only ones allowed to sit indented; every other leader must be at column 0.
  // Without this, an ordinary bulleted list ("• item", "- item", "> quote") would split a message at every
  // bullet, which is the most common prose shape an agent emits.
  var INDENTED_OK = LEAD.tool;

  // The leader glyph -> which kind of message it opens.
  //
  // A GLYPH WINS OVER INDENTATION. Measured in the live buffer, the user/assistant glyphs sit at column 0
  // while a tool-result leader (⎿) sits at indent 2 - so keying on "indent 0 only" would classify every
  // tool result as prose. The converse is also true and is why the indents exist at all: an ordinary
  // indented line with no glyph is BODY, and it must never open a message. Only the small, fixed
  // vocabulary below is allowed to start one.
  function classify(text) {
    var m = /^([ \t]*)(\S)/.exec(text || '');
    if (!m) return { kind: 'blank', indent: 0, lead: '' };
    var indent = m[1].length;
    var ch = m[2];
    if (RULE.indexOf(ch) >= 0) return { kind: 'rule', indent: indent, lead: ch };
    for (var k in LEAD) {
      if (LEAD[k].indexOf(ch) < 0) continue;
      if (indent !== 0 && INDENTED_OK.indexOf(ch) < 0) break;   // indented bullet = prose, not a message
      return { kind: k, indent: indent, lead: ch };
    }
    return { kind: 'body', indent: indent, lead: ch };
  }

  // Rows -> messages. `rows` is [{i, text, wrapped}]; wrapped rows are glued to the row above BEFORE
  // classification, so a paragraph that the pty wrapped at column 209 becomes one line and its continuation
  // does not masquerade as a message body.
  function segment(rows) {
    var joined = [];
    for (var i = 0; i < rows.length; i++) {
      var r = rows[i];
      if (!r) continue;
      if (r.wrapped && joined.length) joined[joined.length - 1] += r.text;
      else joined.push(r.text);
    }
    var out = [];
    var cur = null;
    for (var j = 0; j < joined.length; j++) {
      var text = joined[j];
      var c = classify(text);
      if (c.kind === 'rule') { if (cur) { out.push(cur); cur = null; } continue; }
      if (c.kind === 'blank') { if (cur) cur.lines.push(''); continue; }
      if (c.kind === 'body') {
        if (cur) { cur.lines.push(text); continue; }
        // Body with nothing open (a screen that started mid-message): open an unattributed one rather than
        // dropping the text, so a phone never shows less than the terminal does.
        cur = { role: 'text', lead: '', lines: [text] };
        continue;
      }
      if (cur) out.push(cur);
      cur = { role: c.kind, lead: c.lead, lines: [text] };
    }
    if (cur) out.push(cur);
    // Trim trailing blank lines, and give each message one comparable text blob plus a stable ordinal.
    for (var n = 0; n < out.length; n++) {
      var m = out[n];
      while (m.lines.length && m.lines[m.lines.length - 1].trim() === '') m.lines.pop();
      m.text = m.lines.map(function (l) { return l.replace(/^[ \t]+/, ''); }).join('\n').trim();
      m.index = n;
    }
    return out.filter(function (m) { return m.text !== ''; });
  }

  // What kind of surface is this buffer? The alternate screen is a TUI (vim, an agent's full-screen mode):
  // its rows are a canvas, not a conversation, so the phone must fall back to the plain terminal.
  function bufferKind(buffer) {
    try { return (buffer && buffer.type === 'alternate') ? 'alternate' : 'normal'; } catch (e) { return 'normal'; }
  }

  function summarize(messages) {
    var counts = { user: 0, assistant: 0, tool: 0, status: 0, text: 0 };
    for (var i = 0; i < messages.length; i++) counts[messages[i].role] = (counts[messages[i].role] || 0) + 1;
    return counts;
  }

  global.MuxThreads = {
    segment: segment,
    classify: classify,
    bufferKind: bufferKind,
    summarize: summarize,
    LEAD: LEAD,
  };
})(window);
