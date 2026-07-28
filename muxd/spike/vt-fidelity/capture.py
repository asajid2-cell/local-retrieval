#!/usr/bin/env python3
"""capture.py — record raw ring bytes into vt-fidelity fixtures.

Two sources, both producing the ConPTY dialect muxd actually replays:

  --live <session>   attach to a running muxd session over the LOOPBACK ws
                     (LOCAL_PORT 7699) and keep the scrollback replay payload.
                     This is the same byte range the relay's `t:'sb'` handler
                     serves (muxd.py:3823-3841, session.scrollback(limit)).
  --conpty <preset>  drive a scripted TUI under a real ConPTY via pywinpty
                     and record its output — the fallback when the runner has
                     no live TUI session to record.

Bytes are captured exactly as muxd stores them: pywinpty hands back `str`,
muxd does `data.encode("utf-8", "replace")` (muxd.py:1525-1541), so we do too.

Usage:
  python capture.py --list
  python capture.py --live <name> [--fixture NAME] [--max 800000]
  python capture.py --conpty {pager-alt,tui-alt,shell-plain,all}
  python capture.py --auto        # every alive session + every conpty preset
"""
from __future__ import annotations

import argparse
import asyncio
import base64
import json
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
OUTDIR = os.path.join(HERE, "fixtures", "recorded")
LOCAL_PORT = 7699          # muxd.py:892
DEFAULT_MAX = 800_000      # brief: t:'sb' max


# ---------------------------------------------------------------- fixtures

def write_fixture(name, data: bytes, *, cols, rows, source, desc, recorded_real,
                  capture_method):
    os.makedirs(OUTDIR, exist_ok=True)
    binp = os.path.join(OUTDIR, name + ".bin")
    with open(binp, "wb") as f:
        f.write(data)
    side = {
        "name": name,
        "corpus": "recorded",
        "cols": cols,
        "rows": rows,
        "bytes": len(data),
        "source": source,
        "desc": desc,
        # "live-muxd" | "conpty-<preset>" — how these bytes were obtained.
        "captureMethod": capture_method,
        # Orchestrator flag: true means these bytes are a transcript of a real
        # session on this runner and must be reviewed before being committed.
        "containsRecordedSessionContent": bool(recorded_real),
    }
    with open(os.path.join(OUTDIR, name + ".json"), "w", encoding="utf-8") as f:
        json.dump(side, f, indent=2)
    flag = "  [REAL-SESSION-CONTENT]" if recorded_real else ""
    print(f"wrote {name:<28} {len(data):>9} bytes{flag}")
    return binp


# ---------------------------------------------------------------- live ws

def _ws_connect():
    """Return the awaitable async context manager websockets.connect() hands back.

    Deliberately NOT `async def` + `await`: pre-awaiting connect() yields a bare
    connection object, which under the installed websockets release does not
    implement __aenter__ ("does not support the asynchronous context manager
    protocol"). Callers must do `async with _ws_connect() as ws:`.
    """
    import websockets
    return websockets.connect(
        f"ws://127.0.0.1:{LOCAL_PORT}", max_size=None, open_timeout=6
    )


async def list_sessions():
    async with _ws_connect() as ws:
        await ws.send(json.dumps({"t": "ls"}))
        return json.loads(await asyncio.wait_for(ws.recv(), 6)).get("list", [])


def _payload_bytes(msg):
    """Normalise one ws frame into raw VT bytes, or None if it carries none."""
    if isinstance(msg, (bytes, bytearray)):
        return bytes(msg)
    try:
        d = json.loads(msg)
    except Exception:
        return msg.encode("utf-8", "replace")
    if not isinstance(d, dict):
        return None
    for k in ("b64", "data64", "payload64"):
        if isinstance(d.get(k), str):
            return base64.b64decode(d[k])
    if isinstance(d.get("d"), str):
        return d["d"].encode("utf-8", "replace")
    return None


async def capture_live(session, fixture, maxb, tail_ms):
    """Attach and keep the replay payload, then drain `tail_ms` of live output.

    The attach frame deliberately omits `cols`: sending it would call
    update_local_session_size and RESIZE THE LIVE SESSION (ledger).
    """
    async with _ws_connect() as ws:
        await ws.send(json.dumps({"t": "attach", "s": session, "sb": maxb}))
        buf = bytearray()
        deadline = time.time() + max(tail_ms, 1500) / 1000.0
        while time.time() < deadline:
            try:
                msg = await asyncio.wait_for(ws.recv(), timeout=1.5)
            except asyncio.TimeoutError:
                if buf:
                    break
                continue
            except Exception:
                break
            chunk = _payload_bytes(msg)
            if chunk:
                buf += chunk
                if len(buf) >= maxb:
                    break
    return bytes(buf)


# ---------------------------------------------------------------- conpty

def _drive(cmdline, cols, rows, script, settle=0.6):
    """Run cmdline (argv list) under a real ConPTY, apply `script`, return bytes.

    PtyProcess.read() BLOCKS when the child has produced nothing ("Can block if
    there is nothing to read" — pywinpty docstring), so draining it from the
    same thread that paces the script makes every timing window unbounded: a
    quiet child wedges the capture forever. The reader therefore lives on its
    own daemon thread and the script thread only ever sleeps.
    """
    import threading

    from winpty import PtyProcess

    # pywinpty resolves argv[0] itself; a pre-quoted command string fails
    # its executable lookup, so always hand it a list.
    pty = PtyProcess.spawn(cmdline, dimensions=(rows, cols))
    out = bytearray()
    lock = threading.Lock()
    stop = threading.Event()

    def reader():
        while not stop.is_set():
            try:
                data = pty.read(8192)
            except Exception:          # EOFError once the terminal closes
                return
            if data:
                # muxd.py:1541 — pywinpty hands back str, muxd stores utf-8.
                chunk = data.encode("utf-8", "replace")
                with lock:
                    out.extend(chunk)
            else:
                time.sleep(0.01)

    th = threading.Thread(target=reader, daemon=True)
    th.start()

    time.sleep(settle)
    for keys, wait in script:
        if not pty.isalive():
            break
        try:
            if keys:
                pty.write(keys)
        except Exception:
            break
        time.sleep(wait)
    time.sleep(settle)

    stop.set()
    try:
        if pty.isalive():
            pty.terminate(force=True)
    except Exception:
        pass
    th.join(timeout=1.0)
    with lock:
        return bytes(out)


