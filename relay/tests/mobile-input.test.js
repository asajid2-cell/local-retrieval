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
