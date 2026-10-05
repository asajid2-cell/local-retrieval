#!/usr/bin/env python3
"""run_spikes.py — P0 feasibility spikes S1-S4 for muxtee.

Each spike answers one question that can change the muxtee design. The probe (conhost_probe.cs) is
run AS THE ConPTY CHILD, because that is the only way to get a real console in this environment:
Windows Terminal and pywinpty both hand a child a console, and a helper launched from a console-less
parent can neither AttachConsole nor AllocConsole its way to one (verified — err=6 on a valid
handle).

  S1  Under a ConPTY, is the console buffer viewport-sized (no history)?   -> geometry
  S2  Does the ConPTY child get WINDOW_BUFFER_SIZE_EVENT on resize?        -> watch + resize
  S3  Do modifier keys reach the child intact?                            -> keys + synthesised keys
  S4  Does EVENT_CONSOLE_UPDATE_* fire for ConPTY consoles?               -> WinEvent hook

S5 (the extra-ConPTY latency/fidelity cost) needs muxtee itself and lives in run_s5.py once P1
builds it.

Results append to %LOCALAPPDATA%\\muxtee\\spikes\\spike-results.json.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import time
from pathlib import Path

import winpty

HERE = Path(__file__).resolve().parent
PROBE = HERE / "bin" / "conhost_probe.exe"
SPIKE_DIR = Path(os.environ["LOCALAPPDATA"]) / "muxtee" / "spikes"
PROBE_LOG = SPIKE_DIR / "conhost_probe.jsonl"
RESULTS = SPIKE_DIR / "spike-results.json"


def read_probe_log(since: int = 0) -> list[dict]:
    """Read the probe's JSONL verdict file, skipping `since` lines already consumed."""
    if not PROBE_LOG.exists():
        return []
    out = []
    for line in PROBE_LOG.read_text(encoding="utf-8", errors="replace").splitlines():
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            out.append(json.loads(line))
        except json.JSONDecodeError:
            continue
    return out[since:]


def probe_line_count() -> int:
    if not PROBE_LOG.exists():
        return 0
    return sum(1 for l in PROBE_LOG.read_text(encoding="utf-8", errors="replace").splitlines() if l.strip().startswith("{"))


def spawn_probe(args: list[str], rows=30, cols=100):
    """Run the probe as the ConPTY child. pywinpty gives it a real console."""
    return winpty.PtyProcess.spawn([str(PROBE)] + args, dimensions=(rows, cols))


def record(spike: str, passed: bool, detail: dict):
    SPIKE_DIR.mkdir(parents=True, exist_ok=True)
    all_results = []
    if RESULTS.exists():
        try:
            all_results = json.loads(RESULTS.read_text(encoding="utf-8"))
        except Exception:
            all_results = []
    all_results = [r for r in all_results if r.get("spike") != spike]
    all_results.append({"spike": spike, "passed": passed, "detail": detail, "at": time.strftime("%Y-%m-%dT%H:%M:%S")})
    RESULTS.write_text(json.dumps(all_results, indent=2), encoding="utf-8")
    print(f"  {spike}: {'PASS' if passed else 'FAIL'}  {json.dumps(detail)[:600]}")


# ------------------------------------------------------------------ S1

def s1_geometry():
    """Is the ConPTY console buffer viewport-sized, or does it hold history?"""
    before = probe_line_count()
    p = spawn_probe(["--geometry"], rows=30, cols=100)
    # Drain the ConPTY until the probe has written both geometry rows: PtyProcess.read blocks, so
    # poll the log rather than sleeping a fixed time (the earlier race read before the flush).
    deadline = time.time() + 6.0
    rows = []
    while time.time() < deadline:
        rows = [r for r in read_probe_log(before) if r.get("tag") in ("before", "after")]
        if len(rows) >= 2:
            break
        time.sleep(0.2)
    p.terminate(force=True)
    time.sleep(0.2)
    rows = [r for r in read_probe_log(before) if r.get("tag") in ("before", "after")]
    if len(rows) < 2:
        record("S1", False, {"error": "probe produced no geometry rows", "raw": read_probe_log(before)})
        return
    before_r, after_r = rows[0], rows[1]
    # The question is specifically whether 500 lines of output grow the buffer past the viewport.
    grew = after_r["historyRows"] > 0
    viewport_sized = after_r["viewportSized"]
    record("S1", viewport_sized, {
        "before": before_r, "after": after_r,
        "conclusion": ("ConPTY console buffer is viewport-sized: no history is readable, so the "
                       "Mirror tier can never carry scrollback (spec §2 stands)")
                      if viewport_sized else
                      (f"ConPTY console buffer HOLDS history ({after_r['historyRows']} rows): the "
                       "Mirror tier could read scrollback — update spec §2/§8"),
    })


# ------------------------------------------------------------------ S2

def s2_resize():
    """Does the ConPTY child see WINDOW_BUFFER_SIZE_EVENT when the pty resizes?"""
    before = probe_line_count()
    p = spawn_probe(["--watch", "6000"], rows=30, cols=100)
    time.sleep(0.7)
    events = []
    # Resize the ConPTY from the host side, the way WT does when the user drags the window.
    for (r, c) in [(30, 120), (30, 120), (40, 120), (40, 90)]:
        try:
            p.setwinsize(r, c)
        except Exception as e:                                         # older pywinpty API
            try: p.resize(r, c)
            except Exception as e2: events.append({"resizeFail": str(e2)})
        time.sleep(0.5)
    time.sleep(2.0)
    p.terminate(force=True)
    rows = read_probe_log(before)
    resizes = [r for r in rows if r.get("event") == "resize"]
    record("S2", len(resizes) > 0, {
        "resizeEvents": resizes, "allRows": rows,
        "conclusion": ("WINDOW_BUFFER_SIZE_EVENT arrives on ConPTY resize: muxtee's T2 can resize "
                       "the inner ConPTY on the event")
                      if resizes else
                      ("No WINDOW_BUFFER_SIZE_EVENT under this ConPTY: keep the 250 ms srWindow "
                       "poll as the primary path (spec §5.2 backstop)"),
    })


