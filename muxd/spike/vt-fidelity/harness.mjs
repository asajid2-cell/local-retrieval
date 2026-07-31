#!/usr/bin/env node
// harness.mjs — vt-fidelity spike: pyte vs @xterm/headless on the ConPTY
// dialect muxd replays.
//
// For every fixture in --corpus we run two probes per candidate:
//   native : ingest at the fixture's own geometry -> final visible screen + cursor
//   reflow : ingest at 140 cols, resize to 80, read the screen back
// @xterm/headless is the ORACLE, so its own cell/reflow numbers are identity by
// construction and are flagged oracleSelfIdentity. Its independent signals are
// throughput and the SerializeAddon round-trip (serialize -> replay -> compare).
//
// Throughput is measured, for BOTH candidates, as: one untimed warmup ingest,
// then REPEATS timed repeats of the same fixture in the same process against a
// freshly constructed terminal/screen, reporting the best (minimum) repeat.
// Each timed ingest is a SINGLE bulk write -- awaiting xterm's write callback
// per 8KB chunk costs an event-loop round-trip per chunk and would measure the
// scheduler, not the parser. The headline number is the largest fixture's, not
// a corpus aggregate: summing ingestMs over 15 tiny fixtures charges their
// fixed startup cost against the total. See report.throughputMethod.
//
//   node harness.mjs --corpus ./fixtures --out ./report.json

