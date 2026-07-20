"""Local-attach scroll forwarding: the "can't scroll the local mux terminal" fix.

Root cause under test: a hosted agent TUI (claude/codex) holds the alternate screen (?1049h) —
which has no conhost scrollback — and scrolls its own transcript only when it receives mouse
wheel reports (?1000/1002/1003 + ?1006 SGR). The web terminal (xterm.js) forwards the wheel as
those reports; the old muxctl attach dropped the wheel entirely (mouse input disabled, msvcrt
key-only reader), so nothing could scroll. These tests drive the new forwarding path with
realistic byte streams end to end (muxd replay -> muxctl tracker -> wheel bytes).
"""
import importlib
import random
import unittest

muxctl = importlib.import_module("muxctl")
muxd = importlib.import_module("muxd")


# The exact shape captured from a LIVE claude session's muxd replay (2026-07-20):
# prefix carries alt-screen state; the TUI's startup enables full mouse tracking + SGR.
LIVE_PREFIX = b"\x1b[?25h\x1b[?1049h\x1b[?2004h"
CLAUDE_STARTUP = (b"\x1b[?1049h\x1b[?1h\x1b[?2004h\x1b[?1004h"
                  b"\x1b[?1000h\x1b[?1002h\x1b[?1003h\x1b[?1006h")
CLAUDE_EXIT = (b"\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l"
               b"\x1b[?1004l\x1b[?2004l\x1b[?1l\x1b[?1049l")


def feed_chunked(tracker, data, sizes=(1, 2, 3, 5, 7, 64, 200)):
    i, n = 0, 0
    while i < len(data):
        step = sizes[n % len(sizes)]
        tracker.ingest(data[i:i + step])
        i += step
        n += 1


class ScreenModeTrackerTests(unittest.TestCase):
    def test_plain_shell_stream_keeps_native_conhost_scrollback(self):
        t = muxctl.ScreenModeTracker()
        t.ingest(b"PS C:\\> dir\r\n" + b"x" * 500 + b"\x1b[32mok\x1b[0m\r\n")
        self.assertFalse(t.wants_mouse_capture())
        self.assertEqual(muxctl.wheel_input_sequences(t, 1), b"")

    def test_live_replay_prefix_enables_capture_via_alt_screen(self):
        t = muxctl.ScreenModeTracker()
        t.ingest(LIVE_PREFIX)
        self.assertTrue(t.alt_screen)
        self.assertFalse(t.mouse_tracking)
        self.assertTrue(t.wants_mouse_capture())

    def test_claude_startup_enables_sgr_mouse_even_when_split_across_chunks(self):
        t = muxctl.ScreenModeTracker()
        feed_chunked(t, CLAUDE_STARTUP + b"\x1b[2J\x1b[H transcript line\r\n")
        self.assertTrue(t.alt_screen)
        self.assertTrue(t.mouse_tracking)
        self.assertTrue(t.sgr_mouse)
        self.assertTrue(t.app_cursor_keys)

    def test_tui_exit_releases_capture(self):
        t = muxctl.ScreenModeTracker()
        t.ingest(CLAUDE_STARTUP)
        self.assertTrue(t.wants_mouse_capture())
        feed_chunked(t, CLAUDE_EXIT + b"\r\nPS C:\\> ")
        self.assertFalse(t.alt_screen)
        self.assertFalse(t.mouse_tracking)
        self.assertFalse(t.wants_mouse_capture())
        self.assertEqual(muxctl.wheel_input_sequences(t, 1), b"")

    def test_combined_semicolon_mode_list(self):
        t = muxctl.ScreenModeTracker()
        t.ingest(b"\x1b[?1049;1006;1000h")
        self.assertTrue(t.alt_screen)
        self.assertTrue(t.sgr_mouse)
        t.ingest(b"\x1b[?1006;1000l")
        self.assertFalse(t.mouse_tracking)
        self.assertTrue(t.alt_screen)


