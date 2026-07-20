"""Round-trip the new console input path against the REAL Windows console API.

A child process gets its own (hidden) console via CREATE_NO_WINDOW, injects synthetic key and
mouse-wheel INPUT_RECORDs with WriteConsoleInputW, reads them back with ReadConsoleInputW using
muxctl's ctypes structs, and decodes them with ConsoleInputTranslator. This proves the struct
layout (offsets/union/size), the MOUSE_WHEELED delta math, and the wheel->SGR forwarding decision
against the actual console subsystem — everything short of a human physically spinning the wheel
over the conhost window.
"""
import os
import subprocess
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]

CHILD = r"""
import ctypes, sys
sys.path.insert(0, sys.argv[1])
import muxctl

k = ctypes.windll.kernel32
GENERIC_RW = 0xC0000000
hin = k.CreateFileW("CONIN$", GENERIC_RW, 3, None, 3, 0, None)
if hin in (None, -1):
    sys.stdout.write("ERR no console input handle"); sys.exit(1)

recs = (muxctl.INPUT_RECORD * 6)()

def key(rec, vk, ch, ctrl=0, repeat=1):
    rec.EventType = muxctl.KEY_EVENT_TYPE
    rec.Event.KeyEvent.bKeyDown = 1
    rec.Event.KeyEvent.wRepeatCount = repeat
    rec.Event.KeyEvent.wVirtualKeyCode = vk
    rec.Event.KeyEvent.wVirtualScanCode = 0
    rec.Event.KeyEvent.UnicodeChar = ch or "\x00"
    rec.Event.KeyEvent.dwControlKeyState = ctrl

def wheel(rec, delta, x, y):
    rec.EventType = muxctl.MOUSE_EVENT_TYPE
    rec.Event.MouseEvent.dwMousePosition.X = x
    rec.Event.MouseEvent.dwMousePosition.Y = y
    rec.Event.MouseEvent.dwButtonState = (delta & 0xFFFF) << 16
    rec.Event.MouseEvent.dwControlKeyState = 0
    rec.Event.MouseEvent.dwEventFlags = muxctl.MOUSE_WHEELED

key(recs[0], 0x41, "a")
key(recs[1], 0x26, "")            # VK_UP
wheel(recs[2], 120, 9, 4)         # one notch up at buffer cell (9,4)
wheel(recs[3], -120, 9, 4)        # one notch down
wheel(recs[4], 60, 9, 4)          # half notch: must accumulate, emit nothing
wheel(recs[5], 60, 9, 4)          # second half completes the notch

n = ctypes.c_uint()
if not k.WriteConsoleInputW(hin, recs, len(recs), ctypes.byref(n)) or n.value != len(recs):
    sys.stdout.write("ERR WriteConsoleInputW"); sys.exit(1)

tracker = muxctl.ScreenModeTracker()
tracker.ingest(b"\x1b[?1049h\x1b[?1000h\x1b[?1002h\x1b[?1003h\x1b[?1006h")
# alt_scroll="sgr" pins the report contract: this test validates struct layout and wheel-delta
# decode math, which the page-key default (rate-limited) would hide behind its 90ms limiter.
tr = muxctl.ConsoleInputTranslator(tracker, cell_resolver=lambda pos: (pos.X + 1, pos.Y + 1),
                                   alt_scroll="sgr")

out = bytearray()
seen = 0
while seen < len(recs):
    got = (muxctl.INPUT_RECORD * 16)()
    if not k.ReadConsoleInputW(hin, got, 16, ctypes.byref(n)) or not n.value:
        sys.stdout.write("ERR ReadConsoleInputW"); sys.exit(1)
    for i in range(n.value):
        rec = got[i]
        if rec.EventType in (muxctl.KEY_EVENT_TYPE, muxctl.MOUSE_EVENT_TYPE):
            seen += 1
        data, detach = tr.feed(rec)
        out += data
sys.stdout.write(out.hex())
"""


class ConsoleInputRoundtripTests(unittest.TestCase):
    @unittest.skipUnless(os.name == "nt", "windows console API")
    def test_real_console_records_decode_to_expected_pty_bytes(self):
        r = subprocess.run(
            [sys.executable, "-c", CHILD, str(REPO)],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=30,
            creationflags=subprocess.CREATE_NO_WINDOW,   # own hidden console, not the runner's
        )
        self.assertEqual(r.returncode, 0, msg=r.stdout + r.stderr)
        expected = (
            b"a"                    # plain key
            + b"\x1b[A"             # VK_UP -> CSI A
            + b"\x1b[<64;10;5M"     # wheel up, SGR report at 1-based cell
            + b"\x1b[<65;10;5M"     # wheel down
            + b"\x1b[<64;10;5M"     # two half-notches accumulate into one report
        ).hex()
        self.assertEqual(r.stdout.strip(), expected, msg=r.stdout + r.stderr)


if __name__ == "__main__":
    unittest.main()
