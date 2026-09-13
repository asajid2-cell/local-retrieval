const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const REPO = path.resolve(__dirname, '..');
const ROOT = path.resolve(REPO, '..');
const read = name => fs.readFileSync(path.join(name), 'utf8');
const service = read(path.join(REPO, 'ops', 'multiplex-test.service'));
const provisioner = read(path.join(REPO, 'ops', 'provision-multiplex-test.sh'));
const checker = read(path.join(REPO, 'ops', 'multiplex-test-config-check'));
const installer = read(path.join(REPO, 'ops', 'deploy-multiplex-test'));
const nginx = read(path.join(REPO, 'ops', 'multiplex-test.nginx.conf'));
const client = read(path.join(ROOT, 'scripts', 'deploy-relay-test.sh'));

for (const [label, value] of [
  ['service', service], ['provisioner', provisioner], ['client', client],
]) {
  test(`${label} does not reference production deployment paths`, () => {
    assert.doesNotMatch(value, /multiplex-app\.service|\/opt\/multiplex-releases|\/opt\/multiplex-app|\/var\/lib\/multiplex(?:[^-]|$)|\/etc\/multiplex\.env|:7682/);
  });
}

test('configuration checker reads only the production credential source needed for separation', () => {
  assert.doesNotMatch(checker, /multiplex-app\.service|\/opt\/multiplex-releases|\/opt\/multiplex-app|\/var\/lib\/multiplex(?:[^-]|$)/);
  assert.match(checker, /PRODUCTION_ENV=\/etc\/multiplex-app\.env/);
});

test('installer delegates credential validation and otherwise references only isolated resources', () => {
  assert.doesNotMatch(installer, /multiplex-app\.service|\/opt\/multiplex-releases|\/opt\/multiplex-app|\/var\/lib\/multiplex(?:[^-]|$)|\/etc\/multiplex\.env/);
  assert.match(installer, /CHECKER=\/usr\/local\/bin\/multiplex-test-config-check/);
  assert.match(installer, /"\$CHECKER" >\/dev\/null/);
});


test('test service has an isolated identity, port, prefix, and state', () => {
  assert.match(service, /^Description=.*test endpoint$/m);
  assert.match(service, /^User=svc-multiplex-test$/m);
  assert.match(service, /^Group=svc-multiplex-test$/m);
  assert.match(service, /^WorkingDirectory=\/opt\/multiplex-test-app$/m);
  assert.match(service, /^EnvironmentFile=\/etc\/multiplex-test\.env$/m);
  assert.match(service, /^Environment=MUX_STATE_DIR=\/var\/lib\/multiplex-test$/m);
  assert.match(service, /^Environment=PORT=7683$/m);
  assert.match(service, /^Environment=BIND_HOST=127\.0\.0\.1$/m);
  assert.match(service, /^Environment=PUBLIC_RETURN=\/multiplex-test$/m);
  assert.match(service, /^Environment=MUX_HOST_PROFILE=multiplex-test$/m);
  assert.match(service, /^ExecStartPre=\+\/usr\/local\/bin\/multiplex-test-config-check$/m);
  assert.match(service, /^SupplementaryGroups=$/m);
  assert.match(service, /^NoNewPrivileges=yes$/m);
});

test('configuration checker proves relay credential separation without reading muxd configuration', () => {
  assert.match(checker, /dedicated MUX_HOST_TOKEN is absent/);
  assert.match(checker, /production MUX_HOST_TOKEN is absent; token separation cannot be proven/);
  assert.match(checker, /test token equals the production token/);
  assert.match(checker, /dedicated HL_INTERNAL_KEY is absent/);
  assert.match(checker, /RELAY_LAN belongs to the Windows muxd profile/);
  assert.match(checker, /RELAY_PUBLIC belongs to the Windows muxd profile/);
  assert.doesNotMatch(checker, /MUX_PRODUCTION_HOST_TOKEN/);
});

test('provisioner installs only isolated resources, nginx route, and narrow admin sudo', () => {
  assert.match(provisioner, /multiplex-test\.service/);
  assert.match(provisioner, /\/opt\/multiplex-test-releases/);
  assert.match(provisioner, /\/var\/lib\/multiplex-test/);
  assert.match(provisioner, /\/etc\/multiplex-test\.env/);
  assert.match(provisioner, /\/etc\/sudoers\.d\/admin-deploy-multiplex-test/);
  assert.match(provisioner, /admin ALL=\(root\) NOPASSWD: \/usr\/local\/bin\/deploy-multiplex-test \*/);
  assert.match(provisioner, /visudo -cf/);
  assert.match(provisioner, /\/etc\/nginx\/snippets\/multiplex-test\.conf/);
  assert.match(provisioner, /active harmonizer TLS block marker is not unique/);
  assert.match(provisioner, /nginx -t/);
  assert.match(provisioner, /systemctl reload nginx/);
  assert.match(provisioner, /original configuration restored/);
  assert.match(provisioner, /systemctl disable multiplex-test\.service/);
  assert.doesNotMatch(provisioner, /systemctl (start|restart) multiplex-test/);
});

test('test release installer stages, verifies, switches atomically, checks health, and rolls back', () => {
  assert.match(installer, /sha256sum/);
  assert.match(installer, /node --check/);
  assert.match(installer, /archive links\/devices are refused/);
  assert.match(installer, /mv -Tf -- "\$NEXT_LINK" "\$CURRENT"/);
  assert.match(installer, /127\.0\.0\.1:7683\/api\/health/);
  assert.match(installer, /rolling back/);
  assert.match(installer, /previous test release restored/);
  assert.match(installer, /INCOMING=\/home\/admin\/multiplex-test-incoming/);
  assert.match(installer, /stat -c %U \"\$ARCHIVE\"\).*admin/);
  assert.match(installer, /stop_unit_and_wait/);
  assert.match(installer, /stop did not retire its previous PID/);
  assert.match(installer, /127\\\.0\\\.0\\\.1:7683/);
  assert.match(installer, /pid=\$pid/);
});

test('test nginx route uses the isolated upstream and preserves WebSocket forwarding', () => {
  assert.match(nginx, /location = \/multiplex-test \{/);
  assert.match(nginx, /location \/multiplex-test\/ \{/);
  assert.match(nginx, /proxy_pass http:\/\/127\.0\.0\.1:7683\//);
  assert.match(nginx, /proxy_set_header X-Forwarded-Prefix  \/multiplex-test/);
  assert.match(nginx, /proxy_set_header Upgrade/);
  assert.match(nginx, /proxy_set_header Connection\s+"upgrade"/);
  assert.match(nginx, /proxy_read_timeout 86400s/);
  assert.match(nginx, /proxy_send_timeout 86400s/);
  assert.doesNotMatch(nginx, /7682|multiplex-app\.service|\/opt\/multiplex-app/);
});

test('test client uses the admin lane and preserves committed-release safeguards', () => {
  assert.match(client, /^VPS=harmonizer-admin$/m);
  assert.match(client, /REMOTE_DEPLOY=\/usr\/local\/bin\/deploy-multiplex-test/);
  assert.match(client, /REMOTE_INCOMING=\/home\/admin\/multiplex-test-incoming/);
  assert.match(client, /sudo -n '\$REMOTE_DEPLOY' --preflight/);
  assert.doesNotMatch(client, /sudo -n install/);
  assert.match(client, /git archive --format=tar HEAD relay scripts\/backup-state\.sh/);
  assert.match(client, /sudo -n '\$REMOTE_DEPLOY' '\$COMMIT' '\$SHA256'/);
  assert.doesNotMatch(client, /harmonizer-sub|deploy-multiplex(?!-test)/);
});
