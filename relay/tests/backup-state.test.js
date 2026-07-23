// Hermetic tests for scripts/backup-state.sh — the off-box backup of the relay's
// durable state. Everything runs against tests/fixtures/backup-state and temp dirs;
// no VPS, no ssh, no network. The fixture deliberately parks identity keys, a client
// private key, a local-control credential, a principal registry and session ACL state
// right next to the durable JSON, so "the backup left the secrets behind" is an
// assertion about real neighbouring files rather than a hypothetical.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const posix = (p) => p.replace(/\\/g, '/');

const RELAY_DIR = path.join(__dirname, '..');
const SCRIPT = posix(path.join(RELAY_DIR, '..', 'scripts', 'backup-state.sh'));
const FIXTURE = posix(path.join(__dirname, 'fixtures', 'backup-state'));

const DURABLE = ['projects.json', 'app-commands.json', 'pins.json', 'uploads-meta.json', 'rename-intents.json'];
// Present in the fixture, must never reach the archive.
const SECRETS = ['muxd-identity.json', 'client-private.key', 'local-control.cred', 'principals.json', 'session-acl.json'];

const tempDirs = [];
function tmp(prefix) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), prefix));
  tempDirs.push(dir);
  return dir;
}
test.after(() => {
  for (const dir of tempDirs) fs.rmSync(dir, { recursive: true, force: true });
});

function runBackup(args, opts = {}) {
  return spawnSync('bash', [opts.script || SCRIPT, ...args], {
    cwd: opts.cwd || RELAY_DIR,
    encoding: 'utf8',
  });
}

// tar reads an -f argument containing a colon as a remote host:path spec, so always
// cd into the archive's directory and name it relatively.
function tarIn(tgz, flags, extra = '') {
  const res = spawnSync(
    'bash',
    ['-c', `cd "${posix(path.dirname(tgz))}" && tar ${flags} "${path.basename(tgz)}" ${extra}`],
    { encoding: 'utf8' },
  );
  assert.equal(res.status, 0, `tar ${flags} failed: ${res.stderr}`);
  return res.stdout;
}

function listArchive(tgz) {
  return tarIn(tgz, '-tzf').split('\n').map((l) => l.trim()).filter(Boolean);
}

function extractArchive(tgz, into) {
  tarIn(tgz, '-xzf', `-C "${posix(into)}"`);
}

// One dry run + verify feeds most of the assertions below.
const outDir = tmp('mux-backup-out-');
const archive = path.join(outDir, 'mux-relay-state-dryrun.tgz');
const built = runBackup(['--dry-run-to', posix(outDir), '--state-dir', FIXTURE, '--verify']);

test('dry run with --verify builds an archive and proves the restore roundtrip', () => {
  assert.equal(built.status, 0, `backup-state.sh failed:\n${built.stdout}\n${built.stderr}`);
  assert.match(built.stdout, /verified restore roundtrip: 7 file\(s\) byte-identical/);
  assert.match(built.stdout, /no scp/);
  assert.ok(fs.existsSync(archive), 'dry run did not write the archive');
});

test('archive carries the durable state plus the .bak siblings durable-state.js relies on', () => {
  const members = listArchive(archive).filter((m) => !m.endsWith('/'));
  const expected = [
    'relay-state/app-commands.json',
    'relay-state/EXPOSURE.txt',
    'relay-state/MANIFEST.sha256',
    'relay-state/pins.json',
    'relay-state/pins.json.bak',
    'relay-state/projects.json',
    'relay-state/projects.json.bak',
    'relay-state/rename-intents.json',
    'relay-state/uploads-meta.json',
  ];
  assert.deepEqual(members.sort(), expected.sort());
  // .bak siblings are load-bearing: durableJsonLoad() falls back to them on a torn write.
  for (const name of DURABLE) {
    if (fs.existsSync(path.join(FIXTURE, `${name}.bak`))) {
      assert.ok(members.includes(`relay-state/${name}.bak`), `missing .bak sibling for ${name}`);
    }
  }
});

