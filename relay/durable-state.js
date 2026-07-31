'use strict';

const fs = require('fs');
const path = require('path');

let tempSequence = 0;

function checkpoint(options, stage, file) {
  if (options && typeof options.fault === 'function') options.fault(stage, file);
  const faultFile = process.env.MUX_TEST_PERSIST_FAULT_FILE;
  if (!faultFile || !fs.existsSync(faultFile)) return;
  let requested;
  try { requested = JSON.parse(fs.readFileSync(faultFile, 'utf8')); } catch { return; }
  if (requested && requested.stage === stage
      && (!requested.file || path.basename(file) === requested.file)) {
    const remaining = Math.max(1, Number(requested.remaining) || 1);
    try {
      if (remaining > 1)
        fs.writeFileSync(faultFile, JSON.stringify({ ...requested, remaining: remaining - 1 }));
      else
        fs.rmSync(faultFile, { force: true });
    } catch {}
    throw new Error(`injected persistence failure at ${stage} for ${path.basename(file)}`);
  }
}

function fsyncDirectory(directory) {
  if (process.platform === 'win32') return;
  const fd = fs.openSync(directory, fs.constants.O_RDONLY);
  try {
    fs.fsyncSync(fd);
  } finally {
    fs.closeSync(fd);
  }
}

function commitBytes(file, bytes, options = {}) {
  const directory = path.dirname(file);
  fs.mkdirSync(directory, { recursive: true });
  const tmp = `${file}.${process.pid}.${++tempSequence}.tmp`;
  let fd = null;
  let replaced = false;
  try {
    checkpoint(options, 'beforeWrite', file);
    fd = fs.openSync(tmp, 'wx', 0o600);
    fs.writeFileSync(fd, bytes);
    checkpoint(options, 'beforeFileFsync', file);
    fs.fsyncSync(fd);
    fs.closeSync(fd);
    fd = null;

    checkpoint(options, 'beforeReplace', file);
    fs.renameSync(tmp, file);
    replaced = true;
    checkpoint(options, 'beforeDirectoryFsync', file);
    fsyncDirectory(directory);
    checkpoint(options, 'beforeReadback', file);
    const committed = fs.readFileSync(file);
    if (!committed.equals(bytes)) {
      throw new Error(`committed state did not read back identically: ${file}`);
    }
  } catch (error) {
    error.target = file;
    error.replaced = replaced;
    error.committed = false;
    error.mismatch = false;
    error.unknown = false;
    if (replaced) {
      try {
        const actual = fs.readFileSync(file);
        error.committed = actual.equals(bytes);
        error.mismatch = !error.committed;
      } catch (verificationError) {
        error.unknown = true;
        error.verificationError = verificationError;
      }
    }
    throw error;
  } finally {
    if (fd !== null) {
      try { fs.closeSync(fd); } catch {}
    }
    try { fs.rmSync(tmp, { force: true }); } catch {}
  }
}

function compareBytes(file, expected) {
  try {
    return fs.readFileSync(file).equals(expected) ? 'match' : 'mismatch';
  } catch {
    return 'unknown';
  }
}

function durableWrite(file, data, options = {}) {
  const bytes = Buffer.isBuffer(data) ? data : Buffer.from(String(data), 'utf8');
  const backup = file + '.bak';
  if (fs.existsSync(file)) {
    try {
      commitBytes(backup, fs.readFileSync(file), options.backup || {});
    } catch (error) {
      error.primaryCommitted = false;
      error.committed = false;
      throw error;
    }
  }
  try {
    commitBytes(file, bytes, options);
  } catch (error) {
    if (error.committed) {
      try {
        commitBytes(file, bytes, options.retry || {});
        return;
      } catch (retryError) {
        retryError.firstError = error;
        const comparison = compareBytes(file, bytes);
        retryError.committed = comparison === 'match';
        retryError.mismatch = comparison === 'mismatch';
        retryError.unknown = comparison === 'unknown';
        error = retryError;
      }
    }
    if (error.unknown) {
      try {
        const actual = fs.readFileSync(file);
        if (actual.equals(bytes)) {
          commitBytes(file, bytes);
          return;
        }
        error.unknown = false;
        error.mismatch = true;
      } catch (verificationError) {
        error.verificationError = verificationError;
      }
    }
    if (error.replaced && error.mismatch && fs.existsSync(backup)) {
      try {
        commitBytes(file, fs.readFileSync(backup));
        error.recovered = true;
        error.committed = false;
      } catch (recoveryError) {
        error.recoveryError = recoveryError;
      }
    }
    throw error;
  }
}

