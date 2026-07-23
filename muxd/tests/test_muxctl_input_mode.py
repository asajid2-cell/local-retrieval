"""Attach console input-mode math: wheel capture and QuickEdit are one switch.

A local attach must feel like a local terminal. On a plain shell (normal buffer) conhost owns the
wheel and the selection, so ENABLE_QUICK_EDIT_MODE has to be ON — that is native drag-select and
Enter-to-copy, including its pause-output-while-dragging behaviour. Only while the hosted app owns
scrolling (alternate screen / mouse tracking) does muxctl take the wheel, and then QuickEdit must
go or conhost swallows the drag as a selection instead of delivering MOUSE_EVENTs. VT input is a
separate contract (terminal-generated reports) and never gets QuickEdit.

These are pure bit computations — no console handle, so they run anywhere.
"""
import importlib
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
muxctl = importlib.import_module("muxctl")

# a console entering an attach with every mode bit muxctl cares about already set
DIRTY = (
    muxctl.ENABLE_PROCESSED_INPUT
    | muxctl.ENABLE_LINE_INPUT
    | muxctl.ENABLE_ECHO_INPUT
    | muxctl.ENABLE_WINDOW_INPUT
    | muxctl.ENABLE_MOUSE_INPUT
    | muxctl.ENABLE_QUICK_EDIT_MODE
    | muxctl.ENABLE_VIRTUAL_TERMINAL_INPUT
)
MATRIX = [(c, v) for c in (False, True) for v in (False, True)]


class AttachInputModeMatrixTests(unittest.TestCase):
    """(mouse capture on|off) x (MUXCTL_VT_INPUT on|off), from clean and dirty entering modes."""

    def modes(self, capture, vt_input):
        return [muxctl.attach_input_mode(base, vt_input=vt_input, mouse_capture=capture)
                for base in (0, DIRTY)]

    def test_quick_edit_set_exactly_when_capture_off_and_vt_input_off(self):
        for capture, vt_input in MATRIX:
            want = not capture and not vt_input
            for mode in self.modes(capture, vt_input):
                with self.subTest(capture=capture, vt_input=vt_input):
                    self.assertEqual(bool(mode & muxctl.ENABLE_QUICK_EDIT_MODE), want)

    def test_mouse_input_set_exactly_when_capture_on(self):
        for capture, vt_input in MATRIX:
            for mode in self.modes(capture, vt_input):
                with self.subTest(capture=capture, vt_input=vt_input):
                    self.assertEqual(bool(mode & muxctl.ENABLE_MOUSE_INPUT), capture)

    def test_extended_flags_always_set(self):
        for capture, vt_input in MATRIX:
            for mode in self.modes(capture, vt_input):
                with self.subTest(capture=capture, vt_input=vt_input):
                    self.assertTrue(mode & muxctl.ENABLE_EXTENDED_FLAGS)

    def test_vt_input_bit_follows_the_flag(self):
        for capture, vt_input in MATRIX:
            for mode in self.modes(capture, vt_input):
                with self.subTest(capture=capture, vt_input=vt_input):
                    self.assertEqual(bool(mode & muxctl.ENABLE_VIRTUAL_TERMINAL_INPUT), vt_input)

    def test_cooked_and_generated_event_streams_always_rejected(self):
        # line/echo/processed input would cook keystrokes; window events are noise muxctl never reads
        for capture, vt_input in MATRIX:
            for mode in self.modes(capture, vt_input):
                with self.subTest(capture=capture, vt_input=vt_input):
                    self.assertEqual(mode & muxctl.ENABLE_LINE_INPUT, 0)
                    self.assertEqual(mode & muxctl.ENABLE_ECHO_INPUT, 0)
                    self.assertEqual(mode & muxctl.ENABLE_PROCESSED_INPUT, 0)
                    self.assertEqual(mode & muxctl.ENABLE_WINDOW_INPUT, 0)

    def test_default_attach_is_the_plain_shell_local_terminal(self):
        # no kwargs = what terminal_attach_mode() enters with: conhost keeps wheel + drag-select
        mode = muxctl.attach_input_mode(DIRTY)
        self.assertTrue(mode & muxctl.ENABLE_QUICK_EDIT_MODE)
        self.assertEqual(mode & muxctl.ENABLE_MOUSE_INPUT, 0)
        self.assertEqual(mode & muxctl.ENABLE_VIRTUAL_TERMINAL_INPUT, 0)


