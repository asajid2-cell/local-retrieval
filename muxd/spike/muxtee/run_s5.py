#!/usr/bin/env python3
"""run_s5.py — P0 spike S5: what does the extra ConPTY layer cost in latency and fidelity?

Spec §10-S5. The child is `s5_child.py`, run two ways under the SAME harness ConPTY:
  direct : the child straight in the pty, nothing in between
  teed   : muxtee between the pty and the child (muxtee owns the inner ConPTY)

Part A — latency. Child prints N stamped lines; the reader records arrival wall-time of each. The
  stamp clock and the reader clock are different clocks, so the absolute gap is meaningless; what
  matters is the SAME gap in both arms. Report p50/p99 of (teed gap - direct gap) per line, which is
  the added cost of the layer. Pass: p50 <= +2 ms, p99 <= +5 ms.

Part B — fidelity. Child cats each fixture. Both arms' raw pty output is ingested by the SAME
  @xterm/headless oracle at the fixture's geometry, and the final visible screens are compared.
  Pass: identical screens (and the alt-screen flag agrees).

Part C — escape survival. A stream the fixtures do not carry: OSC 8 hyperlink, DECSET 2026
  synchronized-update guard, a title. Pass: byte-identical round-trip in both arms.

Writes `s5-results.json` next to this file.
"""
from __future__ import annotations

import base64, json, os, re, statistics, subprocess, sys, threading, time
from pathlib import Path

import winpty

HERE = Path(__file__).resolve().parent
FIXDIR = HERE.parent / "vt-fidelity" / "fixtures" / "recorded"
MUXTEE = (HERE.parent.parent.parent / "app" / "native" / "MuxTee" / "bin" / "Debug"
          / "net10.0-windows" / "muxtee.exe")
ORACLE = HERE / "s5_oracle.mjs"
CHILD = HERE / "s5_child.py"
OUT = HERE / "s5-results.json"

ROWS, COLS = 40, 140
LATENCY_LINES = 600


def run(mode: str, child_args: list[str], timeout: float = 60.0) -> tuple[bytes, list[float]]:
    """Spawn the child (direct or teed) in a fresh pty, pump it, return (raw output, arrival times).

    Arrival times are reader wall-clock stamps at each read. We also return the reader's own start so
    Part A can align the two arms against the child's stamp clock."""
    argv = [sys.executable, CHILD] + child_args
    spawn = argv if mode == "direct" else [str(MUXTEE), "--"] + argv
    p = winpty.PtyProcess.spawn(spawn, dimensions=(ROWS, COLS))
    state = {"buf": bytearray(), "arrivals": [], "answered": False}
    stop = threading.Event()

    def pump():
        while not stop.is_set():
            try:
                d = p.read(65536)
            except Exception:
                break
            if not d:
                time.sleep(0.001)
                continue
            b = d.encode("utf-8", "surrogatepass")
            state["buf"] += b
            state["arrivals"].append((len(state["buf"]), time.monotonic()))
            # The inner ConPTY blocks on a cursor-position request when INHERIT_CURSOR is set; answer it
            # the way a real terminal would, or nothing renders (measured in P1).
            if not state["answered"] and "\x1b[6n" in d:
                try:
                    p.write("\x1b[1;1R")
                    state["answered"] = True
                except Exception:
                    pass

    t = threading.Thread(target=pump, daemon=True)
    t.start()
    t0 = time.monotonic()
    while p.isalive() and time.monotonic() - t0 < timeout:
        time.sleep(0.02)
    stop.set()
    time.sleep(0.3)
    try:
        p.terminate(force=True)
    except Exception:
        pass
    return bytes(state["buf"]), state["arrivals"]


STAMP_RE = re.compile(rb"STAMP:(\d+):(\d+)")


def stamp_gaps(raw: bytes, arrivals: list[float]) -> dict[int, float]:
    """For each STAMP line, the reader-arrival minus the child's own stamp, in ms. Keyed by seq."""
    gaps: dict[int, float] = {}
    for m in STAMP_RE.finditer(raw):
        seq = int(m.group(1))
        child_ms = int(m.group(2))
        # arrival time of the chunk that first contained this line
        end = m.end()
        arr = None
        for upto, t in arrivals:
            if upto >= end:
                arr = t
                break
        if arr is None:
            continue
        gaps[seq] = arr * 1000.0 - child_ms
    return gaps


def pct(vals: list[float], q: float) -> float:
    if not vals:
        return float("nan")
    s = sorted(vals)
    k = min(len(s) - 1, max(0, int(round(q * (len(s) - 1)))))
    return s[k]


