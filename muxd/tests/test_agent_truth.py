"""Process truth: does muxd report whether the agent process is ACTUALLY alive?

The heuristic ladder in `session_agent_status` reads terminal bytes - it can only ever
guess. These tests drive the real thing: a real child process, really killed, and a
deliberately wedged probe. The load-bearing claims are (1) truth tracks the OS, and
(2) truth is never allowed to make a status call slow.
"""
import importlib
import os
import subprocess
import sys
import threading
import time
import unittest


muxd = importlib.import_module("muxd")

POLL = 0.2


class TruthSession:
    """The parts of a mux session that `session_payload` actually touches."""

    def __init__(self, child_pid=0, child_start_token="", cmd="codex --resume", alive=True):
        self.name = "truth"
        self.cmd = cmd
        self.cwd = r"Z:\tmp"
        self.created = time.time() - 600
        self.last_out = time.time() - 600
        self.cols = 120
        self.rows = 30
        self.heal = False
        self.local = set()
        self.owner = False
        self.session_id = "truth-1"
        self.aliases = []
        self.identity_pending = False
        self.lifecycle = "active"
        self.child_pid = int(child_pid)
        self.child_start_token = str(child_start_token)
        self._alive = alive

    def alive(self):
        return self._alive

    def tail_text(self, nbytes=None):
        return "working on it\n"


def reset_truth_cache():
    with muxd._AGENT_TRUTH_LOCK:
        muxd._AGENT_TRUTH_CACHE.clear()
        muxd._AGENT_TRUTH_INFLIGHT = 0