def preset_pager_alt():
    """`less` on a large file — a real alt-screen pager under ConPTY."""
    less = shutil.which("less")
    if not less:
        return None
    target = os.path.join(HERE, "..", "..", "muxd.py")
    target = os.path.abspath(target)
    if not os.path.exists(target):
        target = os.path.abspath(__file__)
    data = _drive(
        [less, "-R", target], 140, 40,
        [(" ", 0.35), (" ", 0.35), ("G", 0.5), ("g", 0.35),
         ("/def\r", 0.5), ("n", 0.3), ("q", 0.4)],
    )
    return data, 140, 40, f"less -R {os.path.basename(target)} under ConPTY", \
        "alt-screen pager: 1049 enter/leave, full repaints, reverse-video status line"


def preset_tui_alt():
    """Scripted alt-screen TUI (stands in for the curses demo; no _curses on Windows)."""
    demo = os.path.join(HERE, "tui_demo.py")
    data = _drive(
        [sys.executable, "-u", demo], 140, 40,
        [("\r", 0.3)] * 8,
    )
    return data, 140, 40, "tui_demo.py under ConPTY", \
        "alt-screen TUI: 1049/1/2004/1000/1002/1006, DECSTBM, SGR 256+truecolor, wide chars"


def preset_shell_plain():
    """Plain normal-buffer shell session."""
    data = _drive(
        ["cmd.exe"], 140, 40,
        [("echo vt-fidelity plain shell capture\r", 0.4),
         ("ver\r", 0.4),
         ("dir /w\r", 0.6),
         ("echo %CD%\r", 0.4),
         ("exit\r", 0.5)],
    )
    return data, 140, 40, "cmd.exe under ConPTY", \
        "plain shell on the normal buffer: prompts, echoed input, scrolling output"


PRESETS = {
    "pager-alt": preset_pager_alt,
    "tui-alt": preset_tui_alt,
    "shell-plain": preset_shell_plain,
}


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--live", metavar="SESSION")
    ap.add_argument("--fixture", metavar="NAME")
    ap.add_argument("--max", type=int, default=DEFAULT_MAX)
    ap.add_argument("--tail-ms", type=int, default=1800)
    ap.add_argument("--conpty", metavar="PRESET")
    ap.add_argument("--auto", action="store_true")
    a = ap.parse_args()

    if a.list:
        for s in asyncio.run(list_sessions()):
            print(f"{s.get('name'):<44} alive={str(s.get('alive')):<5} "
                  f"{s.get('cols')}x{s.get('rows')} kind={s.get('kind')}")
        return

    if a.live:
        data = asyncio.run(capture_live(a.live, a.fixture, a.max, a.tail_ms))
        if not data:
            print(f"NO BYTES from live session {a.live!r}", file=sys.stderr)
            return 1
        # Geometry comes from the session record, not the attach frame: asking
        # for a size over ws would resize the live session.
        cols, rows = 80, 24
        try:
            for s in asyncio.run(list_sessions()):
                if s.get("name") == a.live:
                    cols = s.get("cols") or cols
                    rows = s.get("rows") or rows
                    break
        except Exception:
            pass
        write_fixture(a.fixture or ("live-" + a.live[:28]), data,
                      cols=cols, rows=rows, source=f"live muxd session {a.live}",
                      desc="recorded muxd scrollback replay payload",
                      recorded_real=True, capture_method="live-muxd")
        return

    todo = []
    if a.conpty:
        todo = list(PRESETS) if a.conpty == "all" else [a.conpty]
    elif a.auto:
        todo = list(PRESETS)
    else:
        ap.print_help()
        return

    for name in todo:
        fn = PRESETS.get(name)
        if not fn:
            print(f"unknown preset {name}", file=sys.stderr)
            continue
        got = fn()
        if not got:
            print(f"skip {name}: tool unavailable", file=sys.stderr)
            continue
        data, cols, rows, source, desc = got
        if not data:
            print(f"skip {name}: no bytes captured", file=sys.stderr)
            continue
        # ConPTY presets record scripted programs, not human session content.
        write_fixture(name, data, cols=cols, rows=rows, source=source,
                      desc=desc, recorded_real=False,
                      capture_method=f"conpty-{name}")

    if a.auto:
        try:
            sessions = asyncio.run(list_sessions())
        except Exception as e:
            print(f"muxd not reachable ({e}); conpty fixtures only", file=sys.stderr)
            return
        for s in sessions:
            if not s.get("alive"):
                continue
            nm = s.get("name")
            data = asyncio.run(capture_live(nm, None, a.max, a.tail_ms))
            if not data:
                print(f"skip live {nm}: no bytes", file=sys.stderr)
                continue
            write_fixture("live-" + nm[:28], data,
                          cols=s.get("cols") or 80, rows=s.get("rows") or 24,
                          source=f"live muxd session {nm} (kind={s.get('kind')})",
                          desc="recorded muxd scrollback replay payload",
                          recorded_real=True, capture_method="live-muxd")


if __name__ == "__main__":
    sys.exit(main() or 0)
