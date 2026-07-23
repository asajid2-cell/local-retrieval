"""Unit tests for scripts/latency_probe.py against a loopback fake.

Covers the three things the probe can silently get wrong:
  * sample matching  - a sample's RTT must belong to that sample's marker and nothing else
  * percentile math  - p50/p95/p99 must be right, or every budget verdict is noise
  * timeout + kill   - a hung child must trip the clamp AND still kill the probe session

Nothing here touches a real muxd: every transport is a fake, so this runs anywhere.
"""
from __future__ import annotations

import asyncio
import base64
import collections
import contextlib
import importlib.util
import io
import json
import os
import sys
import tempfile
import unittest

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PROBE_PATH = os.path.join(REPO_ROOT, "scripts", "latency_probe.py")


def _load_probe():
    spec = importlib.util.spec_from_file_location("latency_probe_under_test", PROBE_PATH)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


probe = _load_probe()


def arun(coro):
    return asyncio.run(coro)


# --------------------------------------------------------------------------- fakes

class LoopbackTransport:
    """Fake muxd transport with a timed delivery queue.

    `send` queues the echo to become readable `delay_s` from now; `recv(timeout)` returns the
    next chunk that is due, or b"" once the timeout expires. `mangle` rewrites one marker into
    the chunk sequence the child would actually emit (split, noisy, wrong, or nothing).
    """

    def __init__(self, delay_s=0.0, mangle=None, preamble=()):
        self.delay_s = delay_s
        self.mangle = mangle
        self.sent = []
        self._q = collections.deque()
        for chunk in preamble:
            self._q.append((-1.0, bytes(chunk)))  # readable immediately

    @staticmethod
    def _now():
        return asyncio.get_running_loop().time()

    async def send(self, data):
        data = bytes(data)
        self.sent.append(data)
        chunks = self.mangle(data) if self.mangle is not None else [data]
        at = self._now() + self.delay_s
        for chunk in chunks:
            self._q.append((at, bytes(chunk)))

    async def recv(self, timeout):
        deadline = self._now() + max(0.0, timeout)
        while True:
            now = self._now()
            if self._q and self._q[0][0] <= now:
                return self._q.popleft()[1]
            if now >= deadline:
                return b""
            wait = deadline - now
            if self._q:
                wait = min(wait, self._q[0][0] - now)
            await asyncio.sleep(max(wait, 0.0005))


class FakeWs:
    """Fake raw websocket: `recv()` blocks until a frame is due (no timeout of its own)."""

    def __init__(self, echo=True, json_output=False, ready=True):
        self.echo = echo
        self.json_output = json_output
        self.sent = []
        self.closed = False
        self._q = collections.deque()
        if ready:
            self._q.append(probe.READY_TOKEN.encode("ascii") + b"\r\n")

    def push(self, frame):
        self._q.append(frame)

    async def send(self, data):
        self.sent.append(data)
        if isinstance(data, (bytes, bytearray)) and self.echo:
            if self.json_output:
                self._q.append(json.dumps(
                    {"t": "o", "d": base64.b64encode(bytes(data)).decode("ascii")}))
            else:
                self._q.append(bytes(data))

    async def recv(self):
        while not self._q:
            await asyncio.sleep(0.0005)
        return self._q.popleft()


class _FakeConnect:
    def __init__(self, ws):
        self.ws = ws

    async def __aenter__(self):
        return self.ws

    async def __aexit__(self, *exc):
        self.ws.closed = True
        return False


class FakeWebsocketsModule:
    def __init__(self, ws):
        self.ws = ws
        self.connects = 0

    def connect(self, *args, **kwargs):
        self.connects += 1
        return _FakeConnect(self.ws)


class ProbePatch:
    """Swap module attributes on the probe for the duration of a test."""

    def __init__(self, testcase, **attrs):
        for name, value in attrs.items():
            original = getattr(probe, name)
            testcase.addCleanup(setattr, probe, name, original)
            setattr(probe, name, value)


# --------------------------------------------------------------------------- percentiles

