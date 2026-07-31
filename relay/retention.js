'use strict';
// State retention: the second, GLOBAL fence over everything this relay parks on disk.
//
// Each store already enforces its own bounds at write time (a transcript push is capped per page and
// per session; the command journal compacts on every commit). Those are per-write rules and they only
// ever see the write in front of them. This module is the periodic pass that looks at the whole state
// directory and asks a different question: how big has the sum of it become. It prunes whole records,
// never parts of them, and it reports what it could not prune so growth is visible before it hurts.
//
// Discipline: this module NEVER touches the filesystem itself for mutations. It computes a candidate
// set and hands it back; server.js commits through writeJsonState, so a blocked-persistence relay
// simply skips the sweep instead of writing behind the operator's back.
const fs = require('fs');
const path = require('path');

const MINUTE_MS = 60 * 1000;
const HOUR_MS = 60 * MINUTE_MS;
const DAY_MS = 24 * HOUR_MS;

const TERMINAL_COMMAND_STATES = new Set(['done', 'failed']);

function boundedInt(raw, fallback, floor) {
  const value = Number(raw);
  return Number.isFinite(value) && value >= floor ? Math.floor(value) : fallback;
}

// Every cap is env-tunable; the floors keep a fat-fingered env var from turning retention into
// deletion. The sweep interval is the one knob tests are allowed to shrink below the production
// floor, and only under MUX_TEST_MODE — same escape-hatch shape as the command lease duration.
function retentionConfig(env = process.env) {
  const testMode = env.MUX_TEST_MODE === '1';
  return {
    testMode,
    transcriptMaxSessions: boundedInt(env.MUX_RETENTION_TRANSCRIPT_MAX_SESSIONS, 20, 1),
    transcriptMaxBytes: boundedInt(env.MUX_RETENTION_TRANSCRIPT_MAX_BYTES, 256 * 1024 * 1024, 1024),
    commandTerminalMaxAgeMs: boundedInt(env.MUX_RETENTION_COMMAND_MAX_AGE_MS, 14 * DAY_MS, 1000),
    commandTerminalFloor: boundedInt(env.MUX_RETENTION_COMMAND_FLOOR, 200, 1),
    sweepIntervalMs: boundedInt(
      env.MUX_RETENTION_SWEEP_MS,
      testMode ? 250 : 6 * HOUR_MS,
      testMode ? 50 : MINUTE_MS,
    ),
  };
}

// ---- transcript page store -------------------------------------------------------------------
// A session's weight is the sum of its stored pages. page.bytes is stamped by the push path from the
// exact JSON it committed, so this needs no re-serialization of a possibly-huge store.
function transcriptBytes(record) {
  const pages = Array.isArray(record && record.pageList) ? record.pageList : [];
  const summed = pages.reduce((total, page) => total + (Number(page && page.bytes) || 0), 0);
  return summed > 0 ? summed : Math.max(0, Number(record && record.bytes) || 0);
}

function transcriptFetchTime(record) {
  return Number(record && record.updatedAt) || Number(record && record.expiresAt) || 0;
}

// Newest-fetch-first, then two whole-session caps. An expired capture is dropped outright: the TTL
// already made it unreadable, keeping the bytes is pure cost. Ordering is by fetch time so "newest
// N" means the N sessions the owner most recently asked for, not the N that happen to sort first.
function pruneTranscripts(records, config, now = Date.now()) {
  const list = Array.isArray(records) ? records.filter(Boolean) : [];
  const kept = [];
  const dropped = [];
  let bytes = 0;
  let overflowed = false;
  const ordered = list
    .map((record, index) => ({ record, index }))
    .sort((a, b) => transcriptFetchTime(b.record) - transcriptFetchTime(a.record) || a.index - b.index);
  for (const { record } of ordered) {
    const size = transcriptBytes(record);
    const expired = Number(record.expiresAt) > 0 && Number(record.expiresAt) <= now;
    // Once the byte budget overflows, every older session goes too — an older capture can never be
    // worth more than the newer one that already did not fit.
    if (expired || overflowed || kept.length >= config.transcriptMaxSessions
        || bytes + size > config.transcriptMaxBytes) {
      if (!expired && (overflowed || bytes + size > config.transcriptMaxBytes)) overflowed = true;
      dropped.push(record);
      continue;
    }
    kept.push(record);
    bytes += size;
  }
  const keptSet = new Set(kept);
  return { kept: list.filter(record => keptSet.has(record)), dropped, bytes };
}

