"""Round-trip the new console input path against the REAL Windows console API.

A child process gets its own (hidden) console via CREATE_NO_WINDOW, injects synthetic key and
mouse-wheel INPUT_RECORDs with WriteConsoleInputW, reads them back with ReadConsoleInputW using
muxctl's ctypes structs, and decodes them with ConsoleInputTranslator. This proves the struct
layout (offsets/union/size), the MOUSE_WHEELED delta math, and the wheel->SGR forwarding decision
against the actual console subsystem — everything short of a human physically spinning the wheel
over the conhost window.
"""
import json
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

SCREEN_READ_ERROR_CHILD = r"""
import ctypes, sys
sys.path.insert(0, sys.argv[1])
import muxrun
k = ctypes.windll.kernel32
original = k.ReadConsoleOutputCharacterW
calls = 0
def read(handle, buf, count, coord, written):
    global calls
    calls += 1
    if calls == 2:
        return 0
    return original(handle, buf, count, coord, written)
k.ReadConsoleOutputCharacterW = read
try:
    sys.stdout.write(repr(muxrun.read_visible_screen()))
finally:
    k.ReadConsoleOutputCharacterW = original
"""
SHORT_SCREEN_READ_CHILD = r"""
import ctypes, sys
sys.path.insert(0, sys.argv[1])
import muxrun
k = ctypes.windll.kernel32
original = k.ReadConsoleOutputCharacterW
calls = 0
def read(handle, buf, count, coord, written):
    global calls
    calls += 1
    result = original(handle, buf, count, coord, written)
    if calls == 2:
        ctypes.cast(written, ctypes.POINTER(ctypes.c_uint))[0] = count - 1
    return result
k.ReadConsoleOutputCharacterW = read
try:
    sys.stdout.write(repr(muxrun.read_visible_screen()))
finally:
    k.ReadConsoleOutputCharacterW = original
"""
SCREEN_CHILD = r"""
import ctypes, sys
sys.path.insert(0, sys.argv[1])
import muxrun
k = ctypes.windll.kernel32
hout = k.CreateFileW('CONOUT$', 0xC0000000, 3, None, 3, 0, None)
if hout in (None, -1):
    sys.stdout.write('ERR no console output handle'); sys.exit(1)
k.SetStdHandle(muxrun.STD_OUTPUT_HANDLE, hout)
info = muxrun.CONSOLE_SCREEN_BUFFER_INFO()
if not k.GetConsoleScreenBufferInfo(hout, ctypes.byref(info)):
    sys.stdout.write('ERR screen info'); sys.exit(1)
left, top = info.srWindow.Left, info.srWindow.Top
written = ctypes.c_uint()
# Console cells between header and footer may be uninitialized (NUL), not spaces.
content = 'HEADER' + '\x00' * 8 + 'FOOTER'
if not k.WriteConsoleOutputCharacterW(hout, ctypes.create_unicode_buffer(content), len(content), muxrun.COORD(left, top), ctypes.byref(written)):
    sys.stdout.write('ERR write screen'); sys.exit(1)
cols, rows, text = muxrun.read_visible_screen()
sys.stdout.write(repr(text.split('\r\n')[0]))
"""

ACTIVE_SCREEN_CHILD = r"""
import ctypes, sys
sys.path.insert(0, sys.argv[1])
import muxrun
k = ctypes.windll.kernel32
original = k.GetStdHandle(muxrun.STD_OUTPUT_HANDLE)
active = k.CreateConsoleScreenBuffer(0xC0000000, 3, None, 1, None)
if active in (None, -1):
    sys.stdout.write('ERR create screen'); sys.exit(1)
try:
    if not k.SetConsoleActiveScreenBuffer(active):
        sys.stdout.write('ERR activate screen'); sys.exit(1)
    written = ctypes.c_uint()
    info = muxrun.CONSOLE_SCREEN_BUFFER_INFO()
    if not k.GetConsoleScreenBufferInfo(active, ctypes.byref(info)):
        sys.stdout.write('ERR active info'); sys.exit(1)
    at = muxrun.COORD(info.srWindow.Left, info.srWindow.Bottom)
    if not k.WriteConsoleOutputCharacterW(active, 'ACTIVE FOOTER', 13, at, ctypes.byref(written)):
        sys.stdout.write('ERR write active'); sys.exit(1)
    cols, rows, text = muxrun.read_visible_screen()
    if muxrun.visible_size() != (cols, rows):
        sys.stdout.write('ERR active size mismatch'); sys.exit(1)
    sys.stdout.write(repr(text.split('\r\n')[-1]))
finally:
    k.SetConsoleActiveScreenBuffer(original)
    k.CloseHandle(active)
"""