class PercentileTests(unittest.TestCase):
    def test_linear_interpolation_matches_hand_computed_values(self):
        xs = [10.0, 20.0, 30.0, 40.0, 50.0]
        self.assertAlmostEqual(probe.percentile(xs, 0.50), 30.0)
        self.assertAlmostEqual(probe.percentile(xs, 0.95), 48.0)   # pos 3.8
        self.assertAlmostEqual(probe.percentile(xs, 0.99), 49.6)   # pos 3.96
        self.assertAlmostEqual(probe.percentile(xs, 0.0), 10.0)
        self.assertAlmostEqual(probe.percentile(xs, 1.0), 50.0)

    def test_hundred_samples(self):
        xs = list(range(1, 101))
        self.assertAlmostEqual(probe.percentile(xs, 0.50), 50.5)
        self.assertAlmostEqual(probe.percentile(xs, 0.95), 95.05)
        self.assertAlmostEqual(probe.percentile(xs, 0.99), 99.01)

    def test_input_order_does_not_matter(self):
        forward = probe.percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 0.95)
        shuffled = probe.percentile([7, 1, 9, 3, 10, 2, 8, 4, 6, 5], 0.95)
        self.assertAlmostEqual(forward, shuffled)

    def test_single_sample_is_every_percentile(self):
        for q in (0.0, 0.5, 0.95, 0.99, 1.0):
            self.assertAlmostEqual(probe.percentile([4.2], q), 4.2)

    def test_rejects_empty_and_out_of_range(self):
        with self.assertRaises(ValueError):
            probe.percentile([], 0.5)
        with self.assertRaises(ValueError):
            probe.percentile([1.0], 1.5)
        with self.assertRaises(ValueError):
            probe.percentile([1.0], -0.1)

    def test_summarize_reports_the_full_shape(self):
        s = probe.summarize("local muxd ws", [5.0, 1.0, 3.0, 2.0, 4.0])
        self.assertEqual(s["label"], "local muxd ws")
        self.assertEqual(s["count"], 5)
        self.assertAlmostEqual(s["min"], 1.0)
        self.assertAlmostEqual(s["max"], 5.0)
        self.assertAlmostEqual(s["mean"], 3.0)
        self.assertAlmostEqual(s["p50"], 3.0)

    def test_summarize_of_no_samples_is_empty_not_a_crash(self):
        s = probe.summarize("dead series", [])
        self.assertEqual(s["count"], 0)
        self.assertIsNone(s["p95"])
        self.assertIn("no samples", probe.format_summary(s, 50.0, True))

    def test_format_summary_flags_over_budget_and_enforcement(self):
        under = probe.format_summary(probe.summarize("x", [1.0] * 10), 50.0, True)
        self.assertIn("OK", under)
        self.assertIn("ENFORCED", under)
        over = probe.format_summary(probe.summarize("x", [900.0] * 10), 50.0, False)
        self.assertIn("OVER", over)
        self.assertIn("diagnostic", over)


# --------------------------------------------------------------------------- matching

class EchoMatcherTests(unittest.TestCase):
    def test_markers_never_repeat_back_to_back(self):
        m = probe.EchoMatcher()
        markers = [m.marker_for(i) for i in range(len(probe.MARKER_ALPHABET) * 3)]
        self.assertTrue(all(len(x) == 1 for x in markers))
        for a, b in zip(markers, markers[1:]):
            self.assertNotEqual(a, b)

    def test_matches_a_marker_split_across_chunks(self):
        m = probe.EchoMatcher(alphabet=b"XY")
        m.marker = b"ZZ"  # two-byte marker exercises the split-tail path
        self.assertFalse(m.feed(b"noiseZ"))
        self.assertTrue(m.feed(b"Zmore"))

    def test_unarmed_matcher_refuses_to_feed(self):
        with self.assertRaises(RuntimeError):
            probe.EchoMatcher().feed(b"a")

    def test_arm_drops_stale_buffer(self):
        m = probe.EchoMatcher()
        m.arm(b"a")
        self.assertFalse(m.feed(b"zzz"))
        m.arm(b"b")
        self.assertEqual(m.buf, b"")
        self.assertFalse(m.feed(b"a"))  # the previously buffered marker cannot match now

    def test_rejects_empty_alphabet_and_empty_marker(self):
        with self.assertRaises(ValueError):
            probe.EchoMatcher(alphabet=b"")
        with self.assertRaises(ValueError):
            probe.EchoMatcher().arm(b"")


