#!/usr/bin/env node
// Negative control for tests/agent-truth.test.js — proves the suite is NON-VACUOUS.
//
// Named .mjs (not *.test.js) on purpose: `node --test tests/` collects the whole directory,
// and this script must never be collected as a test — it mutates server.js on disk.
//
// Method: hostProcessTruth() is the single chokepoint for the entire agentTruth feature. When it
// returns null, relay behaviour is byte-for-byte what it was before agentTruth existed. We insert
// an unreachable-code `return null;` as its first statement, assert the suite goes RED with the
// three headline assertions failing, then restore server.js byte-for-byte and assert 10/10 green.
//
// Run from relay/:  node tests/negative-control-agent-truth.mjs

import { strict as assert } from 'node:assert';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const relayDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = path.resolve(relayDir, '..');
const serverPath = path.join(relayDir, 'server.js');
const testFile = 'tests/agent-truth.test.js';

// server.js is CRLF on this checkout; derive the EOL from the file so the anchor matches byte-exactly.
const anchorFor = eol =>
  `function hostProcessTruth(h) {${eol}  if (!hostSupportsCap('agentTruth')) return null;`;
const neuterFor = eol =>
  `function hostProcessTruth(h) {${eol}  return null;${eol}  if (!hostSupportsCap('agentTruth')) return null;`;

const RED_TESTS = [
  'procAlive:false beats a healthy footer and the host heuristic: red and heal-eligible',
  'procAlive:true suppresses the relay pane-regex death read: not red, not heal-eligible',
  'cpuActiveRecent keeps a silent but busy agent green',
];

function runSuite(label) {
  // Pin the TAP reporter — node 22's default is `spec`, whose output has no `not ok` / `# pass N` lines.
  const r = spawnSync(process.execPath, ['--test', '--test-reporter=tap', testFile], {
    cwd: relayDir,
    encoding: 'utf8',
    timeout: 180000,
    maxBuffer: 32 * 1024 * 1024,
  });
  if (r.error) throw new Error(`${label}: failed to spawn node --test: ${r.error.message}`);
  return { code: r.status, out: `${r.stdout || ''}${r.stderr || ''}` };
}

const original = fs.readFileSync(serverPath, 'utf8');
const backupPath = path.join(os.tmpdir(), `relay-server-negctl-${process.pid}.js.bak`);
fs.writeFileSync(backupPath, original, 'utf8');

let red;
try {
  const EOL = original.includes('\r\n') ? '\r\n' : '\n';
  const ANCHOR = anchorFor(EOL);
  const NEUTERED = neuterFor(EOL);
  const occurrences = original.split(ANCHOR).length - 1;
  assert.equal(occurrences, 1,
    `anchor must appear exactly once in relay/server.js, found ${occurrences}. Anchor:\n${ANCHOR}`);

  fs.writeFileSync(serverPath, original.replace(ANCHOR, NEUTERED), 'utf8');
  assert.equal(fs.readFileSync(serverPath, 'utf8').includes(NEUTERED), true,
    'neutered server.js was not written to disk');

  red = runSuite('RED');
  assert.notEqual(red.code, 0,
    `VACUOUS TESTS: tests/agent-truth.test.js still passed (exit 0) with hostProcessTruth() neutered. ` +
    `The suite does not exercise the agentTruth feature.\n${red.out}`);
  for (const name of RED_TESTS) {
    assert.equal(red.out.includes(`not ok`) && red.out.includes(name), true,
      `expected TAP output to contain a failing entry for: ${name}\n${red.out}`);
    const line = red.out.split('\n').find(l => l.includes(name) && /^\s*(not ok|ok)\b/.test(l.trim()));
    assert.equal(line !== undefined && line.trim().startsWith('not ok'), true,
      `expected "not ok" for test: ${name}\ngot line: ${line === undefined ? '<none>' : line.trim()}`);
  }
} finally {
  fs.writeFileSync(serverPath, original, 'utf8');
}

assert.equal(fs.readFileSync(serverPath, 'utf8'), original,
  'relay/server.js was not restored byte-for-byte');

const green = runSuite('GREEN');
assert.equal(green.code, 0, `restored suite must exit 0, got ${green.code}\n${green.out}`);
assert.equal(green.out.includes('# pass 10'), true, `expected "# pass 10" after restore\n${green.out}`);
assert.equal(green.out.includes('# fail 0'), true, `expected "# fail 0" after restore\n${green.out}`);

const git = spawnSync('git', ['status', '--porcelain', 'relay/server.js'], {
  cwd: repoRoot, encoding: 'utf8', timeout: 60000,
});
assert.equal(git.status, 0, `git status failed: ${git.stderr}`);
assert.equal((git.stdout || '').trim(), '',
  `relay/server.js is not clean after the negative control:\n${git.stdout}`);

fs.rmSync(backupPath, { force: true });
console.log(`negative-control OK: neutered => exit ${red.code} with ${RED_TESTS.length} named tests "not ok"; restored => exit 0, # pass 10 / # fail 0; git clean.`);