import { createRequire } from 'node:module';
import { spawnSync } from 'node:child_process';
import { readFileSync, readdirSync, writeFileSync, statSync } from 'node:fs';
import { dirname, join, resolve, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(HERE, '..', '..', '..');
const require = createRequire(join(ROOT, 'relay', 'package.json'));
const { Terminal } = require('@xterm/headless');
const { SerializeAddon } = require('@xterm/addon-serialize');

const REFLOW_FROM = 140;   // brief: ingest at 140 cols...
const REFLOW_TO = 80;      // ...resize to 80 and compare
const SCROLLBACK = 5000;
const REPEATS = 3;         // timed ingest repeats per fixture, after a warmup

// GO thresholds (brief).
const GATE = {
  syntheticCellPct: 99.5,
  recordedCellPct: 99.0,
  reflowPct: 98.0,
  throughputBytesPerSec: 5 * 1024 * 1024,
};

function argv(flag, dflt) {
  const i = process.argv.indexOf(flag);
  return i > -1 && process.argv[i + 1] ? process.argv[i + 1] : dflt;
}

// ------------------------------------------------------------------ corpus

function findFixtures(dir) {
  const out = [];
  const walk = (d) => {
    for (const e of readdirSync(d, { withFileTypes: true })) {
      const p = join(d, e.name);
      if (e.isDirectory()) walk(p);
      else if (e.name.endsWith('.bin')) {
        const sidecar = p.slice(0, -4) + '.json';
        let meta = {};
        try { meta = JSON.parse(readFileSync(sidecar, 'utf8')); } catch { /* defaults below */ }
        out.push({
          name: meta.name || e.name.slice(0, -4),
          path: p,
          corpus: meta.corpus || (p.includes('recorded') ? 'recorded' : 'synthetic'),
          cols: meta.cols || 80,
          rows: meta.rows || 24,
          desc: meta.desc || '',
          source: meta.source || '',
          bytes: statSync(p).size,
          containsRecordedSessionContent: !!meta.containsRecordedSessionContent,
        });
      }
    }
  };
  walk(dir);
  return out.sort((a, b) => a.name.localeCompare(b.name));
}

// ------------------------------------------------------------- xterm probe

function readScreen(term, rows) {
  const b = term.buffer.active;
  const top = b.baseY;
  const lines = [];
  for (let i = 0; i < rows; i++) {
    const line = b.getLine(top + i);
    lines.push((line ? line.translateToString(false) : '').padEnd(term.cols).slice(0, term.cols));
  }
  return {
    screen: lines,
    cursor: { x: b.cursorX, y: b.cursorY },
    altScreen: b.type === 'alternate',
  };
}

function newTerm(cols, rows) {
  return new Terminal({
    cols, rows,
    allowProposedApi: true,
    scrollback: SCROLLBACK,
    // muxd drives a Windows ConPTY; this is the config the relay terminal runs
    // under, and it is what enables xterm's native reflow on resize.
    windowsPty: { backend: 'conpty', buildNumber: 26200 },
    // Without this xterm SKIPS reflowing the wrapped-line group containing the
    // cursor, which would understate its reflow ability on mid-stream fixtures.
    reflowCursorLine: true,
  });
}

function write(term, data) {
  return new Promise((res) => term.write(data, res));
}

// One timed bulk ingest into a FRESH terminal: a single term.write() of the
// whole buffer and exactly ONE completion callback awaited. Terminal
// construction is outside the clock; nothing is serialized or resized. Note
// that queueing the chunks unawaited is NOT an option -- xterm's WriteBuffer
// throws past 50 queued chunks ('write data discarded').
async function timedIngest(data, cols, rows) {
  const term = newTerm(cols, rows);
  const t0 = process.hrtime.bigint();
  await write(term, data);
  const ms = Number(process.hrtime.bigint() - t0) / 1e6;
  term.dispose();
  return ms;
}

async function bestOfIngest(data, cols, rows, repeats) {
  await timedIngest(data, cols, rows);   // warmup, discarded
  let best = Infinity;
  for (let i = 0; i < repeats; i++) best = Math.min(best, await timedIngest(data, cols, rows));
  return best;
}

async function xtermProbe(fixture, { cols, rows, resizeTo, timed }) {
  const data = readFileSync(fixture.path);
  const term = newTerm(cols, rows);
  const ser = new SerializeAddon();
  term.loadAddon(ser);

  // Correctness pass: chunked at muxd's 8192-byte pty read size. Untimed --
  // the per-chunk await here is what made the old measurement meaningless.
  const t0 = process.hrtime.bigint();
  for (let i = 0; i < data.length; i += 8192) await write(term, data.subarray(i, i + 8192));
  let ingestMs = Number(process.hrtime.bigint() - t0) / 1e6;

  const native = readScreen(term, rows);
  if (timed) ingestMs = await bestOfIngest(data, cols, rows, REPEATS);
  const out = { ...native, ingestMs, bytes: data.length };

  if (resizeTo && resizeTo !== cols) {
    term.resize(resizeTo, rows);
    await new Promise((r) => setImmediate(r));
    out.reflow = readScreen(term, rows);
  } else {
    // Serialize round-trip: replay xterm's own emitted VT into a fresh terminal
    // and compare. This is the signal that matters for a serialize-based relay.
    const vt = ser.serialize();
    const rt = newTerm(cols, rows);
    await write(rt, vt);
    out.serialize = { bytes: Buffer.byteLength(vt), ...readScreen(rt, rows) };
    rt.dispose();
  }
  term.dispose();
  return out;
}

// -------------------------------------------------------------- pyte probe

function pyteProbe(jobs) {
  const py = process.env.PYTHON || 'python';
  const r = spawnSync(py, [join(HERE, 'pyte_probe.py')], {
    input: JSON.stringify(jobs),
    encoding: 'utf8',
    maxBuffer: 256 * 1024 * 1024,
  });
  if (r.status !== 0) {
    throw new Error(`pyte_probe.py failed (${r.status}): ${(r.stderr || '').slice(0, 1200)}`);
  }
  const parsed = JSON.parse(r.stdout);
  const byName = new Map(parsed.results.map((x) => [x.name, x]));
  return { version: parsed.version, byName };
}

// -------------------------------------------------------------- comparison

function cellFidelity(candidate, oracle) {
  if (!candidate || !oracle) return { pct: 0, matched: 0, total: 0, note: 'missing probe' };
  const rows = oracle.length;
  let matched = 0, total = 0;
  for (let r = 0; r < rows; r++) {
    const a = candidate[r] ?? '';
    const b = oracle[r] ?? '';
    const w = Math.max(a.length, b.length);
    for (let c = 0; c < w; c++) {
      total++;
      if ((a[c] ?? ' ') === (b[c] ?? ' ')) matched++;
    }
  }
  return { pct: total ? (matched / total) * 100 : 100, matched, total };
}

const pct = (n) => Math.round(n * 1000) / 1000;
const mean = (xs) => (xs.length ? xs.reduce((a, b) => a + b, 0) / xs.length : 0);

// -------------------------------------------------------------------- main

async function main() {
  const corpusDir = resolve(argv('--corpus', join(HERE, 'fixtures')));
  const outPath = resolve(argv('--out', join(HERE, 'report.json')));
  const fixtures = findFixtures(corpusDir);
  if (!fixtures.length) throw new Error(`no .bin fixtures under ${corpusDir}`);

  // One python spawn for the whole corpus: native + reflow job per fixture.
  // Only the native job is timed -- the reflow job exists for the resize
  // comparison and its ingest number is never reported.
  const jobs = [];
  for (const f of fixtures) {
    jobs.push({ name: `${f.name}#native`, path: f.path, cols: f.cols, rows: f.rows, reflowCols: 0, repeats: REPEATS });
    jobs.push({ name: `${f.name}#reflow`, path: f.path, cols: REFLOW_FROM, rows: f.rows, reflowCols: REFLOW_TO, repeats: 0 });
  }
  const pyte = pyteProbe(jobs);

  const rows = [];
  for (const f of fixtures) {
    const oNative = await xtermProbe(f, { cols: f.cols, rows: f.rows, timed: true });
    const oReflow = await xtermProbe(f, { cols: REFLOW_FROM, rows: f.rows, resizeTo: REFLOW_TO });
    const pNative = pyte.byName.get(`${f.name}#native`);
    const pReflow = pyte.byName.get(`${f.name}#reflow`);

    const pyteCell = pNative?.error ? { pct: 0, note: pNative.error } : cellFidelity(pNative?.screen, oNative.screen);
    const pyteRef = pReflow?.error ? { pct: 0, note: pReflow.error } : cellFidelity(pReflow?.reflow?.screen, oReflow.reflow?.screen);
    const xSerial = cellFidelity(oNative.serialize?.screen, oNative.screen);

    const secs = (ms) => Math.max(ms, 0.001) / 1000;
    rows.push({
      name: f.name,
      corpus: f.corpus,
      bytes: f.bytes,
      geometry: `${f.cols}x${f.rows}`,
      desc: f.desc,
      containsRecordedSessionContent: f.containsRecordedSessionContent,
      candidates: {
        pyte: {
          cellFidelityPct: pct(pyteCell.pct),
          cellsMatched: pyteCell.matched, cellsTotal: pyteCell.total,
          cursorMatch: !!pNative && pNative.cursor?.x === oNative.cursor.x && pNative.cursor?.y === oNative.cursor.y,
          reflowFidelityPct: pct(pyteRef.pct),
          throughputBytesPerSec: pNative ? Math.round(f.bytes / secs(pNative.ingestMs)) : 0,
          ingestMs: pNative ? pct(pNative.ingestMs) : null,
          error: pNative?.error || pReflow?.error || null,
        },
        'xterm-headless': {
          cellFidelityPct: 100, oracleSelfIdentity: true,
          cursorMatch: true,
          reflowFidelityPct: 100, reflowIsNative: true,
          serializeRoundTripPct: pct(xSerial.pct),
          serializeBytes: oNative.serialize?.bytes ?? null,
          throughputBytesPerSec: Math.round(f.bytes / secs(oNative.ingestMs)),
          ingestMs: pct(oNative.ingestMs),
          error: null,
        },
      },
      oracleAltScreen: oNative.altScreen,
    });
  }

  const byCorpus = (c, cand, key) =>
    rows.filter((r) => r.corpus === c).map((r) => r.candidates[cand][key]).filter((v) => typeof v === 'number');
  const totalBytes = rows.reduce((a, r) => a + r.bytes, 0);

  // The headline throughput is the LARGEST fixture's, measured best-of-repeats.
  // Dividing total bytes by summed ingestMs (the previous aggregate) charges 15
  // tiny fixtures' fixed per-ingest cost against the corpus and buries the one
  // fixture big enough to actually measure a parser.
  const largest = rows.reduce((a, r) => (r.bytes > a.bytes ? r : a));
  const throughputMethod = {
    mode: 'bulk-single-write',
    repeats: REPEATS,
    warmup: true,
    measuredOnFixture: largest.name,
    measuredOnBytes: largest.bytes,
    aggregate: 'best-of-repeats',
    excludes: ['process-spawn', 'file-io', 'serialize', 'reflow-resize'],
  };
  const headlineThroughput = (cand) => largest.candidates[cand].throughputBytesPerSec;

  const candidates = {};
  for (const cand of ['pyte', 'xterm-headless']) {
    const synth = mean(byCorpus('synthetic', cand, 'cellFidelityPct'));
    const rec = mean(byCorpus('recorded', cand, 'cellFidelityPct'));
    const hasRec = rows.some((r) => r.corpus === 'recorded');
    const reflow = mean(rows.map((r) => r.candidates[cand].reflowFidelityPct).filter((v) => typeof v === 'number'));
    const thr = headlineThroughput(cand);
    const checks = {
      syntheticCell: { value: pct(synth), gate: GATE.syntheticCellPct, pass: synth >= GATE.syntheticCellPct },
      recordedCell: { value: pct(rec), gate: GATE.recordedCellPct, pass: hasRec && rec >= GATE.recordedCellPct },
      reflow: { value: pct(reflow), gate: GATE.reflowPct, pass: reflow >= GATE.reflowPct },
      throughput: { value: thr, gate: GATE.throughputBytesPerSec, pass: thr >= GATE.throughputBytesPerSec },
    };
    const go = Object.values(checks).every((c) => c.pass);
    candidates[cand] = {
      go,
      verdict: go ? 'GO' : 'NO-GO',
      failed: Object.entries(checks).filter(([, c]) => !c.pass).map(([k]) => k),
      checks,
      throughputMBPerSec: pct(thr / (1024 * 1024)),
      ...(cand === 'pyte'
        ? {
            version: pyte.version,
            note: 'pyte Screen.resize truncates columns; it does not reflow wrapped lines. '
                + 'The reflow number below is a real measurement of that gap, not a harness artifact.',
          }
        : {
            role: 'oracle',
            oracleSelfIdentity: true,
            note: 'cellFidelity/reflow are identity by construction (this candidate IS the oracle). '
                + 'Independent signals: throughput, native reflow on resize, and serializeRoundTripPct '
                + 'per fixture (SerializeAddon emits replayable VT).',
          }),
    };
  }

  const report = {
    schema: 'vt-fidelity/1',
    generatedBy: 'muxd/spike/vt-fidelity/harness.mjs',
    node: process.version,
    corpusDir: relative(ROOT, corpusDir).replace(/\\/g, '/'),
    oracle: 'xterm-headless',
    reflowProbe: { fromCols: REFLOW_FROM, toCols: REFLOW_TO },
    gate: GATE,
    throughputMethod,
    totals: {
      fixtures: rows.length,
      synthetic: rows.filter((r) => r.corpus === 'synthetic').length,
      recorded: rows.filter((r) => r.corpus === 'recorded').length,
      bytes: totalBytes,
      fixturesWithRecordedSessionContent: rows.filter((r) => r.containsRecordedSessionContent).map((r) => r.name),
    },
    candidates,
    fixtures: rows,
  };

  writeFileSync(outPath, JSON.stringify(report, null, 2));
  for (const [k, v] of Object.entries(candidates)) {
    console.log(`${k.padEnd(15)} ${v.verdict.padEnd(6)} synth=${v.checks.syntheticCell.value}% `
      + `rec=${v.checks.recordedCell.value}% reflow=${v.checks.reflow.value}% `
      + `thr=${v.throughputMBPerSec}MB/s${v.failed.length ? '  failed: ' + v.failed.join(',') : ''}`);
  }
  console.log(`throughput: ${throughputMethod.mode}, best of ${REPEATS} after warmup, `
    + `on ${throughputMethod.measuredOnFixture} (${throughputMethod.measuredOnBytes} bytes)`);
  console.log(`${rows.length} fixtures (${report.totals.bytes} bytes) -> ${outPath}`);
}

main().catch((e) => { console.error(e); process.exit(1); });
