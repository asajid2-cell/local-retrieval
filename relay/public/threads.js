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
  //     ⏺ ● ○   assistant text, a tool call, or a status note
  //     ⏎ ⎿ └   a tool result block
  //     ✻ ✳ ∗   a run summary ("Cooked for 46m")
  //     ∴ 💭    a thought / reasoning trace
  //   indent 2   the body of the message above (paragraphs, list items, quotes)
  //   indent 5+  tool detail: the sub-lines of a tool result
  //   ─ ━         a separator row: never a message, never body
  //
  // A message keeps absorbing rows until the next indent-0 glyph row. Blank rows belong to whatever is
  // open, so a message's paragraph spacing survives. When the buffer is the ALTERNATE screen a full-screen
  // TUI owns every cell and rows are not conversation scrollback, though structured chrome/pickers can
  // still be inspected for phone controls.

  var LEAD = {
    user:     ['❯', '⏵', '›'],
    assistant:['⏺', '●', '○'],
    tool:     ['⏎', '⎿', '└'],
    // The app cycles its run-summary glyphs, so the list is the ones seen live - but NOT '*' or '-':
    // those are markdown bullets at column 0 in ordinary prose, and they must stay body.
    status:   ['✻', '✳', '✢', '✽', '✺'],
    thought:  ['∴', '💭'],
  };
  var RULE = ['─', '━', '═', '▁', '▔'];
  // The activity line ("✢ Boogieing…", "✽ Noodling…", "⠋ Running tests… (12s)") is a STATUS message, not body.
  // It is not a fixed glyph list - the app cycles stars, Braille spinners, and quadrant glyphs - and the tell
  // is its SHAPE: a spinner glyph + text ending with an ellipsis or an interrupt hint.
  var SPINNER_GLYPH_RE = /^[\s]*[\u2722\u2727\u2733\u2735\u2736\u273a\u273b\u273c\u273d\u2217\u2800-\u28ff\u25dc-\u25df\u25f4-\u25f7\u25d0-\u25d3]\s+\S/;
  var SPINNER = /^\s*(?:[\u2722\u2727\u2733\u2735\u2736\u273a\u273b\u273c\u273d\u2217\u2800-\u28ff\u25dc-\u25df\u25f4-\u25f7\u25d0-\u25d3]\s+.*(?:\u2026|\.\.\.|\((?:esc|ctrl\+c)\s+to\s+interrupt\b)|.*\((?:esc|ctrl\+c)\s+to\s+interrupt\b)/i;
  // Explicit thought-trace headers emitted by agent TUIs.
  var THOUGHT_HEADER = /^\s*(?:(?:\u2234|\ud83d\udcad)\s*\S*|(?:[\u23fa\u25cf\u25cb]\s+)?(?:Thinking(?:\u2026|\.\.\.|:)|Thought\s+for\s+\d|Reasoning:))/i;
  // Tool invocation headers (e.g. "⏺ Bash(ping -n 3 host)", "● Update(main.cpp)").
  var TOOLCALL_RE = /^\s*[\u23fa\u25cf\u25cb]\s+(?:(?:Bash|Read|Edit|Update|Write|MultiEdit|Grep|Glob|WebFetch|WebSearch|Agent|Task|Skill|NotebookEdit|TodoWrite|ListDir|ViewFile|RunCommand|Search)\b|[A-Z][A-Za-z0-9_.-]{1,32}\()/;
  // The tool leaders (and thought marker ∴) are allowed to sit slightly indented; every other leader must be at column 0.
  // Without this, an ordinary bulleted list ("• item", "- item", "> quote") would split a message at every
  // bullet, which is the most common prose shape an agent emits.
  var INDENTED_OK = LEAD.tool.concat(LEAD.thought);
  // The relay's own STATE banner, painted over the top of the screen when the session's output could not be
  // trusted. It is the transport talking, not the agent, and on the conversation surface it read as the first
  // thing the agent had said - so it is chrome, never a message.
  var RELAY_BANNER = /^\s*\[scrollback unavailable\b[^\]]*\]\s*$/i;

  function classify(text, rowMeta) {
    var raw = String(text || '');
    var m = /^([ \t]*)(\S)/.exec(raw);
    if (!m) return { kind: 'blank', indent: 0, lead: '' };
    var indent = m[1].length;
    var ch = m[2];
    if (RELAY_BANNER.test(raw)) return { kind: 'rule', indent: indent, lead: ch };
    if (RULE.indexOf(ch) >= 0) return { kind: 'rule', indent: indent, lead: ch };
    // A SPINNER row starts a status message no matter its glyph, so it can never extend the turn above it.
    if (SPINNER.test(raw)) return { kind: 'status', indent: indent, lead: ch };
    if (THOUGHT_HEADER.test(raw) && indent <= 2) return { kind: 'thought', indent: indent, lead: ch };
    if (indent === 0 && TOOLCALL_RE.test(raw)) return { kind: 'toolcall', indent: indent, lead: ch };
    for (var k in LEAD) {
      if (LEAD[k].indexOf(ch) < 0) continue;
      if (indent !== 0 && INDENTED_OK.indexOf(ch) < 0) break;   // indented bullet = prose, not a message
      return { kind: k, indent: indent, lead: ch };
    }
    if (rowMeta && rowMeta.dim && rowMeta.italic) {
      return { kind: 'thought', indent: indent, lead: '', styled: true };
    }
    return { kind: 'body', indent: indent, lead: ch };
  }

  function rowNum(r, fallback) {
    return (r && typeof r.i === 'number' && isFinite(r.i)) ? r.i : fallback;
  }

  function isPromptLine(text) {
    return /^[ \t]*[\u276f\u23f5\u203a](?:\s.*|\s*)$/.test(String(text || ''));
  }

  // Locate the agent app's live COMPOSER sandwich (rule, prompt line(s), rule) near the bottom of the screen,
  // returning { start, promptStart, promptEnd, ruleEnd } or null. Searching from the bottom within the last
  // ~24 rows keeps markdown `───` blocks in earlier turns from matching, and supports non-empty typed
  // prompts (e.g. `❯ /model`) as well as empty ones (`❯ `).
  function findComposerSandwich(rows) {
    if (!rows || rows.length < 3) return null;
    var minStart = Math.max(0, rows.length - 24);
    for (var i = rows.length - 3; i >= minStart; i--) {
      if (classify(rows[i] && rows[i].text).kind !== 'rule') continue;
      if (!isPromptLine(rows[i + 1] && rows[i + 1].text)) continue;
      // Find the closing rule within 1..6 lines after the prompt start (allows multi-line/wrapped prompt input).
      var closeAt = -1;
      var maxClose = Math.min(rows.length - 1, i + 6);
      for (var j = i + 2; j <= maxClose; j++) {
        var cj = classify(rows[j] && rows[j].text);
        if (cj.kind === 'rule') { closeAt = j; break; }
        if (cj.kind === 'assistant' || cj.kind === 'toolcall' || cj.kind === 'status' || cj.kind === 'tool') break;
      }
      if (closeAt < 0) continue;
      // Sanity guard: a historical prompt box has conversation turns after it; the live composer never does.
      var hasTurnAfter = false;
      for (var k = closeAt + 1; k < rows.length; k++) {
        var ck = classify(rows[k] && rows[k].text);
        if (ck.kind === 'assistant' || ck.kind === 'toolcall') { hasTurnAfter = true; break; }
      }
      if (hasTurnAfter) continue;
      return { start: i, promptStart: i + 1, promptEnd: closeAt - 1, ruleEnd: closeAt };
    }
    return null;
  }

  // Where the agent app's COMPOSER starts, or rows.length when there is none.
  function chromeStart(rows) {
    var sw = findComposerSandwich(rows);
    return sw ? sw.start : (rows ? rows.length : 0);
  }

  // The agent app's own FOOTER widgets, painted over the bottom rows of the screen.
  var FOOTER_TASKS_RE = /^\s*[\u25b8\u25be>]\s*\d+\s+tasks?\b/i;
  var FOOTER = [
    /^\s*Jump to bottom\b/i,
    /^\s*[\u25b8\u25be>]\s*\d+\s+tasks?\s*\(\d+ done/i,
    /^\s*\(ctrl\+[a-z]\)\s*$/i,
    /^\s*Press .* for .*$/i,
    /^\s*Esc to (?:cancel|close|exit)\b/i,
  ];

  function isFooter(text) {
    var t = String(text || '');
    for (var i = 0; i < FOOTER.length; i++) if (FOOTER[i].test(t)) return true;
    return false;
  }

  // The gateway's REAL leaf carousel (LeafWorkspaceDialog.tsx / LeafCarousel, read 2026-11-16):
  //   Box paddingX={2} gap={1}:   ‹  [item W] [item W] ...  ›  All N
  //   W = min(20, max(8, columns - 14))
  //   item = "<number> <name>" + (state ? " · working|approval|failed" : "")
  //          (the FIRST item is the matriarch and has no number)
  //   the CURRENT leaf is drawn INVERSE; nothing in the text marks it.
  // Every item is padded to W and truncated with an ellipsis, so when an item fills its box the gap to the
  // next one is a SINGLE space. Splitting on 2+ spaces therefore merges neighbours - which is exactly how a
  // tap landed on the wrong leaf ("it switches the wrong item").
  // WHAT A BAR ITEM LOOKS LIKE. A row is only a leaf bar if its items are leaf items. Without this, a
  // conversation row that merely MENTIONS a leaf - "(fd94f334…), \"Leaf 3: tv\" (…), \"web; deep5\" (…)" -
  // became a bar whose "leaves" were prose fragments, and tapping one clicked a sentence. Measured live
  // on tab-2470: one message about session ids produced a chip naming the whole line.
  //   prefixed   Leaf 3: tv / [Leaf 2: beta]      (the matriarch and numbered members)
  //   numbered   23 htv-lanes                     (a numbered member)
  //   bare       sndo / harmonizer-suite          (one token; spaces allowed only when geometry vouches
  //                                                for the box)
  // Anything carrying sentence punctuation - parentheses, quotes, commas, a full stop mid-item - is prose.
  var LEAF_ITEM = /^(?:\[?Leaf\s+\d+\s*:\s*[^()",\]]*\]?|\d+\s+[^()",]+|[A-Za-z0-9][A-Za-z0-9_.:\/ -]{0,39}(?:\s+\u00b7\s+(?:working|approval|failed|idle|queued))?)$/;
  var LEAF_ITEM_BARE = /^(?:\[?Leaf\s+\d+\s*:\s*[^()",\]]*\]?|\d+\s+[^()",]+|[A-Za-z0-9][A-Za-z0-9_.:\/-]{0,31}(?:\s+\u00b7\s+(?:working|approval|failed|idle|queued))?)$/;
  function leavesLookLikeLeaves(leaves, re) {
    if (!leaves.length) return false;
    for (var i = 0; i < leaves.length; i++) if (!re.test(leaves[i].label)) return false;
    return true;
  }

  function parseLeafCarousel(raw, rNum, cols, invSpans) {
    // The column count is what makes the box width knowable. Without it this parser would be guessing, so
    // it declines and the tolerant tokenizer below handles the row (which is how the older fixtures are
    // written). The live page always passes term.cols.
    if (!(cols | 0)) return null;
    var open = raw.indexOf('\u2039'), close = raw.lastIndexOf('\u203a');
    if (open < 0 || close < 0 || close <= open) return null;
    // A BAR ENDS AT ITS RIGHT ARROW. Only the count the gateway prints beside it may follow. A sentence
    // that quotes a carousel ("‹ 22 name › means the second leaf") is prose, not a bar.
    if (!/^\s*(?:All\s+\d+)?\s*$/.test(raw.slice(close + 1))) return null;
    if (!/\bLeaf\b|\bAll\s+\d+\b|\b\d+\s+\S/.test(raw)) return null;
    var W = Math.min(20, Math.max(8, ((cols | 0) || 100) - 14));
    var leaves = [];
    var pos = open + 2;                       // the arrow, then one gap column
    while (pos < close) {
      var seg = raw.slice(pos, Math.min(close, pos + W));
      var lead = seg.length - seg.replace(/^\s+/, '').length;
      var text = seg.trim();
      if (text) {
        var state = null;
        var sm = /\s+\u00b7\s+(working|approval|failed|idle|queued)\s*$/i.exec(text);
        if (sm) { state = sm[1].toLowerCase(); text = text.slice(0, sm.index).replace(/\s+$/, ''); }
        var num = null, name = text;
        var nm = /^(\d+)\s+(.*)$/.exec(text);
        if (nm) { num = Number(nm[1]); name = nm[2]; }
        leaves.push({ label: text, name: name, number: num, state: state,
                      row: rNum, col: pos + lead, active: false });
      }
      pos += W + 1;
    }
    // Every box must hold a leaf item. One prose fragment is enough to say this row is not a bar: the
    // items are fixed-width, so a real bar cannot have a box full of sentence.
    if (!leavesLookLikeLeaves(leaves, LEAF_ITEM)) return null;
    // active = the item drawn inverse. Without spans, no leaf is active: a guess here renders every chip
    // as "you are here", which is worse than no highlight at all.
    if (Array.isArray(invSpans)) {
      for (var i = 0; i < leaves.length; i++) {
        var a = leaves[i].col, b = a + leaves[i].label.length;
        for (var s = 0; s < invSpans.length; s++) {
          var sp = invSpans[s];
          if (sp && sp[1] > a && sp[0] < b) { leaves[i].active = true; break; }
        }
      }
    }
    return { text: raw.trim(), row: rNum, leaves: leaves,
             arrows: { prev: { row: rNum, col: open }, next: { row: rNum, col: close } } };
  }

  // Parse the gateway leaf bar row, e.g.:
  //   "  ‹ Leaf 3: tv           23 htv-lanes         24 speed-policy      › All 3"
  //   "  < Leaf 1: alpha  |  Leaf 2: beta  |  Leaf 3: gamma >   All 3"
  // Returns { text, row, leaves: [ { label, row, col, active } ] } or null.
  function parseLeafBar(rawText, rNum, cols, invSpans) {
    var raw = String(rawText || '');
    if (!raw.trim()) return null;
    var carousel = parseLeafCarousel(raw, rNum, cols, invSpans);
    if (carousel) return carousel;
    var m = /^(\s*[\u2039<])([\s\S]*?)([\u203a>](?:\s*All\s+\d+)?\s*)$/i.exec(raw);
    var hasLeafKeyword = /\bLeaf\s+\d+\s*:/i.test(raw);
    // A BAR IS DELIMITED. Both arrows, on one row, with nothing after the right one but an optional count.
    // The word "Leaf" appearing in a sentence is not a bar: keying on the keyword alone is how a message
    // ABOUT session ids turned into chips, and a chip is a tap at a cell.
    if (!m) return null;

    var innerStart = m[1].length;
    var inner = m[2];
    var leaves = [];
    // Walk tokens separated by |, │, or 2+ spaces while keeping raw buffer column offsets exact.
    var pos = 0;
    while (pos < inner.length) {
      // Skip separators / leading whitespace
      while (pos < inner.length && (inner[pos] === ' ' || inner[pos] === '\t' || inner[pos] === '|' || inner[pos] === '│')) pos++;
      if (pos >= inner.length) break;
      var end = pos;
      while (end < inner.length) {
        if (inner[end] === '|' || inner[end] === '│') break;
        if (inner[end] === ' ' && inner[end + 1] === ' ') break;
        end++;
      }
      var label = inner.slice(pos, end).replace(/\s+$/, '');
      if (label) {
        leaves.push({
          label: label,
          row: rNum,
          col: innerStart + pos,
          active: false,
          _explicit: /^[\u25cf\u25b8*]/.test(label) || /^\[.*\]$/.test(label),
          _leafPrefix: /^Leaf\s+\d+\s*:/i.test(label),
        });
      }
      pos = end;
    }
    if (!leaves.length) return null;
    // Delimited, but still prose inside the arrows? Then it is not a bar either. The bare form is a single
    // token here because there is no column geometry to say where one item ends; a name with spaces can
    // only be trusted from the fixed-width parse above.
    if (!leavesLookLikeLeaves(leaves, LEAF_ITEM_BARE)) return null;
    var explicitCount = 0, leafPrefixCount = 0;
    for (var li = 0; li < leaves.length; li++) {
      if (leaves[li]._explicit) explicitCount++;
      if (leaves[li]._leafPrefix) leafPrefixCount++;
    }
    for (var lj = 0; lj < leaves.length; lj++) {
      var lf = leaves[lj];
      if (explicitCount > 0) lf.active = lf._explicit;
      else if (leafPrefixCount > 0 && (leaves.length === 1 || leafPrefixCount < leaves.length)) lf.active = lf._leafPrefix;
      delete lf._explicit;
      delete lf._leafPrefix;
    }
    return {
      text: raw.trim(),
      row: rNum,
      leaves: leaves,
    };
  }

  function isTransientSpinner(m) {
    if (!m || m.role !== 'status') return false;
    var t = String(m.text || '');
    return SPINNER.test(t) || /\((?:esc|ctrl\+c)\s+to\s+interrupt/i.test(t);
  }

  // Collapse transient spinner frames in an accumulated list so live animation ticks update one
  // live status slot instead of appending dozens of dead spinner bubbles.
  function collapseTransient(list) {
    if (!Array.isArray(list) || !list.length) return [];
    var out = [];
    for (var i = 0; i < list.length; i++) {
      var m = list[i];
      if (!m) continue;
      if (isTransientSpinner(m)) {
        // Only keep a transient spinner at the tail; if an older transient spinner was sitting
        // directly before it (or earlier before a completed turn), drop the older one.
        if (out.length && isTransientSpinner(out[out.length - 1])) {
          out[out.length - 1] = m;
        } else {
          out.push(m);
        }
      } else {
        // A real message or completed summary (e.g. "✻ Cooked for ...") supersedes any prior
        // transient spinner directly above it.
        while (out.length && isTransientSpinner(out[out.length - 1])) out.pop();
        out.push(m);
      }
    }
    return out;
  }

  // Detect interactive picker/menu options in a slice of rows.
  // Matches either cursor-prefixed choices ("❯ 1. Sonnet" / "  2. Opus" / "❯ Yes" / "  No")
  // or numbered choices ("[1] ...", "1. ...") when at least 2 options are contiguous.
  function parsePicker(rows, startIdx) {
    var best = null;
    var i = Math.max(0, startIdx || 0);
    while (i < rows.length) {
      var t = String((rows[i] && rows[i].text) || '');
      var cur = /^[ \t]*([\u276f\u203a\u25b8])(?:\s+(.*))$/.exec(t);
      var num = /^[ \t]*(?:([\u276f\u203a\u25b8])\s+)?((?:\d+[.)]|\[\d+\])\s+\S.*)$/.exec(t);
      if (!cur && !num) { i++; continue; }
      // Start scanning a contiguous option group (allowing single blank rows between options).
      var opts = [];
      var selected = -1;
      // If the cursor is not on the first option (e.g. "  Sonnet\n❯ Opus\n  Haiku"), collect
      // indented sibling options immediately above `i` before walking downward.
      if (cur) {
        var back = [];
        for (var b = i - 1; b >= Math.max(0, i - 6); b--) {
          var bline = String((rows[b] && rows[b].text) || '');
          if (!bline.trim()) continue;
          var bm = /^[ \t]{2,}(\S.*)$/.exec(bline);
          if (!bm) break;
          var bt = bm[1].trim();
          if (isFooter(bline) || /[:?]$/.test(bt) || /^cd:\s/i.test(bt) || /^[<\u2039]/.test(bt) || /^\/[a-z0-9_:-]+/i.test(bt)) break;
          var bc = classify(bline);
          if (bc.kind !== 'body' && bc.kind !== 'blank') break;
          back.unshift({ text: bt, row: rowNum(rows[b], b) });
        }
        for (var bi = 0; bi < back.length; bi++) opts.push(back[bi]);
      }
      var j = i;
      var sawCursor = false;
      var sawNumbered = false;
      while (j < rows.length) {
        var rowObj = rows[j];
        var line = String((rowObj && rowObj.text) || '');
        if (!line.trim()) {
          // Allow blank line only if next line is also an option
          var nextLine = j + 1 < rows.length ? String((rows[j + 1] && rows[j + 1].text) || '') : '';
          if (/^[ \t]*(?:[\u276f\u203a\u25b8]\s+\S|(?:\d+[.)]|\[\d+\])\s+\S)/.test(nextLine)) { j++; continue; }
          break;
        }
        var mCur = /^[ \t]*([\u276f\u203a\u25b8])\s+(.*)$/.exec(line);
        var mNum = /^[ \t]*(?:([\u276f\u203a\u25b8])\s+)?((?:\d+[.)]|\[\d+\])\s+\S.*)$/.exec(line);
        var mIndented = opts.length > 0 && /^[ \t]{2,}(\S.*)$/.exec(line);
        if (mCur) {
          sawCursor = true;
          if (/^(?:\d+[.)]|\[\d+\])\s/.test(mCur[2])) sawNumbered = true;
          if (selected < 0) selected = opts.length;
          opts.push({ text: mCur[2].trim(), row: rowNum(rowObj, j) });
          j++;
        } else if (mNum) {
          sawNumbered = true;
          if (mNum[1] && selected < 0) { sawCursor = true; selected = opts.length; }
          opts.push({ text: mNum[2].trim(), row: rowNum(rowObj, j) });
          j++;
        } else if (mIndented && !isFooter(line) && !/^cd:\s/i.test(mIndented[1]) && !/^[<\u2039]/.test(mIndented[1]) && !/^\/[a-z0-9_:-]+/i.test(mIndented[1])) {
          // Sibling option without cursor (only valid if we already saw a cursor or numbered option, and not a status/leaf/slash row)
          opts.push({ text: mIndented[1].trim(), row: rowNum(rowObj, j) });
          j++;
        } else {
          break;
        }
      }
      if (opts.length >= 2 && (sawCursor || sawNumbered)) {
        best = { options: opts, selected: selected < 0 ? 0 : selected, startIdx: i, endIdx: j };
      }
      i = Math.max(i + 1, j);
    }
    return best;
  }

  function segmentRows(rows) {
    var joined = [];
    for (var i = 0; i < rows.length; i++) {
      var r = rows[i];
      if (!r) continue;
      if (r.wrapped && joined.length) {
        joined[joined.length - 1].text += r.text;
      } else {
        joined.push({ text: String(r.text || ''), dim: !!r.dim, italic: !!r.italic });
      }
    }
    var out = [];
    var cur = null;
    for (var j = 0; j < joined.length; j++) {
      var item = joined[j];
      var text = item.text;
      var c = classify(text, item);
      if (c.kind === 'rule') { if (cur) { out.push(cur); cur = null; } continue; }
      // A footer widget is never body: it would otherwise extend whichever message it lands under.
      if (isFooter(text)) { if (cur) { out.push(cur); cur = null; } continue; }
      if (c.kind === 'blank') { if (cur) cur.lines.push(''); continue; }
      if (c.kind === 'body' || (c.styled && cur && cur.role === 'thought')) {
        if (cur) { cur.lines.push(text); continue; }
        // Body with nothing open (a screen that started mid-message): open an unattributed one rather than
        // dropping the text, so a phone never shows less than the terminal does.
        cur = { role: c.styled ? 'thought' : 'text', lead: '', lines: [text] };
        continue;
      }
      if (cur) out.push(cur);
      cur = { role: c.kind, lead: c.lead, lines: [text] };
    }
    if (cur) out.push(cur);
    for (var n = 0; n < out.length; n++) {
      var m = out[n];
      while (m.lines.length && m.lines[m.lines.length - 1].trim() === '') m.lines.pop();
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

  function inspectScreen(rows, bufType, cols) {
    var list = Array.isArray(rows) ? rows : [];
    var kind = (typeof bufType === 'string')
      ? (bufType === 'alternate' ? 'alternate' : 'normal')
      : bufferKind(bufType);

    var sw = findComposerSandwich(list);
    var bodyEnd = sw ? sw.start : list.length;

    var chrome = {
      prompt: null,
      statusLines: [],
      leafBar: null,
      spinner: null,
      completions: [],
      picker: null,
      footerTasks: null,
    };

    if (sw) {
      var promptParts = [];
      for (var p = sw.promptStart; p <= sw.promptEnd; p++) {
        var pt = String((list[p] && list[p].text) || '');
        if (p === sw.promptStart) pt = pt.replace(/^[ \t]*[\u276f\u23f5\u203a]\s?/, '');
        else pt = pt.replace(/^[ \t]+/, '');
        promptParts.push(pt.replace(/\s+$/, ''));
      }
      chrome.prompt = {
        row: rowNum(list[sw.promptStart], sw.promptStart),
        text: promptParts.join('\n').trim(),
      };
    }

    // Scan chrome rows (below composer sandwich if present, otherwise bottom rows for footers/leafBar/picker).
    var chromeFrom = sw ? (sw.ruleEnd + 1) : Math.max(0, list.length - 18);
    var slashCompRe = /^[ \t]*(?:[\u276f\u203a\u25b8\u2022]\s+)?(\/[a-z0-9_:-]+(?:\s+.*)?)$/i;
    var pickerRows = [];

    // Without a composer sandwich, check if a bare slash prompt line ("❯ /...") sits near the bottom
    // with no assistant/tool turn after it, so bare completion menus can still populate chrome.completions.
    var barePromptIdx = -1;
    if (!sw) {
      for (var bp = list.length - 1; bp >= chromeFrom; bp--) {
        var bpt = String((list[bp] && list[bp].text) || '');
        if (/^[ \t]*[\u276f\u23f5\u203a]\s*\/\S*/.test(bpt)) { barePromptIdx = bp; break; }
        var bpk = classify(bpt).kind;
        if (bpk === 'assistant' || bpk === 'toolcall') break;
      }
      if (barePromptIdx >= 0 && !chrome.prompt) {
        chrome.prompt = {
          row: rowNum(list[barePromptIdx], barePromptIdx),
          text: String(list[barePromptIdx].text || '').replace(/^[ \t]*[\u276f\u23f5\u203a]\s?/, '').trim(),
        };
      }
    }

    for (var idx = chromeFrom; idx < list.length; idx++) {
      var r = list[idx];
      if (!r) continue;
      var raw = String(r.text || '');
      var trimmed = raw.trim();
      if (!trimmed) continue;
      var rIdx = rowNum(r, idx);

      if (!chrome.leafBar) {
        var lb = parseLeafBar(raw, rIdx, cols, r && r.inv);
        if (lb) { chrome.leafBar = lb; continue; }
      }
      if (FOOTER_TASKS_RE.test(raw)) {
        chrome.footerTasks = trimmed;
        continue;
      }
      if (SPINNER.test(raw)) {
        chrome.spinner = trimmed;
        continue;
      }
      if (classify(raw).kind === 'rule') continue;

      if (sw) {
        var cm = slashCompRe.exec(raw);
        if (cm) {
          chrome.completions.push({ text: cm[1].trim(), row: rIdx });
          continue;
        }
        pickerRows.push({ i: rIdx, text: raw, wrapped: !!r.wrapped });
        if (!isFooter(raw)) chrome.statusLines.push(trimmed);
      } else {
        if (idx === barePromptIdx) continue;
        if (kind === 'alternate' || (barePromptIdx >= 0 && idx > barePromptIdx)) {
          var acm = slashCompRe.exec(raw);
          if (acm) {
            chrome.completions.push({ text: acm[1].trim(), row: rIdx });
            continue;
          }
        }
        // Without a composer sandwich, only collect genuine footer/picker/status rows at the bottom.
        if (isFooter(raw)) continue;
        pickerRows.push({ i: rIdx, text: raw, wrapped: !!r.wrapped });
      }
    }

    // Look for an interactive picker either inside post-composer chrome or in the bottom rows of an
    // alternate-screen / permission-prompt screen (where no composer sandwich is drawn).
    var tailOffset = Math.max(0, list.length - 18);
    var pickerCandidate = sw ? pickerRows : list.slice(tailOffset);
    var pk = parsePicker(pickerCandidate, 0);
    if (pk) {
      // If there's no composer sandwich on normal screen, only accept the picker when it is at the very tail
      // (not followed by assistant/status turns) and has an explicit cursor glyph (❯/›/▸) or alt-screen.
      var validTailPicker = true;
      if (!sw && kind === 'normal') {
        var tailAfter = pickerCandidate.slice(pk.endIdx);
        var hasFollowTurn = tailAfter.some(function (x) {
          var k = classify(x && x.text).kind;
          return k === 'assistant' || k === 'toolcall' || k === 'user';
        });
        var hasCursor = pickerCandidate.slice(pk.startIdx, pk.endIdx).some(function (x) {
          return /^[ \t]*[\u276f\u203a\u25b8]\s+/.test(String((x && x.text) || ''));
        });
        validTailPicker = !hasFollowTurn && hasCursor;
      }
      if (validTailPicker) {
        chrome.picker = { options: pk.options, selected: pk.selected };
        // Exclude a trailing inline picker from message segmentation so "❯ 1. Yes" never renders
        // as a fake sent user bubble before the user taps an option.
        if (!sw && (tailOffset + pk.startIdx) < bodyEnd) {
          bodyEnd = tailOffset + pk.startIdx;
        }
        // Remove picker option lines from statusLines when both appeared in post-composer chrome.
        if (sw && chrome.statusLines.length) {
          var optTexts = pk.options.map(function (o) { return o.text; });
          chrome.statusLines = chrome.statusLines.filter(function (line) {
            var stripped = line.replace(/^[\u276f\u203a\u25b8]\s+/, '').trim();
            return optTexts.indexOf(stripped) < 0 && optTexts.indexOf(line) < 0;
          });
        }
      }
    }

    var msgRows = list.slice(0, bodyEnd);
    var messages = segmentRows(msgRows);

    // Also inspect message footers & active spinner from the full row list when not already found in chrome.
    if (!chrome.footerTasks) {
      for (var fi = list.length - 1; fi >= Math.max(0, list.length - 18); fi--) {
        var ft = String((list[fi] && list[fi].text) || '');
        if (FOOTER_TASKS_RE.test(ft)) { chrome.footerTasks = ft.trim(); break; }
      }
    }
    if (!chrome.spinner && messages.length) {
      for (var mi = messages.length - 1; mi >= 0; mi--) {
        if (isTransientSpinner(messages[mi])) { chrome.spinner = messages[mi].text; break; }
        if (messages[mi].role !== 'status') break;
      }
    }

    return {
      kind: kind,
      messages: messages,
      chrome: chrome,
    };
  }

  function segment(rows) {
    return inspectScreen(rows, 'normal').messages;
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
      // A RUN is one speaker's turn. Tool calls, tool results, and thought traces are part of the agent's
      // turn, so they stay under the same speaker run without repeating the session label on every card.
      var who = m.role || 'text';
      var speaker = (who === 'tool' || who === 'toolcall' || who === 'thought') ? 'assistant' : who;
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
  // word-wrapped.
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
    var counts = { user: 0, assistant: 0, tool: 0, toolcall: 0, thought: 0, status: 0, text: 0 };
    for (var i = 0; i < messages.length; i++) counts[messages[i].role] = (counts[messages[i].role] || 0) + 1;
    return counts;
  }

  // THE SEGMENTER AND THE PAGE ARE ONE BUILD. The page decides how to read a leaf carousel out of these
  // rows, and this file is what reads it; a page from one build with a segmenter from another silently
  // draws the old parse as if it were current - merged chips, taps on the wrong leaf, and no error
  // anywhere. The page refuses leaf chips unless this matches its own stamp, so the two can never drift.
  var BUILD = '2026-11-16-leaf-switch-3';

  global.MuxThreads = {
    BUILD: BUILD,
    // Can this copy parse the leaf carousel by geometry? Declared as a capability, not sniffed from the
    // function body: an old build has no parseLeafCarousel at all and must never be asked to guess.
    carousel: true,
    segment: segment,
    inspectScreen: inspectScreen,
    collapseTransient: collapseTransient,
    classify: classify,
    bufferKind: bufferKind,
    blockKind: blockKind,
    summarize: summarize,
    chromeStart: chromeStart,
    grouped: grouped,
    LEAD: LEAD,
  };
})(window);