VT_MODE_CHILD = r"""
import ctypes, json, sys
sys.path.insert(0, sys.argv[1])
import muxrun
k = ctypes.windll.kernel32
h = k.CreateFileW('CONOUT$', 0xC0000000, 3, None, 3, 0, None)
k.SetConsoleMode(h, 0x0001 | 0x0002 | 0x0004)
original = k.GetStdHandle(muxrun.STD_OUTPUT_HANDLE)
def info(handle):
    i = muxrun.CONSOLE_SCREEN_BUFFER_INFO()
    if not k.GetConsoleScreenBufferInfo(handle, ctypes.byref(i)):
        return None
    return (i.dwSize.X, i.dwSize.Y, i.srWindow.Top, i.srWindow.Bottom)
hin = k.CreateFileW('CONIN$', 0xC0000000, 3, None, 3, 0, None)
def input_mode():
    mode = ctypes.c_uint()
    if not k.GetConsoleMode(hin, ctypes.byref(mode)):
        raise ctypes.WinError()
    return mode.value
def write(text):
    n = ctypes.c_uint()
    if not k.WriteConsoleW(h, text, len(text), ctypes.byref(n), None):
        raise ctypes.WinError()
def query():
    # This child owns its isolated input queue. Never flush or consume the app's
    # shared console input from a production owner sidecar.
    k.FlushConsoleInputBuffer(hin)
    write('\x1b[?1000$p\x1b[?1006$p\x1b[?1049$p')
    ready = k.WaitForSingleObject(hin, 500)
    chars = []
    if ready == 0:
        records = (muxrun.INPUT_RECORD * 128)()
        count = ctypes.c_uint()
        k.ReadConsoleInputW(hin, records, 128, ctypes.byref(count))
        chars = [records[i].Event.KeyEvent.uChar.UnicodeChar for i in range(count.value)
                 if records[i].EventType == muxrun.KEY_EVENT and records[i].Event.KeyEvent.bKeyDown]
    return ready, ''.join(chars)
before = (info(original), info(h), input_mode(), muxrun.read_visible_screen()[2], query())
write('\x1b[?1000h\x1b[?1006h')
mouse_only = (info(original), info(h), input_mode(), muxrun.read_visible_screen()[2], query())
write('\x1b[?1049hAPP MODE')
during = (info(original), info(h), input_mode(), muxrun.read_visible_screen()[2], query())
write('\x1b[?1000l\x1b[?1006l\x1b[?1049l')
after = (info(original), info(h), input_mode(), muxrun.read_visible_screen()[2], query())
sys.stdout.write(json.dumps({'before': before, 'mouse_only': mouse_only, 'during': during, 'after': after}))
"""

OWNER_CHILD = r"""
import ctypes, sys
sys.path.insert(0, sys.argv[1])
import muxctl, muxrun
k = ctypes.windll.kernel32
hin = k.CreateFileW("CONIN$", 0xC0000000, 3, None, 3, 0, None)
if hin in (None, -1):
    sys.stdout.write("ERR no console input handle"); sys.exit(1)
records = muxrun.input_records(b"\x1b[<64;10;5M\x1b[<65;10;5M\x1b[<0;10;5M\x1b[<0;10;5m")
wrote = ctypes.c_uint()
if not k.WriteConsoleInputW(hin, (muxrun.INPUT_RECORD * len(records))(*records), len(records), ctypes.byref(wrote)) or wrote.value != len(records):
    sys.stdout.write("ERR WriteConsoleInputW"); sys.exit(1)
read = (muxctl.INPUT_RECORD * 8)()
count = ctypes.c_uint()
if not k.ReadConsoleInputW(hin, read, 8, ctypes.byref(count)):
    sys.stdout.write("ERR ReadConsoleInputW"); sys.exit(1)
seen = [read[i] for i in range(count.value) if read[i].EventType == muxctl.MOUSE_EVENT_TYPE]
if len(seen) != 4:
    sys.stdout.write("ERR wheel or click records missing"); sys.exit(1)
for record in seen:
    m = record.Event.MouseEvent
    sys.stdout.write("%d,%d,%d,%d;" % (m.dwMousePosition.X, m.dwMousePosition.Y,
                    ctypes.c_short(m.dwButtonState >> 16).value, m.dwEventFlags))
    if m.dwEventFlags == 0:
        sys.stdout.write("button=%d;" % m.dwButtonState)
"""


