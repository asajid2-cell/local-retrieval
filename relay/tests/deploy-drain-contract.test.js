const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');

const REPO = path.resolve(__dirname, '..');
const ROOT = path.resolve(REPO, '..');
const deployClient = fs.readFileSync(path.join(ROOT, 'scripts', 'deploy-relay.sh'), 'utf8');
const installer = fs.readFileSync(path.join(REPO, 'ops', 'deploy-multiplex'), 'utf8');
const provisioner = fs.readFileSync(path.join(REPO, 'ops', 'provision-multiplex-deploy.sh'), 'utf8');
const service = fs.readFileSync(path.join(REPO, 'ops', 'multiplex-app.service'), 'utf8');
const backupService = fs.readFileSync(path.join(REPO, 'ops', 'mux-relay-backup.service'), 'utf8');

test('release client packages only a clean immutable commit for the admin release account', () => {
  assert.match(deployClient, /^VPS=harmonizer-admin$/m);
  assert.match(deployClient, /^REMOTE_DEPLOY=\/usr\/local\/bin\/deploy-multiplex$/m);
  assert.match(deployClient, /git status --porcelain/);
  assert.match(deployClient, /git rev-parse HEAD/);
  assert.match(deployClient, /sudo -n '\$REMOTE_DEPLOY' --preflight/);
  assert.ok(
    deployClient.indexOf("sudo -n '$REMOTE_DEPLOY' --preflight")
      < deployClient.indexOf('scp -q -- "$ARCHIVE"'),
    'remote preflight must run before release upload',
  );
  assert.match(deployClient, /RELEASE_COMMIT/);
  assert.match(deployClient, /sha256sum/);
  assert.match(deployClient, /sudo '\$REMOTE_DEPLOY' '\$COMMIT' '\$SHA256'/);
  // Admin IS the sanctioned path: installing a release is a system change, not an operate action.
  // The guard is against the other routes — the retired read-only release account, and ad-hoc
  // deploy tooling that would bypass the wrapper entirely.
  assert.doesNotMatch(deployClient, /harmonizer-sub|deploy-stack|MUX_DEPLOY_CONFIRM/);
});

test('root installer accepts identities, never caller-controlled paths or commands', () => {
  assert.match(installer, /--preflight/);
  assert.match(installer, /\[ "\$#" -eq 2 \]/);
  assert.match(installer, /\^\[0-9a-f\]\{40\}\$/);
  assert.match(installer, /\^\[0-9a-f\]\{64\}\$/);
  assert.match(installer, /ARCHIVE="\/home\/admin\/multiplex-incoming\/multiplex-\$COMMIT\.tgz"/);
  assert.match(installer, /release archive must be owned by admin/);
  assert.match(installer, /QUARANTINE="\$STATE\/deploy-incoming"/);
  assert.match(installer, /mv -- "\$ARCHIVE" "\$QUARANTINE\/multiplex-\$COMMIT\.tgz"/);
  assert.match(installer, /archive links\/devices are refused/);
  assert.match(installer, /archive commit marker mismatch/);
  assert.match(installer, /release archive checksum mismatch/);
});

test('deployment installs dependencies unprivileged and code root-owned', () => {
  assert.match(installer, /runuser -u svc-multiplex -- env HOME="\$STATE" npm/);
  assert.match(installer, /--omit=dev --ignore-scripts/);
  assert.match(installer, /chown -R root:root "\$STAGE"/);
  assert.match(installer, /chmod -R go-w "\$STAGE"/);
  // `mktemp -d` creates the stage 0700 and `go-w` cannot widen it, so an unwidened stage reaches
  // the release root untraversable and the service account cannot read the swapped symlink target.
  assert.match(installer, /chmod 0755 "\$STAGE" "\$STAGE\/relay"/);
  assert.match(installer, /chmod 0755 "\$RELEASE" "\$RELEASE\/relay"/);
});