// ---- app command journal ----------------------------------------------------------------------
function commandTerminalTime(command) {
  return Number(command && command.doneAt) || Number(command && command.ts) || 0;
}

// Age out terminal outcomes past the horizon, but NEVER a command still in flight (pending/leased):
// the app may be about to lease it, or already holds a lease whose ack has to find its record. The
// newest-N floor keeps the recent journal readable no matter how quiet the relay has been.
//
// Dedupe-horizon note: pruning a terminal record re-enables replay of its intentId, because enqueue
// dedupes by scanning the journal for that intentId. That is deliberate and safe. The client intent
// journal's retry horizon is MINUTES — 3 attempts at 250/750ms backoff, 256 records — so a 14-day
// horizon sits orders of magnitude beyond any redelivery that could actually arrive. Inside the
// horizon the record is still there and the enqueue still answers deduplicated:true.
function pruneCommands(commands, config, now = Date.now()) {
  const list = Array.isArray(commands) ? commands.filter(Boolean) : [];
  const kept = [];
  const dropped = [];
  let terminalSeen = 0;
  const ordered = list
    .map((command, index) => ({ command, index }))
    .sort((a, b) => commandTerminalTime(b.command) - commandTerminalTime(a.command) || a.index - b.index);
  for (const { command } of ordered) {
    if (!TERMINAL_COMMAND_STATES.has(String(command.status))) { kept.push(command); continue; }
    terminalSeen += 1;
    if (terminalSeen <= config.commandTerminalFloor) { kept.push(command); continue; }
    if (now - commandTerminalTime(command) <= config.commandTerminalMaxAgeMs) { kept.push(command); continue; }
    dropped.push(command);
  }
  const keptSet = new Set(kept);
  return { kept: list.filter(command => keptSet.has(command)), dropped };
}

// ---- already-bounded stores: assert, never prune ------------------------------------------------
// The archive index, the attention-episode state and the deferred-send queue are each capped by the
// leaf that owns them. Retention does not get a second opinion on their contents — it only proves
// the cap is still holding, so a regression there shows up as a health violation instead of silent
// growth. Absent file = that leaf has not landed in this build; that is not a violation.
const BOUNDED_STORES = [
  { name: 'archiveIndex', file: 'archive-index.json', maxCount: 500, maxBytes: 2 * 1024 * 1024, rowKeys: ['chats'] },
  // attention-episodes.json is a Map entry array ([[muxName, {since, notified}], ...]), so extractRows
  // takes the array branch and rowKeys is never consulted for it. It previously listed
  // ['episodes','sessions'] — keys server.js has never written. Left empty rather than fictional: a
  // reader should not have to check server.js to find out that this store has no wrapper object.
  { name: 'attentionEpisodes', file: 'attention-episodes.json', maxCount: 512, maxBytes: 1024 * 1024, rowKeys: [] },
  { name: 'deferredSends', file: 'deferred-sends.json', maxCount: 256, maxBytes: 4 * 1024 * 1024, rowKeys: ['queue', 'pending', 'sends'] },
];

function fileBytes(file) {
  try { return fs.statSync(file).size; } catch { return 0; }
}

function readJsonQuietly(file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch { return undefined; }
}

// Permissive on shape by design: these stores belong to other leaves and may be an array or a
// wrapper object. Unrecognized shape means "no row count available", not "violation".
function extractRows(value, rowKeys) {
  if (Array.isArray(value)) return value;
  if (value && typeof value === 'object') {
    for (const key of rowKeys) if (Array.isArray(value[key])) return value[key];
  }
  return null;
}