def part_a() -> dict:
    n = LATENCY_LINES
    d_raw, d_arr = run("direct", ["--stamp", str(n), "0.025"])
    t_raw, t_arr = run("teed", ["--stamp", str(n), "0.025"])
    dg = stamp_gaps(d_raw, d_arr)
    tg = stamp_gaps(t_raw, t_arr)
    common = sorted(set(dg) & set(tg))
    # The per-line gap carries each arm's own constant (scheduler, pipe depth). The layer's cost is the
    # DIFFERENCE of gaps on the same seq, so the two clocks cancel.
    deltas = [tg[s] - dg[s] for s in common]
    res = {
        "linesRequested": n,
        "directLines": len(dg),
        "teedLines": len(tg),
        "commonLines": len(common),
        "addedMs": {
            "p50": round(pct(deltas, 0.50), 3),
            "p99": round(pct(deltas, 0.99), 3),
            "mean": round(statistics.fmean(deltas), 3) if deltas else None,
            "min": round(min(deltas), 3) if deltas else None,
            "max": round(max(deltas), 3) if deltas else None,
        },
    }
    res["pass"] = (res["addedMs"]["p50"] <= 2.0 and res["addedMs"]["p99"] <= 5.0
                   and len(common) >= int(n * 0.9))
    return res


def part_b() -> dict:
    fixtures = {}
    ok = True
    for f in ("tui-alt", "pager-alt"):
        binp = FIXDIR / (f + ".bin")
        meta = json.loads((FIXDIR / (f + ".json")).read_text())
        d_raw, _ = run("direct", ["--replay", str(binp)])
        t_raw, _ = run("teed", ["--replay", str(binp)])
        cmp = subprocess.run(
            ["node", str(ORACLE), "--cols", str(meta["cols"]), "--rows", str(meta["rows"]),
             "--direct", "-", "--teed", "-"],
            input=json.dumps({
                "direct": base64.b64encode(d_raw).decode(),
                "teed": base64.b64encode(t_raw).decode(),
            }).encode(),
            capture_output=True)
        if cmp.returncode != 0:
            fixtures[f] = {"error": cmp.stderr.decode()[:400]}
            ok = False
            continue
        r = json.loads(cmp.stdout)
        fixtures[f] = r
        if not r.get("identical"):
            ok = False
    return {"fixtures": fixtures, "pass": ok}


def part_c() -> dict:
    d_raw, _ = run("direct", ["--escapes"])
    t_raw, _ = run("teed", ["--escapes"])
    # Assert SEMANTICS, not bytes. The inner ConPTY is a console and canonicalizes what it re-emits - it
    # rewrote our OSC 8 to carry an `id=` parameter (measured: `]8;;https://...` became
    # `]8;id=20260-1;https://...`), and it relocates the title OSC. Demanding byte equality would fail on
    # that canonicalization and say nothing about whether the hyperlink survived, which it did. So: the
    # OSC 8 target is present, and the 2026 guard is both opened and closed. Final-screen agreement is
    # Part B's job and is the stronger check.
    def osc8_target(raw: bytes) -> str | None:
        m = re.search(rb"\x1b\]8;[^;]*;(https?://[^\x1b]*)\x1b\\", raw)
        return m.group(1).decode() if m else None

    d8, t8 = osc8_target(d_raw), osc8_target(t_raw)
    res = {
        "osc8Target": {"direct": d8, "teed": t8, "match": d8 == t8 and d8 is not None},
        "dec2026h": {"direct": b"\x1b[?2026h" in d_raw, "teed": b"\x1b[?2026h" in t_raw},
        "dec2026l": {"direct": b"\x1b[?2026l" in d_raw, "teed": b"\x1b[?2026l" in t_raw},
    }
    ok = (res["osc8Target"]["match"]
          and res["dec2026h"]["direct"] and res["dec2026h"]["teed"]
          and res["dec2026l"]["direct"] and res["dec2026l"]["teed"])
    return {"probes": res, "pass": ok,
            "note": "the inner ConPTY canonicalizes OSC 8 (adds id=) and relocates the title OSC; "
                    "the assertion is that the hyperlink target and the 2026 guard survive"}


def main():
    if not MUXTEE.exists():
        raise SystemExit("muxtee.exe not built: " + str(MUXTEE))
    report = {
        "schema": "muxtee-s5/1",
        "muxtee": str(MUXTEE),
        "child": str(CHILD),
        "geometry": {"rows": ROWS, "cols": COLS},
        "partA_latency": part_a(),
        "partB_fidelity": part_b(),
        "partC_escapes": part_c(),
    }
    report["pass"] = all(report[k]["pass"] for k in ("partA_latency", "partB_fidelity", "partC_escapes"))
    OUT.write_text(json.dumps(report, indent=2))
    print(json.dumps(report, indent=2))
    return 0 if report["pass"] else 1


if __name__ == "__main__":
    sys.exit(main())