test('archive leaves identity keys, credentials, principals and ACL state behind', () => {
  const listing = listArchive(archive).join('\n');
  for (const secret of SECRETS) {
    assert.ok(fs.existsSync(path.join(FIXTURE, secret)), `fixture lost its ${secret} decoy`);
    assert.ok(!listing.includes(secret), `archive leaked ${secret}`);
  }
  const restored = tmp('mux-backup-restore-');
  extractArchive(archive, restored);
  const manifest = fs.readFileSync(path.join(restored, 'relay-state', 'MANIFEST.sha256'), 'utf8');
  for (const secret of SECRETS) assert.ok(!manifest.includes(secret), `manifest references ${secret}`);
});

test('archive documents its own exposure profile for whoever restores it', () => {
  const restored = tmp('mux-backup-exposure-');
  extractArchive(archive, restored);
  const exposure = fs.readFileSync(path.join(restored, 'relay-state', 'EXPOSURE.txt'), 'utf8');
  for (const phrase of ['muxd identity keys', 'client private keys', 'local-control', 'principal registries', 'session ACL']) {
    assert.ok(exposure.includes(phrase), `EXPOSURE.txt does not name the excluded ${phrase}`);
  }
  for (const phrase of ['plaintext transcript', 'archive indexes', 'signed intent envelopes', 'timing']) {
    assert.ok(exposure.includes(phrase), `EXPOSURE.txt does not name the inherited exposure: ${phrase}`);
  }
  assert.match(exposure, /as sensitive as the relay host/);
});

test('repeat dry runs overwrite in place instead of piling archives up', () => {
  const again = runBackup(['--dry-run-to', posix(outDir), '--state-dir', FIXTURE]);
  assert.equal(again.status, 0, again.stderr);
  const tgz = fs.readdirSync(outDir).filter((f) => f.endsWith('.tgz'));
  assert.deepEqual(tgz, ['mux-relay-state-dryrun.tgz']);
});

test('an empty state dir is a hard failure, not a silently empty backup', () => {
  const empty = tmp('mux-backup-empty-');
  const res = runBackup(['--dry-run-to', posix(tmp('mux-backup-emptyout-')), '--state-dir', posix(empty)]);
  assert.notEqual(res.status, 0, 'shipping an empty backup should fail the run');
  assert.match(res.stderr, /refusing to ship an empty backup/);
});

test('a missing state dir fails before anything is staged', () => {
  const gone = posix(path.join(tmp('mux-backup-gone-'), 'nope'));
  const res = runBackup(['--dry-run-to', posix(tmp('mux-backup-goneout-')), '--state-dir', gone]);
  assert.notEqual(res.status, 0);
  assert.match(res.stderr, /state dir does not exist/);
});

test('the refusal sweep catches an allowlist widened onto secret-bearing state', () => {
  // Regression guard for the trust amendment: the allowlist is the primary defence,
  // but a future edit that adds a secret-bearing name must still be refused by name.
  const source = fs.readFileSync(SCRIPT.replace(/\//g, path.sep), 'utf8');
  const widened = source.replace('  rename-intents.json\n)', '  rename-intents.json\n  principals.json\n)');
  assert.notEqual(widened, source, 'could not patch the allowlist — did STATE_FILES change shape?');
  const scriptCopy = path.join(tmp('mux-backup-widened-'), 'backup-state.sh');
  fs.writeFileSync(scriptCopy, widened);

  const res = runBackup(
    ['--dry-run-to', posix(tmp('mux-backup-widenedout-')), '--state-dir', FIXTURE],
    { script: posix(scriptCopy) },
  );
  assert.notEqual(res.status, 0, 'a widened allowlist should abort the run');
  assert.match(res.stderr, /allowlist entry principals\.json is refused by the trust boundary/);
  assert.match(res.stderr, /name contains .?principal/);
});

test('the state dir is resolved against the caller cwd, not the script location', () => {
  // The committed verifier runs from relay/ and passes a relay-relative path.
  const res = runBackup(['--dry-run-to', posix(tmp('mux-backup-rel-')), '--state-dir', 'tests/fixtures/backup-state']);
  assert.equal(res.status, 0, res.stderr);
  assert.match(res.stdout, /7 file\(s\), no scp/);
});