class WheelTranslationTests(unittest.TestCase):
    def _tracker(self, stream):
        t = muxctl.ScreenModeTracker()
        t.ingest(stream)
        return t

    def test_sgr_wheel_reports_match_web_terminal(self):
        t = self._tracker(CLAUDE_STARTUP)
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, 10, 5), b"\x1b[<64;10;5M")
        self.assertEqual(muxctl.wheel_input_sequences(t, -1, 10, 5), b"\x1b[<65;10;5M")
        self.assertEqual(muxctl.wheel_input_sequences(t, 2, 1, 1), b"\x1b[<64;1;1M" * 2)

    def test_x10_fallback_without_sgr(self):
        t = self._tracker(b"\x1b[?1000h")
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, 3, 4),
                         b"\x1b[M" + bytes((32 + 64, 32 + 3, 32 + 4)))

    def test_urxvt_encoding(self):
        t = self._tracker(b"\x1b[?1000h\x1b[?1015h")
        self.assertEqual(muxctl.wheel_input_sequences(t, -1, 2, 2), b"\x1b[97;2;2M")

    def test_alt_screen_without_mouse_tracking_scrolls_via_arrows(self):
        # xterm.js parity: no-scrollback buffer + no mouse tracking -> 3 arrow keys per notch.
        t = self._tracker(b"\x1b[?1049h")
        self.assertEqual(muxctl.wheel_input_sequences(t, 1), b"\x1b[A" * 3)
        self.assertEqual(muxctl.wheel_input_sequences(t, -1), b"\x1b[B" * 3)

    def test_alt_screen_arrows_honor_application_cursor_keys(self):
        t = self._tracker(b"\x1b[?1049h\x1b[?1h")
        self.assertEqual(muxctl.wheel_input_sequences(t, 1), b"\x1bOA" * 3)

    def test_wheel_burst_is_capped(self):
        t = self._tracker(CLAUDE_STARTUP)
        self.assertEqual(muxctl.wheel_input_sequences(t, 50, 1, 1), b"\x1b[<64;1;1M" * 8)

    def test_position_is_clamped_to_valid_cells(self):
        t = self._tracker(CLAUDE_STARTUP)
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, 0, -3), b"\x1b[<64;1;1M")


class KeyTranslationTests(unittest.TestCase):
    def test_navigation_keys_keep_old_ext_map_bytes(self):
        # byte-for-byte parity with the retired msvcrt scan-code table
        expected = {0x26: b"\x1b[A", 0x28: b"\x1b[B", 0x27: b"\x1b[C", 0x25: b"\x1b[D",
                    0x24: b"\x1b[H", 0x23: b"\x1b[F", 0x21: b"\x1b[5~", 0x22: b"\x1b[6~",
                    0x2E: b"\x1b[3~", 0x2D: b"\x1b[2~"}
        for vk, seq in expected.items():
            self.assertEqual(muxctl.translate_key_event(vk, "", 0), seq)

    def test_shift_tab_sends_backtab(self):
        self.assertEqual(muxctl.translate_key_event(0x09, "\t", muxctl.SHIFT_PRESSED), b"\x1b[Z")

    def test_plain_and_control_characters_pass_through(self):
        self.assertEqual(muxctl.translate_key_event(0x41, "a", 0), b"a")
        self.assertEqual(muxctl.translate_key_event(0x43, "\x03", muxctl.LEFT_CTRL_PRESSED), b"\x03")
        self.assertEqual(muxctl.translate_key_event(0x0D, "\r", 0), b"\r")

    def test_unicode_is_utf8_for_the_pty(self):
        # muxd Session.write decodes input as UTF-8
        self.assertEqual(muxctl.translate_key_event(0x00, "é", 0), "é".encode("utf-8"))

    def test_alt_letter_gets_esc_prefix_but_altgr_stays_plain(self):
        self.assertEqual(muxctl.translate_key_event(0x41, "a", muxctl.LEFT_ALT_PRESSED), b"\x1ba")
        altgr = muxctl.LEFT_ALT_PRESSED | muxctl.LEFT_CTRL_PRESSED
        self.assertEqual(muxctl.translate_key_event(0x32, "@", altgr), b"@")

    def test_unmapped_keys_produce_nothing(self):
        self.assertIsNone(muxctl.translate_key_event(0x10, "", muxctl.SHIFT_PRESSED))  # bare shift
        self.assertIsNone(muxctl.translate_key_event(0x70, "", 0))                     # F1


def _key_record(vk, ch, ctrl=0, repeat=1, down=1):
    rec = muxctl.INPUT_RECORD()
    rec.EventType = muxctl.KEY_EVENT_TYPE
    rec.Event.KeyEvent.bKeyDown = down
    rec.Event.KeyEvent.wRepeatCount = repeat
    rec.Event.KeyEvent.wVirtualKeyCode = vk
    rec.Event.KeyEvent.UnicodeChar = ch or "\x00"
    rec.Event.KeyEvent.dwControlKeyState = ctrl
    return rec


def _mouse_record(flags, button_state=0, x=0, y=0):
    rec = muxctl.INPUT_RECORD()
    rec.EventType = muxctl.MOUSE_EVENT_TYPE
    rec.Event.MouseEvent.dwMousePosition.X = x
    rec.Event.MouseEvent.dwMousePosition.Y = y
    rec.Event.MouseEvent.dwButtonState = button_state
    rec.Event.MouseEvent.dwEventFlags = flags
    return rec


