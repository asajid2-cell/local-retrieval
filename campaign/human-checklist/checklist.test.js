const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const checklistDir = __dirname;
const requiredPath = path.join(checklistDir, 'r.3.2-web-scroll-affordance.md');
const buildStamp = '2026-07-23-visible-scrollbar-honest-alt-scroll';

test('the web scroll affordance checklist records the shipped acceptance surface', () => {
  assert.ok(fs.existsSync(requiredPath), 'required checklist is missing');
  const source = fs.readFileSync(requiredPath, 'utf8');

  assert.match(source, new RegExp(buildStamp.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
  assert.match(source, /1906x912/);
  assert.match(source, /alternate screen/);
  assert.match(source, /app scroll/);
  assert.match(source, /touch|phone/i);
});

test('every human checklist follows the machine-enforced convention', () => {
  const files = fs.readdirSync(checklistDir)
    .filter((entry) => entry.endsWith('.md'));
  assert.ok(files.length > 0, 'no human checklist files found');

  for (const file of files) {
    const source = fs.readFileSync(path.join(checklistDir, file), 'utf8');
    const lines = source.split(/\r?\n/);
    assert.ok(source.startsWith('# '), `${file} must start with a # heading`);
    assert.match(source, /^## Checks$/m, `${file} is missing ## Checks`);

    const checks = lines
      .map((line, index) => ({ line, index }))
      .filter(({ line }) => line.startsWith('- [ ]'));
    assert.ok(checks.length > 0, `${file} has no checklist items`);

    for (const { index } of checks) {
      const following = lines.slice(index + 1, index + 3).join('\n');
      assert.match(following, /Expected:/, `${file} checklist item at line ${index + 1} lacks Expected:`);
    }
  }
});
