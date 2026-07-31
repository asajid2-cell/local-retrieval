#!/usr/bin/env python3
"""pyte_probe.py — the pyte side of the vt-fidelity harness.

Reads a job list as JSON on stdin, emits results as JSON on stdout. One job:

  {"name":..., "path": "<fixture>.bin", "cols":N, "rows":N, "reflowCols":80,
   "repeats":3}

Result per job:
  {"name",
   "screen":[<rows> strings],      final visible screen after ingest
   "cursor":{"x","y"},
   "reflow":{"screen":[...],"cursor":{...}},   after resize to reflowCols
   "ingestMs", "ingestRepeats", "ingestMethod", "bytes", "altScreen"}

pyte is fed through ByteStream so UTF-8 continuation bytes split across ring
chunks decode the same way muxd's stream would see them. That chunking is a
FIDELITY property, so the correctness pass keeps it; the TIMED path is separate
(see time_bulk) and feeds the whole buffer in one call so the number measures
pyte's parser and not the chunk loop.
"""
from __future__ import annotations

import json
import sys
import time

import pyte

CHUNK = 8192   # muxd reads the pty in 8192-byte reads (muxd.py:1525)
REPEATS = 3    # default timed repeats when a job does not say otherwise


def visible(screen):
    """The <rows> currently-displayed lines, padded to <cols>."""
    return [screen.display[i].ljust(screen.columns)[: screen.columns]
            if i < len(screen.display) else " " * screen.columns
            for i in range(screen.lines)]


def time_bulk(data, cols, rows):
    """One bulk ingest into a FRESH screen. Returns milliseconds.

    Only the feed is on the clock: interpreter startup, file I/O and
    screen/stream construction all sit outside it, and nothing is serialized or
    resized. A fresh screen per call means no repeat inherits a warm grid.
    """
    screen = pyte.HistoryScreen(cols, rows, history=2000, ratio=0.5)
    stream = pyte.ByteStream(screen)
    t0 = time.perf_counter()
    stream.feed(data)
    return (time.perf_counter() - t0) * 1000.0


def run(job):
    with open(job["path"], "rb") as f:
        data = f.read()
    cols, rows = int(job["cols"]), int(job["rows"])

    screen = pyte.HistoryScreen(cols, rows, history=2000, ratio=0.5)
    stream = pyte.ByteStream(screen)

    # Correctness pass: chunked exactly as muxd feeds it. Untimed.
    t0 = time.perf_counter()
    for i in range(0, len(data), CHUNK):
        stream.feed(data[i: i + CHUNK])
    ingest_ms = (time.perf_counter() - t0) * 1000.0

    # Timed pass: one untimed warmup, then N repeats, best (minimum) wins. The
    # minimum is the least-noisy estimate of the parser's real cost.
    repeats = int(job.get("repeats", REPEATS) or 0)
    if repeats > 0:
        time_bulk(data, cols, rows)   # warmup, discarded
        ingest_ms = min(time_bulk(data, cols, rows) for _ in range(repeats))

    out = {
        "name": job["name"],
        "bytes": len(data),
        "ingestMs": ingest_ms,
        "ingestRepeats": repeats,
        "ingestMethod": "bulk-single-feed-best-of" if repeats > 0 else "chunked-single-pass",
        "screen": visible(screen),
        "cursor": {"x": screen.cursor.x, "y": screen.cursor.y},
        # pyte tracks the alt buffer only as a mode bit; surfaced for the report.
        "altScreen": bool({47, 1047, 1049} & set(getattr(screen, "mode", set()))),
    }

    reflow_cols = int(job.get("reflowCols") or 0)
    if reflow_cols and reflow_cols != cols:
        # pyte's resize TRUNCATES: it reallocates the grid and drops columns
        # past the new width. No wrapped-line reflow. This is the asymmetry the
        # spike exists to measure, so we measure it rather than work around it.
        screen.resize(rows, reflow_cols)
        out["reflow"] = {
            "screen": visible(screen),
            "cursor": {"x": screen.cursor.x, "y": screen.cursor.y},
        }
    return out


def main():
    jobs = json.load(sys.stdin)
    results = []
    for job in jobs:
        try:
            results.append(run(job))
        except Exception as e:
            results.append({"name": job.get("name"), "error": f"{type(e).__name__}: {e}"})
    json.dump({"candidate": "pyte", "version": getattr(pyte, "__version__", "?"),
               "results": results}, sys.stdout)


if __name__ == "__main__":
    main()
