"""Local-attach scroll forwarding: the "can't scroll the local mux terminal" fix.

Root cause under test: a hosted agent TUI (claude/codex) holds the alternate screen (?1049h),
which has no conhost scrollback, and the old muxctl attach dropped the wheel entirely (mouse
input disabled, msvcrt key-only reader), so nothing could scroll.

muxctl now resolves a notch the way a real local terminal does, in priority order: the app
tracks the mouse -> wheel reports (INCLUDING on the alternate screen); bare alternate screen
with DECSET 1007 -> 3 arrows per notch (SS3 under DECCKM); bare alternate screen without 1007 ->
one rate-limited page key (a deliberate compatibility fallback, not real-terminal behavior);
normal screen -> nothing, conhost keeps its own scrollback. MUXCTL_WHEEL=pagekeys forces the
older alt-screen-first order for a TUI that only scrolls from page keys.

That state -> mechanism mapping is NOT hardcoded here. It lives in the shared vector table
docs/scroll-parity.json, which pins every {surface} x {screen} x {tracking} x {1007} cell for
BOTH surfaces. ScrollParityVectorTests drives every muxctl cell (and every MUXCTL_WHEEL override
cell) straight out of that file, expected bytes included, plus the exactly-one-mechanism
invariant. Change behavior in the vector file first; these tests then say what must follow.

The remaining classes drive the forwarding path with realistic byte streams end to end
(muxd replay -> muxctl tracker -> wheel bytes).
"""
import importlib
import json
import random
import re
import unittest
from pathlib import Path
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


VECTOR_PATH = Path(__file__).resolve().parents[2] / "docs" / "scroll-parity.json"
VECTOR = json.loads(VECTOR_PATH.read_text(encoding="utf-8"))
MECHANISMS = VECTOR["mechanisms"]


def modes_stream(modes_on):
    """The byte stream a TUI would emit to land a tracker in this cell's state."""
    return b"".join(b"\x1b[?%dh" % m for m in modes_on)


def tracker_for(modes_on, app_cursor=False):
    t = muxctl.ScreenModeTracker()
    t.ingest(modes_stream(list(modes_on) + ([1] if app_cursor else [])))
    return t


def render_mechanism(name, modes_on, notches, x, y, app_cursor=False):
    """Expected bytes for one wheel event, rendered from the vector file's OWN mechanism spec.

    Nothing here restates muxctl's logic: the formats, repeats, notch cap and coordinate clamp
    all come out of docs/scroll-parity.json, so a behavior change must edit that file to pass.
    """
    spec = MECHANISMS[name]
    up, count = notches > 0, abs(int(notches))
    if name in ("host_scrollback", "none"):
        return spec["app_input"].encode("utf-8")
    if name == "page_keys":
        return spec["app_input_up" if up else "app_input_down"].encode("utf-8")
    if name == "arrows":
        prefix = "app_cursor_input_" if app_cursor else "app_input_"
        key = prefix + ("up" if up else "down")
        return spec[key].encode("utf-8") * (spec["repeat"] * count)
    if name == "wheel_report":
        btn = spec["buttons"]["up" if up else "down"]
        enc = min((e for e in spec["encodings"]
                   if e["detected_by_mode"] in (None, *modes_on)),
                  key=lambda e: e["precedence"])
        lo = spec["coords"]["clamped_min"]
        x, y = max(lo, x), max(lo, y)
        if enc["style"] == "sgr":
            text = enc["format"].format(button=btn, x=x, y=y)
        elif enc["style"] == "urxvt":
            text = enc["format"].format(button_plus_32=btn + 32, x=x, y=y)
        else:                                             # x10: raw chars, clamped, UTF-8 encoded
            text = enc["format"].format(chr32_button=chr(32 + btn),
                                        chr32_x=chr(32 + min(x, 222)),
                                        chr32_y=chr(32 + min(y, 222)))
        return text.encode("utf-8") * min(spec["max_notches_per_event"], count)
    raise AssertionError("unknown mechanism in vector file: %r" % name)


def mechanism_families(seq):
    """Which scroll sources are present in these bytes — used for exactly-one-mechanism."""
    return {
        "page_keys": seq in (muxctl.PAGE_UP, muxctl.PAGE_DOWN),
        "wheel_report": b"\x1b[<" in seq or b"\x1b[M" in seq or bool(
            re.fullmatch(br"(\x1b\[\d+;\d+;\d+M)+", seq)),
        "arrows": bool(re.fullmatch(br"(\x1b(\[|O)[AB])+", seq)),
    }