class MeasureSeriesTests(unittest.TestCase):
    def test_every_sample_is_measured_and_sent_one_at_a_time(self):
        t = LoopbackTransport(delay_s=0.0)
        rtts = arun(probe.measure_series(t, 60, per_sample_timeout=2.0, drain_timeout=0.001))
        self.assertEqual(len(rtts), 60)
        self.assertEqual(len(t.sent), 60)
        self.assertTrue(all(len(s) == 1 for s in t.sent))
        self.assertTrue(all(r >= 0.0 for r in rtts))
        # markers cycle past the end of the alphabet, and never repeat consecutively
        expected = [probe.MARKER_ALPHABET[i % len(probe.MARKER_ALPHABET):][:1] for i in range(60)]
        self.assertEqual(t.sent, expected)

    def test_rtt_tracks_the_transport_delay(self):
        t = LoopbackTransport(delay_s=0.03)
        rtts = arun(probe.measure_series(t, 5, per_sample_timeout=2.0, drain_timeout=0.001))
        self.assertEqual(len(rtts), 5)
        for r in rtts:
            self.assertGreaterEqual(r, 25.0)
            self.assertLess(r, 900.0)

    def test_buffered_output_is_drained_before_the_clock_starts(self):
        """A stale copy of the marker sitting in the buffer must not fake a ~0ms RTT."""
        stale = probe.MARKER_ALPHABET[:1]  # exactly sample 0's marker, already buffered
        t = LoopbackTransport(delay_s=0.04, preamble=[stale])
        rtts = arun(probe.measure_series(t, 1, per_sample_timeout=2.0, drain_timeout=0.01))
        self.assertEqual(len(rtts), 1)
        self.assertGreaterEqual(rtts[0], 30.0)

    def test_unrelated_output_does_not_satisfy_a_sample(self):
        def noisy(marker):
            return [b"\x1b[2Kchatter ", b"progress...", marker]

        t = LoopbackTransport(delay_s=0.0, mangle=noisy)
        rtts = arun(probe.measure_series(t, 10, per_sample_timeout=2.0, drain_timeout=0.001))
        self.assertEqual(len(rtts), 10)

    def test_a_wrong_echo_never_counts_as_a_match(self):
        t = LoopbackTransport(delay_s=0.0, mangle=lambda marker: [b"!"])
        with self.assertRaises(probe.ProbeTimeout) as ctx:
            arun(probe.measure_series(t, 3, per_sample_timeout=0.08, drain_timeout=0.001))
        self.assertEqual(ctx.exception.code, probe.EXIT_TIMEOUT)

    def test_silent_child_trips_the_per_sample_timeout(self):
        t = LoopbackTransport(delay_s=0.0, mangle=lambda marker: [])
        with self.assertRaises(probe.ProbeTimeout) as ctx:
            arun(probe.measure_series(t, 3, per_sample_timeout=0.08, drain_timeout=0.001))
        self.assertEqual(ctx.exception.code, probe.EXIT_TIMEOUT)
        self.assertIn("no echo", str(ctx.exception))

    def test_wall_deadline_clamps_a_long_series(self):
        t = LoopbackTransport(delay_s=0.0)
        loop_deadline = []

        async def go():
            loop_deadline.append(None)
            return await probe.measure_series(
                t, 10_000, per_sample_timeout=2.0,
                deadline=asyncio.get_running_loop().time() * 0 + _monotonic_soon(),
                drain_timeout=0.001)

        with self.assertRaises(probe.ProbeTimeout) as ctx:
            arun(go())
        self.assertEqual(ctx.exception.code, probe.EXIT_TIMEOUT)
        self.assertIn("wall clamp", str(ctx.exception))

    def test_already_expired_deadline_stops_before_the_first_send(self):
        import time as _time
        t = LoopbackTransport(delay_s=0.0)
        with self.assertRaises(probe.ProbeTimeout):
            arun(probe.measure_series(t, 5, per_sample_timeout=2.0,
                                      deadline=_time.monotonic() - 1.0, drain_timeout=0.001))
        self.assertEqual(t.sent, [])


def _monotonic_soon(offset=0.25):
    import time as _time
    return _time.monotonic() + offset


# --------------------------------------------------------------------------- transports

class LocalMuxdTransportTests(unittest.TestCase):
    def test_binary_frames_pass_through(self):
        ws = FakeWs()
        t = probe.LocalMuxdTransport(ws)

        async def go():
            await t.send(b"q")
            return await t.recv(1.0)

        self.assertEqual(arun(go()), probe.READY_TOKEN.encode("ascii") + b"\r\n")
        self.assertEqual(ws.sent, [b"q"])

    def test_json_output_frames_are_base64_decoded(self):
        ws = FakeWs(json_output=True, ready=False)
        t = probe.LocalMuxdTransport(ws)

        async def go():
            await t.send(b"z")
            return await t.recv(1.0)

        self.assertEqual(arun(go()), b"z")

    def test_error_frame_raises_a_probe_error(self):
        ws = FakeWs(ready=False)
        ws.push(json.dumps({"t": "err", "m": "no such session"}))
        t = probe.LocalMuxdTransport(ws)
        with self.assertRaises(probe.ProbeError) as ctx:
            arun(t.recv(1.0))
        self.assertIn("no such session", str(ctx.exception))

    def test_recv_timeout_returns_empty_not_an_exception(self):
        ws = FakeWs(ready=False)
        self.assertEqual(arun(probe.LocalMuxdTransport(ws).recv(0.02)), b"")

    def test_relay_transport_frames_input_as_text(self):
        ws = FakeWs(ready=False, echo=False)
        arun(probe.RelayWsTransport(ws).send(b"k"))
        self.assertEqual(ws.sent, ["ik"])


# --------------------------------------------------------------------------- session kill

