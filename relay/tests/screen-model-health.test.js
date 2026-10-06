// R3: the relay's screen model is an accelerator on the reattach path, but its two dependencies
// (@xterm/headless, @xterm/addon-serialize) were declared as devDependencies - so a production install
// (`npm install --omit=dev`) would leave the model inert, with nothing anywhere saying so. These pin the
// dependency CLASS (they are runtime deps now) and the runtime OBSERVABILITY (/api/health publishes
// screenModel: live|inert, so an operator can see which path a deployment is actually on).
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const { RelayHarness } = require('./harness');

const REPO = path.join(__dirname, '..');

test('the screen-model dependencies are runtime dependencies, not devDependencies', () => {
  const pkg = JSON.parse(fs.readFileSync(path.join(REPO, 'package.json'), 'utf8'));
  const deps = pkg.dependencies || {};
  const devDeps = pkg.devDependencies || {};
  for (const name of ['@xterm/headless', '@xterm/addon-serialize']) {
    assert.ok(deps[name], `${name} must be a runtime dependency - the relay loads it on the reattach path`);
    assert.equal(devDeps[name], undefined, `${name} must not also be listed as a devDependency`);
  }
});

test('/api/health publishes whether the screen model is live', async t => {
  const h = new RelayHarness();
  await h.start();
  t.after(() => h.stop());
  const health = await h.json('GET', '/api/health');
  assert.ok(health.screenModel === 'live' || health.screenModel === 'inert',
    `screenModel must be 'live' or 'inert', got ${JSON.stringify(health.screenModel)}`);
  // In this repo the two deps are installed, so the model must actually be live - a permanent 'inert'
  // would mean the field is decorative and the deps are not really present at runtime.
  assert.equal(health.screenModel, 'live', 'with the deps installed the screen model must report live');
});
