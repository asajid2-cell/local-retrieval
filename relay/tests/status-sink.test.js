// The status bar is the one place where session names, API/bridge error details and uploaded
// filenames all converge on the screen. It used to render every one of them as innerHTML.
// These tests pin the split: setStatus() is text-only, setStatusHtml() takes literal markup
// whose interpolations are esc()-wrapped, and nothing else writes to #statustext.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const SOURCE_PATH = path.join(__dirname, '..', 'public', 'index.html');
const source = fs.readFileSync(SOURCE_PATH, 'utf8');

// ---- source extraction helpers ----

function fnSource(name) {
  const sig = `function ${name}(`;
  const from = source.indexOf(sig);
  assert.notEqual(from, -1, `missing function ${name}`);
  assert.equal(source.indexOf(sig, from + 1), -1, `function ${name} is declared more than once`);
  const bodyStart = source.indexOf('{', source.indexOf(')', from));
  let depth = 0;
  for (let i = bodyStart; i < source.length; i++) {
    if (source[i] === '{') depth++;
    else if (source[i] === '}' && --depth === 0) return source.slice(from, i + 1);
  }
  throw new Error(`unbalanced braces in ${name}`);
}

// Every `name(` in the source that is a call, not the declaration, returned as full call text.
function callSites(name) {
  const out = [];
  const needle = `${name}(`;
  for (let at = source.indexOf(needle); at !== -1; at = source.indexOf(needle, at + 1)) {
    if (source.slice(Math.max(0, at - 9), at) === 'function ') continue;
    const open = at + needle.length - 1;
    let depth = 0;
    for (let i = open; i < source.length; i++) {
      if (source[i] === '(') depth++;
      else if (source[i] === ')' && --depth === 0) { out.push(source.slice(at, i + 1)); break; }
    }
  }
  return out;
}

// ---- static: the sink is split and text-only by default ----

test('setStatus writes caller-supplied text as textContent, never innerHTML', () => {
  const fn = fnSource('setStatus');
  assert.match(fn, /\$\('#statustext'\)\.textContent\s*=/);
  assert.doesNotMatch(fn, /innerHTML/, 'setStatus must never reach the innerHTML sink');
});

test('setStatusHtml is the single writer of markup into the status node', () => {
  const writers = source.match(/\$\('#statustext'\)\.innerHTML/g) || [];
  assert.equal(writers.length, 1, 'exactly one innerHTML write to #statustext should exist');
  assert.match(fnSource('setStatusHtml'), /\$\('#statustext'\)\.innerHTML\s*=\s*html/);
});

// ---- static: the setStatusHtml allowlist ----