class SessionLifecycleTests(unittest.TestCase):
    def _wire(self, ws, **extra):
        """Patch the probe so run_local_series drives `ws` and records create/kill calls."""
        created, killed, children = [], [], []
        real_write_echo_child = probe.write_echo_child

        async def fake_request_json(port, payload, timeout=6.0):
            created.append(payload)
            return {"ok": True}

        async def fake_kill(port, name):
            killed.append(name)
            return True

        def fake_write_echo_child(directory=None):
            path = real_write_echo_child(directory)
            children.append(path)
            return path

        ProbePatch(self, _websockets=lambda: FakeWebsocketsModule(ws),
                   request_json=fake_request_json, kill_probe_session=fake_kill,
                   write_echo_child=fake_write_echo_child, **extra)
        return created, killed, children

    def test_success_path_measures_then_kills_the_session(self):
        created, killed, children = self._wire(FakeWs())
        rtts = arun(probe.run_local_series(7699, 5, 2.0, None))
        self.assertEqual(len(rtts), 5)
        self.assertEqual(created[0]["t"], "create")
        self.assertEqual(len(killed), 1)
        self.assertEqual(killed[0], created[0]["s"])
        self.assertFalse(os.path.exists(children[0]))

    def test_hung_child_trips_the_timeout_and_still_kills_the_session(self):
        created, killed, children = self._wire(FakeWs(echo=False))
        with self.assertRaises(probe.ProbeTimeout) as ctx:
            arun(probe.run_local_series(7699, 3, 0.08, None))
        self.assertEqual(ctx.exception.code, probe.EXIT_TIMEOUT)
        self.assertEqual(killed, [created[0]["s"]], "probe session leaked after a timeout")
        self.assertFalse(os.path.exists(children[0]), "echo child temp file leaked")

    def test_wall_clamp_during_the_series_still_kills_the_session(self):
        import time as _time
        created, killed, _children = self._wire(FakeWs())
        with self.assertRaises(probe.ProbeTimeout):
            arun(probe.run_local_series(7699, 10_000, 2.0, _time.monotonic() + 0.2))
        self.assertEqual(killed, [created[0]["s"]])

    def test_child_that_never_reports_ready_is_cleaned_up(self):
        created, killed, children = self._wire(FakeWs(ready=False, echo=False))

        async def go():
            return await probe.run_local_series(7699, 3, 0.05, None)

        with ProbePatchTimeout(self, 0.3):
            with self.assertRaises(probe.ProbeError):
                arun(go())
        self.assertEqual(killed, [created[0]["s"]])
        self.assertFalse(os.path.exists(children[0]))

    def test_a_failed_create_kills_nothing(self):
        killed = []

        async def failing_create(port, payload, timeout=6.0):
            raise probe.ProbeError("muxd said no", probe.EXIT_MUXD_UNREACHABLE)

        async def fake_kill(port, name):
            killed.append(name)
            return True

        ProbePatch(self, _websockets=lambda: FakeWebsocketsModule(FakeWs()),
                   request_json=failing_create, kill_probe_session=fake_kill)
        with self.assertRaises(probe.ProbeError) as ctx:
            arun(probe.run_local_series(7699, 3, 1.0, None))
        self.assertEqual(ctx.exception.code, probe.EXIT_MUXD_UNREACHABLE)
        self.assertEqual(killed, [])

    def test_kill_failure_is_reported_but_never_masks_the_real_error(self):
        async def boom(port, payload, timeout=6.0):
            raise OSError("connection refused")

        ProbePatch(self, _request_once=boom)
        self.assertFalse(arun(probe.kill_probe_session(7699, "latprobe-x")))

    def test_probe_session_names_are_unique(self):
        names = {probe.probe_session_name() for _ in range(50)}
        self.assertGreater(len(names), 1)
        self.assertTrue(all(n.startswith("latprobe-") for n in names))


class ProbePatchTimeout:
    """Shrink wait_for_ready's timeout so the not-ready path fails fast."""

    def __init__(self, testcase, timeout):
        self.testcase = testcase
        self.timeout = timeout
        self.original = probe.wait_for_ready

    def __enter__(self):
        original = self.original
        timeout = self.timeout

        async def quick(transport, token=probe.READY_TOKEN, timeout_=timeout, deadline=None):
            return await original(transport, token, timeout_, deadline)

        probe.wait_for_ready = quick
        return self

    def __exit__(self, *exc):
        probe.wait_for_ready = self.original
        return False


# --------------------------------------------------------------------------- echo child

