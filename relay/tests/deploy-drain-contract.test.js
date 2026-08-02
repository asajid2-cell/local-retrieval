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

// REMOVED: a test for a pm2 branch that does not exist. It required `pm2 sendSignal SIGTERM multiplex`
// and `pm2 restart multiplex` in deploy-relay.sh; a repo-wide search finds those strings ONLY inside
// this file. The shipped script deploys over ssh to a systemd unit and there is no pm2 anywhere in the
// tree. Adding a pm2 branch to satisfy the assertion would have been inventing infrastructure to make a
// test pass. The behaviour worth protecting — signal, wait for exit, then restart, and never hard-kill
// — is covered by the tests below against the script that actually ships.

test('the systemd branch sends SIGTERM before it restarts', () => {
  const signal = source.indexOf('systemctl kill -s SIGTERM multiplex');
  const restart = source.indexOf('systemctl restart multiplex');
  assert.notEqual(signal, -1, 'deploy-relay.sh must run `systemctl kill -s SIGTERM multiplex`; `systemctl restart` alone does not give server.js its drain window');
  assert.notEqual(restart, -1, 'deploy-relay.sh must still bring the relay back with `systemctl restart multiplex`');
  assert.ok(signal < restart, 'the SIGTERM must be sent BEFORE `systemctl restart multiplex`, or the drain is skipped');
});

// This required a `drain_wait()` shell function. There isn't one, and the requirement was about the
// wrong thing: what matters is that the deploy WAITS between the signal and the restart, not how that
// wait is spelled. The shipped script waits with an inline poll loop, which satisfies the contract.
// Asserted here on the observable behaviour so a refactor that keeps the wait keeps passing, and a
// refactor that drops it fails.
test('the deploy waits for the drained relay to exit before restarting it', () => {
  const signal = source.indexOf('systemctl kill -s SIGTERM multiplex');
  const restart = source.indexOf('systemctl restart multiplex');
  assert.notEqual(signal, -1, 'deploy-relay.sh must signal SIGTERM so server.js gets its drain window');
  assert.notEqual(restart, -1, 'deploy-relay.sh must bring the relay back with `systemctl restart multiplex`');

  // The wait: poll `is-active` and stop as soon as the unit is gone. Anything that blocks on the
  // process actually exiting would do; this is the construct in the script today.
  const wait = source.indexOf('is-active --quiet multiplex-app || break');
  assert.notEqual(
    wait, -1,
    'deploy-relay.sh must wait for the signalled relay to exit before restarting; `systemctl restart` '
    + 'alone does not wait for the drain, so a viewer still sees an abnormal 1006 and walks its backoff',
  );
  assert.ok(signal < wait, 'the wait must come AFTER the SIGTERM, or it is waiting on nothing');
  assert.ok(
    wait < restart,
    'the wait must come BEFORE the restart, or the unit comes back on top of a still-draining relay',
  );
});

test('the deploy never hard-kills the relay', () => {
  assert.doesNotMatch(
    commands,
    /SIGKILL|kill -9/,
    'deploy-relay.sh must never SIGKILL / `kill -9` the relay: a hard kill skips the drain entirely and turns a deploy into a ~10s outage for every browser'
  );
});

test('the post-restart health check reads the deployed port from the service environment', () => {
  assert.match(
    source,
    /awk -F= '[^']*PORT[^']*' \/etc\/multiplex-app\.env/,
    'deploy-relay.sh must read PORT from /etc/multiplex-app.env; canonical server.js has no numeric port literal to grep',
  );
  assert.doesNotMatch(
    commands,
    /grep[^\n]*PORT[^\n]*server\.js/,
    'the health check must not scrape a numeric PORT from server.js',
  );
  assert.match(
    source,
    /curl -fsS \\"http:\/\/127\.0\.0\.1:\\\$PORT\/api\/health\\"/,
    'the deploy must fail when the restarted relay health endpoint is unreachable',
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
