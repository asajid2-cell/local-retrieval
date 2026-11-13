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
    // The app cycles its run-summary glyphs, so the list is the ones seen live - but NOT '*' or '-':
    // those are markdown bullets at column 0 in ordinary prose, and they must stay body.
    status:   ['✻', '✳', '✢', '✽', '✺'],
  };
  var RULE = ['─', '━', '═', '▁', '▔'];
  // The activity line ("✢ Boogieing…", "✽ Noodling…", "✻ Cooked for 46m") is a STATUS message, not body.
  // It is not a fixed glyph list - the app cycles several - and the tell is its SHAPE: a glyph, one word,
  // and an ellipsis (or a duration in parentheses). Without this rule the spinner row landed as the last
  // line of the user's own turn, so a sent message read "echo phone-keypath-ok / ✢ Boogieing…".
  var SPINNER = /^\s*[\u2722\u2727\u2733\u2735\u2736\u273a\u273b\u273c\u273d\u2217]\s+\S*\u2026/;
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
    // A SPINNER row starts a status message no matter its glyph, so it can never extend the turn above it.
    if (SPINNER.test(text || '')) return { kind: 'status', indent: indent, lead: ch };
    for (var k in LEAD) {
      if (LEAD[k].indexOf(ch) < 0) continue;
      if (indent !== 0 && INDENTED_OK.indexOf(ch) < 0) break;   // indented bullet = prose, not a message
      return { kind: k, indent: indent, lead: ch };
    }
    return { kind: 'body', indent: indent, lead: ch };
  }

  // Where the agent app's COMPOSER starts, or rows.length when there is none.
  //
  // Measured on tab-5bec (2026-11-12): the app boxes its input between two rules with the prompt glyph
  // between them, and paints its status line underneath:
  //     ---------------
  //     > (prompt glyph, alone)
  //     ---------------
  //       cd: ...   bypass permissions on - 3 shells - usage: 37% left
  //       < Leaf 3: tv ... > All 3
  // Every row from the pair's FIRST rule onward is CHROME: it is the app's own interface, not something the
  // agent said. Left in, it becomes the tail of the last message - which is exactly the giant status-bar
  // bubble the phone showed. Detected structurally (rule, then a prompt-only row, then a rule) so it does
  // not depend on the wording of any one status line, and a screen with no such sandwich keeps every row.
  function chromeStart(rows) {
    for (var i = 0; i + 2 < rows.length; i++) {
      var a = classify(rows[i].text), b = classify(rows[i + 1].text), c = classify(rows[i + 2].text);
      if (a.kind !== 'rule') continue;
      // The prompt row: the user leader glyph and nothing else on the line.
      var promptOnly = b.kind === 'user' && /^[\s]*[\u276f\u23f5\u203a]\s*$/.test(rows[i + 1].text || '');
      if (!promptOnly) continue;
      if (c.kind !== 'rule') continue;
      return i;
    }
    return rows.length;
  }

  // Rows -> messages. `rows` is [{i, text, wrapped}]; wrapped rows are glued to the row above BEFORE
  // classification, so a paragraph that the pty wrapped at column 209 becomes one line and its continuation
  // does not masquerade as a message body.
  function segment(rows) {
    // Drop the app's composer and status bar before anything is grouped: they sit at the END of the screen
    // and would otherwise be absorbed into the last message as its body.
    rows = rows.slice(0, chromeStart(rows));
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
      // A footer widget is never body: it would otherwise extend whichever message it lands under.
      if (isFooter(text)) { if (cur) { out.push(cur); cur = null; } continue; }
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
      // Collapse source-column alignment before the phone sees it. A 209-column terminal indents code to
      // line up with source columns, so a row arrives as "135                     ECSENGINE::Spawn..."
      // - on a 390px screen that run is most of the line and the code wraps around it. Any run of 6+
      // interior spaces becomes a fixed 4-column gap: the code keeps its structure, the phone keeps its
      // width. A run that short is ordinary prose spacing and is left alone.
      //
      // NEVER for a table: its columns ARE those runs, so collapsing them would stack every cell into one
      // ragged column and destroy the only thing the box was drawn for. A table is measured from the raw
      // rows, and it is kept byte-exact for the panning renderer.
      var kind = blockKind(m.lines.join('\n'));
      m.kind = kind;
      m.text = kind === 'table'
        ? m.lines.join('\n').replace(/[ \t]+$/, '')
        : m.lines.map(function (l) {
            return l.replace(/^[ \t]+/, '').replace(/[ \t]{6,}/g, '    ');
          }).join('\n').trim();
      if (kind === 'table') m.text = m.text.replace(/^\n+/, '').trimEnd();
      m.index = n;
    }
    return out.filter(function (m) { return m.text !== ''; });
  }

  // The agent app's own FOOTER widgets, painted over the bottom rows of the screen. Measured on tab-5bec
  // (2026-11-12): a scroll hint, a task counter and a key hint appear under whatever the app last drew, so a
  // long command's output appears to continue into them. They are the app's interface, not output.
  var FOOTER = [
    /^\s*Jump to bottom\b/i,
    /^\s*[\u25b8\u25be>]\s*\d+\s+tasks?\s*\(\d+ done/i,
    /^\s*\(ctrl\+[a-z]\)\s*$/i,
    /^\s*Press .* for .*$/i,
  ];

  function isFooter(text) {
    var t = String(text || '');
    for (var i = 0; i < FOOTER.length; i++) if (FOOTER[i].test(t)) return true;
    return false;
  }

  // Which messages get a SENDER LABEL. A messaging view labels a run of turns from one speaker once: the
  // phone showed "TAB-5BEC" five times in a column, which is noise on the smallest screen. This returns the
  // same message objects with `showWho` set, so the caller keeps one source of truth for order and content.
  function grouped(msgs) {
    var out = [], prev = null, seenReal = false;
    for (var i = 0; i < msgs.length; i++) {
      var m = msgs[i];
      // Anything above the FIRST recognised message is the tail of a turn whose header scrolled off - often
      // the agent's own reasoning. It is kept (the phone never shows less than the terminal) but it is
      // CONTEXT, not a message the agent sent you, so the renderer styles it as such and never labels it.
      var role0 = m.role || 'text';
      var isContext = !seenReal && role0 === 'text';
      if (role0 !== 'text') seenReal = true;
      // A RUN is one speaker's turn. A tool result is part of the agent's turn - it is the agent running a
      // command - so it neither starts a run nor carries the label; it stays visually subordinate in
      // monospace. Without this, assistant -> tool -> assistant -> tool printed the session name on every
      // bubble, which is what the phone showed.
      var who = m.role || 'text';
      var speaker = (who === 'tool') ? 'assistant' : who;
      var showWho = who !== 'text' && speaker !== prev;
      out.push({ role: m.role, lead: m.lead, text: m.text, lines: m.lines, index: m.index,
                 kind: m.kind || 'text',
                 showWho: showWho && !isContext, context: isContext });
      if (who !== 'text') prev = speaker;
    }
    return out;
  }

  // What kind of surface is this buffer? The alternate screen is a TUI (vim, an agent's full-screen mode):
  // its rows are a canvas, not a conversation, so the phone must fall back to the plain terminal.
  function bufferKind(buffer) {
    try { return (buffer && buffer.type === 'alternate') ? 'alternate' : 'normal'; } catch (e) { return 'normal'; }
  }

  // What KIND of block is this message? Only one distinction matters on a phone: a BOX TABLE must not be
  // word-wrapped. Measured live (drive A6, 2026-11-12): a git-log table arrived as │-delimited rows and,
  // wrapped at 366px, became a pile of orphaned ─ runs and broken cells - the data was there and unreadable.
  // A table is detected from its own bytes (the box-drawing frame), so no new metadata is carried, and the
  // renderer gives it a horizontal scroller instead of wrapping it: the phone pans a table, it does not
  // reflow one. Prose and code keep wrapping, which is right for them.
  var BOX = /^\s*[\u2500-\u257f]/;
  function blockKind(text) {
    var lines = String(text || '').split('\n'), box = 0, nonEmpty = 0;
    for (var i = 0; i < lines.length; i++) {
      if (!lines[i].trim()) continue;
      nonEmpty++;
      if (BOX.test(lines[i])) box++;
    }
    if (nonEmpty < 2) return 'text';
    return (box / nonEmpty) >= 0.5 ? 'table' : 'text';
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
    blockKind: blockKind,
    summarize: summarize,
    chromeStart: chromeStart,
    grouped: grouped,
    LEAD: LEAD,
  };
})(window);
