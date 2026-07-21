"""Local-attach scroll forwarding: the "can't scroll the local mux terminal" fix.

Root cause under test: a hosted agent TUI (claude/codex) holds the alternate screen (?1049h),
which has no conhost scrollback, and the old muxctl attach dropped the wheel entirely (mouse
input disabled, msvcrt key-only reader), so nothing could scroll.

The PROVEN scroll mechanism in this stack is the web frontend's: on the alternate screen it
sends PageUp/PageDown per wheel event, rate-limited to one per 90ms (scrollAlternate() in
multiplex-app-patch/public/index.html), and it strips ALL mouse reports from input. That is the
local default (MUXCTL_WHEEL=pagekeys). SGR/urxvt/X10 wheel reports remain available as the
standard mouse-tracking contract (MUXCTL_WHEEL=sgr, and always for mouse tracking without the
alternate screen) but are NOT yet demonstrated to scroll claude/codex here. Exactly one
mechanism may fire per notch. These tests drive the forwarding path with realistic byte streams
end to end (muxd replay -> muxctl tracker -> wheel bytes).
"""
import importlib
import random
import unittest
from unittest import mock

muxctl = importlib.import_module("muxctl")
muxd = importlib.import_module("muxd")


def make_session():
    """A real muxd.Session with no spawned pty: ring + replay state are fully live."""
    return muxd.Session("t", "", None, 120, 30, None, None, spawn_now=False)


def session_output(s, data):
    """Exactly what Session._reader does with pty output (ring + replay state)."""
    s.replay_state.ingest(data)
    s._append_ring(data)


# The exact shape captured from a LIVE claude session's muxd replay (2026-07-20):
# prefix carries alt-screen state; the TUI's startup enables full mouse tracking + SGR.
# (Enabling mouse tracking does NOT prove the TUI scrolls from wheel reports — the web
# scrolls it with page keys while stripping reports.)
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

    def test_alt_screen_default_is_page_keys_like_the_web(self):
        # The web's scrollAlternate(): one PageUp/PageDown per wheel event — the only mechanism
        # PROVEN to scroll claude/codex in this stack.
        t = self._tracker(CLAUDE_STARTUP)                 # alt + full mouse tracking (claude)
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, 10, 5), b"\x1b[5~")
        self.assertEqual(muxctl.wheel_input_sequences(t, -1, 10, 5), b"\x1b[6~")
        bare = self._tracker(b"\x1b[?1049h")              # bare alt (less/vim, no tracking)
        self.assertEqual(muxctl.wheel_input_sequences(bare, 1), b"\x1b[5~")
        self.assertEqual(muxctl.wheel_input_sequences(bare, -1), b"\x1b[6~")

    def test_page_key_is_single_even_for_a_burst(self):
        # web parity: one page key per wheel EVENT (the translator adds the 90ms limiter)
        t = self._tracker(CLAUDE_STARTUP)
        self.assertEqual(muxctl.wheel_input_sequences(t, 50, 1, 1), b"\x1b[5~")

    def test_sgr_mode_sends_wheel_reports_on_a_tracking_alt_screen(self):
        t = self._tracker(CLAUDE_STARTUP)
        kw = {"alt_scroll": "sgr"}
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, 10, 5, **kw), b"\x1b[<64;10;5M")
        self.assertEqual(muxctl.wheel_input_sequences(t, -1, 10, 5, **kw), b"\x1b[<65;10;5M")
        self.assertEqual(muxctl.wheel_input_sequences(t, 2, 1, 1, **kw), b"\x1b[<64;1;1M" * 2)
        self.assertEqual(muxctl.wheel_input_sequences(t, 50, 1, 1, **kw), b"\x1b[<64;1;1M" * 8)

    def test_tracking_without_alt_screen_uses_reports_in_both_modes(self):
        # page keys would be meaningless outside the alternate screen
        t = self._tracker(b"\x1b[?1000h\x1b[?1006h")
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, 2, 3), b"\x1b[<64;2;3M")
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, 2, 3, alt_scroll="sgr"),
                         b"\x1b[<64;2;3M")

    def test_x10_fallback_without_sgr(self):
        t = self._tracker(b"\x1b[?1000h")
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, 3, 4),
                         b"\x1b[M" + bytes((32 + 64, 32 + 3, 32 + 4)))

    def test_urxvt_encoding(self):
        t = self._tracker(b"\x1b[?1000h\x1b[?1015h")
        self.assertEqual(muxctl.wheel_input_sequences(t, -1, 2, 2), b"\x1b[97;2;2M")

    def test_sgr_mode_bare_alt_screen_falls_back_to_arrows(self):
        # xterm.js's own fallback: no-scrollback buffer + no tracking -> 3 arrows per notch
        t = self._tracker(b"\x1b[?1049h")
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, alt_scroll="sgr"), b"\x1b[A" * 3)
        self.assertEqual(muxctl.wheel_input_sequences(t, -1, alt_scroll="sgr"), b"\x1b[B" * 3)

    def test_sgr_mode_arrows_honor_application_cursor_keys(self):
        t = self._tracker(b"\x1b[?1049h\x1b[?1h")
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, alt_scroll="sgr"), b"\x1bOA" * 3)

    def test_exactly_one_mechanism_fires_per_call(self):
        # never page keys AND reports for the same notch, in any state x mode combination
        for stream in (CLAUDE_STARTUP, b"\x1b[?1049h", b"\x1b[?1000h\x1b[?1006h", b""):
            for mode in ("pagekeys", "sgr"):
                seq = muxctl.wheel_input_sequences(self._tracker(stream), 1, 5, 5,
                                                   alt_scroll=mode)
                is_page = seq in (muxctl.PAGE_UP, muxctl.PAGE_DOWN)
                has_report = b"\x1b[<" in seq or b"\x1b[M" in seq
                has_arrow = b"\x1b[A" in seq or b"\x1bOA" in seq
                self.assertLessEqual(int(is_page) + int(has_report) + int(has_arrow), 1,
                                     (stream, mode, seq))

    def test_position_is_clamped_to_valid_cells(self):
        t = self._tracker(CLAUDE_STARTUP)
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, 0, -3, alt_scroll="sgr"),
                         b"\x1b[<64;1;1M")