class ConsoleInputRoundtripTests(unittest.TestCase):
    @unittest.skipUnless(os.name == "nt", "windows console API")
    def test_console_vt_mouse_modes_are_not_in_screen_snapshot(self):
        r = subprocess.run(
            [sys.executable, "-c", VT_MODE_CHILD, str(REPO)],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=30,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        self.assertEqual(r.returncode, 0, msg=r.stdout + r.stderr)
        observed = json.loads(r.stdout)
        self.assertIn("APP MODE", observed["during"][3], observed)
        self.assertGreater(observed["before"][1][1], observed["during"][1][1])
        self.assertEqual(observed["before"][1][1], observed["after"][1][1])
        self.assertEqual(observed["before"][1], observed["mouse_only"][1])
        self.assertEqual(observed["before"][2], observed["mouse_only"][2])
        self.assertEqual(observed["before"][3], observed["mouse_only"][3])
        self.assertEqual(observed["before"][2], observed["during"][2])
        for mode in (1000, 1006, 1049):
            self.assertIn(f"\x1b[?{mode};2$y", observed["before"][4][1])
            self.assertIn(f"\x1b[?{mode};1$y", observed["during"][4][1])
            self.assertIn(f"\x1b[?{mode};2$y", observed["after"][4][1])
        for mode in (1000, 1006):
            self.assertIn(f"\x1b[?{mode};1$y", observed["mouse_only"][4][1])
        self.assertIn("\x1b[?1049;2$y", observed["mouse_only"][4][1])
        self.assertNotIn("\x1b[?1000h", observed["during"][3])
        self.assertNotIn("\x1b[?1006h", observed["during"][3])
        self.assertNotIn("\x1b[?1049h", observed["during"][3])

    @unittest.skipUnless(os.name == "nt", "windows console API")
    def test_visible_screen_reads_the_active_console_buffer(self):
        r = subprocess.run(
            [sys.executable, "-c", ACTIVE_SCREEN_CHILD, str(REPO)],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=30,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        self.assertEqual(r.returncode, 0, msg=r.stdout + r.stderr)
        self.assertIn("ACTIVE FOOTER", r.stdout, msg=r.stdout + r.stderr)

    @unittest.skipUnless(os.name == "nt", "windows console API")
    def test_one_failed_console_row_does_not_publish_a_blank_frame(self):
        r = subprocess.run(
            [sys.executable, "-c", SCREEN_READ_ERROR_CHILD, str(REPO)],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=30,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        self.assertEqual(r.returncode, 0, msg=r.stdout + r.stderr)
        self.assertEqual(r.stdout.strip(), "None")

    @unittest.skipUnless(os.name == "nt", "windows console API")
    def test_short_console_row_does_not_publish_a_partial_frame(self):
        r = subprocess.run(
            [sys.executable, "-c", SHORT_SCREEN_READ_CHILD, str(REPO)],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=30,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        self.assertEqual(r.returncode, 0, msg=r.stdout + r.stderr)
        self.assertEqual(r.stdout.strip(), "None")

    @unittest.skipUnless(os.name == "nt", "windows console API")
    def test_visible_screen_preserves_cells_after_embedded_nul(self):
        r = subprocess.run(
            [sys.executable, "-c", SCREEN_CHILD, str(REPO)],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=30,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        self.assertEqual(r.returncode, 0, msg=r.stdout + r.stderr)
        self.assertIn("FOOTER", r.stdout, msg=r.stdout + r.stderr)

    @unittest.skipUnless(os.name == "nt", "windows console API")
    def test_owner_web_mouse_reports_roundtrip_as_real_console_events(self):
        r = subprocess.run(
            [sys.executable, "-c", OWNER_CHILD, str(REPO)],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=30,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        self.assertEqual(r.returncode, 0, msg=r.stdout + r.stderr)
        self.assertEqual(r.stdout.strip(), "9,4,120,4;9,4,-120,4;9,4,0,0;button=1;9,4,0,0;button=0;")

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
