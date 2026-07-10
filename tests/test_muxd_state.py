import importlib
import asyncio
import collections
import json
import os
import queue
import tempfile
import threading
import time
import unittest
from datetime import datetime, timedelta, timezone
from unittest import mock


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
    def test_session_rings_hold_one_lock_for_mutation_and_snapshot_reads(self):
        class GuardedDeque(collections.deque):
            def __init__(self, lock, values=()):
                self.lock = lock
                super().__init__(values)

            def check(self):
                if not self.lock._is_owned():
                    raise AssertionError("session ring accessed without owning its lock")

            def append(self, value):
                self.check()
                return super().append(value)

            def popleft(self):
                self.check()
                return super().popleft()

            def __iter__(self):
                self.check()
                return super().__iter__()

            def __reversed__(self):
                self.check()
                return super().__reversed__()

        session = muxd.Session(
            "ring-lock-session",
            "",
            tempfile.gettempdir(),
            80,
            24,
            None,
            None,
            spawn_now=False,
        )
        session.ring = GuardedDeque(session._ring_lock, [b"a", b"b"])
        session.ring_len = 2
        session._append_ring(b"c")
        self.assertEqual(b"abc", session.scrollback())
        self.assertEqual("abc", session.tail_text())

        owner = muxd.OwnerSession(
            "ring-lock-owner",
            "",
            tempfile.gettempdir(),
            80,
            24,
            None,
            None,
            None,
        )
        owner.ring = GuardedDeque(owner._ring_lock, [b"x", b"y"])
        owner.ring_len = 2
        owner.ingest(b"z")
        self.assertEqual(b"xyz", owner.scrollback())
        self.assertEqual("xyz", owner.tail_text())

    def test_oversized_ring_chunk_keeps_the_newest_bounded_bytes(self):
        payload = b"a" + (b"b" * muxd.RING_CAP)
        session = muxd.Session(
            "oversized-ring-session",
            "",
            tempfile.gettempdir(),
            80,
            24,
            None,
            None,
            spawn_now=False,
        )
        owner = muxd.OwnerSession(
            "oversized-ring-owner",
            "",
            tempfile.gettempdir(),
            80,
            24,
            None,
            None,
            None,
        )

        session._append_ring(payload)
        owner.ingest(payload)

        self.assertEqual(muxd.RING_CAP, session.ring_len)
        self.assertEqual(b"b" * muxd.RING_CAP, session.scrollback(muxd.RING_CAP))
        self.assertEqual(muxd.RING_CAP, owner.ring_len)
        self.assertEqual(b"b" * muxd.RING_CAP, owner.scrollback(muxd.RING_CAP))

    def test_slow_local_viewer_queue_is_bounded_and_disconnected(self):
        session = FakeSession()
        local_queue = asyncio.Queue(maxsize=1)
        local_queue.put_nowait(b"old")
        session.local.add(local_queue)

        muxd.fanout_local_output(session, b"new")

        self.assertNotIn(local_queue, session.local)
        self.assertIs(muxd.LOCAL_VIEWER_SLOW, local_queue.get_nowait())

    def test_supervised_background_task_is_retained_and_restarted(self):
        async def exercise():
            registry = set()
            restarted = asyncio.Event()
            attempts = 0

            async def worker():
                nonlocal attempts
                attempts += 1
                if attempts == 1:
                    raise RuntimeError("injected task failure")
                restarted.set()
                await asyncio.Future()

            task = muxd.start_supervised_background(
                registry,
                "test-worker",
                worker,
                restart_delay=0.01,
            )
            self.assertIn(task, registry)
            await asyncio.wait_for(restarted.wait(), timeout=1)
            self.assertEqual(2, attempts)
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task

        asyncio.run(exercise())

    def test_intent_compaction_bounds_terminal_history_without_dropping_uncertain_work(self):
        now = 1_000_000.0
        records = {
            "local:create:old": {
                "status": "completed",
                "updatedAt": now - muxd.INTENT_TERMINAL_RETENTION_SECONDS - 1,
            },
            "local:create:recent": {
                "status": "failed",
                "updatedAt": now - 10,
            },
            "local:input:uncertain": {
                "status": "dispatching",
                "updatedAt": 1,
            },
            "relay:create:accepted": {
                "status": "accepted",
                "updatedAt": 1,
            },
        }

        compacted = muxd.compact_intent_records(records, now=now)

        self.assertNotIn("local:create:old", compacted)
        self.assertIn("local:create:recent", compacted)
        self.assertIn("local:input:uncertain", compacted)
        self.assertIn("relay:create:accepted", compacted)

    def test_durable_json_write_preserves_previous_state_on_precommit_failures(self):
        with tempfile.TemporaryDirectory(prefix="muxd-durable-") as root:
            path = os.path.join(root, "state.json")
            original = json.dumps({"version": 1}, separators=(",", ":")).encode("utf-8")
            with open(path, "wb") as stream:
                stream.write(original)

            for failed_stage in ("before_write", "before_file_fsync", "before_replace"):
                def fault(stage, _path, expected=failed_stage):
                    if stage == expected:
                        raise OSError("injected " + stage)

                with self.assertRaisesRegex(OSError, "injected " + failed_stage):
                    muxd.durable_json_write(path, {"version": 2}, fault=fault)
                with open(path, "rb") as stream:
                    self.assertEqual(original, stream.read())
                self.assertFalse(any(name.endswith(".tmp") for name in os.listdir(root)))

    def test_durable_json_write_fails_closed_after_replace_uncertainty(self):
        with tempfile.TemporaryDirectory(prefix="muxd-durable-") as root:
            path = os.path.join(root, "state.json")
            with open(path, "w", encoding="utf-8") as stream:
                json.dump({"version": 1}, stream)

            def fail_directory_fsync(stage, _path):
                if stage == "before_directory_fsync":
                    raise OSError("injected directory fsync")

            muxd.durable_json_write(path, {"version": 2}, fault=fail_directory_fsync)
            with open(path, encoding="utf-8") as stream:
                self.assertEqual({"version": 2}, json.load(stream))
            with open(path + ".bak", encoding="utf-8") as stream:
                self.assertEqual({"version": 1}, json.load(stream))

            def corrupt_readback(stage, _path):
                if stage == "before_readback":
                    with open(path, "w", encoding="utf-8") as stream:
                        json.dump({"version": 999}, stream)

            with self.assertRaisesRegex(OSError, "committed state did not read back identically"):
                muxd.durable_json_write(path, {"version": 3}, fault=corrupt_readback)
            with open(path + ".bak", encoding="utf-8") as stream:
                self.assertEqual({"version": 2}, json.load(stream))

    def test_durable_json_load_restores_corrupt_primary_from_backup(self):
        with tempfile.TemporaryDirectory(prefix="muxd-durable-") as root:
            path = os.path.join(root, "state.json")
            with open(path, "w", encoding="utf-8") as stream:
                stream.write('{"broken"')
            with open(path + ".bak", "w", encoding="utf-8") as stream:
                json.dump({"version": 7}, stream)

            self.assertEqual({"version": 7}, muxd.durable_json_load(path, {"version": 0}))
            with open(path, encoding="utf-8") as stream:
                self.assertEqual({"version": 7}, json.load(stream))

    def test_durable_json_load_restores_semantically_invalid_primary(self):
        with tempfile.TemporaryDirectory(prefix="muxd-durable-") as root:
            path = os.path.join(root, "state.json")
            with open(path, "w", encoding="utf-8") as stream:
                json.dump([], stream)
            with open(path + ".bak", "w", encoding="utf-8") as stream:
                json.dump({"valid": {"cmd": "", "cwd": "", "cols": 100, "rows": 30, "heal": False}}, stream)

            loaded = muxd.durable_json_load(path, {}, validate=muxd.valid_manifest)

            self.assertIn("valid", loaded)
            with open(path, encoding="utf-8") as stream:
                self.assertEqual(loaded, json.load(stream))

    def test_backup_commit_error_never_claims_primary_candidate_committed(self):
        with tempfile.TemporaryDirectory(prefix="muxd-durable-") as root:
            path = os.path.join(root, "state.json")
            with open(path, "w", encoding="utf-8") as stream:
                json.dump({"version": 1}, stream)

            def fail_backup(stage, _path):
                if stage == "before_directory_fsync":
                    raise OSError("backup durability uncertain")

            with self.assertRaisesRegex(OSError, "backup durability uncertain") as raised:
                muxd.durable_json_write(path, {"version": 2}, backup_fault=fail_backup)

            self.assertFalse(getattr(raised.exception, "committed", True))
            self.assertFalse(getattr(raised.exception, "primary_committed", True))
            with open(path, encoding="utf-8") as stream:
                self.assertEqual({"version": 1}, json.load(stream))

    def test_post_replace_verification_read_failure_never_rolls_primary_backward(self):
        with tempfile.TemporaryDirectory(prefix="muxd-durable-") as root:
            path = os.path.join(root, "state.json")
            with open(path, "w", encoding="utf-8") as stream:
                json.dump({"version": 1}, stream)

            original_open = open
            primary_reads = 0

            def failing_open(target, mode="r", *args, **kwargs):
                nonlocal primary_reads
                if target == path and "r" in mode:
                    primary_reads += 1
                    if primary_reads > 1:
                        raise OSError("verification read unavailable")
                return original_open(target, mode, *args, **kwargs)

            old_open = muxd.open if hasattr(muxd, "open") else None
            try:
                muxd.open = failing_open
                with self.assertRaises(OSError) as raised:
                    muxd.durable_json_write(path, {"version": 2})
            finally:
                if old_open is None:
                    delattr(muxd, "open")
                else:
                    muxd.open = old_open

            self.assertTrue(getattr(raised.exception, "unknown", False))
            self.assertFalse(getattr(raised.exception, "recovered", False))
            with open(path, encoding="utf-8") as stream:
                self.assertEqual({"version": 2}, json.load(stream))
            with open(path + ".bak", encoding="utf-8") as stream:
                self.assertEqual({"version": 1}, json.load(stream))

    def test_retry_failure_preserves_already_confirmed_committed_generation(self):
        with tempfile.TemporaryDirectory(prefix="muxd-durable-") as root:
            path = os.path.join(root, "state.json")
            with open(path, "w", encoding="utf-8") as stream:
                json.dump({"version": 1}, stream)

            def fail_initial(stage, _path):
                if stage == "before_directory_fsync":
                    raise OSError("initial durability uncertainty")

            def fail_retry(stage, _path):
                if stage == "before_write":
                    raise OSError("retry unavailable")

            with self.assertRaisesRegex(OSError, "retry unavailable") as raised:
                muxd.durable_json_write(
                    path,
                    {"version": 2},
                    fault=fail_initial,
                    retry_fault=fail_retry,
                )

            self.assertTrue(getattr(raised.exception, "committed", False))
            self.assertFalse(getattr(raised.exception, "mismatch", True))
            self.assertFalse(getattr(raised.exception, "unknown", True))
            with open(path, encoding="utf-8") as stream:
                self.assertEqual({"version": 2}, json.load(stream))

    def test_launch_claim_filename_matches_csharp_contract(self):
        self.assertEqual(
            "Parent-ID_123-76d41cbf4b150c76.claim.json",
            muxd.claim_file_name("Parent-ID_123"),
        )

    def test_process_start_token_fences_pid_reuse(self):
        token = muxd._process_start_token(os.getpid())

        self.assertTrue(token)
        self.assertTrue(muxd._same_process_instance(os.getpid(), token))
        self.assertFalse(muxd._same_process_instance(os.getpid(), token + "-stale"))
        self.assertFalse(muxd._same_process_instance(os.getpid(), ""))

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
                self.session_id = "parent-id"
                self.aliases = ("child-id",)
                self.ids = ["parent-id", "child-id"]
                self.owner = True
                self.owner_key = "k" * 32
                self.identity_pending = True
                self.lifecycle = "stopping"
                self.child_pid = 4321
                self.child_start_token = "0000000000001234"
                self.stop_disposition = "replace"
                self.user_killed = True
                self.deaths = [100.0, 200.0]

        old_manifest = muxd.MANIFEST
        old_sessions = muxd.sessions
        with tempfile.TemporaryDirectory(prefix="muxd-manifest-test-") as root:
            try:
                muxd.MANIFEST = os.path.join(root, "sessions.json")
                muxd.sessions = {"manifest-aliases": ManifestSession()}
                muxd.durable_json_write(muxd.MANIFEST, muxd.manifest_payload())
                with open(muxd.MANIFEST, encoding="utf-8") as stream:
                    data = json.load(stream)
                self.assertEqual(
                    ["parent-id", "child-id"],
                    data["sessions"]["manifest-aliases"]["ids"],
                )
                self.assertEqual("parent-id", data["sessions"]["manifest-aliases"]["sessionId"])
                self.assertEqual(["child-id"], data["sessions"]["manifest-aliases"]["aliases"])
                self.assertTrue(data["sessions"]["manifest-aliases"]["owner"])
                self.assertEqual("k" * 32, data["sessions"]["manifest-aliases"]["ownerKey"])
                self.assertTrue(data["sessions"]["manifest-aliases"]["identityPending"])
                self.assertEqual("stopping", data["sessions"]["manifest-aliases"]["lifecycle"])
                self.assertEqual(4321, data["sessions"]["manifest-aliases"]["childPid"])
                self.assertEqual("0000000000001234", data["sessions"]["manifest-aliases"]["childStartToken"])
                self.assertEqual("replace", data["sessions"]["manifest-aliases"]["stopDisposition"])
                self.assertTrue(data["sessions"]["manifest-aliases"]["userKilled"])
                self.assertEqual([100.0, 200.0], data["sessions"]["manifest-aliases"]["deaths"])
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
        self.assertNotIn("cmdSig", payload)

    def test_session_payload_exposes_opaque_identity_without_command_fingerprint(self):
        session = FakeSession(cmd="codex resume canonical-id", alive=True)
        session.session_id = "canonical-id"
        session.aliases = ("child-id",)
        session.ids = ("canonical-id", "child-id")

        payload = muxd.session_payload("agent", session)

        self.assertEqual("canonical-id", payload["sessionId"])
        self.assertEqual(["child-id"], payload["aliases"])
        self.assertNotIn("cmd", payload)
        self.assertNotIn("cmdSig", payload)
        self.assertNotIn("cwd", payload)

    def test_session_payload_exposes_pending_identity_without_command_data(self):
        session = FakeSession(cmd="codex", alive=True)
        session.identity_pending = True

        payload = muxd.session_payload("fresh", session)

        self.assertTrue(payload["identityPending"])
        self.assertEqual("", payload["sessionId"])
        self.assertNotIn("cmd", payload)

    def test_remote_create_protocol_accepts_only_opaque_saved_command_intent(self):
        valid = {
            "t": "create",
            "s": "safe-tab",
            "rid": "hcabc-1",
            "cols": 140,
            "rows": 40,
            "relaunch": True,
            "heal": False,
        }
        self.assertEqual("", muxd.remote_create_violation(valid))

        invalid = [
            {**valid, "cmd": "codex resume secret"},
            {**valid, "cwd": r"C:\Users\Ahmed\secret"},
            {**valid, "sessionId": "secret"},
            {**valid, "ids": ["secret"]},
            {**valid, "s": "../safe-tab"},
            {**valid, "rid": r"C:\secret"},
            {**valid, "cols": 1000000},
            {**valid, "heal": "true"},
        ]
        for frame in invalid:
            self.assertTrue(
                muxd.remote_create_violation(frame),
                f"remote create frame must be rejected: {frame!r}",
            )

    def test_command_payload_is_distinguishable_from_shell(self):
        cmd = "Write-Output TEST_MARKER"
        payload = muxd.session_payload("agent", FakeSession(cmd=cmd, alive=True, last_out=time.time()))

        self.assertTrue(payload["alive"])
        self.assertTrue(payload["ready"])
        self.assertTrue(payload["hasCommand"])
        self.assertFalse(payload["shellOnly"])
        self.assertEqual(payload["kind"], "command")
        self.assertNotIn("cmdSig", payload)
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

    def test_confirmed_input_reports_actual_pty_write_result(self):
        class Pty:
            def __init__(self):
                self.writes = []

            def write(self, value):
                self.writes.append(value)

        session = muxd.Session.__new__(muxd.Session)
        session.name = "input-confirm"
        session.wq = queue.Queue()
        session._writer_lock = threading.Lock()
        session._writer_thread = None
        session._writer_pty = None
        session.pty = Pty()
        session.dead = False
        worker = session._ensure_writer()
        try:
            ok, detail = session.write_confirmed(b"hello\r")
            self.assertTrue(ok, detail)
            self.assertEqual(["hello\r"], session.pty.writes)

            session.dead = True
            ok, detail = session.write_confirmed(b"lost\r")
            self.assertFalse(ok)
            self.assertIn("not live", detail)
            self.assertEqual(["hello\r"], session.pty.writes)
        finally:
            self.assertTrue(session.stop_input_writer())
            worker.join(timeout=2)

    def test_dormant_session_does_not_start_input_writer(self):
        session = muxd.Session(
            "dormant",
            "",
            tempfile.gettempdir(),
            80,
            24,
            None,
            None,
            spawn_now=False,
        )

        self.assertIsNone(session._writer_thread)
        self.assertTrue(session.stop_input_writer())

    def test_dormant_sessions_do_not_accumulate_writer_threads(self):
        with mock.patch.object(
            muxd.threading,
            "Thread",
            side_effect=AssertionError("dormant sessions must not create threads"),
        ):
            sessions = [
                muxd.Session(
                    f"dormant-{index}",
                    "",
                    tempfile.gettempdir(),
                    80,
                    24,
                    None,
                    None,
                    spawn_now=False,
                )
                for index in range(100)
            ]

        self.assertTrue(all(session._writer_thread is None for session in sessions))

    def test_natural_pty_eof_stops_input_writer(self):
        class EofPty:
            def read(self, _):
                raise EOFError()

        class Loop:
            def call_soon_threadsafe(self, callback, *args):
                callback(*args)

        session = muxd.Session.__new__(muxd.Session)
        session.name = "natural-eof"
        session.wq = queue.Queue()
        session._writer_lock = threading.Lock()
        session._writer_thread = None
        session._writer_pty = None
        session.pty = EofPty()
        session.dead = False
        session.loop = Loop()
        session.outq = queue.Queue()
        worker = session._ensure_writer()

        session._reader(session.pty)

        worker.join(timeout=2)
        self.assertTrue(session.dead)
        self.assertFalse(worker.is_alive())
        self.assertIsNone(session._writer_thread)
        self.assertEqual(("dead", "natural-eof", ""), session.outq.get_nowait())

    def test_retiring_writer_generation_cannot_write_to_replacement_pty(self):
        started = threading.Event()
        release = threading.Event()

        class BlockingPty:
            def __init__(self):
                self.writes = []

            def write(self, value):
                if value == "block":
                    started.set()
                    release.wait(timeout=2)
                self.writes.append(value)

        class RecordingPty:
            def __init__(self):
                self.writes = []

            def write(self, value):
                self.writes.append(value)

        session = muxd.Session.__new__(muxd.Session)
        session.name = "writer-generation"
        session.wq = queue.Queue()
        session._writer_lock = threading.Lock()
        session._writer_thread = None
        session._writer_pty = None
        first_pty = BlockingPty()
        second_pty = RecordingPty()
        session.pty = first_pty
        session.dead = False
        retiring = session._ensure_writer()
        session.wq.put("block")
        self.assertTrue(started.wait(timeout=1))
        session.wq.put("stale")
        self.assertFalse(session.stop_input_writer(timeout=0.01))

        session.pty = second_pty
        replacement = session._ensure_writer()
        try:
            ok, detail = session.write_confirmed(b"fresh")
            self.assertTrue(ok, detail)
            release.set()
            retiring.join(timeout=2)

            self.assertFalse(retiring.is_alive())
            self.assertEqual(["block"], first_pty.writes)
            self.assertEqual(["fresh"], second_pty.writes)
        finally:
            release.set()
            self.assertTrue(session.stop_input_writer())
            replacement.join(timeout=2)

    def test_writer_enqueue_completes_before_stop_sentinel(self):
        put_started = threading.Event()
        release_put = threading.Event()
        stop_finished = threading.Event()

        class BlockingQueue:
            def __init__(self):
                self.items = []
                self.calls = 0

            def put_nowait(self, item):
                self.calls += 1
                if self.calls == 1:
                    put_started.set()
                    release_put.wait(timeout=2)
                self.items.append(item)

        class AliveThread:
            def is_alive(self):
                return True

            def join(self, timeout=None):
                return None

        session = muxd.Session.__new__(muxd.Session)
        session.name = "atomic-enqueue"
        session.pty = object()
        session.dead = False
        session._writer_lock = threading.Lock()
        session._writer_thread = AliveThread()
        session._writer_pty = session.pty
        session.wq = BlockingQueue()

        enqueue = threading.Thread(
            target=lambda: session._enqueue_writer_item("input"),
            daemon=True,
        )

        def stop():
            session.stop_input_writer(timeout=0)
            stop_finished.set()

        stopper = threading.Thread(target=stop, daemon=True)
        enqueue.start()
        self.assertTrue(put_started.wait(timeout=1))
        stopper.start()
        self.assertFalse(stop_finished.wait(timeout=0.05))

        release_put.set()
        enqueue.join(timeout=1)
        stopper.join(timeout=1)

        self.assertEqual(["input", None], session.wq.items)

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

    def test_command_resume_target_is_canonical_when_supplied_identity_conflicts(self):
        canonical, aliases, ids = muxd.resolve_session_identity(
            "codex resume actual-id",
            "claimed-id",
            [],
            [],
        )

        self.assertEqual("actual-id", canonical)
        self.assertEqual(("claimed-id",), aliases)
        self.assertEqual(["actual-id", "claimed-id"], ids)

    def test_owner_reconnect_ignores_only_agent_processes_under_reported_child(self):
        old_descends = muxd._pid_descends_from
        try:
            muxd._pid_descends_from = lambda pid, ancestor: pid == 4321 and ancestor == 4000
            ok, ignored, detail = muxd.owner_reconnect_ignored_live(
                ["session-id"],
                4000,
                (True, {"session-id": 4321}, ""),
            )
            self.assertTrue(ok, detail)
            self.assertEqual({"session-id": 4321}, ignored)

            ok, ignored, detail = muxd.owner_reconnect_ignored_live(
                ["session-id"],
                4999,
                (True, {"session-id": 4321}, ""),
            )
            self.assertFalse(ok)
            self.assertEqual({}, ignored)
            self.assertIn("outside", detail)
        finally:
            muxd._pid_descends_from = old_descends

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
    async def test_async_state_lock_is_reentrant_and_serializes_other_tasks(self):
        lock = muxd.AsyncRLock()
        entered = asyncio.Event()

        async def waiter():
            async with lock:
                entered.set()

        async with lock:
            async with lock:
                task = asyncio.create_task(waiter())
                await asyncio.sleep(0.05)
                self.assertFalse(entered.is_set())

        await asyncio.wait_for(task, timeout=1)
        self.assertTrue(entered.is_set())

    async def test_cancelled_state_transaction_finishes_before_next_writer_enters(self):
        lock = muxd.AsyncRLock()
        first_entered = asyncio.Event()
        release_first = asyncio.Event()
        second_entered = asyncio.Event()
        order = []

        async def first_operation():
            first_entered.set()
            await release_first.wait()
            order.append("first")

        async def second_operation():
            second_entered.set()
            order.append("second")

        caller = asyncio.create_task(
            muxd.run_state_transaction(lock, first_operation)
        )
        await asyncio.wait_for(first_entered.wait(), timeout=1)
        caller.cancel()
        second = asyncio.create_task(
            muxd.run_state_transaction(lock, second_operation)
        )
        await asyncio.sleep(0.05)
        self.assertFalse(second_entered.is_set())

        release_first.set()
        with self.assertRaises(asyncio.CancelledError):
            await caller
        await asyncio.wait_for(second, timeout=1)

        self.assertEqual(["first", "second"], order)

    async def test_terminate_already_stopped_session_stops_input_writer(self):
        session = muxd.Session.__new__(muxd.Session)
        session.name = "already-stopped"
        session.wq = queue.Queue()
        session._writer_lock = threading.Lock()
        session._writer_thread = None
        session._writer_pty = None
        session.pty = None
        session.dead = True
        session.user_killed = False
        session._launch_claim = None
        session.claim_paths = []
        worker = session._ensure_writer()

        ok, detail = await muxd.terminate_session_off_loop(session)

        self.assertTrue(ok, detail)
        worker.join(timeout=2)
        self.assertFalse(worker.is_alive())
        self.assertIsNone(session._writer_thread)

    async def test_relay_tls_context_load_runs_off_loop_and_is_cached(self):
        marker = object()
        calls = []
        main_thread = threading.get_ident()
        old_create = muxd.ssl.create_default_context
        old_context = muxd._RELAY_TLS_CONTEXT
        try:
            muxd._RELAY_TLS_CONTEXT = None

            def slow_create():
                calls.append(threading.get_ident())
                time.sleep(0.25)
                return marker

            muxd.ssl.create_default_context = slow_create
            started = time.perf_counter()
            loading = asyncio.create_task(muxd.relay_tls_context("wss://relay.example/host"))
            await asyncio.sleep(0.05)
            self.assertLess(
                time.perf_counter() - started,
                0.2,
                "TLS certificate loading blocked the event loop",
            )
            self.assertIs(await loading, marker)
            self.assertNotEqual(main_thread, calls[0])
            self.assertIs(
                await muxd.relay_tls_context("wss://relay.example/host"),
                marker,
            )
            self.assertEqual(1, len(calls), "TLS context should be loaded once per muxd process")
            self.assertIsNone(await muxd.relay_tls_context("ws://relay.example/host"))
        finally:
            muxd.ssl.create_default_context = old_create
            muxd._RELAY_TLS_CONTEXT = old_context

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

    async def test_terminate_session_releases_pywinpty_transport_handles(self):
        class Resource:
            def __init__(self):
                self.closed = False

            def close(self):
                self.closed = True

        class Pty:
            pid = 0

            def __init__(self):
                self.fileobj = Resource()
                self._server = Resource()
                self.closed = False
                self.fd = 123

            def terminate(self, force=True):
                return True

        pty = Pty()
        sess = muxd.Session.__new__(muxd.Session)
        sess.name = "resource-stop"
        sess.pty = pty
        sess.dead = False
        sess.user_killed = False
        sess.wq = queue.Queue()

        ok, detail = await muxd.terminate_session_off_loop(sess, timeout=2)

        self.assertTrue(ok, detail)
        self.assertTrue(pty.fileobj.closed)
        self.assertTrue(pty._server.closed)
        self.assertTrue(pty.closed)
        self.assertEqual(-1, pty.fd)

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
            def __init__(
                self, name, cmd, cwd, cols, rows, loop, outq, heal=False,
                spawn_now=True, ids=None, session_id="", aliases=None
            ):
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