class MouseCaptureToggleTests(unittest.TestCase):
    """set_mouse_capture()'s math, applied to a LIVE attach mode (not a fresh one)."""

    def test_capture_on_takes_the_wheel_and_drops_quick_edit(self):
        live = muxctl.attach_input_mode(0)  # attached, plain shell, QuickEdit on
        captured = muxctl.mouse_capture_mode(live, True)
        self.assertTrue(captured & muxctl.ENABLE_MOUSE_INPUT)
        self.assertEqual(captured & muxctl.ENABLE_QUICK_EDIT_MODE, 0)

    def test_capture_off_gives_drag_select_back(self):
        captured = muxctl.attach_input_mode(0, mouse_capture=True)
        released = muxctl.mouse_capture_mode(captured, False)
        self.assertEqual(released & muxctl.ENABLE_MOUSE_INPUT, 0)
        self.assertTrue(released & muxctl.ENABLE_QUICK_EDIT_MODE)
        self.assertTrue(released & muxctl.ENABLE_EXTENDED_FLAGS)

    def test_vt_input_attach_never_gains_quick_edit_on_release(self):
        captured = muxctl.attach_input_mode(0, vt_input=True, mouse_capture=True)
        released = muxctl.mouse_capture_mode(captured, False, vt_input=True)
        self.assertEqual(released & muxctl.ENABLE_QUICK_EDIT_MODE, 0)
        self.assertEqual(released & muxctl.ENABLE_MOUSE_INPUT, 0)
        self.assertTrue(released & muxctl.ENABLE_VIRTUAL_TERMINAL_INPUT)

    def test_toggling_capture_is_idempotent_and_reversible(self):
        for vt_input in (False, True):
            base = muxctl.attach_input_mode(DIRTY, vt_input=vt_input)
            with self.subTest(vt_input=vt_input):
                on = muxctl.mouse_capture_mode(base, True, vt_input=vt_input)
                self.assertEqual(muxctl.mouse_capture_mode(on, True, vt_input=vt_input), on)
                off = muxctl.mouse_capture_mode(on, False, vt_input=vt_input)
                self.assertEqual(off, base)
                self.assertEqual(muxctl.mouse_capture_mode(off, False, vt_input=vt_input), off)


class RestoreOnDetachTests(unittest.TestCase):
    """terminal_attach_mode() must hand the console back byte-for-byte, whatever we did to it."""

    def test_finally_block_restores_the_entering_mode_exactly(self):
        import inspect
        src = inspect.getsource(muxctl.terminal_attach_mode)
        self.assertIn("k.SetConsoleMode(hin, in_mode.value)", src)
        self.assertIn("k.SetConsoleMode(hout, out_mode.value)", src)
        finally_body = src.split("finally:", 1)[1]
        self.assertIn("in_mode.value", finally_body)
        self.assertIn("out_mode.value", finally_body)

    def test_entering_mode_is_captured_before_any_mutation(self):
        import inspect
        src = inspect.getsource(muxctl.terminal_attach_mode)
        got_in = src.index("GetConsoleMode(hin")
        set_in = src.index("SetConsoleMode(hin")
        self.assertLess(got_in, set_in)

    def test_restored_mode_is_the_original_not_a_recomputation(self):
        # the attach mode is lossy (bits cleared), so only the saved value can restore a console
        # that entered with, say, line input on — assert the math really is lossy
        for capture, vt_input in MATRIX:
            with self.subTest(capture=capture, vt_input=vt_input):
                mode = muxctl.attach_input_mode(DIRTY, vt_input=vt_input, mouse_capture=capture)
                self.assertNotEqual(mode, DIRTY)


if __name__ == "__main__":
    unittest.main()