class X10EncodingThroughMuxdInputPathTests(unittest.TestCase):
    """X10 coordinate bytes are >0x7F on big terminals; muxd's input path UTF-8-decodes
    (Session.write) and the pty re-encodes, so the report must survive that round trip."""

    def _x10(self, x, y):
        t = muxctl.ScreenModeTracker()
        t.ingest(b"\x1b[?1049h\x1b[?1000h")   # X10-style tracking, no SGR/urxvt
        return muxctl.wheel_input_sequences(t, 1, x, y, alt_scroll="sgr")

    def test_large_coordinates_survive_utf8_decode(self):
        seq = self._x10(150, 100)
        decoded = seq.decode("utf-8", "replace")
        self.assertNotIn("�", decoded)
        self.assertEqual(decoded, "\x1b[M" + chr(32 + 64) + chr(32 + 150) + chr(32 + 100))
        self.assertEqual(decoded.encode("utf-8"), seq)

    def test_end_to_end_through_session_write(self):
        s = make_session()
        captured = []
        with mock.patch.object(s, "_enqueue_writer_item", side_effect=lambda item: captured.append(item) or True):
            s.write(self._x10(180, 120))
        self.assertEqual(len(captured), 1)
        text = captured[0]
        self.assertNotIn("�", text)
        # the pty layer re-encodes UTF-8: the app receives the exact report chars
        self.assertEqual(text, "\x1b[M" + chr(96) + chr(32 + 180) + chr(32 + 120))

    def test_ascii_range_coordinates_are_plain_bytes(self):
        self.assertEqual(self._x10(3, 4), b"\x1b[M" + bytes((32 + 64, 32 + 3, 32 + 4)))


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
    def _translator(self, stream=CLAUDE_STARTUP, **kw):
        t = muxctl.ScreenModeTracker()
        t.ingest(stream)
        return muxctl.ConsoleInputTranslator(
            t, cell_resolver=lambda pos: (pos.X + 1, pos.Y + 1), **kw)

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

    def test_default_wheel_on_claude_sends_rate_limited_page_keys(self):
        # web parity end to end: PageUp per notch, one per 90ms (scrollAlternate)
        now = [100.0]
        tr = self._translator(clock=lambda: now[0])
        up = _mouse_record(muxctl.MOUSE_WHEELED, (120 & 0xFFFF) << 16, 7, 2)
        down = _mouse_record(muxctl.MOUSE_WHEELED, (-120 & 0xFFFF) << 16, 7, 2)
        self.assertEqual(tr.feed(up), (b"\x1b[5~", False))
        now[0] += 0.05                                     # within the 90ms window: suppressed
        self.assertEqual(tr.feed(up), (b"", False))
        now[0] += 0.05                                     # window elapsed
        self.assertEqual(tr.feed(down), (b"\x1b[6~", False))

    def test_typed_page_keys_are_not_rate_limited(self):
        now = [100.0]
        tr = self._translator(clock=lambda: now[0])
        up = _mouse_record(muxctl.MOUSE_WHEELED, (120 & 0xFFFF) << 16, 7, 2)
        self.assertEqual(tr.feed(up), (b"\x1b[5~", False))
        # a REAL PageUp keypress right after the wheel must never be swallowed
        self.assertEqual(tr.feed(_key_record(0x21, "\x00")), (b"\x1b[5~", False))
        self.assertEqual(tr.feed(_key_record(0x21, "\x00")), (b"\x1b[5~", False))

    def test_sgr_mode_forwards_reports_and_accumulates_partials(self):
        tr = self._translator(alt_scroll="sgr")
        self.assertEqual(tr.feed(_mouse_record(muxctl.MOUSE_WHEELED, (120 & 0xFFFF) << 16, 7, 2)),
                         (b"\x1b[<64;8;3M", False))
        self.assertEqual(tr.feed(_mouse_record(muxctl.MOUSE_WHEELED, (60 & 0xFFFF) << 16, 7, 2)),
                         (b"", False))
        self.assertEqual(tr.feed(_mouse_record(muxctl.MOUSE_WHEELED, (60 & 0xFFFF) << 16, 7, 2)),
                         (b"\x1b[<64;8;3M", False))
        self.assertEqual(tr.feed(_mouse_record(muxctl.MOUSE_WHEELED, (-120 & 0xFFFF) << 16, 7, 2)),
                         (b"\x1b[<65;8;3M", False))

    def test_unknown_wheel_mode_falls_back_to_pagekeys(self):
        tr = self._translator(alt_scroll="frobnicate")
        self.assertEqual(tr.alt_scroll, "pagekeys")
        self.assertEqual(tr.feed(_mouse_record(muxctl.MOUSE_WHEELED, (120 & 0xFFFF) << 16, 1, 1)),
                         (b"\x1b[5~", False))

    def test_wheel_on_plain_shell_stays_silent(self):
        tr = self._translator(stream=b"PS C:\\> ")
        self.assertEqual(tr.feed(_mouse_record(muxctl.MOUSE_WHEELED, (120 & 0xFFFF) << 16)),
                         (b"", False))

    def test_struct_sizes_match_win32(self):
        self.assertEqual(muxctl.ctypes.sizeof(muxctl.KEY_EVENT_RECORD), 16)
        self.assertEqual(muxctl.ctypes.sizeof(muxctl.MOUSE_EVENT_RECORD), 16)
        self.assertEqual(muxctl.ctypes.sizeof(muxctl.INPUT_RECORD), 20)

    def test_surrogate_pair_across_two_key_events_emits_one_utf8_char(self):
        tr = self._translator()
        high, low = "\ud83d", "\ude00"                       # U+1F600
        self.assertEqual(tr.feed(_key_record(0x00, high)), (b"", False))
        self.assertEqual(tr.feed(_key_record(0x00, low)), ("😀".encode("utf-8"), False))

    def test_orphaned_high_surrogate_is_dropped_not_mangled(self):
        tr = self._translator()
        self.assertEqual(tr.feed(_key_record(0x00, "\ud83d")), (b"", False))
        self.assertEqual(tr.feed(_key_record(0x41, "a")), (b"a", False))

    def test_alt_numpad_character_arrives_on_alt_key_up(self):
        tr = self._translator()
        # composing digits show as VK_MENU-held key events with no char; the composed
        # character rides the ALT key-UP record
        self.assertEqual(tr.feed(_key_record(0x12, "\x00", down=1)), (b"", False))
        self.assertEqual(tr.feed(_key_record(0x12, "é", down=0)), ("é".encode("utf-8"), False))

    def test_non_alt_key_up_with_char_is_still_ignored(self):
        tr = self._translator()
        self.assertEqual(tr.feed(_key_record(0x41, "a", down=0)), (b"", False))

    def test_vt_input_opt_in_suppresses_all_wheel_synthesis(self):
        # MUXCTL_VT_INPUT=1: the terminal's VT layer owns the wheel; we must emit NOTHING for
        # mouse events in ANY state/mode, or one notch could scroll twice.
        wheel = _mouse_record(muxctl.MOUSE_WHEELED, (120 & 0xFFFF) << 16, 1, 1)
        for stream in (CLAUDE_STARTUP, b"\x1b[?1049h", b"\x1b[?1000h\x1b[?1006h"):
            for mode in ("pagekeys", "sgr"):
                t = muxctl.ScreenModeTracker()
                t.ingest(stream)
                tr = muxctl.ConsoleInputTranslator(t, wheel_synthesis=False, alt_scroll=mode)
                self.assertEqual(tr.feed(wheel), (b"", False), (stream, mode))
                # keyboard input still flows
                self.assertEqual(tr.feed(_key_record(0x41, "a")), (b"a", False))


