#!/usr/bin/env python3
"""pyte_probe.py — the pyte side of the vt-fidelity harness.

Reads a job list as JSON on stdin, emits results as JSON on stdout. One job:

  {"name":..., "path": "<fixture>.bin", "cols":N, "rows":N, "reflowCols":80}

Result per job:
  {"name",
   "screen":[<rows> strings],      final visible screen after ingest
   "cursor":{"x","y"},
   "reflow":{"screen":[...],"cursor":{...}},   after resize to reflowCols
   "ingestMs", "bytes", "altScreen"}

pyte is fed through ByteStream so UTF-8 continuation bytes split across ring
chunks decode the same way muxd's stream would see them.
"""
from __future__ import annotations

import json
import sys
import time

import pyte

CHUNK = 8192   # muxd reads the pty in 8192-byte reads (muxd.py:1525)


def visible(screen):
    """The <rows> currently-displayed lines, padded to <cols>."""
    return [screen.display[i].ljust(screen.columns)[: screen.columns]
            if i < len(screen.display) else " " * screen.columns
            for i in range(screen.lines)]


def run(job):
    with open(job["path"], "rb") as f:
        data = f.read()
    cols, rows = int(job["cols"]), int(job["rows"])

    screen = pyte.HistoryScreen(cols, rows, history=2000, ratio=0.5)
    stream = pyte.ByteStream(screen)

    t0 = time.perf_counter()
    for i in range(0, len(data), CHUNK):
        stream.feed(data[i: i + CHUNK])
    ingest_ms = (time.perf_counter() - t0) * 1000.0

    out = {
        "name": job["name"],
        "bytes": len(data),
        "ingestMs": ingest_ms,
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
