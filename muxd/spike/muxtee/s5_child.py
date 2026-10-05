#!/usr/bin/env python3
"""s5_child.py — the S5 stamping child.

Three modes, all writing to the console the ConPTY handed it:

  --stamp N   print N lines, each "STAMP:<seq>:<t_ms>" with t_ms a monotonic stamp taken the instant
              before the write. The pty reader records arrival wall-time per line, and the difference
              between the two stamp clocks is the added latency of whatever sits in between.
  --replay F  cat fixture F to the console byte-for-byte (write through the raw buffer, not print,
              so no re-encoding mangles the escapes).
  --escapes   emit the VT the fixtures do not carry: an OSC 8 hyperlink, a DECSET 2026 synchronized
              -update guard around a repaint, and a title OSC. The round-trip must be byte-identical.
"""
import sys, time, os

def raw(b):
    # Write bytes to the console handle directly. print() would decode to str and re-encode, which is
    # exactly the mangling S5 is trying to rule out.
    sys.stdout.buffer.write(b)
    sys.stdout.buffer.flush()

def main(argv):
    if argv[0] == "--stamp":
        n = int(argv[1])
        gap = float(argv[2]) if len(argv) > 2 else 0.025
        for i in range(n):
            t = int(time.monotonic() * 1000)
            raw(("STAMP:%d:%d\n" % (i, t)).encode("ascii"))
            # Pace well above the Windows ~15.6 ms scheduler tick. A 2 ms sleep quantizes to 16 ms, and
            # then every arrival delta reads as 0 or 16 regardless of what sits in between - measured,
            # and the reason the first cut of S5 showed a bogus 16 ms p99.
            time.sleep(gap)
    elif argv[0] == "--replay":
        with open(argv[1], "rb") as f:
            raw(f.read())
    elif argv[0] == "--escapes":
        # OSC 8 open + text + close; DECSET 2026 on, a repaint, off; a title.
        stream = (
            b"\x1b]0;S5-ESCAPES\x07"
            b"\x1b[?2026h"
            b"\x1b]8;;https://example.invalid/s5\x1b\\HYPERLINK\x1b]8;;\x1b\\"
            b"\x1b[2;1Hrepaint-under-2026"
            b"\x1b[?2026l"
            b"\x1b[5;1Hdone\r\n"
        )
        raw(stream)
    else:
        raise SystemExit("unknown mode " + argv[0])

if __name__ == "__main__":
    main(sys.argv[1:])
