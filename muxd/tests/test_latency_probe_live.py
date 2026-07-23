"""LIVE tests for scripts/latency_probe.py against a real running muxd.

The sibling test_latency_probe.py drives a loopback fake and proves the probe's *logic*. This
file proves the probe actually works: a real muxd session, a real ConPTY, a real echo child, real
binary input frames, real per-sample matching, real cleanup.

Deliberately NO skipTest anywhere. If muxd is unreachable these tests attempt the same
scheduled-task autostart the probe itself does and then FAIL with the exit-3 message, so this
suite can never pass vacuously on a machine where muxd was simply never started.

No test asserts a particular latency. The numbers are the finding (see docs/latency-baseline.md),
not the requirement; only ordering, finiteness and cleanup are contractual.
"""
from __future__ import annotations

import asyncio
import contextlib
import importlib.util
import io
import math
import os
import sys
import tempfile
import time
import unittest

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PROBE_PATH = os.path.join(REPO_ROOT, "scripts", "latency_probe.py")
BASELINE_DOC = os.path.join(REPO_ROOT, "docs", "latency-baseline.md")


def _load_probe():
    spec = importlib.util.spec_from_file_location("latency_probe_live_under_test", PROBE_PATH)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


probe = _load_probe()

PORT = probe.MUXCTL_PORT
PROBE_PREFIX = "latprobe"
# muxd tears a killed session down asynchronously, so give the list a bounded settle window.
# Bounded, not unbounded: a session that really leaked is still here when the window expires.
SETTLE_S = 8.0


def arun(coro):
    return asyncio.run(coro)


def list_sessions():
    """Session names from {"t":"ls"}.

    The sessions live under the "list" key, NOT "sessions" - a leak check reading "sessions"
    silently sees an empty list and passes no matter how many sessions leaked.
    """
    resp = arun(probe.request_json(PORT, {"t": "ls"}, timeout=8.0))
    return [s.get("name") for s in (resp.get("list") or []) if isinstance(s, dict)]


class _LiveMuxdMixin:
    """Every live test refuses to run against an absent muxd - it fails, it never skips."""

    def require_live_muxd(self):
        try:
            # request_json performs the probe's own scheduled-task autostart before giving up,
            # so reaching the failure branch means autostart was tried and did not help.
            return list_sessions()
        except probe.ProbeError as e:
            self.fail(
                "live muxd is required and could not be reached (probe exit %d, expected %d): %s"
                % (e.code, probe.EXIT_MUXD_UNREACHABLE, e))
        except Exception as e:  # transport/protocol failure is still a hard failure, not a skip
            self.fail("live muxd is required but the control socket at %s failed: %r"
                      % (probe.local_url(PORT), e))

    def run_probe(self, argv):
        """Run the real CLI in-process. Returns (exit_code, stdout)."""
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            code = probe.main(argv)
        return code, buf.getvalue()

    def assert_no_probe_sessions(self, baseline, what):
        """After `what`, no latprobe* session may remain and the count must be back to baseline."""
        deadline = time.monotonic() + SETTLE_S
        leaked, names = None, []
        while True:
            names = list_sessions()
            leaked = [n for n in names if n and n.startswith(PROBE_PREFIX)]
            if not leaked and len(names) == len(baseline):
                return names
            if time.monotonic() >= deadline:
                break
            time.sleep(0.25)
        self.assertEqual(
            leaked, [],
            "%s leaked probe session(s) %s; muxd sessions now: %s" % (what, leaked, names))
        self.assertEqual(
            len(names), len(baseline),
            "%s changed the muxd session count %d -> %d (baseline %s, now %s)"
            % (what, len(baseline), len(names), baseline, names))