function boundedStoreGauges(stateDir) {
  const gauges = {};
  const violations = [];
  for (const store of BOUNDED_STORES) {
    const file = path.join(stateDir, store.file);
    const present = fs.existsSync(file);
    const bytes = present ? fileBytes(file) : 0;
    const gauge = { file: store.file, present, bytes, count: null, maxCount: store.maxCount };
    if (present) {
      const parsed = readJsonQuietly(file);
      if (parsed === undefined) violations.push(`${store.name}: unreadable JSON`);
      const rows = extractRows(parsed, store.rowKeys);
      if (rows) {
        gauge.count = rows.length;
        if (rows.length > store.maxCount)
          violations.push(`${store.name}: ${rows.length} rows exceeds its ${store.maxCount} cap`);
      }
      if (bytes > store.maxBytes)
        violations.push(`${store.name}: ${bytes} bytes exceeds its ${store.maxBytes} cap`);
    }
    gauges[store.name] = gauge;
  }
  return { gauges, violations };
}

// ---- state directory size -----------------------------------------------------------------------
function directoryBytes(dir, depth = 0) {
  let total = 0;
  let files = 0;
  let entries;
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return { bytes: 0, files: 0 }; }
  for (const entry of entries) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (depth >= 4) continue;                     // state is flat; the guard is against a symlink loop
      const nested = directoryBytes(full, depth + 1);
      total += nested.bytes;
      files += nested.files;
      continue;
    }
    if (!entry.isFile()) continue;
    total += fileBytes(full);
    files += 1;
  }
  return { bytes: total, files };
}

// ---- gauges ---------------------------------------------------------------------------------------
// TRUST AMENDMENT: plaintext exposure and signed-envelope custody are reported SEPARATELY. The first
// is chat content we are briefly holding in the clear; the second is opaque authorization proof we
// carry but never read. Collapsing them into one number would hide the only one that matters.
function exposureGauges(transcripts, commands) {
  let plaintextBytes = 0;
  for (const record of transcripts) plaintextBytes += transcriptBytes(record);
  let signedEnvelopeBytes = 0;
  for (const command of commands) {
    if (!command || !command.principalAuth) continue;
    try { signedEnvelopeBytes += Buffer.byteLength(JSON.stringify(command.principalAuth), 'utf8'); } catch {}
  }
  return { plaintextBytes, signedEnvelopeBytes };
}

function countingGauge(stateDir, name, count) {
  const file = path.join(stateDir, name);
  const present = fs.existsSync(file);
  return { file: name, present, bytes: present ? fileBytes(file) : 0, count };
}