class EchoChildTests(unittest.TestCase):
    def test_child_source_is_valid_python_and_prints_the_ready_token(self):
        path = probe.write_echo_child()
        self.addCleanup(lambda: os.path.exists(path) and os.unlink(path))
        with open(path, "r", encoding="utf-8") as fh:
            source = fh.read()
        compile(source, path, "exec")
        self.assertIn(probe.READY_TOKEN, source)
        self.assertIn("ENABLE_ECHO_INPUT", source)
        self.assertIn("ENABLE_LINE_INPUT", source)
        self.assertIn("getwch", source)

    def test_command_is_a_quoted_powershell_call(self):
        cmd = probe.echo_child_command(r"C:\tmp\it's here.py", python=r"C:\py\python.exe")
        self.assertTrue(cmd.startswith("& '"))
        self.assertIn("it''s here.py", cmd)  # PowerShell single-quote escaping


# --------------------------------------------------------------------------- autostart

class MuxdAutostartTests(unittest.TestCase):
    class _R:
        def __init__(self, returncode=0, stdout="", stderr=""):
            self.returncode, self.stdout, self.stderr = returncode, stdout, stderr

    @unittest.skipUnless(os.name == "nt", "scheduled-task autostart is Windows-only")
    def test_running_task_is_left_alone(self):
        calls = []

        def runner(args, timeout=8):
            calls.append(args)
            return self._R(0, '"MuxdSessionHost","N/A","Running"')

        self.assertFalse(probe.ensure_muxd_started(runner=runner, sleep=lambda s: None))
        self.assertEqual(len(calls), 1)
        self.assertIn("/Query", calls[0])

    @unittest.skipUnless(os.name == "nt", "scheduled-task autostart is Windows-only")
    def test_stopped_task_is_started_like_muxctl_does(self):
        calls = []

        def runner(args, timeout=8):
            calls.append(args)
            return self._R(0, '"MuxdSessionHost","N/A","Ready"')

        slept = []
        self.assertTrue(probe.ensure_muxd_started(runner=runner, sleep=slept.append))
        self.assertEqual([a[1] for a in calls], ["/Query", "/Run"])
        self.assertEqual(calls[1][:4], ["schtasks", "/Run", "/TN", probe.TASK_NAME])
        self.assertEqual(slept, [1.2])

    @unittest.skipUnless(os.name == "nt", "scheduled-task autostart is Windows-only")
    def test_missing_task_is_not_startable(self):
        def runner(args, timeout=8):
            return self._R(1, "", "ERROR: The system cannot find the file specified.")

        self.assertFalse(probe.ensure_muxd_started(runner=runner, sleep=lambda s: None))

    @unittest.skipUnless(os.name == "nt", "scheduled-task autostart is Windows-only")
    def test_a_failing_run_is_not_a_success(self):
        def runner(args, timeout=8):
            if "/Query" in args:
                return self._R(0, '"MuxdSessionHost","N/A","Ready"')
            return self._R(1, "", "ERROR: Access is denied.")

        self.assertFalse(probe.ensure_muxd_started(runner=runner, sleep=lambda s: None))

    @unittest.skipUnless(os.name == "nt", "scheduled-task autostart is Windows-only")
    def test_a_raising_runner_is_not_a_success(self):
        def runner(args, timeout=8):
            raise OSError("schtasks missing")

        self.assertFalse(probe.ensure_muxd_started(runner=runner, sleep=lambda s: None))

    def test_unreachable_muxd_with_failed_autostart_exits_three(self):
        async def refused(port, payload, timeout=6.0):
            raise OSError("connection refused")

        ProbePatch(self, _request_once=refused, ensure_muxd_started=lambda **k: False)
        with self.assertRaises(probe.ProbeError) as ctx:
            arun(probe.request_json(7699, {"t": "ls"}))
        self.assertEqual(ctx.exception.code, probe.EXIT_MUXD_UNREACHABLE)
        self.assertIn("scheduled task", str(ctx.exception))

    def test_autostart_then_retry_succeeds(self):
        attempts = []

        async def flaky(port, payload, timeout=6.0):
            attempts.append(payload)
            if len(attempts) < 3:
                raise OSError("connection refused")
            return {"t": "ok"}

        async def nosleep(_s):
            return None

        ProbePatch(self, _request_once=flaky, ensure_muxd_started=lambda **k: True)
        real_sleep = asyncio.sleep
        asyncio.sleep = nosleep
        self.addCleanup(setattr, asyncio, "sleep", real_sleep)
        self.assertEqual(arun(probe.request_json(7699, {"t": "ls"})), {"t": "ok"})
        self.assertEqual(len(attempts), 3)


# --------------------------------------------------------------------------- signed path