// A call is allowed only when its markup argument is a literal template (backticked, no
// concatenation, no variable) — so nothing that is merely *stringly* typed can reach innerHTML.
const ALLOWED_CALL = /^setStatusHtml\(\s*[^,`]+,\s*`[^`]*`\s*\)$/;

test('every setStatusHtml call site is a literal template whose interpolations are esc()-wrapped', () => {
  const sites = callSites('setStatusHtml');
  assert.ok(sites.length >= 5, `expected the known markup call sites, found ${sites.length}`);
  for (const site of sites) {
    assert.match(site, ALLOWED_CALL, `setStatusHtml call is not a literal template: ${site}`);
    const template = site.slice(site.indexOf('`') + 1, site.lastIndexOf('`'));
    const interpolations = template.match(/\$\{[^}]*\}/g) || [];
    assert.ok(
      interpolations.length > 0 || !/\$\{/.test(template),
      `unparsed interpolation in: ${site}`
    );
    for (const slot of interpolations) {
      const expr = slot.slice(2, -1).trim();
      assert.ok(
        expr.startsWith('esc(') && expr.endsWith(')'),
        `interpolated value is not esc()-wrapped: ${slot} in ${site}`
      );
    }
  }
});

test('flash forwards its message to the text-only writer', () => {
  const fn = fnSource('flash');
  assert.match(fn, /setStatus\(\s*''\s*,\s*msg\s*\)/, 'flash must send msg through setStatus');
  assert.doesNotMatch(fn, /setStatusHtml\([^)]*msg/, 'flash must never send msg to the markup writer');
});

test('no setStatus call site tries to smuggle markup through the text writer', () => {
  for (const site of callSites('setStatus')) {
    assert.doesNotMatch(site, /<[a-zA-Z/]/, `markup passed to text-only setStatus: ${site}`);
    assert.doesNotMatch(site, /&[a-z]+;/, `HTML entity passed to text-only setStatus: ${site}`);
  }
});

// ---- behavioural: run the real functions against a stub DOM ----

function htmlEscape(s) {
  return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

// Models the browser contract we depend on: assigning textContent means innerHTML reads back
// escaped, so "did a tag survive?" is answerable by inspecting innerHTML.
class StubNode {
  constructor() { this._class = ''; this._text = ''; this._html = ''; }
  get className() { return this._class; }
  set className(v) { this._class = String(v); }
  get classList() { const self = this; return { add(c) { self._class = self._class ? `${self._class} ${c}` : String(c); } }; }
  get textContent() { return this._text; }
  set textContent(v) { this._text = String(v); this._html = htmlEscape(this._text); }
  get innerHTML() { return this._html; }
  set innerHTML(v) { this._html = String(v); this._text = this._html.replace(/<[^>]*>/g, ''); }
}

function loadStatusApi() {
  const status = new StubNode();
  const statustext = new StubNode();
  const timers = [];
  const ctx = {
    current: null,
    ws: null,
    $(sel) {
      if (sel === '#status') return status;
      if (sel === '#statustext') return statustext;
      throw new Error(`unexpected selector ${sel}`);
    },
    setTimeout(fn) { timers.push(fn); return timers.length; },
  };
  vm.createContext(ctx);
  const src = ['esc', 'setStatusState', 'setStatus', 'setStatusHtml', 'flash'].map(fnSource).join('\n');
  vm.runInContext(src, ctx);
  return { ctx, status, statustext, runTimers: () => { while (timers.length) timers.shift()(); } };
}

const PAYLOAD = '<img src=x onerror=1>';

test('flash renders a hostile string as inert text, not markup', () => {
  const { ctx, statustext } = loadStatusApi();
  ctx.flash(PAYLOAD);
  assert.equal(statustext.textContent, PAYLOAD, 'the message must survive verbatim as text');
  assert.doesNotMatch(statustext.innerHTML, /<\s*[a-zA-Z/!]/, 'no element markup may reach the status node');
  assert.ok(!statustext.innerHTML.includes('onerror=1>'), 'the payload must not stay parseable');
  assert.match(statustext.innerHTML, /&lt;img/);
});

test('the taint paths that feed flash stay inert', () => {
  // collection add failure (:api error detail), bridge error, uploaded filename
  for (const taint of [
    `Couldn’t add: <svg onload=alert(1)>`,
    `Relaunch failed: <iframe src="javascript:alert(1)">`,
    `Transferring “<img src=x onerror=alert(1)>” to the PC…`,
  ]) {
    const { ctx, statustext } = loadStatusApi();
    ctx.flash(taint);
    assert.equal(statustext.textContent, taint);
    assert.doesNotMatch(statustext.innerHTML, /<\s*[a-zA-Z/!]/, `markup escaped through: ${taint}`);
  }
});

test('the live-status restore renders its own markup but escapes the session name', () => {
  const { ctx, statustext, status, runTimers } = loadStatusApi();
  ctx.current = '<script>alert(1)</script>';
  ctx.ws = { readyState: 1 };
  ctx.flash(PAYLOAD);
  runTimers();
  assert.match(statustext.innerHTML, /<span class="name">/, 'the trusted literal template still renders');
  assert.doesNotMatch(statustext.innerHTML, /<script/, 'the session name must not become markup');
  assert.match(statustext.innerHTML, /&lt;script&gt;alert\(1\)&lt;\/script&gt;/);
  assert.equal(status.className, 'live');
});

test('setStatus applies state as a class without touching markup', () => {
  const { ctx, status, statustext } = loadStatusApi();
  ctx.setStatus('warn', PAYLOAD);
  assert.equal(status.className, 'warn');
  assert.doesNotMatch(statustext.innerHTML, /<\s*[a-zA-Z/!]/);
  ctx.setStatus('', 'plain');
  assert.equal(status.className, '', 'an empty state must clear the previous class');
});

// ---- the neighbouring innerHTML sites this change deliberately left alone ----

test('the other innerHTML templates keep their dynamic parts escaped', () => {
  const tabChips = source.slice(source.indexOf('const chips ='), source.indexOf("t.querySelector('.nm').textContent"));
  assert.doesNotMatch(tabChips.split('t.innerHTML')[0], /\$\{(?!s\.hosted|s\.legacyTwin|s\.legacyBlocked)/,
    'tab chips must stay literal-only');
  assert.match(source, /t\.querySelector\('\.nm'\)\.textContent = s\.name/, 'the tab name is set as text');
  assert.match(source, /m\.querySelector\('\.mtitle'\)\.textContent = s\.name/, 'the menu title is set as text');
  assert.match(source, /pop\.innerHTML='<div class="mtitle">Tab color · '\+esc\(s\.name\)/);
  assert.match(source, /label\.innerHTML=.*\+esc\(\(e\.title\|\|e\.id\)\.slice\(0,90\)\)/);
});
