const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const REPO = path.resolve(__dirname, '..');
const ROOT = path.resolve(REPO, '..');
const deployClient = fs.readFileSync(path.join(ROOT, 'scripts', 'deploy-relay.sh'), 'utf8');
const installer = fs.readFileSync(path.join(REPO, 'ops', 'deploy-multiplex'), 'utf8');
const provisioner = fs.readFileSync(path.join(REPO, 'ops', 'provision-multiplex-deploy.sh'), 'utf8');
const service = fs.readFileSync(path.join(REPO, 'ops', 'multiplex-app.service'), 'utf8');
const backupService = fs.readFileSync(path.join(REPO, 'ops', 'mux-relay-backup.service'), 'utf8');

test('release client packages only a clean immutable commit for harmonizer-sub', () => {
  assert.match(deployClient, /^VPS=harmonizer-sub$/m);
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
  assert.doesNotMatch(deployClient, /harmonizer-admin|deploy-stack|MUX_DEPLOY_CONFIRM/);
});

test('root installer accepts identities, never caller-controlled paths or commands', () => {
  assert.match(installer, /--preflight/);
  assert.match(installer, /\[ "\$#" -eq 2 \]/);
  assert.match(installer, /\^\[0-9a-f\]\{40\}\$/);
  assert.match(installer, /\^\[0-9a-f\]\{64\}\$/);
  assert.match(installer, /ARCHIVE="\/home\/sub\/multiplex-incoming\/multiplex-\$COMMIT\.tgz"/);
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
  for (const key of ['PORT', 'MUX_HOST_TOKEN', 'HL_INTERNAL_KEY', 'MUX_BRIDGE_TOKEN']) {
    assert.match(installer, new RegExp(`for key in[\\s\\S]*${key}`));
  }
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

test('one-time provisioner removes stale docker drop-ins and installs only the narrow sudo command', () => {
  assert.match(provisioner, /10-docker-group\.conf/);
  assert.match(provisioner, /gpasswd -d svc-multiplex docker/);
  assert.match(provisioner, /sub ALL=\(root\) NOPASSWD: \/usr\/local\/bin\/deploy-multiplex \*/);
  assert.match(provisioner, /visudo -cf/);
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
  assert.match(backupService, /^ExecCondition=\/usr\/bin\/ssh .* win true$/m);
  assert.match(backupService, /^ReadOnlyPaths=\/var\/lib\/multiplex$/m);
  assert.match(backupService, /^NoNewPrivileges=yes$/m);
  assert.match(installer, /runuser -u svc-multiplex[\s\S]*ssh[\s\S]*win true/);
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
