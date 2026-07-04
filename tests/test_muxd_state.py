import importlib
import asyncio
import time
import unittest


muxd = importlib.import_module("muxd")


class FakeSession:
    def __init__(self, cmd="", alive=True, owner=False):
        self.name = "fake"
        self.cmd = cmd
        self.cwd = r"Z:\tmp"
        self.created = 100.0
        self.last_out = 120.0
        self.cols = 100
        self.rows = 30
        self.heal = False
        self.local = set()
        self.owner = owner
        self._alive = alive

    def alive(self):
        return self._alive

    def tail_text(self):
        return "tail"


class MuxdStateTests(unittest.TestCase):
    def test_shell_only_payload_is_not_command_backed(self):
        payload = muxd.session_payload("shell", FakeSession(cmd="", alive=True))

        self.assertTrue(payload["alive"])
        self.assertTrue(payload["ready"])
        self.assertFalse(payload["hasCommand"])
        self.assertTrue(payload["shellOnly"])
        self.assertEqual(payload["kind"], "shell")
        self.assertEqual(payload["cmdSig"], "")

    def test_command_payload_is_distinguishable_from_shell(self):
        cmd = "Write-Output TEST_MARKER"
        payload = muxd.session_payload("agent", FakeSession(cmd=cmd, alive=True))

        self.assertTrue(payload["alive"])
        self.assertTrue(payload["ready"])
        self.assertTrue(payload["hasCommand"])
        self.assertFalse(payload["shellOnly"])
        self.assertEqual(payload["kind"], "command")
        self.assertEqual(payload["cmdSig"], muxd.command_sig(cmd))

    def test_existing_alive_shell_must_relaunch_when_resume_command_arrives(self):
        self.assertTrue(muxd.needs_relaunch_for_command(FakeSession(cmd="", alive=True), "claude --resume abc"))

    def test_existing_alive_same_command_can_be_reused(self):
        cmd = "claude --resume abc"
        self.assertFalse(muxd.needs_relaunch_for_command(FakeSession(cmd=cmd, alive=True), cmd))

    def test_existing_alive_different_command_must_not_silently_reuse_wrong_session(self):
        self.assertTrue(muxd.needs_relaunch_for_command(FakeSession(cmd="claude --resume old", alive=True), "claude --resume new"))

    def test_empty_command_does_not_restart_alive_shell(self):
        self.assertFalse(muxd.needs_relaunch_for_command(FakeSession(cmd="", alive=True), ""))

    def test_dead_previous_must_relaunch_when_command_arrives(self):
        self.assertTrue(muxd.needs_relaunch_for_command(FakeSession(cmd="claude --resume abc", alive=False), "claude --resume abc"))

    def test_command_signature_ignores_surrounding_whitespace(self):
        self.assertEqual(muxd.command_sig("  claude --resume abc  "), muxd.command_sig("claude --resume abc"))

    def test_session_alive_uses_cached_dead_flag_not_conpty_probe(self):
        class PtyThatMustNotBeQueried:
            def isalive(self):
                raise AssertionError("alive() must not call winpty on the event loop")

        sess = muxd.Session.__new__(muxd.Session)
        sess.dead = False
        sess.pty = PtyThatMustNotBeQueried()

        self.assertTrue(sess.alive())


class MuxdAsyncTests(unittest.IsolatedAsyncioTestCase):
    async def test_new_session_spawn_runs_off_event_loop(self):
        class SlowSession:
            def __init__(self, name, cmd, cwd, cols, rows, loop, outq, heal=False, spawn_now=True):
                self.name = name
                self.spawned = False

            def spawn(self):
                time.sleep(0.3)
                self.spawned = True

        original = muxd.Session
        muxd.Session = SlowSession
        try:
            started = time.perf_counter()
            task = asyncio.create_task(muxd.new_session_off_loop("slow", "", "", 80, 24, asyncio.get_running_loop(), asyncio.Queue()))
            await asyncio.sleep(0.05)
            elapsed = time.perf_counter() - started
            self.assertLess(elapsed, 0.2)
            sess = await task
            self.assertTrue(sess.spawned)
        finally:
            muxd.Session = original


if __name__ == "__main__":
    unittest.main()