class ConsoleInputTranslatorTests(unittest.TestCase):
    def _translator(self, stream=CLAUDE_STARTUP):
        t = muxctl.ScreenModeTracker()
        t.ingest(stream)
        return muxctl.ConsoleInputTranslator(t, cell_resolver=lambda pos: (pos.X + 1, pos.Y + 1))

    def test_navigation_key_with_nul_unicode_char_sends_vt_not_nul(self):
        tr = self._translator()
        self.assertEqual(tr.feed(_key_record(0x26, "\x00")), (b"\x1b[A", False))

    def test_key_up_events_are_ignored(self):
        tr = self._translator()
        self.assertEqual(tr.feed(_key_record(0x41, "a", down=0)), (b"", False))

    def test_repeat_count_expands(self):
        tr = self._translator()
        self.assertEqual(tr.feed(_key_record(0x41, "a", repeat=3)), (b"aaa", False))

    def test_ctrl_bracket_requests_detach(self):
        tr = self._translator()
        self.assertEqual(tr.feed(_key_record(0xDD, "\x1d", ctrl=muxctl.LEFT_CTRL_PRESSED)),
                         (b"", True))

    def test_clicks_and_motion_are_never_forwarded(self):
        tr = self._translator()
        self.assertEqual(tr.feed(_mouse_record(0, button_state=1, x=4, y=4)), (b"", False))
        self.assertEqual(tr.feed(_mouse_record(0x0001, x=9, y=9)), (b"", False))  # MOUSE_MOVED

    def test_wheel_event_forwards_sgr_report_and_accumulates_partials(self):
        tr = self._translator()
        self.assertEqual(tr.feed(_mouse_record(muxctl.MOUSE_WHEELED, (120 & 0xFFFF) << 16, 7, 2)),
                         (b"\x1b[<64;8;3M", False))
        self.assertEqual(tr.feed(_mouse_record(muxctl.MOUSE_WHEELED, (60 & 0xFFFF) << 16, 7, 2)),
                         (b"", False))
        self.assertEqual(tr.feed(_mouse_record(muxctl.MOUSE_WHEELED, (60 & 0xFFFF) << 16, 7, 2)),
                         (b"\x1b[<64;8;3M", False))
        self.assertEqual(tr.feed(_mouse_record(muxctl.MOUSE_WHEELED, (-120 & 0xFFFF) << 16, 7, 2)),
                         (b"\x1b[<65;8;3M", False))

    def test_wheel_on_plain_shell_stays_silent(self):
        tr = self._translator(stream=b"PS C:\\> ")
        self.assertEqual(tr.feed(_mouse_record(muxctl.MOUSE_WHEELED, (120 & 0xFFFF) << 16)),
                         (b"", False))

    def test_struct_sizes_match_win32(self):
        self.assertEqual(muxctl.ctypes.sizeof(muxctl.KEY_EVENT_RECORD), 16)
        self.assertEqual(muxctl.ctypes.sizeof(muxctl.MOUSE_EVENT_RECORD), 16)
        self.assertEqual(muxctl.ctypes.sizeof(muxctl.INPUT_RECORD), 20)


class MidSessionAttachEndToEndTests(unittest.TestCase):
    """muxd replay -> muxctl tracker, for an attach long after the TUI's startup enables."""

    def test_muxd_replay_prefix_now_restores_mouse_modes(self):
        state = muxd.TerminalReplayState()
        state.ingest(CLAUDE_STARTUP)
        prefix = state.prefix()
        for mode in (b"?1000h", b"?1002h", b"?1003h", b"?1006h", b"?1049h"):
            self.assertIn(b"\x1b[" + mode, prefix)

    def test_attach_after_enables_scrolled_out_of_the_ring_still_forwards_sgr_wheel(self):
        # Server side: the enables are followed by way more output than the local replay window,
        # so the raw ring tail no longer contains them — only the prefix can restore the state.
        state = muxd.TerminalReplayState()
        state.ingest(CLAUDE_STARTUP)
        filler = (b"\x1b[2J\x1b[H\x1b[38;2;255;193;7mtranscript\x1b[0m\r\n" * 4000)
        state.ingest(filler)
        window = 60000  # LOCAL_SB_SEND default
        replay = state.prefix() + filler[-window:]
        self.assertNotIn(b"\x1b[?1000h", filler[-window:])

        # Client side: feed the replay through the tracker exactly as recv() does.
        t = muxctl.ScreenModeTracker()
        feed_chunked(t, replay, sizes=(4096,))
        self.assertTrue(t.wants_mouse_capture())
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, 40, 12), b"\x1b[<64;40;12M")

    def test_full_lifecycle_randomized_chunking(self):
        rng = random.Random(1049)
        stream = (b"boot noise\r\n" + CLAUDE_STARTUP + b"\x1b[2J\x1b[Hbody\r\n" * 300 +
                  CLAUDE_EXIT + b"back to shell\r\n")
        t = muxctl.ScreenModeTracker()
        i = 0
        while i < len(stream):
            step = rng.randint(1, 37)
            t.ingest(stream[i:i + step])
            i += step
        self.assertFalse(t.wants_mouse_capture())
        self.assertEqual(muxctl.wheel_input_sequences(t, 1), b"")


if __name__ == "__main__":
    unittest.main()
