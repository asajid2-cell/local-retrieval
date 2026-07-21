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
      commitBytes(file, backupBytes);
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
    commitBytes(file, backupBytes);
    return restored;
  }
  return fallback;
}

module.exports = {
  durableJsonLoad,
  durableJsonWrite,
  durableWrite,
  fsyncDirectory,
};
