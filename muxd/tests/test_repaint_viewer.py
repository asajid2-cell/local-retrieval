import importlib
import unittest


muxd = importlib.import_module("muxd")


class RepaintViewerTests(unittest.TestCase):
    """R5: a viewer that attaches (or re-attaches, resyncs, or asks) to an INLINE app gets a repaint.

    redraw_nudge is alt-screen-only (a size wiggle needs a full-screen TUI to answer it), so a gateway
    rendering inline on the MAIN screen never repainted for a new viewer - the viewer saw whatever cells
    were already in the ring. repaint_viewer asks an app that enabled focus reporting (DECSET 1004) to
    redraw its whole block by injecting FOCUS_IN, and leaves every other case exactly as it was: an alt
    screen keeps the size wiggle, and a shell that never asked for focus reports gets nothing.
    """

    class Session:
        def __init__(self):
            self.name = "repaint-probe"
            self.cols, self.rows = 80, 24
            self.replay_state = muxd.TerminalReplayState()
            self.writes = []
            self._last_nudge = 0.0

            class Pty:
                def __init__(self, outer):
                    self.outer = outer

                def setwinsize(self, rows, cols):
                    self.outer.writes.append(("winsize", rows, cols))

            self.pty = Pty(self)

        def write(self, data):
            self.writes.append(("write", data))

    def test_main_screen_app_with_1004_is_asked_to_repaint(self):
        s = self.Session()
        s.replay_state.ingest(b"\x1b[?1004h")            # the gateway's raw-mode enable
        muxd.repaint_viewer(s)
        self.assertEqual(s.writes, [("write", muxd.FOCUS_IN)])

    def test_a_shell_that_never_enabled_1004_gets_nothing(self):
        s = self.Session()
        muxd.repaint_viewer(s)
        self.assertEqual(s.writes, [])

    def test_a_main_screen_app_that_disabled_1004_gets_nothing(self):
        s = self.Session()
        s.replay_state.ingest(b"\x1b[?1004h\x1b[?1004l")
        muxd.repaint_viewer(s)
        self.assertEqual(s.writes, [])

    def test_alt_screen_keeps_the_size_wiggle_and_no_focus_inject(self):
        s = self.Session()
        s.replay_state.ingest(b"\x1b[?1049h\x1b[?1004h")  # an alt TUI that also reports focus
        muxd.repaint_viewer(s)
        self.assertEqual(s.writes, [("winsize", 23, 80), ("winsize", 24, 80)])
        self.assertNotIn(("write", muxd.FOCUS_IN), s.writes)

    def test_focus_inject_is_rate_limited_to_one_per_second(self):
        s = self.Session()
        s.replay_state.ingest(b"\x1b[?1004h")
        muxd.repaint_viewer(s)
        muxd.repaint_viewer(s)
        muxd.repaint_viewer(s)
        self.assertEqual([w for w in s.writes if w[0] == "write"], [("write", muxd.FOCUS_IN)])

    def test_1004_is_tracked_as_a_replay_private_mode(self):
        # repaint_viewer gates on the session's own private_modes, so 1004 must be tracked there - a
        # mid-session attach restores it too, or the client never reports focus back to the app.
        self.assertIn(1004, muxd.REPLAY_PRIVATE_MODES)
        state = muxd.TerminalReplayState()
        state.ingest(b"\x1b[?1004h")
        self.assertIn(1004, state.private_modes)
        self.assertIn(b"\x1b[?1004h", state.prefix())
