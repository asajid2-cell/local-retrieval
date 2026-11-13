const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const source = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');

function matchingDelimiter(text, openIndex, openChar, closeChar, impact) {
  let depth = 0;
  let quote = null;
  let escaped = false;
  let lineComment = false;
  let blockComment = false;

  for (let i = openIndex; i < text.length; i += 1) {
    const current = text[i];
    const next = text[i + 1];

    if (lineComment) {
      if (current === '\n') lineComment = false;
      continue;
    }
    if (blockComment) {
      if (current === '*' && next === '/') {
        blockComment = false;
        i += 1;
      }
      continue;
    }
    if (quote) {
      if (escaped) {
        escaped = false;
      } else if (current === '\\') {
        escaped = true;
      } else if (current === quote) {
        quote = null;
      }
      continue;
    }
    if (current === '/' && next === '/') {
      lineComment = true;
      i += 1;
      continue;
    }
    if (current === '/' && next === '*') {
      blockComment = true;
      i += 1;
      continue;
    }
    if (current === '\'' || current === '"' || current === '`') {
      quote = current;
      continue;
    }
    if (current === openChar) depth += 1;
    if (current === closeChar) {
      depth -= 1;
      if (depth === 0) return i;
    }
  }

  assert.fail(`${impact}: the relevant client-code block is incomplete, so the user-facing behavior cannot be verified`);
}

function functionSectionBounds(name, impact) {
  const marker = `function ${name}`;
  const from = source.indexOf(marker);
  assert.notEqual(from, -1, `${impact}: the client is missing ${name}`);
  const open = source.indexOf('{', from + marker.length);
  assert.notEqual(open, -1, `${impact}: ${name} has no executable body`);
  const close = matchingDelimiter(source, open, '{', '}', impact);
  return {
    from,
    close,
    text: source.slice(from, close + 1),
  };
}

function functionSection(name, impact) {
  return functionSectionBounds(name, impact).text;
}