class MidSessionAttachEndToEndTests(unittest.TestCase):
    """muxd replay -> muxctl tracker, for an attach long after the TUI's startup enables."""

    def test_muxd_replay_prefix_now_restores_mouse_modes_and_decckm(self):
        state = muxd.TerminalReplayState()
        state.ingest(CLAUDE_STARTUP)
        prefix = state.prefix()
        for mode in (b"?1h", b"?1000h", b"?1002h", b"?1003h", b"?1006h", b"?1049h"):
            self.assertIn(b"\x1b[" + mode, prefix)

    def test_attach_after_enables_scrolled_out_of_the_ring_still_scrolls(self):
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
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, 40, 12), b"\x1b[5~")
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, 40, 12, alt_scroll="sgr"),
                         b"\x1b[<64;40;12M")

    def test_sb_zero_attach_still_delivers_the_mode_prefix(self):
        # MUXCTL_SCROLLBACK=0 regression: gating the whole first frame on sb>0 dropped the mode
        # prefix, the client tracker never learned the app owns the wheel, and the original
        # "can't scroll" bug came back under a knob.
        s = make_session()
        session_output(s, CLAUDE_STARTUP)
        session_output(s, b"\x1b[2J\x1b[Htranscript body\r\n" * 100)

        payload = muxd.attach_replay_payload(s, 0)
        self.assertIn(b"\x1b[?1049h", payload)
        self.assertIn(b"\x1b[?1000h", payload)
        self.assertIn(b"\x1b[?1006h", payload)
        self.assertNotIn(b"transcript body", payload)     # sb=0 means NO content replay

        t = muxctl.ScreenModeTracker()
        t.ingest(payload)
        self.assertTrue(t.wants_mouse_capture())
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, 5, 5), b"\x1b[5~")   # proven default
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, 5, 5, alt_scroll="sgr"),
                         b"\x1b[<64;5;5M")                                       # report contract

    def test_positive_sb_attach_replays_prefix_plus_content(self):
        s = make_session()
        session_output(s, CLAUDE_STARTUP)
        session_output(s, b"\x1b[2J\x1b[Htranscript body\r\n" * 100)
        payload = muxd.attach_replay_payload(s, 60000)
        self.assertTrue(payload.startswith(s.replay_state.prefix()))
        self.assertIn(b"transcript body", payload)

    def test_mode_flapping_across_chunk_boundaries_lands_on_final_state(self):
        # rapid h/l toggling, every sequence split mid-CSI: the tracker must end on the LAST
        # state, never a stale intermediate one
        stream = (b"\x1b[?1000h\x1b[?1000l" * 50 + b"\x1b[?1000h" +
                  b"\x1b[?1049h\x1b[?1049l" * 50 + b"\x1b[?1049h" +
                  b"\x1b[?1006h\x1b[?1006l")
        for sizes in ((1,), (2,), (3,), (5,), (7,), (11,)):
            t = muxctl.ScreenModeTracker()
            feed_chunked(t, stream, sizes=sizes)
            self.assertTrue(t.mouse_tracking, sizes)
            self.assertTrue(t.alt_screen, sizes)
            self.assertFalse(t.sgr_mouse, sizes)
        st = muxd.TerminalReplayState()
        st.ingest(stream)
        self.assertEqual(st.private_modes & {1000, 1049, 1006}, {1000, 1049})

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
