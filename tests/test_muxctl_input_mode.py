import importlib
import unittest


muxctl = importlib.import_module("muxctl")


class MuxctlInputModeTests(unittest.TestCase):
    def test_default_attach_mode_rejects_generated_event_streams(self):
        original = (
            muxctl.ENABLE_PROCESSED_INPUT
            | muxctl.ENABLE_LINE_INPUT
            | muxctl.ENABLE_ECHO_INPUT
            | muxctl.ENABLE_WINDOW_INPUT
            | muxctl.ENABLE_MOUSE_INPUT
            | muxctl.ENABLE_QUICK_EDIT_MODE
            | muxctl.ENABLE_VIRTUAL_TERMINAL_INPUT
        )

        mode = muxctl.attach_input_mode(original)

        self.assertEqual(mode & muxctl.ENABLE_LINE_INPUT, 0)
        self.assertEqual(mode & muxctl.ENABLE_ECHO_INPUT, 0)
        self.assertEqual(mode & muxctl.ENABLE_QUICK_EDIT_MODE, 0)
        self.assertEqual(mode & muxctl.ENABLE_PROCESSED_INPUT, 0)
        self.assertEqual(mode & muxctl.ENABLE_WINDOW_INPUT, 0)
        self.assertEqual(mode & muxctl.ENABLE_MOUSE_INPUT, 0)
        self.assertEqual(mode & muxctl.ENABLE_VIRTUAL_TERMINAL_INPUT, 0)
        self.assertNotEqual(mode & muxctl.ENABLE_EXTENDED_FLAGS, 0)

    def test_vt_input_is_explicit_opt_in_without_mouse_or_window_events(self):
        mode = muxctl.attach_input_mode(0, vt_input=True)

        self.assertNotEqual(mode & muxctl.ENABLE_VIRTUAL_TERMINAL_INPUT, 0)
        self.assertEqual(mode & muxctl.ENABLE_MOUSE_INPUT, 0)
        self.assertEqual(mode & muxctl.ENABLE_WINDOW_INPUT, 0)


if __name__ == "__main__":
    unittest.main()
