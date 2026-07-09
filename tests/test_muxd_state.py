import importlib
import asyncio
import json
import os
import tempfile
import time
import unittest
from datetime import datetime, timedelta, timezone


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
    def test_launch_claim_filename_matches_csharp_contract(self):
        self.assertEqual(
            "Parent-ID_123-76d41cbf4b150c76.claim.json",
            muxd.claim_file_name("Parent-ID_123"),
        )

    def test_launch_claim_ids_reject_non_ascii_aliases(self):
        self.assertEqual(
            ["ascii-id"],
            muxd.launch_candidate_ids("", ["ascii-id", "ünicode-id"]),
        )

    def test_live_process_scan_includes_node_cli_shims(self):
        class Result:
            returncode = 0
            stdout = "[]"
            stderr = ""

        calls = []
        old_run = muxd.subprocess.run
        try:
            muxd.subprocess.run = lambda args, **kwargs: calls.append(args) or Result()

            ok, values, detail = muxd._try_agent_cmdlines()
        finally:
            muxd.subprocess.run = old_run

        self.assertTrue(ok, detail)
        self.assertEqual([], values)
        self.assertIn("Name='node.exe'", " ".join(calls[0]))

    def test_relay_output_backlog_is_bounded(self):
        q = muxd.RelayOutQueue(maxsize=2)

        self.assertTrue(q.put_nowait(("o", "a", b"one")))
        self.assertTrue(q.put_nowait(("o", "a", b"two")))
        self.assertFalse(q.put_nowait(("o", "a", b"three")))

        self.assertEqual(2, q.qsize())
        self.assertEqual(1, q.dropped)

    def test_manifest_preserves_canonical_and_alias_ids(self):
        class ManifestSession(FakeSession):
            def __init__(self):
                super().__init__(cmd="codex resume parent-id")
                self.name = "manifest-aliases"
                self.user_killed = False
                self.heal = True
                self.cols = 120
                self.rows = 36
                self.ids = ["parent-id", "child-id"]

        old_manifest = muxd.MANIFEST
        old_sessions = muxd.sessions
        with tempfile.TemporaryDirectory(prefix="muxd-manifest-test-") as root:
            try:
                muxd.MANIFEST = os.path.join(root, "sessions.json")
                muxd.sessions = {"manifest-aliases": ManifestSession()}
                muxd.manifest_save()
                data = json.load(open(muxd.MANIFEST, encoding="utf-8"))
                self.assertEqual(
                    ["parent-id", "child-id"],
                    data["manifest-aliases"]["ids"],
                )
            finally:
                muxd.MANIFEST = old_manifest
                muxd.sessions = old_sessions

    def test_launch_claim_blocks_alias_overlap_until_release(self):
        old_root = muxd.CLAIM_ROOT
        old_scan = muxd.try_live_session_ids
        with tempfile.TemporaryDirectory(prefix="muxd-claims-") as root:
            try:
                muxd.CLAIM_ROOT = root
                muxd.try_live_session_ids = lambda: (True, {}, "")

                claim, detail = muxd.acquire_launch_claim(
                    "codex resume parent-id",
                    ["parent-id", "child-id"],
                    "test",
                )

                self.assertIsNotNone(claim, detail)
                self.assertEqual(["child-id", "parent-id"], sorted(claim.ids))
                self.assertEqual(2, len(os.listdir(root)))
                blocked, blocked_detail = muxd.acquire_launch_claim(
                    "codex resume child-id",
                    ["child-id"],
                    "second",
                )
                self.assertIsNone(blocked)
                self.assertIn("launch already pending", blocked_detail)

                claim.release()
                self.assertEqual([], os.listdir(root))
            finally:
                muxd.CLAIM_ROOT = old_root
                muxd.try_live_session_ids = old_scan

    def test_csharp_shaped_claim_blocks_muxd(self):
        old_root = muxd.CLAIM_ROOT
        old_scan = muxd.try_live_session_ids
        with tempfile.TemporaryDirectory(prefix="muxd-csharp-claim-") as root:
            try:
                muxd.CLAIM_ROOT = root
                muxd.try_live_session_ids = lambda: (True, {}, "")
                now = datetime.now(timezone.utc)
                path = os.path.join(root, muxd.claim_file_name("shared-id"))
                with open(path, "w", encoding="utf-8") as f:
                    json.dump(
                        {
                            "SessionId": "shared-id",
                            "CandidateIds": ["shared-id"],
                            "OwnerPid": os.getpid(),
                            "OwnerProcess": "CodexLocalRetrieval",
                            "CreatedUtc": now.isoformat(),
                            "ExpiresUtc": (now + timedelta(minutes=2)).isoformat(),
                            "Reason": "C# test claim",
                        },
                        f,
                    )

                claim, detail = muxd.acquire_launch_claim("codex resume shared-id", ["shared-id"])

                self.assertIsNone(claim)
                self.assertIn("CodexLocalRetrieval pid", detail)
            finally:
                muxd.CLAIM_ROOT = old_root
                muxd.try_live_session_ids = old_scan

    def test_launch_claim_fails_closed_when_live_scan_is_unverified(self):
        old_root = muxd.CLAIM_ROOT
        old_scan = muxd.try_live_session_ids
        with tempfile.TemporaryDirectory(prefix="muxd-claims-fail-closed-") as root:
            try:
                muxd.CLAIM_ROOT = root
                muxd.try_live_session_ids = lambda: (False, {}, "WMI unavailable")

                claim, detail = muxd.acquire_launch_claim("codex resume unsafe-id", ["unsafe-id"])

                self.assertIsNone(claim)
                self.assertIn("WMI unavailable", detail)
                self.assertEqual([], os.listdir(root))
            finally:
                muxd.CLAIM_ROOT = old_root
                muxd.try_live_session_ids = old_scan

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
    async def test_terminate_session_waits_for_pty_termination(self):
        class SlowPty:
            pid = 0

            def __init__(self):
                self.finished = False

            def terminate(self, force=True):
                time.sleep(0.25)
                self.finished = True

        sess = muxd.Session.__new__(muxd.Session)
        sess.name = "slow-stop"
        sess.pty = SlowPty()
        sess.dead = False
        sess.user_killed = False
        sess.wq = __import__("queue").Queue()

        started = time.perf_counter()
        ok, detail = await muxd.terminate_session_off_loop(sess, by_user=True, timeout=2)
        elapsed = time.perf_counter() - started

        self.assertTrue(ok, detail)
        self.assertTrue(sess.pty is None)
        self.assertTrue(sess.dead)
        self.assertTrue(elapsed >= 0.2, f"termination returned before the PTY stop completed: {elapsed:.3f}s")

    async def test_terminate_session_releases_claim_only_after_process_exit(self):
        events = []

        class SlowPty:
            pid = 0

            def terminate(self, force=True):
                time.sleep(0.15)
                events.append("process-exited")

        class Claim:
            paths = ("claim.json",)

            def release(self):
                events.append("claim-released")

        sess = muxd.Session.__new__(muxd.Session)
        sess.name = "claim-stop"
        sess.pty = SlowPty()
        sess.dead = False
        sess.user_killed = False
        sess.wq = __import__("queue").Queue()
        sess._launch_claim = Claim()
        sess.claim_paths = ["claim.json"]

        ok, detail = await muxd.terminate_session_off_loop(sess, timeout=2)

        self.assertTrue(ok, detail)
        self.assertEqual(["process-exited", "claim-released"], events)
        self.assertIsNone(sess._launch_claim)
        self.assertEqual([], sess.claim_paths)

    async def test_failed_termination_preserves_live_owner_and_claim(self):
        class LivePty:
            pid = 4242

            def terminate(self, force=True):
                return

        class Claim:
            paths = ("claim.json",)

            def __init__(self):
                self.released = False

            def release(self):
                self.released = True

        pty = LivePty()
        claim = Claim()
        sess = muxd.Session.__new__(muxd.Session)
        sess.name = "failed-stop"
        sess.pty = pty
        sess.dead = False
        sess.user_killed = False
        sess.wq = __import__("queue").Queue()
        sess._launch_claim = claim
        sess.claim_paths = ["claim.json"]

        old_alive = muxd._pid_alive
        old_run = muxd.subprocess.run
        try:
            muxd._pid_alive = lambda pid: True
            muxd.subprocess.run = lambda *args, **kwargs: None

            ok, detail = await muxd.terminate_session_off_loop(sess, timeout=0.1)
        finally:
            muxd._pid_alive = old_alive
            muxd.subprocess.run = old_run

        self.assertFalse(ok)
        self.assertIn("still alive", detail)
        self.assertIs(sess.pty, pty, "failed exit verification must not orphan the live PTY handle")
        self.assertFalse(sess.dead, "the still-live session must remain attached and usable")
        self.assertIs(sess._launch_claim, claim)
        self.assertFalse(claim.released)
        self.assertTrue(sess.wq.empty(), "failed termination must not stop the input writer")

    async def test_visible_owner_requires_explicit_child_exit_confirmation(self):
        class Claim:
            def __init__(self):
                self.released = False

            def release(self):
                self.released = True

        class Owner:
            owner = True
            owner_exit_confirmed = False
            dead = False

            def __init__(self):
                self._launch_claim = Claim()
                self.claim_paths = ["owner.claim.json"]

            def kill(self, by_user=True):
                self.user_killed = by_user

        owner = Owner()
        ok, detail = await muxd.terminate_session_off_loop(owner, timeout=0.1)

        self.assertFalse(ok)
        self.assertIn("did not confirm child-process exit", detail)
        self.assertFalse(owner.dead)
        self.assertFalse(owner._launch_claim.released)

        async def confirm_exit():
            await asyncio.sleep(0.1)
            owner.owner_exit_confirmed = True

        confirmation = asyncio.create_task(confirm_exit())
        ok, detail = await muxd.terminate_session_off_loop(owner, timeout=1)
        await confirmation

        self.assertTrue(ok, detail)
        self.assertTrue(owner.dead)
        self.assertIsNone(owner._launch_claim)

    async def test_new_session_spawn_runs_off_event_loop(self):
        class SlowSession:
            def __init__(self, name, cmd, cwd, cols, rows, loop, outq, heal=False, spawn_now=True, ids=None):
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