function visualViewportListeners(event, impact) {
  const pattern = new RegExp(
    `(?:window\\s*\\.\\s*)?visualViewport\\s*(?:\\.|\\?\\.)\\s*addEventListener\\s*\\(\\s*['"]${event}['"]`,
    'g'
  );
  const listeners = [];
  let match;

  while ((match = pattern.exec(source)) !== null) {
    const open = source.indexOf('(', match.index);
    const close = matchingDelimiter(source, open, '(', ')', impact);
    listeners.push(source.slice(match.index, close + 1));
  }

  assert.ok(
    listeners.length > 0,
    `${impact}: the visual viewport has no ${event} listener`
  );
  return listeners;
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function elementVariableNames(selector) {
  const id = selector.slice(1);
  const selectorPattern = escapeRegExp(selector);
  const idPattern = escapeRegExp(id);
  const lookup = new RegExp(
    `(?:const|let|var)\\s+([A-Za-z_$][\\w$]*)\\s*=\\s*` +
    `(?:\\$\\(\\s*['"]${selectorPattern}['"]\\s*\\)|` +
    `document\\s*\\.\\s*getElementById\\s*\\(\\s*['"]${idPattern}['"]\\s*\\)|` +
    `document\\s*\\.\\s*querySelector\\s*\\(\\s*['"]${selectorPattern}['"]\\s*\\))`,
    'g'
  );
  const names = new Set();
  let match;

  while ((match = lookup.exec(source)) !== null) names.add(match[1]);
  return names;
}

function idAssignedVariableNames(text, id) {
  const pattern = new RegExp(
    `\\b([A-Za-z_$][\\w$]*)\\s*\\.\\s*id\\s*=\\s*['"]${escapeRegExp(id)}['"]`,
    'g'
  );
  const names = new Set();
  let match;

  while ((match = pattern.exec(text)) !== null) names.add(match[1]);
  return names;
}

function listenerCalls(text, targetNames, event) {
  const calls = [];
  const eventPattern = event ? escapeRegExp(event) : '([^\'"]+)';
  for (const name of targetNames) {
    const pattern = new RegExp(
      `\\b${escapeRegExp(name)}\\s*\\.\\s*addEventListener\\s*\\(\\s*['"]${eventPattern}['"]`,
      'g'
    );
    let match;
    while ((match = pattern.exec(text)) !== null) {
      const open = text.indexOf('(', match.index);
      const close = matchingDelimiter(
        text,
        open,
        '(',
        ')',
        `the keybar interaction will break because its ${event} listener cannot be parsed`
      );
      calls.push({
        event: event || match[1],
        source: text.slice(match.index, close + 1),
        index: match.index,
      });
    }
  }
  return calls;
}

function directListenerCalls(text, selector, event) {
  const id = selector.slice(1);
  const selectorPattern = escapeRegExp(selector);
  const idPattern = escapeRegExp(id);
  const eventPattern = event ? escapeRegExp(event) : '([^\'"]+)';
  const pattern = new RegExp(
    `(?:\\$\\(\\s*['"]${selectorPattern}['"]\\s*\\)|` +
    `document\\s*\\.\\s*getElementById\\s*\\(\\s*['"]${idPattern}['"]\\s*\\)|` +
    `document\\s*\\.\\s*querySelector\\s*\\(\\s*['"]${selectorPattern}['"]\\s*\\))` +
    `\\s*\\.\\s*addEventListener\\s*\\(\\s*['"]${eventPattern}['"]`,
    'g'
  );
  const calls = [];
  let match;

  while ((match = pattern.exec(text)) !== null) {
    const open = text.indexOf('(', match.index);
    const close = matchingDelimiter(
      text,
      open,
      '(',
      ')',
      `the keybar interaction will break because its direct ${event} listener cannot be parsed`
    );
    calls.push({
      event: event || match[1],
      source: text.slice(match.index, close + 1),
      index: match.index,
    });
  }
  return calls;
}

function containerListeners(text, selector, event) {
  const names = elementVariableNames(selector);
  const calls = listenerCalls(text, names, event);
  return calls.concat(directListenerCalls(text, selector, event));
}

test('the visual viewport resize handler applies the keyboard-shrunk viewport and keeps debounced fitting', () => {
  const impact = 'the composer will sit under the soft keyboard or the terminal will keep stale dimensions during resize';
  const resizeHandlers = visualViewportListeners('resize', impact);
  assert.ok(
    resizeHandlers.some(handler =>
      /applyViewport\s*\(\s*\)/.test(handler) &&
      /scheduleViewportFit\s*\(\s*isMobile\s*\(\s*\)\s*\?\s*140\s*:\s*40\s*\)/.test(handler)
    ),
    `${impact}: the resize handler does not both apply the viewport and retain the debounced terminal re-measure`
  );
});

// This clause originally REQUIRED a visualViewport 'scroll' listener calling applyViewport, for the
// iOS shift. Shipping it froze the terminal: applyViewport re-measures the grid, ordinary page
// scrolling fires 'scroll', and every scroll became a PTY resize. The contract was wrong, so it is
// inverted here rather than deleted — the regression must stay pinned.
test('viewport re-measurement is never wired to visual viewport scrolling', () => {
  const impact = 'every page scroll would re-measure the grid and storm the PTY with resizes, freezing the terminal';
  // Asserted against the source directly: visualViewportListeners() requires at least one match, so
  // it cannot express absence.
  const registered = /(?:window\s*\.\s*)?visualViewport\s*(?:\.|\?\.)\s*addEventListener\s*\(\s*['"]scroll['"]/.test(source);
  assert.equal(registered, false, `${impact}: a visualViewport 'scroll' listener is registered`);
});

// Also inverted from the original contract, same reason as the scroll clause: compensating for the
// iOS offset by growing the app made the document scrollable, which is what let the scroll storm
// start. The height assignment is the part that actually fixes the keyboard, and it is pinned here.
test('applyViewport sets the keyboard-shrunk height without offsetting the document', () => {
  const impact = 'the composer will sit under the keyboard, or the app will grow the document and re-enter on scroll';
  const apply = functionSection('applyViewport', impact);
  const shiftsDocument =
    /\.\s*style\s*\.\s*(?:marginTop|top|transform)\s*=/.test(apply);
  const readsHeight =
    /(?:\bvisualViewport\b|\bvv\b)\s*\.\s*height\b/.test(apply) ||
    /\{\s*[^}]*\bheight\b[^}]*\}\s*=\s*(?:window\s*\.\s*)?visualViewport\b/.test(apply);
  const appNames = elementVariableNames('#app');
  const writesAppHeight =
    [...appNames].some(name => new RegExp(
      `\\b${escapeRegExp(name)}\\s*\\.\\s*style\\s*\\.\\s*height\\s*=`
    ).test(apply)) ||
    /(?:\$\(\s*['"]#app['"]\s*\)|document\s*\.\s*getElementById\s*\(\s*['"]app['"]\s*\)|document\s*\.\s*querySelector\s*\(\s*['"]#app['"]\s*\))\s*\.\s*style\s*\.\s*height\s*=/.test(apply);

  assert.equal(shiftsDocument, false, `${impact}: applyViewport moves the app with marginTop/top/transform`);
  assert.ok(readsHeight, `${impact}: applyViewport no longer reads the visual viewport height`);
  assert.ok(writesAppHeight, `${impact}: applyViewport no longer assigns the visible height to #app`);
});

test('applyViewport memoises its viewport DOM write during continuous keyboard animation', () => {
  const impact = 'keyboard animation will cause needless DOM writes and visible input jank';
  const apply = functionSection('applyViewport', impact);
  const write = apply.search(/\.\s*style\s*\.\s*(?:height|top)\s*=/);
  assert.notEqual(write, -1, `${impact}: applyViewport has no tracked viewport style write`);
  const beforeWrite = apply.slice(0, write);
  const rememberedValue = /\b_?(?:last|prev|previous|cached|memo(?:ized)?)\w*\b/;
  const comparesRememberedValue =
    new RegExp(`${rememberedValue.source}\\s*(?:===|!==|==|!=)\\s*[A-Za-z_$][\\w$\\.]*`).test(beforeWrite) ||
    new RegExp(`[A-Za-z_$][\\w$\\.]*\\s*(?:===|!==|==|!=)\\s*${rememberedValue.source}`).test(beforeWrite);

  assert.ok(
    comparesRememberedValue,
    `${impact}: the style write is not guarded by a comparison with a remembered last value`
  );
});

test('buildKeybar delegates focus preservation to the keybar container', () => {
  const impact = 'tapping a keybar button will steal focus from the terminal input';
  const keybar = functionSectionBounds('buildKeybar', impact);
  const delegatedGuards = containerListeners(source, '#keybar', 'mousedown').filter(call =>
    /preventDefault\s*\(\s*\)/.test(call.source) &&
    /,\s*true\s*\)\s*$/.test(call.source)
  );

  assert.ok(
    delegatedGuards.length > 0,
    `${impact}: #keybar has no delegated mousedown focus-preserving guard that calls preventDefault during capture`
  );
  assert.ok(
    delegatedGuards.some(call => call.index < keybar.from || call.index > keybar.close),
    `${impact}: the delegated guard is installed inside buildKeybar and will stack a duplicate listener on every rebuild`
  );
});