test('service is non-root and explicitly has no docker supplementary group', () => {
  assert.match(service, /^User=svc-multiplex$/m);
  assert.match(service, /^Group=svc-multiplex$/m);
  assert.match(service, /^SupplementaryGroups=$/m);
  assert.match(service, /^WorkingDirectory=\/opt\/multiplex-app$/m);
  assert.match(service, /^ReadWritePaths=\/var\/lib\/multiplex$/m);
  assert.match(service, /^NoNewPrivileges=yes$/m);
  assert.match(service, /^CapabilityBoundingSet=$/m);
  assert.match(service, /^AmbientCapabilities=$/m);
  assert.match(service, /^ProtectClock=yes$/m);
  assert.match(service, /^SystemCallFilter=@system-service$/m);
  assert.match(service, /^SystemCallErrorNumber=EPERM$/m);
  assert.match(service, /^MemoryDenyWriteExecute=no$/m);
  assert.doesNotMatch(service, /docker/);
  assert.match(installer, /require_unit_value SupplementaryGroups ""/);
});

test('deploy preflight validates the existing auth and host security boundary', () => {
  assert.match(installer, /ENV_FILE=\/etc\/multiplex-app\.env/);
  assert.match(installer, /must be owned by root:root/);
  assert.match(installer, /must have mode 0600/);
  for (const key of ['PORT', 'MUX_HOST_TOKEN', 'HL_INTERNAL_KEY']) {
    assert.match(installer, new RegExp(`for key in[\\s\\S]*${key}`));
  }
  // MUX_BRIDGE_TOKEN gates only the archive-index routes, no producer in this tree exercises them,
  // and the relay fails closed without it. It must stay out of the death-on-absent list so a deploy
  // is not gated on a credential nothing uses, while remaining visible as a warning.
  assert.match(installer, /for key in PORT MUX_HOST_TOKEN HL_INTERNAL_KEY; do/);
  assert.match(installer, /MUX_BRIDGE_TOKEN absent/);
  assert.match(installer, /require_unit_value User svc-multiplex/);
  assert.match(installer, /require_unit_value Group svc-multiplex/);
  assert.match(installer, /require_unit_value NoNewPrivileges yes/);
  assert.match(installer, /require_unit_value ProtectSystem strict/);
  assert.match(installer, /require_unit_value ReadWritePaths \/var\/lib\/multiplex/);
  assert.match(installer, /runtime_identity_ok/);
  assert.match(installer, /\/proc\/\$pid\/status/);
  assert.match(installer, /id -u svc-multiplex/);
  assert.match(installer, /id -g svc-multiplex/);
  assert.match(installer, /getent group docker/);
  assert.match(installer, /running process has the wrong UID\/GID or retains docker access/);
  assert.match(installer, /nginx -t/);
  assert.match(installer, /hl-auth is not listening on port 4200/);
  assert.match(installer, /hl-auth port 4200 must be loopback-only/);
});

test('deploy drains, verifies health, and atomically rolls back', () => {
  assert.match(installer, /systemctl stop "\$UNIT"/);
  assert.match(installer, /KillSignal=SIGTERM|systemctl stop/);
  assert.match(installer, /mv -Tf -- "\$NEXT_LINK" "\$CURRENT"/);
  assert.match(installer, /\/api\/health/);
  assert.match(installer, /release_health_ok/);
  assert.match(installer, /persistence\.get\("ok"\) is True/);
  assert.match(installer, /distributed health remains degraded/);
  assert.match(installer, /rolling back/);
  assert.match(installer, /previous release restored/);
});

test('one-time provisioner removes stale docker drop-ins and grants no sudo command', () => {
  assert.match(provisioner, /10-docker-group\.conf/);
  assert.match(provisioner, /gpasswd -d svc-multiplex docker/);
  // No sudoers grant of any kind. A fixed-policy wrapper cannot authenticate the provenance of the
  // archive it installs (the caller supplies the sha256), so "may run the wrapper" is "may install
  // arbitrary code beside the relay's secrets" — a system change, which is tier 3 by definition.
  assert.doesNotMatch(provisioner, /NOPASSWD/);
  assert.doesNotMatch(provisioner, /visudo -cf/);
  // And the retired tier-2 path must not reappear on a host provisioned by an older revision.
  assert.match(provisioner, /grants a lower-privileged deploy; remove it first/);
  assert.match(provisioner, /chown root:root \/etc\/multiplex-app\.env/);
  assert.match(provisioner, /chmod 0600 \/etc\/multiplex-app\.env/);
  assert.match(provisioner, /systemctl restart multiplex-app\.service/);
  assert.match(provisioner, /deploy-multiplex --preflight/);
  assert.match(provisioner, /live service identity refreshed/);
});