class SignedPathTests(unittest.TestCase):
    def test_missing_trust_plumbing_exits_four_with_a_clear_message(self):
        ProbePatch(self, find_principal_auth=lambda root=None: None)
        with self.assertRaises(probe.ProbeError) as ctx:
            arun(probe.run_signed_series(7699, 10, 1.0, None))
        self.assertEqual(ctx.exception.code, probe.EXIT_SIGNED_UNAVAILABLE)
        self.assertIn("principal-auth.js", str(ctx.exception))

    def test_signature_incapable_muxd_exits_four(self):
        async def info(port, payload, timeout=6.0):
            return {"protocol": 7, "caps": ["attach", "scrollback"]}

        ProbePatch(self, find_principal_auth=lambda root=None: "C:/fake/principal-auth.js",
                   request_json=info)
        with self.assertRaises(probe.ProbeError) as ctx:
            arun(probe.run_signed_series(7699, 10, 1.0, None))
        self.assertEqual(ctx.exception.code, probe.EXIT_SIGNED_UNAVAILABLE)
        self.assertIn("no signature-verification capability", str(ctx.exception))

    def test_env_override_locates_principal_auth(self):
        fd, path = tempfile.mkstemp(suffix="principal-auth.js")
        os.close(fd)
        self.addCleanup(lambda: os.path.exists(path) and os.unlink(path))
        old = os.environ.get("MUXD_PRINCIPAL_AUTH")
        os.environ["MUXD_PRINCIPAL_AUTH"] = path
        self.addCleanup(lambda: os.environ.__setitem__("MUXD_PRINCIPAL_AUTH", old)
                        if old is not None else os.environ.pop("MUXD_PRINCIPAL_AUTH", None))
        self.assertEqual(probe.find_principal_auth(), path)
        os.environ["MUXD_PRINCIPAL_AUTH"] = path + ".missing"
        self.assertIsNone(probe.find_principal_auth())


# --------------------------------------------------------------------------- cli / budget

class BudgetEnforcementTests(unittest.TestCase):
    def _with_local_samples(self, rtts):
        async def fake_series(port, samples, per_sample_timeout, deadline, keep_session=False):
            return list(rtts)

        ProbePatch(self, run_local_series=fake_series)

    def test_inside_budget_exits_zero(self):
        self._with_local_samples([5.0] * 200)
        self.assertEqual(probe.main(["--local", "--samples", "200", "--budget-p95-ms", "50"]),
                         probe.EXIT_OK)

    def test_over_budget_exits_non_zero(self):
        self._with_local_samples([10.0] * 180 + [400.0] * 20)
        self.assertEqual(probe.main(["--local", "--samples", "200", "--budget-p95-ms", "50"]),
                         probe.EXIT_OVER_BUDGET)

    def test_default_local_budget_is_fifty_ms(self):
        self._with_local_samples([49.0] * 100)
        self.assertEqual(probe.main(["--local", "--samples", "100"]), probe.EXIT_OK)
        self._with_local_samples([51.0] * 100)
        self.assertEqual(probe.main(["--local", "--samples", "100"]), probe.EXIT_OVER_BUDGET)
        self.assertEqual(probe.DEFAULT_LOCAL_BUDGET_MS, 50.0)
        self.assertEqual(probe.DEFAULT_RELAY_BUDGET_MS, 120.0)
        self.assertEqual(probe.DEFAULT_SAMPLES, 200)

    def test_local_is_the_default_series(self):
        self._with_local_samples([1.0] * 10)
        self.assertEqual(probe.main(["--samples", "10"]), probe.EXIT_OK)

    def test_bad_sample_count_and_timeout_are_usage_errors(self):
        self.assertEqual(probe.main(["--local", "--samples", "0"]), probe.EXIT_ERROR)
        self.assertEqual(probe.main(["--local", "--timeout", "0"]), probe.EXIT_ERROR)

    def test_signed_series_is_authoritative_and_demotes_the_local_one(self):
        """With --signed the enforced verdict comes from the signed series, not the local one."""
        self._with_local_samples([9999.0] * 50)  # diagnostic only -> must not fail the run

        async def fake_signed(port, samples, per_sample_timeout, deadline):
            return [1.0] * 50

        ProbePatch(self, run_signed_series=fake_signed)
        self.assertEqual(probe.main(["--local", "--signed", "--samples", "50",
                                     "--budget-p95-ms", "50"]), probe.EXIT_OK)

    def test_signed_series_over_budget_fails_the_run(self):
        self._with_local_samples([1.0] * 50)

        async def fake_signed(port, samples, per_sample_timeout, deadline):
            return [900.0] * 50

        ProbePatch(self, run_signed_series=fake_signed)
        self.assertEqual(probe.main(["--local", "--signed", "--samples", "50",
                                     "--budget-p95-ms", "50"]), probe.EXIT_OVER_BUDGET)

    def test_signed_unavailable_propagates_exit_four(self):
        async def unavailable(port, samples, per_sample_timeout, deadline):
            raise probe.ProbeError("no trust plumbing", probe.EXIT_SIGNED_UNAVAILABLE)

        ProbePatch(self, run_signed_series=unavailable)
        self.assertEqual(probe.main(["--signed", "--samples", "10"]),
                         probe.EXIT_SIGNED_UNAVAILABLE)

    def test_timeout_clamp_propagates_exit_five(self):
        async def clamped(port, samples, per_sample_timeout, deadline, keep_session=False):
            raise probe.ProbeTimeout("hard --timeout wall clamp tripped")

        ProbePatch(self, run_local_series=clamped)
        self.assertEqual(probe.main(["--local", "--samples", "10"]), probe.EXIT_TIMEOUT)

    def test_relay_without_the_env_gate_is_skipped_not_run(self):
        ran = []

        async def fake_relay(port, samples, per_sample_timeout, deadline):
            ran.append(True)
            return [1.0] * 10

        old = os.environ.pop("MUXD_VPS_TESTS", None)
        self.addCleanup(lambda: os.environ.__setitem__("MUXD_VPS_TESTS", old)
                        if old is not None else None)
        self._with_local_samples([1.0] * 10)
        ProbePatch(self, run_relay_series=fake_relay)
        self.assertEqual(probe.main(["--local", "--relay", "--samples", "10"]), probe.EXIT_OK)
        self.assertEqual(ran, [])

    def test_relay_budget_defaults_to_one_twenty_when_gated_on(self):
        async def fake_relay(port, samples, per_sample_timeout, deadline):
            return [119.0] * 20

        os.environ["MUXD_VPS_TESTS"] = "1"
        self.addCleanup(lambda: os.environ.pop("MUXD_VPS_TESTS", None))
        ProbePatch(self, run_relay_series=fake_relay)
        self.assertEqual(probe.main(["--relay", "--samples", "20"]), probe.EXIT_OK)

        async def slow_relay(port, samples, per_sample_timeout, deadline):
            return [121.0] * 20

        ProbePatch(self, run_relay_series=slow_relay)
        self.assertEqual(probe.main(["--relay", "--samples", "20"]), probe.EXIT_OVER_BUDGET)

    def test_relay_enabled_reads_the_env_gate(self):
        for value, want in (("1", True), ("true", True), ("on", True),
                            ("0", False), ("", False)):
            os.environ["MUXD_VPS_TESTS"] = value
            self.assertEqual(probe.relay_enabled(), want, value)
        os.environ.pop("MUXD_VPS_TESTS", None)
        self.assertFalse(probe.relay_enabled())