def drain_inflight(timeout=20.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        with muxd._AGENT_TRUTH_LOCK:
            if muxd._AGENT_TRUTH_INFLIGHT <= 0:
                return True
        time.sleep(0.05)
    return False


def poll_payload(sess, predicate, timeout):
    """Repeatedly build a session payload until `predicate` holds. Returns the payload."""
    deadline = time.monotonic() + timeout
    payload = muxd.session_payload(sess.name, sess)
    while time.monotonic() < deadline:
        if predicate(payload):
            return payload
        time.sleep(POLL)
        payload = muxd.session_payload(sess.name, sess)
    return payload


class AgentTruthTests(unittest.TestCase):
    def setUp(self):
        reset_truth_cache()
        self.addCleanup(reset_truth_cache)

    # ---- the additive-only contract ------------------------------------------------

    def test_agent_truth_is_an_additive_capability_on_protocol_4(self):
        self.assertEqual(muxd.PROTOCOL, 4, "the relay tests protocol equality; a bump bricks pairing")
        self.assertIn("agentTruth", muxd.CAPS)

    def test_probe_budget_stays_inside_the_declared_discipline(self):
        self.assertGreaterEqual(muxd.AGENT_TRUTH_TTL, 10.0)
        self.assertLessEqual(muxd.AGENT_TRUTH_PROBE_TIMEOUT, 3.0)
        self.assertLessEqual(muxd._AGENT_TRUTH_EXECUTOR._max_workers, 4)

    def test_session_without_a_child_pid_reports_heuristic(self):
        payload = muxd.session_payload("truth", TruthSession(child_pid=0))
        self.assertEqual(payload["agentStateSource"], "heuristic")
        self.assertEqual(payload["agentTruth"]["procAlive"], False)
        self.assertEqual(payload["agentTruth"]["checkedUtc"], "")
        self.assertIn("agentState", payload)

    # ---- a real process, really alive, really killed --------------------------------

    def test_live_child_reads_as_process_truth_and_flips_false_when_killed(self):
        child = subprocess.Popen(
            [sys.executable, "-c", "import time; time.sleep(60)"],
            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )
        self.addCleanup(lambda: child.poll() is None and child.kill())
        sess = TruthSession(child_pid=child.pid,
                            child_start_token=muxd._process_start_token(child.pid))
        self.assertTrue(sess.child_start_token, "could not fence the child pid by creation time")

        payload = poll_payload(sess, lambda p: p["agentStateSource"] == "process", timeout=20)
        self.assertEqual(payload["agentStateSource"], "process",
                         f"probe never landed: {payload.get('agentTruth')}")
        truth = payload["agentTruth"]
        self.assertTrue(truth["procAlive"], truth)
        self.assertTrue(truth["checkedUtc"].endswith("Z"), truth)
        self.assertIn("python", truth["exe"].lower(), truth)
        self.assertIsInstance(truth["cpuActiveRecent"], bool)
        # Truth is additive - the heuristic ladder still answers.
        self.assertIn(payload["agentState"], ("working", "attention", "stopped", "neutral", "dormant"))

        child.kill()
        child.wait(timeout=10)
        killed_at = time.monotonic()
        payload = poll_payload(sess, lambda p: p["agentTruth"]["procAlive"] is False,
                               timeout=muxd.AGENT_TRUTH_TTL + 15)
        self.assertFalse(payload["agentTruth"]["procAlive"],
                         "process truth still claims a killed process is alive")
        self.assertLessEqual(time.monotonic() - killed_at, muxd.AGENT_TRUTH_TTL + 15)
        self.assertTrue(drain_inflight())

    def test_results_are_cached_so_repeated_payloads_do_not_reprobe(self):
        calls = []
        real = muxd._agent_truth_probe

        def counting_probe(pid, start_token, previous=None):
            calls.append(pid)
            return {"procAlive": True, "cpuActiveRecent": False, "exe": "agent.exe",
                    "checkedUtc": "2026-07-23T00:00:00Z"}, 1, int(pid)

        muxd._agent_truth_probe = counting_probe
        self.addCleanup(lambda: setattr(muxd, "_agent_truth_probe", real))
        sess = TruthSession(child_pid=os.getpid(), child_start_token="")
        payload = poll_payload(sess, lambda p: p["agentStateSource"] == "process", timeout=10)
        self.assertEqual(payload["agentStateSource"], "process")
        for _ in range(25):
            muxd.session_payload(sess.name, sess)
        self.assertTrue(drain_inflight())
        self.assertEqual(len(calls), 1, f"cache did not hold for {muxd.AGENT_TRUTH_TTL}s: {calls}")

    # ---- fault injection: a probe that never returns ---------------------------------

    def test_hung_probe_leaves_status_responsive_and_degrades_to_heuristic(self):
        released = threading.Event()
        entered = threading.Event()
        real = muxd._agent_truth_probe

        def hung_probe(pid, start_token, previous=None):
            entered.set()
            released.wait(60)          # wedged: the OS never answers
            return dict(muxd.AGENT_TRUTH_UNKNOWN), 0, 0

        muxd._agent_truth_probe = hung_probe
        self.addCleanup(drain_inflight)
        self.addCleanup(released.set)
        self.addCleanup(lambda: setattr(muxd, "_agent_truth_probe", real))

        sess = TruthSession(child_pid=os.getpid(), child_start_token="")
        started = time.monotonic()
        payload = muxd.session_payload(sess.name, sess)
        first = time.monotonic() - started
        self.assertTrue(entered.wait(5), "the probe was never scheduled onto the executor")

        # The probe is now wedged in a worker thread. Every further status call must be
        # instant, and must say so honestly.
        started = time.monotonic()
        for _ in range(50):
            payload = muxd.session_payload(sess.name, sess)
        elapsed = time.monotonic() - started

        self.assertLess(first, 1.0, f"the first status call blocked on a probe ({first:.3f}s)")
        self.assertLess(elapsed, 1.0, f"status calls blocked behind a wedged probe ({elapsed:.3f}s)")
        self.assertEqual(payload["agentStateSource"], "heuristic")
        self.assertEqual(payload["agentTruth"], dict(muxd.AGENT_TRUTH_UNKNOWN))
        # The heuristic answer is untouched and still usable.
        self.assertTrue(payload["agentLabel"])
        with muxd._AGENT_TRUTH_LOCK:
            self.assertLessEqual(muxd._AGENT_TRUTH_INFLIGHT, muxd.AGENT_TRUTH_MAX_INFLIGHT)


    # ---- timeout enforcement: capacity recovery without manual release ----------

    def test_hung_probe_recovers_capacity_after_timeout(self):
        """Two hung probes must not permanently exhaust the executor.

        After AGENT_TRUTH_PROBE_TIMEOUT the inflight count must recover on its
        own -- no manual event release -- so a fresh session can still be probed.
        This is the contract the adversarial review called out: a genuinely hung
        syscall must not starve every other session forever.
        """
        released = threading.Event()
        entered1 = threading.Event()
        entered2 = threading.Event()
        real = muxd._agent_truth_probe

        def hung_probe(pid, start_token, previous=None):
            if not entered1.is_set():
                entered1.set()
            elif not entered2.is_set():
                entered2.set()
            released.wait(60)          # wedged; never returns until cleanup
            return dict(muxd.AGENT_TRUTH_UNKNOWN), 0, 0

        muxd._agent_truth_probe = hung_probe
        self.addCleanup(drain_inflight)
        self.addCleanup(released.set)
        self.addCleanup(lambda: setattr(muxd, "_agent_truth_probe", real))

        # Session 1 -- trigger first hung probe
        sess1 = TruthSession(child_pid=os.getpid(), child_start_token="tok1")
        muxd.session_payload(sess1.name, sess1)
        self.assertTrue(entered1.wait(5), "first probe was never scheduled")

        # Session 2 (different cache key) -- trigger second hung probe
        sess2 = TruthSession(child_pid=os.getpid() + 1, child_start_token="tok2")
        muxd.session_payload(sess2.name, sess2)
        self.assertTrue(entered2.wait(5), "second probe was never scheduled")

        # Both executor workers are now wedged.  A third unique session
        # can not even be scheduled until inflight drops below the cap.
        with muxd._AGENT_TRUTH_LOCK:
            self.assertEqual(muxd._AGENT_TRUTH_INFLIGHT, 2,
                             "expected both inflight slots consumed")

        # Wait for the probe timeout + a small buffer.  The timeout is
        # enforced by _agent_truth_refresh's daemon-thread .join, so
        # inflight MUST recover here without anyone calling released.set().
        time.sleep(muxd.AGENT_TRUTH_PROBE_TIMEOUT + 1.0)

        with muxd._AGENT_TRUTH_LOCK:
            self.assertEqual(muxd._AGENT_TRUTH_INFLIGHT, 0,
                             "inflight count did not recover after probe timeout -- "
                             "the timeout is not enforced or the counter is stuck")

        # Now restore the real probe and verify a fresh session works.
        muxd._agent_truth_probe = real
        sess3 = TruthSession(child_pid=os.getpid(), child_start_token="tok3")
        payload3 = poll_payload(
            sess3, lambda p: p["agentStateSource"] == "process", timeout=20)
        self.assertEqual(payload3["agentStateSource"], "process",
                         "a fresh session could not be probed after timeout recovery")
        self.assertTrue(drain_inflight())


class SnapshotIsTakenOncePerTickTests(unittest.TestCase):
    """The snapshot must cost ONE call per status read, not one per SESSION in it.

    This is the per-session latency, and it is the difference between a responsive terminal and a
    multi-second one. `sess_list()` -> `session_payload()` -> `session_agent_truth()` reaches
    `_process_rows()` once for every session, and one snapshot over ~570 processes costs 17.2 ms p50
    on this box. Measured live: `ls`/`create`/`kill` ran 1.2-1.7 s p50 with a 43-48 s tail, while the
    identical call with ZERO sessions returned in 4.6 ms -- the per-session term is the whole delay.

    It reaches further than the read: the relay confirms a create by polling `ls` every 100 ms
    (relay/server.js waitForHostState), so a per-session cost inside `ls` is a per-session cost
    inside create.

    These tests drive the REAL function and count real snapshots through the fake kernel32, so a
    future edit that removes the cache fails here rather than silently restoring the delay.
    """

    def setUp(self):
        self._saved = (muxd.PROCESS_ROWS_TTL, dict(muxd._process_rows_cache))
        muxd._process_rows_cache["at"] = 0.0
        muxd._process_rows_cache["rows"] = None

        def restore():
            muxd.PROCESS_ROWS_TTL = self._saved[0]
            muxd._process_rows_cache.update(self._saved[1])
        self.addCleanup(restore)

    def _counting_kernel32(self):
        """Counts CreateToolhelp32Snapshot calls, so 'how many snapshots' is directly observable."""
        fake = type("CountingKernel32", (), {})()
        fake.snapshots = 0
        fake.closed = []
        fake._rows = [101, 102, 103]

        def _emit(entry_ptr, i):
            if i >= len(fake._rows):
                return 0
            entry = entry_ptr._obj
            entry.th32ProcessID = fake._rows[i]
            entry.th32ParentProcessID = 1
            entry.szExeFile = "fake.exe"
            return 1

        def create_snapshot(flags, pid):
            fake.snapshots += 1
            return 0x9000 + fake.snapshots

        def close_handle(h):
            fake.closed.append(h)
            return 1

        def process32_first(h, entry_ptr):
            fake._i = 0
            muxd.ctypes.set_last_error(0)
            return _emit(entry_ptr, 0)

        def process32_next(h, entry_ptr):
            fake._i += 1
            muxd.ctypes.set_last_error(0)
            return _emit(entry_ptr, fake._i)

        fake.CreateToolhelp32Snapshot = create_snapshot
        fake.CloseHandle = close_handle
        fake.Process32FirstW = process32_first
        fake.Process32NextW = process32_next
        return fake

    def _install(self, fake):
        real = muxd.ctypes.WinDLL
        muxd.ctypes.WinDLL = lambda *a, **k: fake
        self.addCleanup(lambda: setattr(muxd.ctypes, "WinDLL", real))

    def test_many_calls_inside_one_tick_take_exactly_one_snapshot(self):
        """N per-session calls in one `sess_list()` must cost 1 snapshot, not N."""
        fake = self._counting_kernel32()
        self._install(fake)
        for _ in range(20):
            rows = muxd._process_rows()
            self.assertEqual([r[0] for r in rows], [101, 102, 103])
        self.assertEqual(fake.snapshots, 1,
                         f"20 calls took {fake.snapshots} snapshots; the per-session cost is back")
        self.assertEqual(fake.closed, [0x9001], "the single snapshot handle must still be released")

    def test_the_cache_expires_so_the_process_table_is_never_frozen(self):
        """A TTL that never expires would report a stale process table forever.

        The rows must be re-read once the window passes -- a session that died is only noticed by
        taking a new snapshot.
        """
        fake = self._counting_kernel32()
        self._install(fake)
        muxd.PROCESS_ROWS_TTL = 0.05
        muxd._process_rows()
        self.assertEqual(fake.snapshots, 1)
        time.sleep(0.08)                       # past the window
        muxd._process_rows()
        self.assertEqual(fake.snapshots, 2, "the cache never expired; the process table would freeze")

    def test_ttl_zero_restores_one_snapshot_per_call(self):
        """The kill switch must reproduce the pre-change behaviour exactly.

        If this passes with a cache still in place, the knob does not do what it says and an
        operator diagnosing a suspect cache has no way to turn it off.
        """
        fake = self._counting_kernel32()
        self._install(fake)
        muxd.PROCESS_ROWS_TTL = 0.0
        for _ in range(5):
            muxd._process_rows()
        self.assertEqual(fake.snapshots, 5, "PROCESS_ROWS_TTL=0 must take a snapshot every call")

    def test_a_raised_snapshot_error_still_propagates_and_caches_nothing(self):
        """A genuine fault must stay visible AND leave the cache empty.

        Caching [] here would be worse than not caching at all: the caller would see a valid-looking
        process table with no descendants and report every live agent as gone.
        """
        muxd._process_rows_cache["at"] = 0.0
        muxd._process_rows_cache["rows"] = None
        boom = self._counting_kernel32()
        def first_raises(h, entry_ptr):
            muxd.ctypes.set_last_error(1455)   # a real fault, not the transient OOM code
            return 0
        boom.Process32FirstW = first_raises
        self._install(boom)
        with self.assertRaises(OSError):
            muxd._process_rows()
        self.assertIsNone(muxd._process_rows_cache["rows"],
                          "a raised error left rows cached; the fault would be masked")


class MutationIsAnnouncedImmediatelyTests(unittest.TestCase):
    """A create or a kill must push the new session list at once, not on the next 5 s pump.

    `pump_status` refreshes the relay's `hostSessions` on a fixed 5 s cadence and is the ONLY thing
    that does. The relay's create and erase paths confirm against that map, so without an immediate
    push a mutation that had ALREADY happened stayed invisible for whatever was left of the timer:

      * erase - the relay's delete handler sends the kill and then polls `waitForHostState(...6000)`
        for the session to disappear. muxd removed it synchronously, then announced it up to 5 s
        later, which is the "erasing a terminal takes a handful of seconds" report.
      * create - the relay queues the start over the app-command bridge and then polls
        `waitForHostState(..., 15000)` for the session to APPEAR, again against its own map.

    Both handlers now push the list on the same websocket before returning, so the confirmation is
    immediate. Structural, because the timing is the whole point: driving a real relay link would
    test the transport, not the cadence this guards. The claim being pinned is the ORDER of the
    pushes inside each handler -- the list must be sent after the result frame, in the same branch.
    """

    @classmethod
    def setUpClass(cls):
        import pathlib
        cls.src = (pathlib.Path(__file__).resolve().parent.parent
                   / "muxd.py").read_text(encoding="utf-8")

    def _handler_span(self, start_marker, end_marker):
        start = self.src.index(start_marker)
        end = self.src.index(end_marker, start)
        return self.src[start:end]

    def test_the_kill_handler_announces_the_list_before_it_returns(self):
        span = self._handler_span('elif t == "kill":', "finally:\n                        for tk in tasks")
        killed = span.index('"t": "killed"')
        push = span.find('{"t": "sessions", "list": sess_list()}', killed)
        self.assertGreater(push, killed,
                           "the kill handler does not push the new list after reporting the kill; "
                           "the relay would wait out pump_status (up to 5 s) to see the removal")

    def test_the_create_handler_announces_the_list_on_success(self):
        span = self._handler_span('if t == "create":', 'elif t == "heal"')
        ok_branch = span.index('"t": "createResult"')
        push = span.find('{"t": "sessions", "list": sess_list()}', ok_branch)
        self.assertGreater(push, ok_branch,
                           "the create handler does not push the new list after a successful "
                           "create; the relay's waitForHostState would not see the session appear "
                           "until the next pump_status tick")

    def test_the_create_push_is_on_the_success_branch_only(self):
        """A refused create has no new list to announce, and must not pretend otherwise.

        Pushing on the refusal path would tell the relay a session exists that muxd just declined to
        create -- the relay's confirmation poll reads that list as authority.
        """
        span = self._handler_span('if t == "create":', 'elif t == "heal"')
        refusal = span.index('"ok": False')
        success = span.index('"ok": True')
        push = span.find('{"t": "sessions", "list": sess_list()}', refusal)
        self.assertGreater(push, success,
                           "the list push sits in the refusal branch; a refused create must not "
                           "announce a session list")


class SnapshotHandleDisciplineTests(unittest.TestCase):
    """The snapshot handle must be released on EVERY exit path, including the errors.

    The failure this guards is not "an exception was raised" -- it is that a snapshot handle
    is a KERNEL HANDLE, and leaving one behind once per probe accumulated 682 handles / 76
    threads and parked muxd's event loop for 30-126s. A raised WinError is a bad day; a
    leaked handle is the black terminal.
    """

    def _fake_kernel32(self, handle, first_error=0, next_error=0, rows=(101, 102)):
        """A kernel32 stand-in: real Process32First/Next semantics, injectable error codes."""
        fake = type("FakeKernel32", (), {})()
        fake.closed = []
        fake._rows = list(rows)
        fake._i = 0

        def _emit(entry_ptr):
            if fake._i >= len(fake._rows):
                return 0
            entry = entry_ptr._obj
            entry.th32ProcessID = fake._rows[fake._i]
            entry.th32ParentProcessID = 1
            entry.szExeFile = "fake.exe"
            fake._i += 1
            return 1

        def create_snapshot(flags, pid):
            return handle

        def close_handle(h):
            fake.closed.append(h)
            return 1

        def process32_first(h, entry_ptr):
            muxd.ctypes.set_last_error(first_error)
            return 0 if first_error else _emit(entry_ptr)

        def process32_next(h, entry_ptr):
            muxd.ctypes.set_last_error(next_error)
            return 0 if next_error else _emit(entry_ptr)

        fake.CreateToolhelp32Snapshot = create_snapshot
        fake.CloseHandle = close_handle
        fake.Process32FirstW = process32_first
        fake.Process32NextW = process32_next
        return fake

    def _rows_with(self, fake):
        real = muxd.ctypes.WinDLL
        muxd.ctypes.WinDLL = lambda *a, **k: fake
        self.addCleanup(lambda: setattr(muxd.ctypes, "WinDLL", real))
        return muxd._process_rows()

    def test_out_of_memory_first_entry_degrades_and_still_closes_the_handle(self):
        """ERROR_NOT_ENOUGH_MEMORY on the first call is transient capacity pressure.

        It must return "no rows" -- NOT raise -- and the handle must still be closed. Before
        the guard, this path raised out of `_process_rows` on the probe thread and skipped
        CloseHandle, stranding a kernel handle for the life of the process.
        """
        fake = self._fake_kernel32(0x1234, first_error=muxd.ERROR_NOT_ENOUGH_MEMORY)
        self.assertEqual(self._rows_with(fake), [])
        self.assertEqual(fake.closed, [0x1234], "the snapshot handle was not released")

    def test_out_of_memory_midwalk_keeps_partial_rows_and_closes_the_handle(self):
        fake = self._fake_kernel32(0x2345, next_error=muxd.ERROR_NOT_ENOUGH_MEMORY,
                                   rows=(201, 202, 203))
        rows = self._rows_with(fake)
        self.assertEqual([r[0] for r in rows], [201], "partial rows were discarded")
        self.assertEqual(fake.closed, [0x2345], "the snapshot handle was not released")

    def test_empty_snapshot_is_a_valid_answer_not_an_error(self):
        fake = self._fake_kernel32(0x3456, first_error=18)  # ERROR_NO_MORE_FILES
        self.assertEqual(self._rows_with(fake), [])
        self.assertEqual(fake.closed, [0x3456])

    def test_a_genuine_error_still_raises_but_never_leaks_the_handle(self):
        """A real fault must stay visible -- and must not become a leak on its way out."""
        fake = self._fake_kernel32(0x4567, first_error=1455)
        with self.assertRaises(OSError):
            self._rows_with(fake)
        self.assertEqual(fake.closed, [0x4567],
                         "an error path leaked the snapshot handle")


class AbandonedProbePressureTests(unittest.TestCase):
    """A timed-out probe is unreapable, so it must at least be COUNTED.

    The counter is the only signal that muxd is drifting toward the stall that blacked out the
    terminals. Silently abandoning 400 probes over one run is how this went unnoticed for days.
    """

    def setUp(self):
        self._saved = (muxd._AGENT_TRUTH_ABANDONED, muxd._AGENT_TRUTH_ABANDONED_WARNED,
                       muxd.AGENT_TRUTH_PROBE_TIMEOUT, muxd._agent_truth_probe)
        muxd.AGENT_TRUTH_PROBE_TIMEOUT = 0.25     # keep the test fast; the guard is the join
        self.logs = []
        real_log = muxd.log
        muxd.log = lambda msg, *a, **k: self.logs.append(str(msg))
        def restore():
            (muxd._AGENT_TRUTH_ABANDONED, muxd._AGENT_TRUTH_ABANDONED_WARNED,
             muxd.AGENT_TRUTH_PROBE_TIMEOUT, muxd._agent_truth_probe) = self._saved
            muxd.log = real_log
        self.addCleanup(restore)
        reset_truth_cache()

    def _abandon_one_probe(self, pid):
        released = threading.Event()
        self.addCleanup(released.set)

        def hung_probe(p, start_token, previous=None):
            released.wait(30)
            return dict(muxd.AGENT_TRUTH_UNKNOWN), 0, 0

        muxd._agent_truth_probe = hung_probe
        muxd._agent_truth_refresh(("k", pid, "tok"), pid, "tok", None)

    def test_a_timed_out_probe_is_counted_and_reported(self):
        before = muxd._AGENT_TRUTH_ABANDONED
        self._abandon_one_probe(os.getpid())
        self.assertEqual(muxd._AGENT_TRUTH_ABANDONED, before + 1,
                         "a timed-out probe was abandoned without being counted")
        self.assertTrue(any("abandoned, not reaped" in m for m in self.logs),
                        f"the abandonment was not reported: {self.logs}")

    def test_the_pressure_warning_fires_once_per_step_not_per_probe(self):
        muxd._AGENT_TRUTH_ABANDONED = muxd._AGENT_TRUTH_ABANDONED_WARN_AT - 1
        muxd._AGENT_TRUTH_ABANDONED_WARNED = 0
        self._abandon_one_probe(os.getpid())
        warnings = [m for m in self.logs if "probe threads are accumulating" in m]
        self.assertEqual(len(warnings), 1, f"expected one warning at the threshold: {self.logs}")
        self.assertIn("MuxdSessionHost", warnings[0])

        self.logs.clear()
        self._abandon_one_probe(os.getpid() + 1)   # one step below WARN_STEP: stay quiet
        self.assertEqual([m for m in self.logs if "probe threads are accumulating" in m], [],
                         "the warning repeated per probe instead of per step")


if __name__ == "__main__":
    unittest.main()