test('backup timer runs as svc-multiplex with a bounded writable surface', () => {
  assert.match(backupService, /^User=svc-multiplex$/m);
  assert.match(backupService, /^Group=svc-multiplex$/m);
  assert.match(backupService, /^Environment=HOME=\/var\/lib\/multiplex$/m);
  // The probe's remote command is `exit 0`, not `true`. The PC runs Windows OpenSSH with cmd.exe as its
  // shell, where `true` does not exist and exits 1 — so the old probe failed on every tick, the unit was
  // skipped without a journal entry, and the off-box backup never ran even once. `exit 0` is a builtin
  // in cmd, POSIX sh and PowerShell, so this asserts the ROUTE rather than the remote shell's dialect.
  // RequestTTY=no and -n keep a non-interactive hourly run from ever waiting on a terminal.
  assert.match(backupService, /^ExecCondition=\/usr\/bin\/ssh .* -n win exit 0$/m);
  assert.match(backupService, /^ReadOnlyPaths=\/var\/lib\/multiplex$/m);
  assert.match(backupService, /^NoNewPrivileges=yes$/m);
  assert.match(installer, /runuser -u svc-multiplex[\s\S]*ssh[\s\S]*win exit 0/);
  assert.match(installer, /systemctl enable --now mux-relay-backup\.timer/);
  assert.match(installer, /timer activation failed; application release remains healthy/);
  assert.match(installer, /systemctl is-enabled --quiet mux-relay-backup\.timer/);
  assert.doesNotMatch(installer, /systemctl disable --now mux-relay-backup\.timer/);
  assert.match(installer, /existing timer remains enabled and will skip cleanly/);
  assert.match(installer, /backup timer left disabled/);
});

test('the sigprobe debug scaffolding stays deleted', () => {
  assert.equal(fs.existsSync(path.join(REPO, 'sigprobe.js')), false);
  assert.equal(fs.existsSync(path.join(REPO, 'sigprobe-child.js')), false);
});

// The release tarball is produced by `git archive` on a Windows checkout, and core.autocrlf=true
// rewrites anything .gitattributes does not pin. *.sh/*.service/*.timer were pinned; the two
// extensionless scripts the host EXECS directly were not, so they reached the release with CRLF -
// and `deploy-multiplex --preflight` died with `/usr/bin/env: 'bash\r': No such file or directory`
// (exit 127) on the very host that runs deploys, while the healthcheck timer failed the same way
// every minute. Assert the archive itself, not the working tree: the working tree was always LF,
// which is exactly why nothing caught this.
test('the shipped ops and scripts trees reach the release archive with LF endings', () => {
  const git = (args, opts = {}) => execFileSync('git', args, { cwd: ROOT, ...opts });
  const shipped = git(['ls-files', '-z', 'relay/ops', 'scripts'], { encoding: 'utf8' })
    .split('\0').filter(Boolean);
  assert.ok(shipped.length > 0, 'git reported no files under relay/ops or scripts');

  for (const rel of shipped) {
    const attr = git(['check-attr', 'eol', '--', rel], { encoding: 'utf8' }).trim();
    assert.match(
      attr, /: eol: lf$/,
      `${rel} is not pinned to LF; a CR in a shebang makes the kernel look for "bash\\r"`,
    );
  }

  // The end-to-end proof: the bytes the host actually receives, straight out of git archive.
  const archived = git(['archive', '--format=tar', 'HEAD', 'relay/ops', 'scripts']);
  assert.equal(
    archived.includes(0x0d), false,
    'the release archive carries CRLF; the host execs these files directly and they will exit 127',
  );
});