class ScrollParityVectorTests(unittest.TestCase):
    """Every cell of docs/scroll-parity.json, asserted against the real translation.

    The vector file is the contract; this class is the enforcement. Cells for the `web` surface
    are structural-only here (their behavior lives in the browser) but are still checked for
    shape, coverage and the cross-surface divergence record.
    """

    NOTCHES = (1, -1, 2, -3, 50)

    def muxctl_cells(self):
        return [c for c in VECTOR["cells"] if c["surface"] == "muxctl"]

    # ---- coverage / structure -------------------------------------------------------------

    def test_table_covers_every_state_exactly_once(self):
        axes = VECTOR["state_axes"]
        want = {(s, sc, tr, alt)
                for s in axes["surface"] for sc in axes["screen"]
                for tr in axes["tracking"] for alt in axes["alt_scroll_1007"]}
        got = [(c["surface"], c["screen"], c["tracking"], c["alt_scroll_1007"])
               for c in VECTOR["cells"]]
        self.assertEqual(len(got), len(set(got)), "duplicate cell in the vector file")
        self.assertEqual(set(got), want)

    def test_every_cell_names_exactly_one_known_mechanism(self):
        for cell in VECTOR["cells"]:
            with self.subTest(cell=cell):
                self.assertIsInstance(cell["mechanism"], str)
                self.assertIn(cell["mechanism"], MECHANISMS)

    def test_cell_modes_realize_the_state_the_cell_claims(self):
        for cell in self.muxctl_cells():
            with self.subTest(cell=cell):
                t = tracker_for(cell["modes_on"])
                self.assertEqual(t.alt_screen, cell["screen"] == "alt")
                self.assertEqual(t.mouse_tracking, cell["tracking"])
                self.assertEqual(t.alternate_scroll, cell["alt_scroll_1007"])

    def test_tracked_modes_are_replayed_by_muxd(self):
        # invariant: a mid-session attach must classify into the SAME cell as a fresh one
        keyed = {m for c in VECTOR["cells"] for m in c["modes_on"]}
        keyed |= {e["detected_by_mode"] for e in MECHANISMS["wheel_report"]["encodings"]
                  if e["detected_by_mode"]}
        keyed.add(MECHANISMS["arrows"]["app_cursor_mode"])
        self.assertTrue(keyed <= muxd.REPLAY_PRIVATE_MODES, keyed - muxd.REPLAY_PRIVATE_MODES)
        self.assertTrue(keyed <= muxctl.TRACKED_MODES, keyed - muxctl.TRACKED_MODES)

    def test_web_cell_anchors_point_at_real_source_lines(self):
        root = VECTOR_PATH.parent.parent
        for cell in (c for c in VECTOR["cells"] if c["surface"] == "web"):
            with self.subTest(cell=cell):
                path, _, span = cell["anchor"].partition(":")
                target = root / path
                self.assertTrue(target.is_file(), target)
                last = int(span.split("-")[-1])
                self.assertLessEqual(last, len(target.read_text(
                    encoding="utf-8", errors="replace").splitlines()))

    def test_divergences_match_the_two_surfaces(self):
        by_state = {(c["surface"], c["screen"], c["tracking"], c["alt_scroll_1007"]):
                    c["mechanism"] for c in VECTOR["cells"]}
        disagree = {k[1:] for k in by_state
                    if by_state[("muxctl",) + k[1:]] != by_state[("web",) + k[1:]]}
        recorded = set()
        for d in VECTOR["divergences"]:
            _, screen, tracking, alt = d["cell"].split("/")
            for a in ((True, False) if alt == "*" else (alt.endswith("true"),)):
                recorded.add((screen, tracking.endswith("true"), a))
            self.assertTrue(d["accepted"])
        self.assertEqual(disagree, recorded)

    # ---- muxctl behavior ------------------------------------------------------------------

    def test_every_muxctl_cell_produces_its_mechanism_by_default(self):
        for cell in self.muxctl_cells():
            for notches in self.NOTCHES:
                for app_cursor in (False, True):
                    with self.subTest(cell=cell, notches=notches, app_cursor=app_cursor):
                        t = tracker_for(cell["modes_on"], app_cursor)
                        seq = muxctl.wheel_input_sequences(t, notches, 10, 5)
                        self.assertEqual(seq, render_mechanism(
                            cell["mechanism"], cell["modes_on"], notches, 10, 5, app_cursor))

    def test_every_override_cell_produces_its_mechanism_under_MUXCTL_WHEEL_pagekeys(self):
        override = VECTOR["muxctl_overrides"]["MUXCTL_WHEEL=pagekeys"]
        by_state = {(c["screen"], c["tracking"], c["alt_scroll_1007"]): c
                    for c in self.muxctl_cells()}
        for cell in override["cells"]:
            modes = by_state[(cell["screen"], cell["tracking"],
                              cell["alt_scroll_1007"])]["modes_on"]
            for notches in self.NOTCHES:
                with self.subTest(cell=cell, notches=notches):
                    seq = muxctl.wheel_input_sequences(tracker_for(modes), notches, 10, 5,
                                                       alt_scroll="pagekeys")
                    self.assertEqual(seq, render_mechanism(
                        cell["mechanism"], modes, notches, 10, 5))

    def test_exactly_one_mechanism_fires_per_call(self):
        # never a page key AND a report (or a report AND arrows) for the same notch
        for mode in muxctl.ALT_SCROLL_MODES_ALLOWED:
            for cell in self.muxctl_cells():
                with self.subTest(cell=cell, alt_scroll=mode):
                    seq = muxctl.wheel_input_sequences(tracker_for(cell["modes_on"]), 1, 5, 5,
                                                       alt_scroll=mode)
                    fired = [k for k, v in mechanism_families(seq).items() if v]
                    self.assertLessEqual(len(fired), 1, (seq, fired))
                    if mode == muxctl.DEFAULT_ALT_SCROLL and seq:
                        self.assertEqual(fired, [cell["mechanism"]])

    def test_tracking_wins_invariant(self):
        for cell in self.muxctl_cells():
            if cell["tracking"]:
                self.assertEqual(cell["mechanism"], "wheel_report", cell)
                self.assertIn(b"\x1b[<", muxctl.wheel_input_sequences(
                    tracker_for(cell["modes_on"] + [1006]), 1, 4, 4))

    def test_normal_screen_without_tracking_stays_the_hosts(self):
        for cell in VECTOR["cells"]:
            if cell["screen"] == "normal" and not cell["tracking"]:
                self.assertEqual(cell["mechanism"], "host_scrollback", cell)
                if cell["surface"] == "muxctl":
                    t = tracker_for(cell["modes_on"])
                    self.assertFalse(t.wants_mouse_capture())
                    self.assertEqual(muxctl.wheel_input_sequences(t, 1), b"")

    def test_page_key_rate_limit_matches_the_vector_file(self):
        self.assertAlmostEqual(muxctl.ConsoleInputTranslator.PAGE_SCROLL_INTERVAL,
                               MECHANISMS["page_keys"]["rate_limit_ms"] / 1000.0)

    def test_default_alt_scroll_is_the_native_terminal_model(self):
        self.assertEqual(muxctl.DEFAULT_ALT_SCROLL, "sgr")
        self.assertIn("pagekeys", muxctl.ALT_SCROLL_MODES_ALLOWED)

    def test_position_is_clamped_to_the_vector_files_minimum(self):
        lo = MECHANISMS["wheel_report"]["coords"]["clamped_min"]
        t = tracker_for([1049, 1000, 1006])
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, 0, -3),
                         ("\x1b[<64;%d;%dM" % (lo, lo)).encode())


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

    def test_default_wheel_on_claude_sends_wheel_reports_not_page_keys(self):
        # the r.3.1 priority flip, end to end: claude tracks the mouse, so the notch is a report
        # even on the alternate screen — and the page-key limiter never fires.
        now = [100.0]
        tr = self._translator(clock=lambda: now[0])
        up = _mouse_record(muxctl.MOUSE_WHEELED, (120 & 0xFFFF) << 16, 7, 2)
        down = _mouse_record(muxctl.MOUSE_WHEELED, (-120 & 0xFFFF) << 16, 7, 2)
        self.assertEqual(tr.feed(up), (b"\x1b[<64;8;3M", False))
        now[0] += 0.001                                    # no 90ms window on the report path
        self.assertEqual(tr.feed(up), (b"\x1b[<64;8;3M", False))
        self.assertEqual(tr.feed(down), (b"\x1b[<65;8;3M", False))

    def test_bare_alt_screen_page_keys_are_rate_limited(self):
        # compat fallback (no tracking, no 1007): one page key per wheel EVENT, one per 90ms
        now = [100.0]
        tr = self._translator(stream=b"\x1b[?1049h", clock=lambda: now[0])
        up = _mouse_record(muxctl.MOUSE_WHEELED, (120 & 0xFFFF) << 16, 7, 2)
        down = _mouse_record(muxctl.MOUSE_WHEELED, (-120 & 0xFFFF) << 16, 7, 2)
        self.assertEqual(tr.feed(up), (b"\x1b[5~", False))
        now[0] += 0.05                                     # within the 90ms window: suppressed
        self.assertEqual(tr.feed(up), (b"", False))
        now[0] += 0.05                                     # window elapsed
        self.assertEqual(tr.feed(down), (b"\x1b[6~", False))

    def test_bare_alt_with_1007_sends_arrows_per_notch(self):
        tr = self._translator(stream=b"\x1b[?1049h\x1b[?1007h")
        up = _mouse_record(muxctl.MOUSE_WHEELED, (120 & 0xFFFF) << 16, 7, 2)
        self.assertEqual(tr.feed(up), (b"\x1b[A" * 3, False))
        # arrows are per notch and NOT rate limited: a second notch scrolls again immediately
        self.assertEqual(tr.feed(up), (b"\x1b[A" * 3, False))

    def test_forced_pagekeys_override_keeps_the_old_alt_first_order(self):
        tr = self._translator(alt_scroll="pagekeys")       # claude state, forced compat
        self.assertEqual(tr.feed(_mouse_record(muxctl.MOUSE_WHEELED, (120 & 0xFFFF) << 16, 7, 2)),
                         (b"\x1b[5~", False))

    def test_typed_page_keys_are_not_rate_limited(self):
        now = [100.0]
        tr = self._translator(stream=b"\x1b[?1049h", clock=lambda: now[0])
        up = _mouse_record(muxctl.MOUSE_WHEELED, (120 & 0xFFFF) << 16, 7, 2)
        self.assertEqual(tr.feed(up), (b"\x1b[5~", False))
        # a REAL PageUp keypress right after the wheel must never be swallowed
        self.assertEqual(tr.feed(_key_record(0x21, "\x00")), (b"\x1b[5~", False))
        self.assertEqual(tr.feed(_key_record(0x21, "\x00")), (b"\x1b[5~", False))

    def test_wheel_reports_accumulate_partial_notches(self):
        tr = self._translator(alt_scroll="sgr")
        self.assertEqual(tr.feed(_mouse_record(muxctl.MOUSE_WHEELED, (120 & 0xFFFF) << 16, 7, 2)),
                         (b"\x1b[<64;8;3M", False))
        self.assertEqual(tr.feed(_mouse_record(muxctl.MOUSE_WHEELED, (60 & 0xFFFF) << 16, 7, 2)),
                         (b"", False))
        self.assertEqual(tr.feed(_mouse_record(muxctl.MOUSE_WHEELED, (60 & 0xFFFF) << 16, 7, 2)),
                         (b"\x1b[<64;8;3M", False))
        self.assertEqual(tr.feed(_mouse_record(muxctl.MOUSE_WHEELED, (-120 & 0xFFFF) << 16, 7, 2)),
                         (b"\x1b[<65;8;3M", False))

    def test_unknown_wheel_mode_falls_back_to_the_default_contract(self):
        tr = self._translator(alt_scroll="frobnicate")
        self.assertEqual(tr.alt_scroll, muxctl.DEFAULT_ALT_SCROLL)
        self.assertEqual(tr.feed(_mouse_record(muxctl.MOUSE_WHEELED, (120 & 0xFFFF) << 16, 1, 1)),
                         (b"\x1b[<64;2;2M", False))

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
        # the restored prefix must land this attach in the SAME vector cell as a fresh one
        # (muxctl/alt/tracking=true/1007=false -> wheel_report), not the bare-alt fallback
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, 40, 12), b"\x1b[<64;40;12M")
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, 40, 12, alt_scroll="pagekeys"),
                         b"\x1b[5~")

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
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, 5, 5),
                         b"\x1b[<64;5;5M")                         # default report contract
        self.assertEqual(muxctl.wheel_input_sequences(t, 1, 5, 5, alt_scroll="pagekeys"),
                         b"\x1b[5~")                               # forced compat override

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
