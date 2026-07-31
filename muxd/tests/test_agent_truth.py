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


if __name__ == "__main__":
    unittest.main()
