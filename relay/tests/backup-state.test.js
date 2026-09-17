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
const BASH = process.platform === 'win32'
  ? [
      path.join(process.env.ProgramFiles || 'C:\\Program Files', 'Git', 'bin', 'bash.exe'),
      path.join(process.env.LOCALAPPDATA || '', 'Programs', 'Git', 'bin', 'bash.exe'),
    ].find((candidate) => candidate && fs.existsSync(candidate)) || 'bash'
  : 'bash';

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
  return spawnSync(BASH, [opts.script || SCRIPT, ...args], {
    cwd: opts.cwd || RELAY_DIR,
    encoding: 'utf8',
  });
}

// tar reads an -f argument containing a colon as a remote host:path spec, so always
// cd into the archive's directory and name it relatively.
function tarIn(tgz, flags, extra = '') {
  const res = spawnSync(
    BASH,
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
  // The dry run must say it sent nothing. The wording tracks the transport: it used to say "no scp",
  // and the real run now uses sftp (the PC's sshd runs a non-POSIX shell, so a remote `mkdir` there is
  // not sh-parsed). What this line guards is unchanged — that a dry run never touches the network.
  assert.match(built.stdout, /nothing sent/);
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
  assert.match(res.stdout, /7 file\(s\), nothing sent/);
});

// The send path is the one place this script must not guess about the far side. The destination is a
// Windows OpenSSH server with a non-POSIX login shell (measured: PowerShell 5.1, taken from OpenSSH's
// registry DefaultShell, since sshd_config sets none), so the original
// `ssh win "mkdir -p 'mux-relay-backups'"` was parsed by that shell: single quotes do not quote there,
// and the directory that got created was not the one scp was told to write. The run now drives sftp instead,
// which never invokes the remote shell. A stub captures exactly what was sent, so the batch commands
// are asserted rather than assumed — no route, no host key, no network.
//
// The stub is an EXPORTED BASH FUNCTION, not a script on PATH: this suite runs under MSYS bash on
// Windows, where a shebang file with no extension cannot be exec'd, so a PATH stub would silently
// fall through to the real sftp. An exported function is inherited by the child shell and shadows the
// command outright (verified: bash 5.2 exports via BASH_FUNC_<name>%%).
// A single stub has to serve every sftp invocation the run makes (the put, the
// listing, and the prune), so it numbers its calls and records each one separately.
// `-b -` batches arrive on stdin; a `-b <path>` batch is a file the run built, so it
// is copied out before the temp dir it lives in is cleaned up. `LISTING` is printed
// on the listing call so retention has something to decide about.
function stubEnv(binDir, listing = []) {
  const base = posix(binDir);
  const argsAt = (n) => posix(path.join(binDir, `args-${n}.txt`));
  const stdinAt = (n) => posix(path.join(binDir, `stdin-${n}.txt`));
  const copyAt = (n) => posix(path.join(binDir, `batch-${n}.txt`));
  // The batch arrives on stdin, so the LISTING is the call whose stdin carries
  // `ls -1 mux-relay-state-*` - keying on the recorded arguments cannot work, because
  // the arguments are the same for every call. Filenames are built inside the shell by
  // concatenation: a single-quoted `args-$n.txt` would never expand, so every call
  // would overwrite one file and the per-call assertions would silently read the
  // first call's arguments instead of the one under test.
  const listLines = listing.map((l) => `'${l}'`).join(' ');
  return {
    argsAt,
    stdinAt,
    copyAt,
    env: {
      ...process.env,
      'BASH_FUNC_sftp%%': `() {
        c='${base}/call-count'
        n=$(cat "$c" 2>/dev/null || printf 0); n=$((n+1)); printf '%s' "$n" > "$c"
        printf '%s\\n' "$@" > '${base}/args-'"$n"'.txt'
        batch='${base}/stdin-'"$n"'.txt'
        prev=""
        for a in "$@"; do
          if [ "$prev" = "-b" ] && [ "$a" != "-" ]; then batch='${base}/batch-'"$n"'.txt'; cp "$a" "$batch"; fi
          prev="$a"
        done
        case " $* " in *" -b - "*) cat > "$batch" ;; esac
        if grep -q 'mux-relay-state-\\*' "$batch" 2>/dev/null; then
          # The real route echoes the command and answers with the path relative to
          # the directory it was given. Reproducing that shape matters: a clean
          # bare-name listing would make the parser look correct while the live sweep
          # matched nothing and silently kept the whole directory forever.
          printf 'sftp> ls -1 mux-relay-state-*\\n'
          for l in ${listLines}; do printf './%s\\n' "$l"; done
          printf 'sftp-mux-prune-sentinel\\n'
        fi
      }`,
    },
  };
}