test('buildKeybar does not prevent touchstart on the keybar container', () => {
  const impact = 'iOS keybar taps will stop synthesizing clicks and the controls will become unusable';
  const keybar = functionSection('buildKeybar', impact);
  const touchstartCalls = containerListeners(keybar, '#keybar', 'touchstart');

  assert.ok(
    touchstartCalls.every(call => !/preventDefault\s*\(\s*\)/.test(call.source)),
    `${impact}: the keybar container prevents the touchstart that iOS needs to synthesize a click`
  );
});

test('buildKeybar preserves every size-button long-press listener', () => {
  const impact = 'holding the size button will lose its long-press behavior on touch or mouse';
  const keybar = functionSection('buildKeybar', impact);
  const sizeNames = new Set([
    ...elementVariableNames('#sizebtn'),
    ...idAssignedVariableNames(keybar, 'sizebtn'),
  ]);
  const requiredEvents = ['touchstart', 'touchend', 'touchmove', 'mousedown', 'mouseup', 'mouseleave'];

  for (const event of requiredEvents) {
    assert.ok(
      listenerCalls(keybar, sizeNames, event).length > 0 ||
        directListenerCalls(keybar, '#sizebtn', event).length > 0,
      `${impact}: #sizebtn is missing its ${event} listener`
    );
  }
});