# ------------------------------------------------------- reporting contract (r.3.5.2)

class _RunsMainMixin:
    """Shared fakes: patch the series coroutines and capture the probe's stdout."""

    def _local_samples(self, rtts):
        async def fake_series(port, samples, per_sample_timeout, deadline, keep_session=False):
            return list(rtts)

        ProbePatch(self, run_local_series=fake_series)

    def _signed_unavailable(self):
        async def unavailable(port, samples, per_sample_timeout, deadline):
            raise probe.ProbeError("no trust plumbing on this branch",
                                   probe.EXIT_SIGNED_UNAVAILABLE)

        ProbePatch(self, run_signed_series=unavailable)

    def _main(self, argv):
        """Return (exit_code, stdout). stderr is left alone - it is the human channel."""
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            code = probe.main(argv)
        return code, buf.getvalue()


class SignedDiagnosticsTests(_RunsMainMixin, unittest.TestCase):
    """An unmeasurable signed path must not swallow the series the user asked for."""

    def test_local_numbers_survive_an_unavailable_signed_path(self):
        self._signed_unavailable()
        self._local_samples([7.0] * 10)
        code, out = self._main(["--local", "--signed", "--samples", "10"])
        self.assertEqual(code, probe.EXIT_SIGNED_UNAVAILABLE)
        self.assertIn("local muxd ws", out)
        for field in ("p50=", "p95=", "p99="):
            self.assertIn(field, out)
        self.assertIn("7.00", out)          # the numbers really are there, not just the labels
        self.assertIn("DIAGNOSTIC ONLY", out)

    def test_over_budget_diagnostic_does_not_downgrade_exit_four(self):
        self._signed_unavailable()
        self._local_samples([9999.0] * 10)
        code, out = self._main(["--local", "--signed", "--samples", "10",
                                "--budget-p95-ms", "50"])
        self.assertEqual(code, probe.EXIT_SIGNED_UNAVAILABLE)
        self.assertNotEqual(code, probe.EXIT_OVER_BUDGET)
        self.assertIn("local muxd ws", out)

    def test_other_probe_errors_from_the_signed_path_still_propagate(self):
        async def unreachable(port, samples, per_sample_timeout, deadline):
            raise probe.ProbeError("muxd unreachable", probe.EXIT_MUXD_UNREACHABLE)

        ProbePatch(self, run_signed_series=unreachable)
        self._local_samples([1.0] * 10)
        code, _ = self._main(["--local", "--signed", "--samples", "10"])
        self.assertEqual(code, probe.EXIT_MUXD_UNREACHABLE)


