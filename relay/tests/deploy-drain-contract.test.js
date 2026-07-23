const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const REPO = path.resolve(__dirname, '..');
const SCRIPT = path.resolve(__dirname, '..', '..', 'scripts', 'deploy-relay.sh');
// deploy-relay.sh is stored with CRLF terminators. Normalize once, here: a trailing '\r' is not
// matched by '.' , so every line-anchored regex below (comment stripping, `^\s*drain_wait\s*$`)
// would silently no-op against the raw text.
const source = fs.readFileSync(SCRIPT, 'utf8').replace(/\r\n/g, '\n');

// Comments explain WHY the restart is SIGTERM-first and name SIGKILL to rule it out, so the
// hard-kill assertion looks at commands only.
const commands = source
  .split('\n')
  .map((line) => line.replace(/(^|\s)#.*$/, ''))
  .join('\n');

function branch(start, end) {
  const from = source.indexOf(start);
  assert.notEqual(from, -1, `deploy-relay.sh no longer contains the branch starting at: ${start}`);
  const to = source.indexOf(end, from + start.length);
  assert.notEqual(to, -1, `deploy-relay.sh no longer contains the branch end marker: ${end}`);
  return source.slice(from, to);
}

test('the pm2 branch sends SIGTERM before it restarts', () => {
  const signal = source.indexOf('pm2 sendSignal SIGTERM multiplex');
  const restart = source.indexOf('pm2 restart multiplex');
  assert.notEqual(signal, -1, 'deploy-relay.sh must run `pm2 sendSignal SIGTERM multiplex`; `pm2 restart` alone sends SIGINT, which has no handler and so skips the drain');
  assert.notEqual(restart, -1, 'deploy-relay.sh must still bring the relay back with `pm2 restart multiplex`');
  assert.ok(signal < restart, 'the SIGTERM must be sent BEFORE `pm2 restart multiplex`; restarting first skips the drain and every browser sees a 1006 close');
});

test('the systemd branch sends SIGTERM before it restarts', () => {
  const signal = source.indexOf('systemctl kill -s SIGTERM multiplex');
  const restart = source.indexOf('systemctl restart multiplex');
  assert.notEqual(signal, -1, 'deploy-relay.sh must run `systemctl kill -s SIGTERM multiplex`; `systemctl restart` alone does not give server.js its drain window');
  assert.notEqual(restart, -1, 'deploy-relay.sh must still bring the relay back with `systemctl restart multiplex`');
  assert.ok(signal < restart, 'the SIGTERM must be sent BEFORE `systemctl restart multiplex`, or the drain is skipped');
});

test('both branches wait for the drained process to exit before restarting', () => {
  assert.match(source, /drain_wait\(\)\s*\{/, 'deploy-relay.sh must define a `drain_wait` function that waits for the signalled relay to exit');

  const pm2 = branch('if pm2 describe multiplex', 'elif ');
  assert.ok(
    pm2.search(/^\s*drain_wait\s*$/m) !== -1,
    'the pm2 branch must call drain_wait on its own line between the signal and the restart'
  );
  assert.ok(
    pm2.indexOf('pm2 sendSignal SIGTERM multiplex') < pm2.search(/^\s*drain_wait\s*$/m),
    'the pm2 branch must call drain_wait AFTER sending SIGTERM'
  );
  assert.ok(
    pm2.search(/^\s*drain_wait\s*$/m) < pm2.indexOf('pm2 restart multiplex'),
    'the pm2 branch must call drain_wait BEFORE `pm2 restart multiplex`, or it restarts on top of a still-draining relay'
  );

  const systemd = branch('elif command -v systemctl', '\nelse');
  assert.ok(
    systemd.search(/^\s*drain_wait\s*$/m) !== -1,
    'the systemd branch must call drain_wait on its own line between the signal and the restart'
  );
  assert.ok(
    systemd.indexOf('systemctl kill -s SIGTERM multiplex') < systemd.search(/^\s*drain_wait\s*$/m),
    'the systemd branch must call drain_wait AFTER `systemctl kill -s SIGTERM multiplex`'
  );
  assert.ok(
    systemd.search(/^\s*drain_wait\s*$/m) < systemd.indexOf('systemctl restart multiplex'),
    'the systemd branch must call drain_wait BEFORE `systemctl restart multiplex`'
  );
});

test('the deploy never hard-kills the relay', () => {
  assert.doesNotMatch(
    commands,
    /SIGKILL|kill -9/,
    'deploy-relay.sh must never SIGKILL / `kill -9` the relay: a hard kill skips the drain entirely and turns a deploy into a ~10s outage for every browser'
  );
});

test('the sigprobe debug scaffolding stays deleted', () => {
  assert.equal(
    fs.existsSync(path.join(REPO, 'sigprobe.js')),
    false,
    'relay/sigprobe.js is a throwaway debug probe; anything under relay/ is tarred onto the production VPS by scripts/deploy-relay.sh'
  );
  assert.equal(
    fs.existsSync(path.join(REPO, 'sigprobe-child.js')),
    false,
    'relay/sigprobe-child.js is a throwaway debug probe; anything under relay/ is tarred onto the production VPS by scripts/deploy-relay.sh'
  );
});