class LiveLocalSeriesTests(_LiveMuxdMixin, unittest.TestCase):
    """A real 200-sample series against a real muxd session."""

    def test_two_hundred_real_round_trips(self):
        self.require_live_muxd()

        real_series = probe.run_local_series
        captured = []

        async def spy(*args, **kwargs):
            rtts = await real_series(*args, **kwargs)
            captured.append(list(rtts))
            return rtts

        probe.run_local_series = spy
        try:
            with tempfile.TemporaryDirectory() as tmp:
                artifact = os.path.join(tmp, "live.json")
                code, out = self.run_probe(
                    ["--local", "--samples", "200", "--timeout", "180", "--json-out", artifact])
                self.assertTrue(os.path.isfile(artifact), "--json-out artifact was not written")
        finally:
            probe.run_local_series = real_series

        self.assertEqual(code, probe.EXIT_OK,
                         "live 200-sample series did not succeed; stdout:\n%s" % out)
        self.assertEqual(len(captured), 1, "run_local_series was not called exactly once")
        rtts = captured[0]

        self.assertEqual(len(rtts), 200,
                         "expected exactly 200 measured round trips, got %d" % len(rtts))
        for i, v in enumerate(rtts):
            self.assertTrue(math.isfinite(v), "sample %d is not finite: %r" % (i, v))
            self.assertGreater(v, 0.0, "sample %d is not strictly positive: %r" % (i, v))

        p50 = probe.percentile(rtts, 0.50)
        p95 = probe.percentile(rtts, 0.95)
        p99 = probe.percentile(rtts, 0.99)
        self.assertLessEqual(p50, p95, "p50 %r > p95 %r" % (p50, p95))
        self.assertLessEqual(p95, p99, "p95 %r > p99 %r" % (p95, p99))

        for field in ("p50=", "p95=", "p99="):
            self.assertIn(field, out, "probe output did not report %s:\n%s" % (field, out))
        self.assertIn("n=200", out)


class LiveBudgetEnforcementTests(_LiveMuxdMixin, unittest.TestCase):
    """Enforcement proven in BOTH directions without hard-coding a machine-specific threshold."""

    SAMPLES = "30"

    def test_impossible_budget_exits_over_budget(self):
        self.require_live_muxd()
        code, out = self.run_probe(["--local", "--samples", self.SAMPLES, "--timeout", "120",
                                    "--budget-p95-ms", "0.0001"])
        self.assertEqual(code, probe.EXIT_OVER_BUDGET,
                         "a 0.0001ms p95 budget must fail a real series; stdout:\n%s" % out)
        self.assertIn("OVER", out)

    def test_generous_budget_exits_ok(self):
        self.require_live_muxd()
        code, out = self.run_probe(["--local", "--samples", self.SAMPLES, "--timeout", "120",
                                    "--budget-p95-ms", "100000"])
        self.assertEqual(code, probe.EXIT_OK,
                         "a 100000ms p95 budget must pass a real series; stdout:\n%s" % out)
        self.assertIn("OK", out)


class LiveSessionHygieneTests(_LiveMuxdMixin, unittest.TestCase):
    """'Always kill the probe session on every exit path' - proven on all three exit paths."""

    SAMPLES = "20"

    def test_successful_run_leaves_no_session(self):
        baseline = self.require_live_muxd()
        code, out = self.run_probe(["--local", "--samples", self.SAMPLES, "--timeout", "120"])
        self.assertEqual(code, probe.EXIT_OK, "stdout:\n%s" % out)
        self.assert_no_probe_sessions(baseline, "a successful run (exit 0)")

    def test_over_budget_run_leaves_no_session(self):
        baseline = self.require_live_muxd()
        code, out = self.run_probe(["--local", "--samples", self.SAMPLES, "--timeout", "120",
                                    "--budget-p95-ms", "0.0001"])
        self.assertEqual(code, probe.EXIT_OVER_BUDGET, "stdout:\n%s" % out)
        self.assert_no_probe_sessions(baseline, "an over-budget run (exit 1)")

    def test_wall_clamp_run_leaves_no_session(self):
        baseline = self.require_live_muxd()
        # A wall far shorter than a 200-sample series: the clamp trips mid-flight, while the
        # session exists and is attached. That is the exit path most likely to leak.
        code, out = self.run_probe(["--local", "--samples", "200", "--timeout", "0.6"])
        self.assertEqual(code, probe.EXIT_TIMEOUT,
                         "a 0.6s wall on a 200-sample series must trip the clamp; stdout:\n%s"
                         % out)
        self.assert_no_probe_sessions(baseline, "a wall-clamp run (exit 5)")


>
    """The measured numbers must be recorded, not just produced and thrown away."""

    def test_baseline_doc_records_the_measurement(self):
        self.assertTrue(os.path.isfile(BASELINE_DOC),
                        "missing measured baseline document at %s" % BASELINE_DOC)
        with open(BASELINE_DOC, "r", encoding="utf-8") as fh:
            text = fh.read()
        for token in ("p50", "p95", "p99", "DEFERRED"):
            self.assertIn(token, text, "%s does not mention %r" % (BASELINE_DOC, token))


if __name__ == "__main__":
    unittest.main()