// ---- runtime --------------------------------------------------------------------------------------
// deps: { stateDir, transcripts(), commands(), commitTranscripts(list), commitCommands(list),
//         blocked(), extraGauges?(), onError?(err, what) }
function createRetention(deps, env = process.env) {
  const config = retentionConfig(env);
  let timer = null;
  const stats = { sweeps: 0, lastSweepAt: 0, nextSweepAt: 0, lastError: '', skipped: 0,
                  dropped: { transcriptSessions: 0, commands: 0 } };

  function sweep(now = Date.now()) {
    const outcome = { at: now, transcriptSessions: 0, commands: 0, skipped: '', violations: [] };
    const bounded = boundedStoreGauges(deps.stateDir);
    outcome.violations = bounded.violations;
    if (deps.blocked && deps.blocked()) {
      // Persistence is blocked pending operator recovery. Growth is the lesser evil against writing
      // over state we already failed to verify — observe, do not mutate.
      outcome.skipped = 'persistence blocked';
      stats.skipped += 1;
      stats.lastSweepAt = now;
      stats.nextSweepAt = timer ? now + config.sweepIntervalMs : 0;
      return outcome;
    }
    try {
      const transcripts = deps.transcripts();
      const pruned = pruneTranscripts(transcripts, config, now);
      if (pruned.dropped.length) {
        deps.commitTranscripts(pruned.kept);
        outcome.transcriptSessions = pruned.dropped.length;
        stats.dropped.transcriptSessions += pruned.dropped.length;
      }
    } catch (error) {
      stats.lastError = String(error && error.message || error);
      if (deps.onError) deps.onError(error, 'transcripts');
    }
    try {
      const commands = deps.commands();
      const pruned = pruneCommands(commands, config, now);
      if (pruned.dropped.length) {
        deps.commitCommands(pruned.kept);
        outcome.commands = pruned.dropped.length;
        stats.dropped.commands += pruned.dropped.length;
      }
    } catch (error) {
      stats.lastError = String(error && error.message || error);
      if (deps.onError) deps.onError(error, 'commands');
    }
    stats.sweeps += 1;
    stats.lastSweepAt = now;
    stats.nextSweepAt = timer ? now + config.sweepIntervalMs : 0;
    return outcome;
  }

  function start() {
    if (timer) return;
    timer = setInterval(() => { try { sweep(); } catch {} }, config.sweepIntervalMs);
    if (timer.unref) timer.unref();      // retention must never be the reason this process stays alive
    stats.nextSweepAt = Date.now() + config.sweepIntervalMs;
  }

  function stop() { if (timer) { clearInterval(timer); timer = null; } }

  function gauges() {
    const transcripts = (() => { try { return deps.transcripts() || []; } catch { return []; } })();
    const commands = (() => { try { return deps.commands() || []; } catch { return []; } })();
    const bounded = boundedStoreGauges(deps.stateDir);
    const dir = directoryBytes(deps.stateDir);
    const inFlight = commands.filter(c => !TERMINAL_COMMAND_STATES.has(String(c && c.status))).length;
    const stores = {
      transcripts: {
        ...countingGauge(deps.stateDir, 'transcripts.json', transcripts.length),
        sessions: transcripts.length,
        pages: transcripts.reduce((n, r) => n + ((r && r.pageList) ? r.pageList.length : 0), 0),
        contentBytes: transcripts.reduce((n, r) => n + transcriptBytes(r), 0),
      },
      commands: {
        ...countingGauge(deps.stateDir, 'app-commands.json', commands.length),
        inFlight,
        terminal: commands.length - inFlight,
      },
      projects: countingGauge(deps.stateDir, 'projects.json', null),
      pins: countingGauge(deps.stateDir, 'pins.json', null),
      renameIntents: countingGauge(deps.stateDir, 'rename-intents.json', null),
      uploadsMeta: countingGauge(deps.stateDir, 'uploads-meta.json', null),
      uploads: (() => {
        const uploads = directoryBytes(path.join(deps.stateDir, 'uploads'));
        return { file: 'uploads/', present: fs.existsSync(path.join(deps.stateDir, 'uploads')),
                 bytes: uploads.bytes, count: uploads.files };
      })(),
      ...bounded.gauges,
    };
    return {
      stateDirBytes: dir.bytes,
      stateDirFiles: dir.files,
      intervalMs: config.sweepIntervalMs,
      caps: {
        transcriptMaxSessions: config.transcriptMaxSessions,
        transcriptMaxBytes: config.transcriptMaxBytes,
        commandTerminalMaxAgeMs: config.commandTerminalMaxAgeMs,
        commandTerminalFloor: config.commandTerminalFloor,
      },
      sweeps: stats.sweeps,
      lastSweepAt: stats.lastSweepAt,
      nextSweepAt: stats.nextSweepAt,
      skippedSweeps: stats.skipped,
      lastError: stats.lastError,
      dropped: { ...stats.dropped },
      stores,
      exposure: exposureGauges(transcripts, commands),
      invariants: { ok: bounded.violations.length === 0, violations: bounded.violations },
    };
  }

  return { config, sweep, start, stop, gauges };
}

module.exports = {
  BOUNDED_STORES,
  DAY_MS,
  boundedStoreGauges,
  createRetention,
  directoryBytes,
  pruneCommands,
  pruneTranscripts,
  retentionConfig,
  transcriptBytes,
};
