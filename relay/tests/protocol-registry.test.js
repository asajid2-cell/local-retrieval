const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const repo = path.join(__dirname, '..', '..');
const registry = fs.readFileSync(path.join(repo, 'docs', 'protocol-fields.md'), 'utf8');
const muxd = fs.readFileSync(path.join(repo, 'muxd', 'muxd.py'), 'utf8');
const server = fs.readFileSync(path.join(repo, 'relay', 'server.js'), 'utf8');

function literalList(source, declaration, open, close, label) {
  const from = source.indexOf(declaration);
  assert.notEqual(from, -1, `missing declaration: ${label}`);
  const start = source.indexOf(open, from);
  const end = source.indexOf(close, start);
  assert.ok(start !== -1 && end !== -1, `unterminated declaration: ${label}`);
  const body = source.slice(start + open.length, end);
  const items = [...body.matchAll(/'([^']+)'|"([^"]+)"/g)].map(m => m[1] || m[2]);
  assert.ok(items.length > 0, `${label} declared no capabilities`);
  return items;
}

// The registry records a capability as a table row whose first cell is the bare literal.
// A passing mention in prose is deliberately NOT enough - it has to be a registry entry.
function registered(cap) {
  return new RegExp(`^\\|\\s*\`${cap.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\`\\s*\\|`, 'm').test(registry);
}

const muxdCaps = literalList(muxd, 'CAPS = [', '[', ']', 'muxd.py CAPS');
const requiredCaps = literalList(server, 'REQUIRED_HOST_CAPS = new Set(', '[', ']', 'server.js REQUIRED_HOST_CAPS');

test('every capability muxd advertises is registered in docs/protocol-fields.md', () => {
  const missing = muxdCaps.filter(cap => !registered(cap));
  assert.deepEqual(missing, [], `muxd.py CAPS entries absent from the registry: ${missing.join(', ')}`);
});

test('every capability the relay requires of the host is registered', () => {
  const missing = requiredCaps.filter(cap => !registered(cap));
  assert.deepEqual(missing, [], `REQUIRED_HOST_CAPS entries absent from the registry: ${missing.join(', ')}`);
});

test('the relay cannot require a capability muxd never advertises', () => {
  const unmet = requiredCaps.filter(cap => !muxdCaps.includes(cap));
  assert.deepEqual(unmet, [], `REQUIRED_HOST_CAPS entries missing from muxd.py CAPS: ${unmet.join(', ')}`);
});

test('the registry protocol number matches muxd and the relay', () => {
  const doc = registry.match(/^- \*\*Protocol number:\*\* `(\d+)`/m);
  assert.ok(doc, 'registry does not state a protocol number in the expected form');
  const host = muxd.match(/^PROTOCOL = (\d+)/m);
  const relay = server.match(/^const REQUIRED_HOST_PROTOCOL = (\d+);/m);
  assert.ok(host, 'muxd.py does not declare PROTOCOL');
  assert.ok(relay, 'server.js does not declare REQUIRED_HOST_PROTOCOL');
  // The relay tests protocol equality, so a bump on one side alone bricks the pairing.
  assert.equal(host[1], relay[1], 'muxd PROTOCOL and relay REQUIRED_HOST_PROTOCOL disagree');
  assert.equal(doc[1], host[1], 'the registry protocol number is stale');
});

test('the additive/no-bump rule and the trust-amendment names stay registered', () => {
  assert.match(registry, /Add fields and capabilities\. Never bump the protocol number\./);
  const trust = [
    'principalAuthV1', 'principalAclV1', 'muxdSignedOutputV1',
    'muxdWriteLeaseV1', 'deferredInputV1',
  ];
  const missing = trust.filter(cap => !registered(cap));
  assert.deepEqual(missing, [], `trust-amendment capabilities absent from the registry: ${missing.join(', ')}`);
  const terminal = ['snapshot', 'sbtext', 'input'];
  const missingTerminal = terminal.filter(cap => !registered(cap));
  assert.deepEqual(missingTerminal, [], `terminal-model capabilities absent from the registry: ${missingTerminal.join(', ')}`);
});