test('the real run drives sftp with a create-then-put batch, never a remote shell command', () => {
  const binDir = tmp('mux-backup-bin-');
  const stub = stubEnv(binDir);
  const argsFile = stub.argsAt(1);
  const batchFile = stub.stdinAt(1);

  const res = spawnSync(BASH, [SCRIPT, '--state-dir', FIXTURE, '--ssh-host', 'win', '--no-prune'], {
    cwd: RELAY_DIR,
    encoding: 'utf8',
    env: stub.env,
  });

  assert.equal(res.status, 0, `the stubbed send should succeed:\n${res.stdout}\n${res.stderr}`);
  const args = fs.readFileSync(argsFile, 'utf8').split('\n').filter(Boolean);
  assert.equal(args[0], '-q', 'quiet: an hourly timer must not fill the journal');
  assert.ok(args.includes('-b'), '-b reads the batch from stdin');
  assert.ok(args.includes('-'), 'the batch is the stdin operand');
  assert.ok(args.includes('win'), 'the destination alias is passed through');
  // -F pins the route file. ssh takes its per-user config from the account's PASSWD home, not from
  // the HOME this unit exports, and svc-multiplex's passwd home does not exist - so relying on
  // discovery meant the `Host win` alias never applied and the run died on "Could not resolve
  // hostname win" before it ever reached the key or the host key.
  assert.ok(
    args.includes('-F') && args.includes('/var/lib/multiplex/.ssh/config'),
    `the send must name its ssh config explicitly; got ${JSON.stringify(args)}`,
  );
  for (const opt of ['BatchMode=yes', 'ConnectTimeout=10', 'StrictHostKeyChecking=yes']) {
    assert.ok(args.includes(opt), `unattended runs must pin ${opt}; got ${JSON.stringify(args)}`);
  }

  // The batch itself is the contract with the far side. `-mkdir` is the idempotent form of `mkdir -p`
  // (create it, do not fail if it exists); `put` names the archive once, relatively, so nothing about
  // the destination path depends on a remote shell expanding it.
  const batch = fs.readFileSync(batchFile, 'utf8').split('\n').map((l) => l.trim()).filter(Boolean);
  assert.equal(batch.length, 2, `exactly two batch commands; got ${JSON.stringify(batch)}`);
  assert.equal(batch[0], '-mkdir mux-relay-backups');
  assert.match(batch[1], /^put .*mux-relay-state-.*\.tgz mux-relay-backups\/mux-relay-state-.*\.tgz$/);
  assert.ok(!/\bmkdir\b\s+-p/.test(batch.join('\n')), 'the sh-only `mkdir -p` form must be gone');

  assert.match(res.stdout, /sent mux-relay-state-.*\.tgz to win:mux-relay-backups\//);
});

// --- retention ----------------------------------------------------------------
// The archive name carries a UTC stamp, so every run is a new file and an hourly
// timer would leave ~8760 near-identical copies a year on the far side. These tests
// pin both halves: the decision (pure, so it is asserted exhaustively offline) and
// the wiring (the deletes must ride an sftp batch that runs AFTER the put, and the
// sweep must never name anything that is not one of our archives).

const stamp = (iso, hh, mm = '00', ss = '00') => `mux-relay-state-${iso}T${hh}${mm}${ss}Z.tgz`;

function plan(lines, args = []) {
  const res = spawnSync(BASH, [SCRIPT, '--prune-plan', ...args], {
    input: lines.join('\n') + '\n',
    encoding: 'utf8',
  });
  assert.equal(res.status, 0, `--prune-plan failed: ${res.stderr}`);
  const decisions = new Map();
  for (const line of res.stdout.split('\n').filter(Boolean)) {
    const [verb, ...rest] = line.split(' ');
    decisions.set(rest.join(' '), verb);
  }
  return decisions;
}

const dayOf = (name) => name.slice('mux-relay-state-'.length, 'mux-relay-state-'.length + 8);

// Builds a valid stamp for N consecutive days starting 2026-01-01. Written with a real
// date object because the date field must be exactly 8 digits: a hand-rolled
// `2026010${d}` produces a 9-digit field for d >= 10, which fails the archive match,
// and every entry then reads as a foreign file that is always kept.
function daysFrom(startIso, count, perDay = 1) {
  const out = [];
  const start = Date.parse(`${startIso}T00:00:00Z`);
  for (let d = 0; d < count; d += 1) {
    const iso = new Date(start + d * 86400000).toISOString().slice(0, 10).replace(/-/g, '');
    for (let i = 0; i < perDay; i += 1) {
      out.push(stamp(iso, String(Math.floor((i * 24) / perDay)).padStart(2, '0')));
    }
  }
  return out;
}

test('retention stops at keep-daily and never keeps an unbounded history', () => {
  // The cap is the whole point of the flag: with more distinct days than keep-daily,
  // everything past it must go. Exercised with 45 distinct days and a small window so
  // the branch that deletes on the cap is actually reached.
  const lines = daysFrom('2026-01-01', 45);
  assert.equal(new Set(lines.map(dayOf)).size, 45, 'the fixture must span 45 distinct days');
  const decisions = plan(lines, ['--keep-recent', '2', '--keep-daily', '5']);
  const kept = [...decisions.entries()].filter(([, v]) => v === 'keep').map(([k]) => k);
  assert.equal(kept.length, 7, '2 newest outright plus one for each of 5 older days');
  assert.equal([...decisions.values()].filter((v) => v === 'delete').length, 38);
  // The 7 keepers are the two newest days and the newest of the five days before them.
  // Anything older than that window must be pruned — 45 distinct days, 7 retained.
  const distinct = [...new Set(lines.map(dayOf))].sort();
  assert.deepEqual(kept.map(dayOf).sort(), distinct.slice(-7),
    'the keepers must be a suffix of the day sequence, not an arbitrary subset');
});

test('retention keeps the newest N outright and one archive per older day', () => {
  // 30 days x 2 archives/day = 60. The newest 24 outright is the last 12 days entire,
  // leaving 18 older days at 2 each; each of those contributes its newest and deletes
  // the other. So 24 + 18 kept, 18 deleted.
  const lines = [];
  for (let d = 1; d <= 30; d += 1) {
    const iso = `202608${String(d).padStart(2, '0')}`;
    lines.push(stamp(iso, '00'), stamp(iso, '12'));
  }
  const decisions = plan(lines);
  assert.equal([...decisions.values()].filter((v) => v === 'keep').length, 42);
  assert.equal([...decisions.values()].filter((v) => v === 'delete').length, 18);
  // The keeper for each older day is that day's NEWEST archive, not an arbitrary one.
  assert.equal(decisions.get(stamp('20260805', '12')), 'keep');
  assert.equal(decisions.get(stamp('20260805', '00')), 'delete');
});

test('retention bounds a year of hourly archives to the same ceiling as daily ones', () => {
  // The regression that matters: cadence must not change the ceiling. 24 days of
  // hourly archives (576) and 24 daily archives both land at 24 + 30.
  const hourly = [];
  for (let d = 1; d <= 24; d += 1) {
    for (let h = 0; h < 24; h += 1) {
      hourly.push(stamp(`202608${String(d).padStart(2, '0')}`, String(h).padStart(2, '0'), '00'));
    }
  }
  const decisions = plan(hourly);
  const kept = [...decisions.entries()].filter(([, v]) => v === 'keep').map(([k]) => k);
  // 24 newest hours = the whole last day; the other 23 days contribute one each.
  assert.equal(kept.length, 47, 'an hourly year must not grow without bound');
  const newest = [...hourly].sort().reverse().slice(0, 24);
  for (const name of kept) {
    if (newest.includes(name)) continue;
    const sameDay = kept.filter((n) => dayOf(n) === dayOf(name));
    assert.equal(sameDay.length, 1, `day ${dayOf(name)} kept ${sameDay.length} archives, not 1`);
  }
});

test('retention never deletes anything it does not recognise', () => {
  // The directory may be shared, and a name this script did not write is not this
  // script's to remove. Whitespace and traversal shapes are refused by the same
  // strict match, which is also what keeps a newline out of the sftp batch.
  const foreign = ['notes.txt', 'mux-relay-state-BAD.tgz', 'mux-relay-state-20260916T224625Z.tgz.bak',
    'mux-relay-state-20260916T224625Z.tgz.1', '../escape', 'mux-relay-state-20260916T22462Z.tgz'];
  const decisions = plan([...foreign, stamp('20260916', '22', '46', '25')]);
  for (const name of foreign) assert.equal(decisions.get(name), 'keep', `${name} must be left alone`);
});

test('retention flags refuse values that would turn a sweep into a wipe', () => {
  const zero = spawnSync(BASH, [SCRIPT, '--prune-plan', '--keep-recent', '0'], { input: '', encoding: 'utf8' });
  assert.notEqual(zero.status, 0);
  assert.match(zero.stderr, /at least the archive just sent/);
  const junk = spawnSync(BASH, [SCRIPT, '--prune-plan', '--keep-recent', 'abc'], { input: '', encoding: 'utf8' });
  assert.notEqual(junk.status, 0);
  assert.match(junk.stderr, /non-negative integer/);
});

test('a real run lists, then prunes over the same sftp route, after the put', () => {
  const binDir = tmp('mux-backup-prune-');
  // The listing the PC actually held on 2026-09-17: five archives over two days, plus
  // a file this script did not write.
  const listing = [stamp('20260916', '22', '46', '25'), stamp('20260916', '22', '49', '43'),
    stamp('20260916', '23', '03', '17'), stamp('20260917', '00', '02', '34'),
    stamp('20260917', '01', '00', '33'), 'README.txt'];
  const stub = stubEnv(binDir, listing);

  const res = spawnSync(BASH, [SCRIPT, '--state-dir', FIXTURE, '--ssh-host', 'win',
    '--keep-recent', '2', '--keep-daily', '1'], {
    cwd: RELAY_DIR,
    encoding: 'utf8',
    env: stub.env,
  });
  assert.equal(res.status, 0, `pruning run failed:\n${res.stdout}\n${res.stderr}`);

  // Three calls, in this order: put, listing, prune. The listing comes after the put
  // so a fresh archive is never a candidate for its own sweep.
  const call1 = fs.readFileSync(stub.stdinAt(1), 'utf8');
  assert.match(call1, /-mkdir mux-relay-backups/, 'call 1 is the put');
  const listArgs = fs.readFileSync(stub.argsAt(2), 'utf8');
  assert.match(listArgs, /StrictHostKeyChecking=yes/, 'the listing pins the route too');
  const pruneArgs = fs.readFileSync(stub.argsAt(3), 'utf8');
  assert.match(pruneArgs, /-b/, 'the prune rides a batch file, not stdin');

  const batch = fs.readFileSync(stub.copyAt(3), 'utf8');
  assert.match(batch, /^cd mux-relay-backups$/m, 'deletes are relative to the remote dir');
  // keep-recent=2 holds the two 09-17 archives outright. The next one, 09-16T230317Z,
  // is the newest archive of the older day, so keep-daily=1 holds that too. The two
  // remaining same-day copies are the surplus, and only they may be named.
  assert.match(batch, /^rm mux-relay-state-20260916T224943Z\.tgz$/m);
  assert.match(batch, /^rm mux-relay-state-20260916T224625Z\.tgz$/m);
  for (const keep of ['20260917T010033Z', '20260917T000234Z', '20260916T230317Z']) {
    assert.doesNotMatch(batch, new RegExp(keep), `${keep} must survive the sweep`);
  }
  assert.ok(!batch.includes('README.txt'), 'a foreign file must never be named in a delete batch');
  assert.match(res.stdout, /retention: kept 4, pruned 2 of them from mux-relay-backups/);
});

test('--no-prune sends the archive and touches nothing else', () => {
  const binDir = tmp('mux-backup-noprune-');
  const stub = stubEnv(binDir, [stamp('20260916', '22', '46', '25')]);
  const res = spawnSync(BASH, [SCRIPT, '--state-dir', FIXTURE, '--ssh-host', 'win', '--no-prune'], {
    cwd: RELAY_DIR,
    encoding: 'utf8',
    env: stub.env,
  });
  assert.equal(res.status, 0, res.stderr);
  assert.equal(fs.existsSync(stub.argsAt(2)), false, '--no-prune must not make a second call');
  assert.ok(!/pruned/.test(res.stdout));
});

test('an incomplete remote listing is refused, and the backup still succeeds', () => {
  // The put succeeded; failing the unit here would cost a full day of backups over a
  // directory that only needs a human to glance at it. But an incomplete listing must
  // never be ACTED on: deciding what to delete from a stream that stopped early is how
  // a sweep eats backups. This stub answers every listing with nothing at all, which
  // is the shape of an unknown shell or a truncated read - the sentinel never arrives.
  const binDir = tmp('mux-backup-nolist-');
  const stub = stubEnv(binDir, []);
  const noAnswer = stub.env['BASH_FUNC_sftp%%'].replace('if grep -q', 'if false && grep -q');
  assert.notEqual(noAnswer, stub.env['BASH_FUNC_sftp%%'], 'the stub could not be silenced');
  const res = spawnSync(BASH, [SCRIPT, '--state-dir', FIXTURE, '--ssh-host', 'win'], {
    cwd: RELAY_DIR,
    encoding: 'utf8',
    env: { ...stub.env, 'BASH_FUNC_sftp%%': noAnswer },
  });
  assert.equal(res.status, 0, `a failed listing must not fail the run:\n${res.stdout}\n${res.stderr}`);
  assert.match(res.stdout, /retention: WARNING — the remote listing did not complete/);
  assert.match(res.stdout, /sent mux-relay-state-.*\.tgz to win/);
  // And it must not have tried to delete anything.
  assert.equal(fs.existsSync(stub.argsAt(3)), false, 'an incomplete listing must not reach a prune call');
});
