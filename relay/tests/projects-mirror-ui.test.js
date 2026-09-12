const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', 'public', 'projects.html'), 'utf8');
const terminalSource = fs.readFileSync(path.join(__dirname, '..', 'public', 'index.html'), 'utf8');

test('projects page scripts remain syntactically valid', () => {
  const scripts = [...source.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/gi)]
    .map(match => match[1])
    .filter(text => text.trim());
  assert.ok(scripts.length > 0);
  for (const script of scripts) new vm.Script(script, { filename: 'projects.html' });
});

test('running local chats offer Mirror and enqueue mirrorlocal without a destructive handoff', () => {
  const button = source.indexOf("btn.textContent='Mirror'");
  const helper = source.indexOf('async function mirrorAndOpen');
  const enqueue = source.indexOf("{type:'mirrorlocal'", helper);
  const resume = source.indexOf('async function resume(ch, inMux, localRun)');
  const localBranch = source.indexOf('if(localRun){', resume);
  const mirrorCall = source.indexOf('await mirrorAndOpen({', localBranch);
  const relaunch = source.indexOf("postIntent('api/sessions/'+encodeURIComponent(name)+'/relaunch'", resume);

  assert.ok(button > 0, 'local-running archive rows must say Mirror');
  assert.ok(helper > 0 && enqueue > helper, 'Mirror must enqueue the dedicated app command');
  assert.ok(source.slice(enqueue, enqueue + 180).includes('pid:target.pid'), 'the exact local pid must ride the command');
  assert.ok(localBranch > resume && mirrorCall > localBranch && relaunch > mirrorCall,
    'local-running resume must mirror and return before the destructive relaunch path');
  assert.equal(source.includes('I will stop that copy first'), false);
});

test('running-on-PC rows expose Mirror or Attach only with a verified identity', () => {
  assert.ok(source.includes("mirrorBtn.className='mirrorbtn'"));
  assert.ok(source.includes("mirrorBtn.textContent='Attach'"));
  assert.ok(source.includes('!s.sessionId || !muxName || !s.pid'));
});

test('projects running rows reclaim the chat instead of killing it', () => {
  // One flow per chat: reclaim stops the chat's processes, releases custody and keeps the transcript,
  // whether the agent runs as a mux tab or in a plain local terminal. The old bare "kill" is gone.
  assert.match(source, /reclaimBtn\.textContent='Reclaim'/);
  assert.match(source, /async function reclaimSession\(s, row, btn\)/);
  assert.match(source, /type:'reclaim',confirmed:true/);
  assert.match(source, /This stops the chat's processes and releases its custody\. The transcript is kept/);
  assert.doesNotMatch(source, /type:'kill'/);
  assert.doesNotMatch(source, /'kill-local'/);
  assert.doesNotMatch(source, /detachedAdopted/);
});

test('obsolete kill-before-relaunch helper is gone', () => {
  assert.doesNotMatch(source, /function stopLocalCopyForRelaunch/);
  assert.doesNotMatch(source, /async function awaitCmd\(/);
  assert.doesNotMatch(source, /local PC cop/);
});

test('terminal stop distinguishes detached adopted mirrors from live sessions', () => {
  assert.match(terminalSource, /s\.adopted && s\.detachedLocal/);
  assert.match(terminalSource, /The local terminal and its agent keep running on your PC/);
  assert.match(terminalSource, /result\.detail\|\|'Session ended'/);
});

test('projects controls keep the established visual system and fit mobile navigation', () => {
  assert.match(source, /#wscolbtn\s*\{[^}]*border:1px solid var\(--line\)[^}]*background:var\(--panel-2\)[^}]*color:var\(--txt\)/);
  assert.match(source, /#wscolbtn:hover\s*\{[^}]*border-color:var\(--rose\)/);
  assert.match(source, /#mobileNav\s*\{[\s\S]*?grid-template-columns:repeat\(4,minmax\(0,1fr\)\)/);
});

test('collection dialog reports only the current save result', () => {
  assert.match(source, /id="wscolcount">0 open tabs will be added\./);
  assert.match(source, /dormant tab'\+\(dormant!==1\?'s':''\)\+' skipped\.'/);
  assert.doesNotMatch(source, /Chat-linked tabs keep their chat/);
});

test('collection dialog makes the covered application explicitly inert', () => {
  assert.match(source, /\$\('#app'\)\.inert=true;\s*\$\('#wscoldlg'\)\.showModal\(\)/);
  assert.match(source, /#wscoldlg'\)\.addEventListener\('close',\s*\(\)=>\{\s*\$\('#app'\)\.inert=false;/);
});

test('projects page has distinct loading and initial-load error states', () => {
  assert.match(source, /Loading projects&hellip;/);
  assert.match(source, /Projects could not be loaded\.<br>Retrying automatically\./);
  assert.match(source, /Could not load projects<\/span> &middot; retrying/);
  assert.match(source, /catch\(e\)\{\s*projectsFailed=true;\s*\}/);
  assert.match(source, /if\(nd && initialLoadFailed\)\{[\s\S]*?buildList\(\); updateRunPill\(\); return;/);
  assert.match(source, /id="wscolbtn"[^>]*disabled/);
  assert.match(source, /\$\('#wscolbtn'\)\.disabled=!live/);
});