class JsonArtifactTests(_RunsMainMixin, unittest.TestCase):
    """--json-out is the machine-readable record; --json keeps its stdout-only behaviour."""

    SERIES_FIELDS = ("label", "count", "p50", "p95", "p99", "min", "max", "mean",
                     "budget_p95_ms", "enforced")

    @staticmethod
    def _read(path):
        with open(path, "r", encoding="utf-8") as fh:
            return json.load(fh)

    def test_json_out_writes_the_whole_record_and_creates_parents(self):
        self._local_samples([5.0] * 20)
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "nested", "deeper", "probe.json")
            code, _ = self._main(["--local", "--samples", "20", "--json-out", out])
            self.assertEqual(code, probe.EXIT_OK)
            self.assertTrue(os.path.isfile(out), out)
            doc = self._read(out)
            self.assertEqual(doc["exit_code"], code)
            self.assertFalse(doc["signed_available"])
            self.assertEqual(len(doc["series"]), 1)
            series = doc["series"][0]
            for field in self.SERIES_FIELDS:
                self.assertIn(field, series)
            self.assertEqual(series["count"], 20)
            self.assertEqual(series["label"], "local muxd ws")
            self.assertAlmostEqual(series["p95"], 5.0)
            self.assertEqual(series["budget_p95_ms"], probe.DEFAULT_LOCAL_BUDGET_MS)
            self.assertTrue(series["enforced"])

    def test_artifact_is_written_on_the_over_budget_exit(self):
        self._local_samples([400.0] * 20)
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "over.json")
            code, _ = self._main(["--local", "--samples", "20", "--budget-p95-ms", "50",
                                  "--json-out", out])
            self.assertEqual(code, probe.EXIT_OVER_BUDGET)
            doc = self._read(out)
            self.assertEqual(doc["exit_code"], probe.EXIT_OVER_BUDGET)
            self.assertEqual(doc["series"][0]["count"], 20)

    def test_artifact_is_written_when_the_signed_path_is_unavailable(self):
        self._signed_unavailable()
        self._local_samples([5.0] * 15)
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "signed.json")
            code, _ = self._main(["--local", "--signed", "--samples", "15", "--json-out", out])
            self.assertEqual(code, probe.EXIT_SIGNED_UNAVAILABLE)
            doc = self._read(out)
            self.assertEqual(doc["exit_code"], code)
            self.assertFalse(doc["signed_available"])
            self.assertEqual(doc["series"][0]["count"], 15)
            self.assertFalse(doc["series"][0]["enforced"])

    def test_bare_json_flag_still_only_prints(self):
        self._local_samples([5.0] * 10)
        code, out = self._main(["--local", "--samples", "10", "--json"])
        self.assertEqual(code, probe.EXIT_OK)
        payload = json.loads(out[out.index("["):])   # the stdout blob is still a bare list
        self.assertEqual(payload[0]["count"], 10)
        self.assertEqual(payload[0]["label"], "local muxd ws")


class RelayBudgetIsolationTests(unittest.TestCase):
    """--budget-p95-ms is local/signed only: a loopback budget must never judge a WAN hop."""

    def setUp(self):
        old = os.environ.get("MUXD_VPS_TESTS")
        os.environ["MUXD_VPS_TESTS"] = "1"
        self.addCleanup(lambda: os.environ.__setitem__("MUXD_VPS_TESTS", old)
                        if old is not None else os.environ.pop("MUXD_VPS_TESTS", None))

        async def fake_relay(port, samples, per_sample_timeout, deadline):
            return [110.0] * 20

        ProbePatch(self, run_relay_series=fake_relay)

    def _main(self, argv):
        with contextlib.redirect_stdout(io.StringIO()):
            return probe.main(argv)

    def test_tight_local_budget_does_not_leak_into_the_relay_series(self):
        self.assertEqual(self._main(["--relay", "--samples", "20", "--budget-p95-ms", "50"]),
                         probe.EXIT_OK)

    def test_the_explicit_relay_budget_is_the_one_that_binds(self):
        self.assertEqual(self._main(["--relay", "--samples", "20", "--budget-p95-ms", "50",
                                     "--relay-budget-p95-ms", "100"]),
                         probe.EXIT_OVER_BUDGET)


class GitignoreHygieneTests(unittest.TestCase):
    """Running this suite writes scripts/__pycache__/; the repo-root ignore must cover it."""

    def test_repo_root_gitignore_ignores_pycache(self):
        path = os.path.join(REPO_ROOT, ".gitignore")
        self.assertTrue(os.path.isfile(path), path)
        with open(path, "r", encoding="utf-8") as fh:
            lines = [line.strip() for line in fh]
        self.assertIn("__pycache__/", lines)


if __name__ == "__main__":
    unittest.main()