test('every keybar button is created as a non-submitting button', () => {
  const impact = 'keybar taps in a form context will submit and reload the terminal page';
  const keybar = functionSection('buildKeybar', impact);
  const creationPattern = /(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*document\s*\.\s*createElement\s*\(\s*['"]button['"]\s*\)/g;
  const creations = [...keybar.matchAll(creationPattern)];

  assert.ok(
    creations.length > 0,
    `${impact}: buildKeybar creates no button elements that can be checked for type="button"`
  );
  for (let i = 0; i < creations.length; i += 1) {
    const name = creations[i][1];
    const from = creations[i].index;
    const to = i + 1 < creations.length ? creations[i + 1].index : keybar.length;
    const buttonCode = keybar.slice(from, to);
    const hasButtonType =
      new RegExp(`\\b${escapeRegExp(name)}\\s*\\.\\s*type\\s*=\\s*['"]button['"]`).test(buttonCode) ||
      new RegExp(`\\b${escapeRegExp(name)}\\s*\\.\\s*setAttribute\\s*\\(\\s*['"]type['"]\\s*,\\s*['"]button['"]\\s*\\)`).test(buttonCode);

    assert.ok(
      hasButtonType,
      `${impact}: the button created at source offset ${from} has no type="button"`
    );
  }
});

test('a stationary tap on the terminal focuses the helper textarea so the iOS keyboard can open', () => {
  const impact = 'tapping the prompt does nothing and no keyboard appears, so the phone cannot type at all';
  // The focus must actually land on the helper textarea — the one focusable node xterm routes typing
  // through — not merely on some ancestor.
  const focuser = functionSection('focusForTyping', impact);
  assert.match(focuser, /\.xterm-helper-textarea/, `${impact}: the tap focus does not target the helper textarea`);
  assert.match(focuser, /\.focus\s*\(/, `${impact}: the helper textarea is never focused`);

  // It must run from touchend — the gesture's last moment, the only point iOS honors a programmatic
  // focus that opens the keyboard.
  const termTouches = containerListeners(source, '#term', 'touchend');
  assert.ok(termTouches.length > 0, `${impact}: the terminal has no touchend listener`);
  const tap = termTouches.find(call => /focusForTyping\s*\(/.test(call.source));
  assert.ok(tap, `${impact}: no touchend handler focuses the terminal`);
  // Guarded: a real drag scrolls and the Sel toggle selects — neither wants a keyboard.
  assert.match(tap.source, /\bmoved\b/, `${impact}: the tap focus is not guarded against a drag`);
  assert.match(tap.source, /\bselectMode\b/, `${impact}: the tap focus is not guarded against the Sel toggle`);
});

test('the terminal touchstart listener does not preventDefault, so iOS can still open the keyboard', () => {
  const impact = 'preventing the touchstart default blocks the keyboard and text selection on the phone';
  const termTouches = containerListeners(source, '#term', 'touchstart');
  assert.ok(termTouches.length > 0, `${impact}: the terminal has no touchstart listener`);
  const scrolling = termTouches.filter(call => /passive\s*:\s*true/.test(call.source));
  for (const call of scrolling) {
    assert.ok(
      !/preventDefault\s*\(\s*\)/.test(call.source),
      `${impact}: a passive terminal touchstart calls preventDefault`
    );
  }
});

// ---- the delta forwarder must not invent deletes ------------------------------------------------------
//
// Live defect (2026-11-12): a phone lost the space after every word and whole words "backspaced
// themselves", so sentences arrived run together ("makesthesentencescomeoutlikethis"). The forwarder mailed
// the delta between the textarea and its cached baseline, so ANY event that left the field shorter than the
// baseline - a reset, a lost IME commit, a keyboard that re-creates the field - was sent as that many DEL
// characters, which deleted the word the user had just typed. The event itself says which edit it was, so
// the rule is: deletes come only from a delete event; an insert that does not extend the baseline re-anchors
// instead of deleting.
const taTypes = (() => {
  // A module-level const, not part of the function body, so read it off the source.
  const m = source.match(/const TA_DELETE_TYPES = (\/[^\n]+?\/);/);
  return m ? m[1] : null;
})();

test('a stale baseline is re-anchored, never forwarded as backspaces', () => {
  const impact = 'a stale textarea baseline must not be forwarded as backspaces';
  assert.ok(taTypes, `${impact}: the forwarder has no delete-type rule`);
  const isDelete = new RegExp(taTypes.replace(/^\/|\/$/g, ''));
  assert.equal(isDelete.test('deleteContentBackward'), true, 'a real backspace is a delete');
  assert.equal(isDelete.test('deleteWordBackward'), true, 'a word delete is a delete');
  assert.equal(isDelete.test('deleteSoftLineBackward'), true, 'a soft-line delete is a delete');
  for (const insert of ['insertText', 'insertCompositionText', 'insertReplacementText', 'insertLineBreak']) {
    assert.equal(isDelete.test(insert), false, `${insert} must never delete`);
  }
  // And the forwarder only emits DELs when the event WAS a delete.
  const block = functionSection('taFlushDelta', impact);
  const guard = block.indexOf('!TA_DELETE_TYPES.test(inputType) && back > 0');
  assert.notEqual(guard, -1, `${impact}: the re-anchor is not gated on the event being an insert`);
  assert.ok(block.indexOf('back = 0') > guard, `${impact}: the insert path does not clear the delete count`);
});

test('the mobile helper textarea is 16px, so iOS does not zoom into the terminal', () => {
  // iOS Safari auto-zooms when a focused field is under 16px. The helper textarea is xterm's hidden input
  // and measured 13.3px, so every tap into the terminal zoomed the page.
  assert.match(source, /\.xterm-helper-textarea\s*\{[^}]*font-size\s*:\s*16px/,
    'the helper textarea must not be under 16px');
  // The message composer had the same problem at 15px.
  assert.match(source, /#composer textarea\s*\{[^}]*font-size\s*:\s*16px/s,
    'the composer must not be under 16px');
});

test('the app shell disables double-tap zoom but keeps pinch', () => {
  // Nothing set touch-action, so double-tapping the pane zoomed the whole view ("it constantly zooms in and
  // out"). manipulation still allows a deliberate pinch.
  assert.match(source, /#app\s*\{[^}]*touch-action\s*:\s*manipulation/s, 'the app shell must not double-tap zoom');
  assert.match(source, /#term\s*\{[^}]*touch-action\s*:\s*manipulation/s, 'and neither must the pane');
  assert.doesNotMatch(source, /touch-action\s*:\s*none/, 'pinch-zoom must stay possible');
});

test('every bottom-nav destination is on screen at phone widths', () => {
  // The row was a scrolling strip of fixed 62px items (477px total), so Search chats and Keys sat past the
  // right edge at every phone width - "there is no keys button" was literally true.
  // The rule lives inside the phone media query, so take the LAST #mobileNav block (the mobile one).
  const navBlocks = [...source.matchAll(/#mobileNav\s*\{[^}]*\}/gs)].map(m => m[0]);
  const nav = navBlocks.find(b => /grid-auto-flow/.test(b)) || '';
  assert.match(nav, /grid-auto-flow\s*:\s*column/, 'the nav must lay its items out as equal columns');
  assert.match(nav, /grid-auto-columns\s*:\s*minmax\(0\s*,\s*1fr\)/, 'and those columns must share the width');
  assert.doesNotMatch(nav, /overflow-x\s*:\s*auto/, 'a scrolling nav is what hid the Keys button');
  assert.match(source, /#mobileNav button,\s*#mobileNav a\s*\{[^}]*min-width\s*:\s*0/s,
    'items must be allowed to shrink into their column');
});

test('the terminal view has quick keys, and they do not summon the soft keyboard', () => {
  assert.match(source, /id="quickkeys"/, 'the terminal needs a quick-key strip');
  for (const key of ['esc', 'tab', 'up', 'down', 'left', 'right', 'enter']) {
    assert.match(source, new RegExp(`data-qk="${key}"`), `the strip is missing ${key}`);
  }
  const send = functionSection('sendQuickKey', 'quick keys must not steal focus on a phone');
  assert.match(send, /seqForKey\s*\(/, 'a quick key sends the same sequence the Keys view does');
  assert.match(send, /!isMobile\(\)\),?\s*term\.focus\(\)|if\s*\(\s*!isMobile\(\)\s*\)\s*term\.focus\(\)/,
    'focus is returned on desktop only - on a phone it would pop the keyboard');
});

test('a display-only control never pulls focus into the terminal on a phone', () => {
  // Tapping the size chip opened the keyboard over the screen the user was reading.
  const refocus = functionSection('refocusTerminal', 'a readout tap must not open the keyboard');
  assert.match(refocus, /isMobile\s*\(\s*\)/, 'the phone case is handled explicitly');
  assert.match(refocus, /ownerTap/, 'the caller says whether a tap owns the keyboard');
  const ladder = functionSection('cycleSize', 'the size ladder must not open the keyboard');
  assert.match(ladder, /refocusTerminal\(\s*\{\s*ownerTap\s*:\s*true\s*\}\s*\)/,
    'the size chip must hand the tap back rather than focusing the terminal');
});
