#!/usr/bin/env python3
"""Scripted alt-screen TUI, driven under a real ConPTY by capture.py.

Windows CPython has no `curses` (`_curses` is not built), so this emits the
same VT the brief's curses demo would: alt screen + scroll region + SGR +
cursor addressing + box drawing, i.e. the shape codex/claude TUIs produce.
Reads one keystroke per frame from stdin so the driver can step it.
"""
import sys, time

W = sys.stdout.write


def flush():
    sys.stdout.flush()


def frame(n, label):
    W("\x1b[H")                              # home
    W("\x1b[1;38;5;39m")                     # bold, 256-color
    W("+" + "-" * 60 + "+\r\n")
    W(f"| \x1b[0m\x1b[97mvt-fidelity TUI demo \x1b[38;2;255;140;0mframe {n:>3}\x1b[0m")
    W("\x1b[1;38;5;39m" + " " * 20 + "|\r\n")
    W("+" + "-" * 60 + "+\x1b[0m\r\n")
    W("\x1b[4;1H\x1b[J")                     # cursor to row 4, erase below
    for i in range(6):
        bar = "#" * ((n * 3 + i * 5) % 40)
        W(f"\x1b[38;5;{160 + i}mrow {i}\x1b[0m |{bar:<40}| {label}\r\n")
    W("\x1b[K")
    W(f"\x1b[38;5;244mstatus: {label} — press any key\x1b[0m\r\n")
    flush()


def main():
    W("\x1b[?1049h")                         # enter alt screen
    W("\x1b[?1h")                            # DECCKM application cursor keys
    W("\x1b[?2004h")                         # bracketed paste
    W("\x1b[?1000h\x1b[?1002h\x1b[?1006h")   # mouse tracking (SGR)
    W("\x1b]0;vt-fidelity tui demo\x07")     # OSC 0 title
    W("\x1b[2J")
    flush()

    for n in range(6):
        frame(n, ["boot", "scan", "build", "verify", "commit", "done"][n])
        sys.stdin.readline()

    # scroll region churn inside the alt screen
    W("\x1b[12;20r")                         # DECSTBM rows 12..20
    W("\x1b[12;1H")
    for i in range(24):
        W(f"\x1b[38;5;{40 + (i % 8)}mscroll line {i:>3} ── 你好 \U0001f680\x1b[0m\r\n")
        if i % 8 == 7:
            flush()
            time.sleep(0.01)
    W("\x1b[r")                              # reset scroll region
    flush()
    sys.stdin.readline()

    W("\x1b[?1000l\x1b[?1002l\x1b[?1006l")
    W("\x1b[?2004l\x1b[?1l")
    W("\x1b[?1049l")                         # leave alt screen
    W("back on the normal buffer\r\n")
    flush()


if __name__ == "__main__":
    main()