function durableJsonWrite(file, value, options = {}) {
  durableWrite(file, Buffer.from(JSON.stringify(value), 'utf8'), options);
}

function parseJson(bytes, file) {
  try {
    return JSON.parse(bytes.toString('utf8'));
  } catch (error) {
    const wrapped = new Error(`invalid persisted JSON: ${file}`);
    wrapped.cause = error;
    throw wrapped;
  }
}

function readValidatedJson(file, validate) {
  const value = parseJson(fs.readFileSync(file), file);
  if (validate && !validate(value)) {
    throw new Error(`invalid persisted state shape: ${file}`);
  }
  return value;
}

// Republishing a recovered backup as the primary is BEST EFFORT, and it must never be able to destroy
// the recovery it just performed.
//
// The value has already parsed AND validated by the time we get here, so it is authoritative in memory
// whether or not this write lands. Previously commitBytes() sat inside the recovery try-block, so any
// transient write failure -- disk full, an AV scanner holding the file, a read-only mount -- threw out
// of durableJsonLoad. Since every caller in server.js runs at module scope, that killed startup while a
// perfectly good .bak sat on disk. The load must not fail over a write it does not need.
//
// The failure is recorded rather than swallowed: it surfaces in /api/health and forces `degraded`, and
// the next ordinary save of that store rewrites the primary anyway.
const recoveryWriteFailures = [];

function republishRecoveredBackup(file, backupBytes) {
  try {
    commitBytes(file, backupBytes);
  } catch (error) {
    const reason = String(error && error.message || error);
    recoveryWriteFailures.push({ file: path.basename(file), reason, at: Date.now() });
    console.error(
      `[persistence] recovered ${path.basename(file)} from its backup but could not republish it: ${reason}; `
      + 'the recovered value is live in memory and the next save will rewrite the primary',
    );
  }
}

function recoveryWriteFailureReport() {
  return recoveryWriteFailures.map(record => ({ ...record }));
}

// NOTE ON THE THROW: an unusable primary with no usable backup still throws, which still prevents the
// relay from listening. That is deliberate upstream, not an oversight -- server.js:1385 rewrites
// projects.json through the allowlist on every boot, and "fails startup" is the mechanism that stops
// that rewrite from clobbering a projection this build does not recognise (e.g. one written by a NEWER
// relay). relay.test.js pins exactly that contract. Making a bad state file non-fatal is a real and
// worthwhile change, but it requires a write-refusal design for the quarantined store first, or the
// boot-time rewrite simply destroys the file the throw was protecting. Tracked separately; do not
// "fix" this by deleting the throw alone.
function durableJsonLoad(file, fallback, validate = null) {
  if (fs.existsSync(file)) {
    try {
      return readValidatedJson(file, validate);
    } catch (primaryError) {
      const backup = file + '.bak';
      if (!fs.existsSync(backup)) throw primaryError;
      const backupBytes = fs.readFileSync(backup);
      const restored = parseJson(backupBytes, backup);
      if (validate && !validate(restored)) {
        throw new Error(`invalid persisted state shape: ${backup}`, { cause: primaryError });
      }
      republishRecoveredBackup(file, backupBytes);
      return restored;
    }
  }

  const backup = file + '.bak';
  if (fs.existsSync(backup)) {
    const backupBytes = fs.readFileSync(backup);
    const restored = parseJson(backupBytes, backup);
    if (validate && !validate(restored)) {
      throw new Error(`invalid persisted state shape: ${backup}`);
    }
    republishRecoveredBackup(file, backupBytes);
    return restored;
  }
  return fallback;
}

module.exports = {
  durableJsonLoad,
  durableJsonWrite,
  durableWrite,
  fsyncDirectory,
  recoveryWriteFailureReport,
};