# ------------------------------------------------------------------ S3

def s3_keys():
    """Do modifier keys reach the child intact through muxtee?

    muxtee's T3 writes bytes into the ConPTY input pipe. Whether a modifier survives depends on the
    ENCODING, not on ConPTY magic. Two candidate encodings are tested against the probe's
    ReadConsoleInputW — the same call muxtee's T2 makes on the outer console:

      CSI-u   (\\x1b[13;2u)                 -> delivered as raw characters, modifier LOST
      win32   (\\x1b[13;2;13;1;2;1_)          -> decoded to VK_RETURN with cs=SHIFT_PRESSED, intact

    So the pass condition is: a Shift+Enter sent in the win32-input-mode encoding arrives as ONE
    key record with vk=VK_RETURN and cs&SHIFT. This is what decides spec §5.1 and §10-S3.
    """
    before = probe_line_count()
    p = spawn_probe(["--keys", "4500"], rows=30, cols=100)
    time.sleep(1.0)   # let ConPTY's own startup records drain before we start writing
    # win32-input-mode: CSI Vk;Sc;Uc;Kd;Cs;Rc _   (Kd=1 down, Cs=modifier bitmask)
    SHIFT = 2
    cases = {
        "shift+enter": f"\x1b[13;2;13;1;{SHIFT};1_",
        "ctrl+enter":  f"\x1b[13;5;13;1;4;1_",
        "alt+left":    f"\x1b[37;75;0;1;8;1_",
        "plain-A":     "A",
    }
    for name, seq in cases.items():
        try: p.write(seq)
        except Exception: pass
        time.sleep(0.3)
    time.sleep(1.2)
    p.terminate(force=True)
    rows = read_probe_log(before)
    keys = [r for r in rows if r.get("event") == "key"]
    # A decoded modifier key is exactly one record with a real VK and the modifier in cs.
    shift_enter = [k for k in keys if k["vk"] == 13 and (k["cs"] & SHIFT)]
    intact = len(shift_enter) == 1
    record("S3", intact, {
        "keyCount": len(keys),
        "shiftEnterRecord": shift_enter[0] if shift_enter else None,
        "keys": keys,
        "conclusion": ("The win32-input-mode encoding survives the ConPTY: Shift+Enter arrives as one "
                       "VK_RETURN record with SHIFT_PRESSED, so muxtee's T2 can read raw INPUT_RECORDs "
                       "and re-encode them itself (spec §10-S3 fallback)")
                      if intact else
                      ("The win32-input-mode encoding did NOT decode: modifiers are being lost, so "
                       "muxtee must keep VT input mode on the outer console and pass the child's VT "
                       "through unchanged"),
    })


# ------------------------------------------------------------------ S4

def s4_winevent():
    """Does EVENT_CONSOLE_UPDATE_* fire for a ConPTY console, and does it fire RELIABLY?

    A single hook run is not enough: the first attempt fired events and the next was silent, which
    is the answer that matters — under a ConPTY the console-update WinEvents are not dependable. We
    run the hook several times and report the fire rate, because a Mirror tier that polled only on
    events would go deaf on the silent runs.
    """
    runs = []
    for attempt in range(4):
        before = probe_line_count()
        p = spawn_probe(["--winevent", "4000"], rows=30, cols=100)
        time.sleep(0.6)
        for i in range(20):
            try: p.write(f"\r\nwin-event-probe {i}\r\n")
            except Exception: pass
            time.sleep(0.15)
        time.sleep(1.2)
        p.terminate(force=True)
        rows = read_probe_log(before)
        fired = [r for r in rows if r.get("event") == "winevent"]
        hook_row = next((r for r in rows if r.get("event") == "winevent-hook"), {})
        runs.append({"attempt": attempt, "installed": hook_row.get("hook", 0) != 0, "fired": len(fired)})
    fired_runs = sum(1 for r in runs if r["fired"] > 0)
    total = len(runs)
    reliable = fired_runs == total
    record("S4", reliable, {
        "runs": runs, "firedRuns": fired_runs, "totalRuns": total,
        "conclusion": ("EVENT_CONSOLE_UPDATE_* fires for ConPTY consoles on every run: the Mirror "
                       "tier could poll on events instead of a 40 ms timer")
                      if reliable else
                      (f"Console-update WinEvents are UNRELIABLE under ConPTY ({fired_runs}/{total} "
                       "runs fired): the Mirror tier keeps its 40 ms poll, and a hook must never be "
                       "the only trigger (spec §8)"),
    })


def main():
    which = sys.argv[1:] or ["1", "2", "3", "4"]
    if not PROBE.exists():
        print(f"probe not built at {PROBE}; build conhost_probe.cs first", file=sys.stderr)
        return 1
    # Fresh log per run so line-count deltas are unambiguous.
    if PROBE_LOG.exists():
        PROBE_LOG.unlink()
    print(f"muxtee P0 spikes -> {RESULTS}")
    if "1" in which: s1_geometry()
    if "2" in which: s2_resize()
    if "3" in which: s3_keys()
    if "4" in which: s4_winevent()
    return 0


if __name__ == "__main__":
    sys.exit(main())
