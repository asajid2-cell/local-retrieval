import importlib
import asyncio
import time
import unittest


muxd = importlib.import_module("muxd")


class FakeSession:
    def __init__(self, cmd="", alive=True, owner=False, tail="tail", last_out=120.0, created=100.0):
        self.name = "fake"
        self.cmd = cmd
        self.cwd = r"Z:\tmp"
        self.created = created
        self.last_out = last_out
        self.cols = 100
        self.rows = 30
        self.heal = False
        self.local = set()
        self.owner = owner
        self._alive = alive
        self._tail = tail

    def alive(self):
        return self._alive

    def tail_text(self):
        return self._tail


class MuxdStateTests(unittest.TestCase):
    def test_single_instance_mutex_refuses_duplicate_muxd(self):
        class FakeCall:
            def __init__(self, result):
                self.result = result
                self.calls = []

            def __call__(self, *args):
                self.calls.append(args)
                return self.result

        class FakeKernel32:
            def __init__(self):
                self.CreateMutexW = FakeCall(1234)
                self.CloseHandle = FakeCall(True)

        fake = FakeKernel32()
        old_windll = muxd.ctypes.WinDLL
        old_get_last_error = muxd.ctypes.get_last_error
        old_handle = muxd._INSTANCE_MUTEX_HANDLE
        try:
            muxd._INSTANCE_MUTEX_HANDLE = None
            muxd.ctypes.WinDLL = lambda *args, **kwargs: fake
            muxd.ctypes.get_last_error = lambda: 183

            ok, detail = muxd.acquire_single_instance()

            self.assertFalse(ok)
            self.assertIn("already owns", detail)
            self.assertEqual(fake.CloseHandle.calls, [(1234,)])
            self.assertIsNone(muxd._INSTANCE_MUTEX_HANDLE)
        finally:
            muxd.ctypes.WinDLL = old_windll
            muxd.ctypes.get_last_error = old_get_last_error
            muxd._INSTANCE_MUTEX_HANDLE = old_handle

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
        payload = muxd.session_payload("agent", FakeSession(cmd=cmd, alive=True, last_out=time.time()))

        self.assertTrue(payload["alive"])
        self.assertTrue(payload["ready"])
        self.assertTrue(payload["hasCommand"])
        self.assertFalse(payload["shellOnly"])
        self.assertEqual(payload["kind"], "command")
        self.assertEqual(payload["cmdSig"], muxd.command_sig(cmd))
        self.assertEqual(payload["agentState"], "working")
        self.assertFalse(payload["needsAttention"])

    def test_terminal_sanitizer_removes_cursor_style_sequences(self):
        self.assertEqual(muxd.clean_terminal_text("\x1b[0 qhello\x1b[?25l"), "hello")
        self.assertEqual(muxd.clean_terminal_text("[0 qhello[49m"), "hello")

    def test_quiet_command_backed_agent_needs_attention(self):
        cmd = "codex resume abc"
        payload = muxd.session_payload("quiet", FakeSession(cmd=cmd, alive=True, last_out=time.time() - 120))

        self.assertEqual(payload["agentState"], "attention")
        self.assertTrue(payload["needsAttention"])

    def test_command_backed_shell_prompt_is_stopped(self):
        cmd = "codex resume abc"
        payload = muxd.session_payload("stopped", FakeSession(cmd=cmd, alive=True, tail=r"PS C:\Users\Ahmed>", last_out=time.time() - 120))

        self.assertEqual(payload["agentState"], "stopped")
        self.assertTrue(payload["needsAttention"])

    def test_shell_only_payload_is_neutral_attention_state(self):
        payload = muxd.session_payload("shell", FakeSession(cmd="", alive=True))

        self.assertEqual(payload["agentState"], "neutral")
        self.assertFalse(payload["needsAttention"])

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

    def test_resume_parser_accepts_quoted_ids(self):
        self.assertEqual(muxd._parse_resume_id('claude --resume "quoted-claude"'), "quoted-claude")
        self.assertEqual(muxd._parse_resume_id('codex resume --include-non-interactive "quoted-codex"'), "quoted-codex")

    def test_resume_conflict_checks_alias_candidates(self):
        conflict = muxd.resume_conflict("codex resume parent-id", live={"child-id": 4242}, ids=["parent-id", "child-id"])

        self.assertIsNotNone(conflict)
        self.assertEqual(conflict[0], "child-id")
        self.assertEqual(conflict[1], 4242)

    def test_resume_conflict_fails_closed_when_live_scan_unverified(self):
        conflict = muxd.resume_conflict("codex resume parent-id", live=(False, {}, "WMI unavailable"))

        self.assertIsNotNone(conflict)
        self.assertEqual(conflict[0], "parent-id")
        self.assertIn("WMI unavailable", muxd.conflict_detail(conflict))

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
