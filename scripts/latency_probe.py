#!/usr/bin/env python
"""latency_probe — input->echo round-trip latency for a muxd session.

Opens a throwaway muxd session running a pure echo child, sends single-byte inputs over the
same paths a real terminal uses, and measures the time from "byte handed to the transport" to
"that exact byte comes back as output". Measure-only: this probe never tunes anything.

Series it can report:
  --local            local muxd ws (ws://127.0.0.1:$MUXCTL_PORT, the path muxctl uses)
  --relay            relay ws path, gated behind MUXD_VPS_TESTS=1 (test_vps_smoke plumbing)
  --signed           SIGNED/ENFORCED path: principal-auth.js channel open, muxd signature
                     verification, signed output verification.

TRUST NOTE: the enforced (signed) path is the shipping path. When a signed series is present it
is the AUTHORITATIVE one and the budget is enforced against it; the unsigned local/relay series
are diagnostic only.

Exit codes:
  0 every enforced series inside budget       3 muxd unreachable (scheduled-task autostart failed)
  1 an enforced series blew its p95 budget    4 signed path requested but trust plumbing absent
  2 usage / probe error                       5 hard --timeout wall clamp tripped
"""
from __future__ import annotations

import argparse
import asyncio
import base64
import contextlib
import json
import math
import os
import subprocess
import sys
import tempfile
import time
import uuid

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

EXIT_OK = 0
EXIT_OVER_BUDGET = 1
EXIT_ERROR = 2
EXIT_MUXD_UNREACHABLE = 3
EXIT_SIGNED_UNAVAILABLE = 4
EXIT_TIMEOUT = 5

DEFAULT_LOCAL_BUDGET_MS = 50.0
DEFAULT_RELAY_BUDGET_MS = 120.0
DEFAULT_SAMPLES = 200

MUXCTL_PORT = int(os.environ.get("MUXCTL_PORT", "7699"))
TASK_NAME = os.environ.get("MUXD_TASK", "MuxdSessionHost")
CREATE_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)

# Single-byte markers. Cycled so no two consecutive samples share a byte, which makes a stale
# echo from the previous sample impossible to mistake for the current one.
MARKER_ALPHABET = b"abcdefghijklmnopqrstuvwxyz0123456789"
READY_TOKEN = "MUXD-LATENCY-PROBE-READY"
# Pre-send drain wait. Must be > 0: a zero-timeout wait_for never schedules the recv, so already
# buffered frames would leak into the next sample's window and fake a near-zero RTT.
DRAIN_TIMEOUT_S = 0.005


class ProbeError(RuntimeError):
    """Probe could not complete; carries the process exit code to use."""

    def __init__(self, message, code=EXIT_ERROR):
        super().__init__(message)
        self.code = code


class ProbeTimeout(ProbeError):
    def __init__(self, message):
        super().__init__(message, EXIT_TIMEOUT)


# --------------------------------------------------------------------------- statistics

def percentile(values, q):
    """Linear-interpolated percentile. q in [0,1]. Matches numpy's default ('linear')."""
    if not values:
        raise ValueError("percentile of an empty sample set")
    if not 0.0 <= q <= 1.0:
        raise ValueError("q must be in [0,1]")
    xs = sorted(float(v) for v in values)
    if len(xs) == 1:
        return xs[0]
    pos = (len(xs) - 1) * q
    lo = math.floor(pos)
    hi = math.ceil(pos)
    if lo == hi:
        return xs[int(pos)]
    return xs[lo] + (xs[hi] - xs[lo]) * (pos - lo)


def summarize(label, samples):
    xs = sorted(float(v) for v in samples)
    return {
        "label": label,
        "count": len(xs),
        "min": xs[0] if xs else None,
        "p50": percentile(xs, 0.50) if xs else None,
        "p95": percentile(xs, 0.95) if xs else None,
        "p99": percentile(xs, 0.99) if xs else None,
        "max": xs[-1] if xs else None,
        "mean": (sum(xs) / len(xs)) if xs else None,
    }


def format_summary(summary, budget_ms, enforced):
    kind = "ENFORCED" if enforced else "diagnostic"
    if not summary["count"]:
        return "  %-22s %s  no samples" % (summary["label"], kind)
    return (
        "  %-22s %s  n=%-4d p50=%7.2fms  p95=%7.2fms  p99=%7.2fms  "
        "(min=%.2f max=%.2f mean=%.2f) budget p95<=%.1fms %s"
        % (
            summary["label"], kind, summary["count"],
            summary["p50"], summary["p95"], summary["p99"],
            summary["min"], summary["max"], summary["mean"],
            budget_ms,
            "OK" if summary["p95"] <= budget_ms else "OVER",
        )
    )


# --------------------------------------------------------------------------- echo matching

class EchoMatcher:
    """Streaming matcher: arm a marker, feed output chunks, report the first full match.

    Keeps a bounded tail so a marker split across two chunks still matches.
    """

    def __init__(self, alphabet=MARKER_ALPHABET):
        if not alphabet:
            raise ValueError("marker alphabet must be non-empty")
        self.alphabet = bytes(alphabet)
        self.buf = b""
        self.marker = b""

    def marker_for(self, index):
        return self.alphabet[index % len(self.alphabet):][:1]

    def arm(self, marker):
        """Arm on a marker and drop everything buffered so far (pre-send drain)."""
        self.marker = bytes(marker)
        if not self.marker:
            raise ValueError("marker must be non-empty")
        self.buf = b""
        return self.marker

    def feed(self, chunk):
        if not self.marker:
            raise RuntimeError("matcher is not armed")
        if not chunk:
            return False
        self.buf += bytes(chunk)
        if self.marker in self.buf:
            self.buf = b""
            return True
        # keep only what could still be the head of a split marker
        keep = len(self.marker) - 1
        if keep > 0:
            self.buf = self.buf[-keep:]
        else:
            self.buf = b""
        return False


async def measure_series(transport, samples, per_sample_timeout=3.0, deadline=None,
                         alphabet=MARKER_ALPHABET, drain_timeout=DRAIN_TIMEOUT_S):
    """Send `samples` single-byte markers one at a time; return per-sample RTTs in ms.

    Strictly sequential: sample N+1 is not sent until sample N's echo is matched, so a sample
    can never be attributed to the wrong marker.
    """
    matcher = EchoMatcher(alphabet)
    rtts = []
    for i in range(samples):
        _check_wall(deadline)
        # drain anything the child emitted on its own before starting the clock
        while True:
            pending = await transport.recv(drain_timeout)
            if not pending:
                break
        marker = matcher.arm(matcher.marker_for(i))
        started = time.perf_counter()
        await transport.send(marker)
        while True:
            waited = time.perf_counter() - started
            budget = per_sample_timeout - waited
            if budget <= 0:
                raise ProbeTimeout(
                    "sample %d: no echo of %r within %.1fs" % (i, marker, per_sample_timeout))
            if deadline is not None:
                budget = min(budget, deadline - time.monotonic())
                _check_wall(deadline)
            chunk = await transport.recv(budget)
            if not chunk:
                continue
            if matcher.feed(chunk):
                rtts.append((time.perf_counter() - started) * 1000.0)
                break
    return rtts


def _check_wall(deadline):
    if deadline is not None and time.monotonic() >= deadline:
        raise ProbeTimeout("hard --timeout wall clamp tripped; probe session is being killed")


# --------------------------------------------------------------------------- local muxd

def _run_quiet(args, timeout=8):
    return subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                          timeout=timeout, creationflags=CREATE_NO_WINDOW)


def ensure_muxd_started(task_name=TASK_NAME, runner=_run_quiet, sleep=time.sleep):
    """Start the MuxdSessionHost scheduled task exactly like muxctl.py:426-445 does."""
    if os.name != "nt":
        return False
    try:
        q = runner(["schtasks", "/Query", "/TN", task_name, "/FO", "CSV", "/NH"])
    except Exception as e:
        sys.stderr.write("[probe] could not query scheduled task %s: %s\n" % (task_name, e))
        return False
    if q.returncode != 0:
        sys.stderr.write("[probe] scheduled task %s is not available: %s\n"
                         % (task_name, (q.stderr or q.stdout or "").strip()))
        return False
    if "Running" in q.stdout:
        return False
    try:
        r = runner(["schtasks", "/Run", "/TN", task_name])
    except Exception as e:
        sys.stderr.write("[probe] could not start scheduled task %s: %s\n" % (task_name, e))
        return False
    if r.returncode != 0:
        sys.stderr.write("[probe] scheduled task %s failed to start: %s\n"
                         % (task_name, (r.stderr or r.stdout or "").strip()))
        return False
    sys.stderr.write("[probe] started scheduled task %s; waiting for muxd...\n" % task_name)
    sleep(1.2)
    return True


def _websockets():
    try:
        import websockets  # noqa: F401
    except ImportError:
        raise ProbeError("latency_probe needs: pip install websockets", EXIT_ERROR)
    return sys.modules["websockets"]


def local_url(port):
    return "ws://127.0.0.1:%d" % port


async def _request_once(port, payload, timeout=6.0):
    websockets = _websockets()
    async with websockets.connect(local_url(port), open_timeout=timeout, close_timeout=1,
                                  ping_interval=None) as ws:
        await asyncio.wait_for(ws.send(json.dumps(payload)), timeout)
        raw = await asyncio.wait_for(ws.recv(), timeout)
        if isinstance(raw, (bytes, bytearray)):
            raw = bytes(raw).decode("utf-8", "replace")
        return json.loads(raw)


async def request_json(port, payload, timeout=6.0):
    """One control request, with muxctl's scheduled-task autostart on an unreachable muxd."""
    try:
        return await _request_once(port, payload, timeout)
    except (OSError, asyncio.TimeoutError) as first:
        if not ensure_muxd_started():
            raise ProbeError(
                "muxd is not reachable at %s and the %s scheduled task could not be started "
                "(%s). Start muxd, then re-run." % (local_url(port), TASK_NAME, first),
                EXIT_MUXD_UNREACHABLE)
        for _ in range(12):
            try:
                return await _request_once(port, payload, timeout)
            except (OSError, asyncio.TimeoutError):
                await asyncio.sleep(0.5)
        raise ProbeError(
            "muxd did not come up at %s after starting %s" % (local_url(port), TASK_NAME),
            EXIT_MUXD_UNREACHABLE)


class LocalMuxdTransport:
    """Attach stream on the local muxd ws: binary in, binary or {"t":"o"} out."""

    def __init__(self, ws):
        self.ws = ws

    async def send(self, data):
        await self.ws.send(bytes(data))

    async def recv(self, timeout):
        try:
            raw = await asyncio.wait_for(self.ws.recv(), max(0.0, timeout))
        except asyncio.TimeoutError:
            return b""
        if isinstance(raw, (bytes, bytearray)):
            return bytes(raw)
        try:
            m = json.loads(raw)
        except Exception:
            return b""
        if m.get("t") == "o":
            return base64.b64decode(m.get("d", "") or "")
        if m.get("t") == "err":
            raise ProbeError("muxd attach error: %s" % m.get("m", "error"), EXIT_ERROR)
        return b""


ECHO_CHILD_SOURCE = '''\
"""Pure echo child: one byte in -> the same byte out, no line discipline, no console echo."""
import ctypes, msvcrt, sys

STD_INPUT_HANDLE = -10
ENABLE_LINE_INPUT = 0x0002
ENABLE_ECHO_INPUT = 0x0004
ENABLE_PROCESSED_INPUT = 0x0001

try:
    k = ctypes.windll.kernel32
    h = k.GetStdHandle(STD_INPUT_HANDLE)
    mode = ctypes.c_uint()
    if k.GetConsoleMode(h, ctypes.byref(mode)):
        raw = mode.value & ~(ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT)
        k.SetConsoleMode(h, raw)
except Exception:
    pass

out = sys.stdout
out.write("%(ready)s\\r\\n")
out.flush()
while True:
    try:
        ch = msvcrt.getwch()
    except Exception:
        break
    if ch == "\\x03" or ch == "\\x04":
        break
    out.write(ch)
    out.flush()
'''


def write_echo_child(directory=None):
    fd, path = tempfile.mkstemp(prefix="muxd_echo_child_", suffix=".py", dir=directory)
    with os.fdopen(fd, "w", encoding="utf-8") as fh:
        fh.write(ECHO_CHILD_SOURCE % {"ready": READY_TOKEN})
    return path


def echo_child_command(child_path, python=None):
    """PowerShell command string; muxd runs it via powershell -NoLogo -NoExit -EncodedCommand."""
    exe = python or sys.executable or "python"
    return "& '%s' '%s'" % (exe.replace("'", "''"), child_path.replace("'", "''"))


def probe_session_name(prefix="latprobe"):
    # uuid4, not a clock: Windows' time_ns() only ticks every ~15.6 ms, so back-to-back
    # calls would collide and two probes could fight over one session name.
    return "%s-%d-%s" % (prefix, os.getpid(), uuid.uuid4().hex[:8])


async def wait_for_ready(transport, token=READY_TOKEN, timeout=25.0, deadline=None):
    want = token.encode("ascii")
    seen = b""
    started = time.perf_counter()
    while True:
        _check_wall(deadline)
        if time.perf_counter() - started > timeout:
            raise ProbeError(
                "echo child never printed %s within %.0fs; last output: %r"
                % (token, timeout, seen[-200:]), EXIT_ERROR)
        chunk = await transport.recv(0.5)
        if not chunk:
            continue
        seen = (seen + chunk)[-8192:]
        if want in seen:
            return True


async def run_local_series(port, samples, per_sample_timeout, deadline, keep_session=False):
    child_path = write_echo_child()
    name = probe_session_name()
    websockets = _websockets()
    created = False
    try:
        await request_json(port, {"t": "create", "s": name, "cmd": echo_child_command(child_path),
                                  "cols": 100, "rows": 30}, timeout=20.0)
        created = True
        async with websockets.connect(local_url(port), max_size=8_000_000, open_timeout=10,
                                      ping_interval=None) as ws:
            await ws.send(json.dumps({"t": "attach", "s": name, "cols": 100, "rows": 30, "sb": 0}))
            transport = LocalMuxdTransport(ws)
            await wait_for_ready(transport, deadline=deadline)
            return await measure_series(transport, samples,
                                        per_sample_timeout=per_sample_timeout, deadline=deadline)
    finally:
        # every exit path (success, budget failure, timeout, Ctrl-C, crash) kills the session
        if created and not keep_session:
            await kill_probe_session(port, name)
        with contextlib.suppress(OSError):
            os.unlink(child_path)


async def kill_probe_session(port, name):
    try:
        await asyncio.wait_for(_request_once(port, {"t": "kill", "s": name}, timeout=6.0), 8.0)
    except Exception as e:  # never let cleanup mask the real failure
        sys.stderr.write("[probe] warning: could not kill probe session %s: %s\n" % (name, e))
        return False
    return True


# --------------------------------------------------------------------------- relay series

def relay_enabled():
    return os.environ.get("MUXD_VPS_TESTS", "").lower() in ("1", "true", "yes", "on")


class RelayWsTransport:
    """Relay web-terminal ws: text frames 'i'+data in, raw output frames out."""

    def __init__(self, ws):
        self.ws = ws

    async def send(self, data):
        await self.ws.send("i" + bytes(data).decode("latin-1"))

    async def recv(self, timeout):
        try:
            raw = await asyncio.wait_for(self.ws.recv(), max(0.0, timeout))
        except asyncio.TimeoutError:
            return b""
        if isinstance(raw, (bytes, bytearray)):
            return bytes(raw)
        return str(raw).encode("utf-8", "replace")


async def run_relay_series(port, samples, per_sample_timeout, deadline):
    """Relay ws series. Requires MUXD_VPS_TESTS=1 plus a reachable relay ws.

    MUXD_RELAY_WS gives a directly reachable relay ws base (e.g. wss://host). Without it the
    relay only listens on the VPS loopback, so the sample loop is driven there over the
    test_vps_smoke ssh plumbing and the raw RTTs come back for the same percentile math.
    """
    child_path = write_echo_child()
    name = probe_session_name("latrelay")
    created = False
    try:
        await request_json(port, {"t": "create", "s": name, "cmd": echo_child_command(child_path),
                                  "cols": 100, "rows": 30}, timeout=20.0)
        created = True
        base = os.environ.get("MUXD_RELAY_WS", "").strip()
        if base:
            return await _relay_series_direct(base, name, samples, per_sample_timeout, deadline)
        return _relay_series_over_ssh(name, samples, per_sample_timeout, deadline)
    finally:
        if created:
            await kill_probe_session(port, name)
        with contextlib.suppress(OSError):
            os.unlink(child_path)


async def _relay_series_direct(base, name, samples, per_sample_timeout, deadline):
    websockets = _websockets()
    from urllib.parse import quote
    url = "%s/ws?session=%s&cols=100&rows=30&dev=latency-probe&label=latency-probe" % (
        base.rstrip("/"), quote(name))
    async with websockets.connect(url, max_size=8_000_000, open_timeout=15,
                                  ping_interval=None) as ws:
        transport = RelayWsTransport(ws)
        await wait_for_ready(transport, deadline=deadline)
        return await measure_series(transport, samples, per_sample_timeout=per_sample_timeout,
                                    deadline=deadline)


def _relay_series_over_ssh(name, samples, per_sample_timeout, deadline):
    sys.path.insert(0, os.path.join(REPO_ROOT, "muxd", "tests"))
    try:
        import test_vps_smoke as smoke
    except Exception as e:
        raise ProbeError("relay series needs muxd/tests/test_vps_smoke.py plumbing: %s" % e,
                         EXIT_ERROR)
    wall = max(60, int(samples * per_sample_timeout / 4) + 60)
    script = """
const WebSocket = require('ws');
const name = %(name)s, samples = %(samples)d, ready = %(ready)s;
const alphabet = 'abcdefghijklmnopqrstuvwxyz0123456789';
const perSample = %(per)d;
let seen = '', i = 0, armed = null, t0 = 0, isReady = false, done = false;
const rtts = [];
function finish(obj, code) { if (done) return; done = true; console.log(JSON.stringify(obj));
  try { ws.close(); } catch {} process.exit(code); }
const ws = new WebSocket('ws://127.0.0.1:7682/ws?session=' + encodeURIComponent(name) +
  '&cols=100&rows=30&dev=latency-probe&label=latency-probe');
const wall = setTimeout(() => finish({ok:false, error:'wall-timeout', rtts}, 5), %(wall)d * 1000);
let perTimer = null;
function next() {
  if (i >= samples) { clearTimeout(wall); finish({ok:true, rtts}, 0); return; }
  seen = ''; armed = alphabet[i %% alphabet.length];
  t0 = process.hrtime.bigint();
  ws.send('i' + armed);
  perTimer = setTimeout(() => finish({ok:false, error:'sample-timeout', sample:i, rtts}, 5), perSample);
}
ws.on('open', () => {});
ws.on('message', data => {
  const text = Buffer.from(data).toString('latin1');
  if (!isReady) { seen += text; if (seen.includes(ready)) { isReady = true; setTimeout(next, 250); } return; }
  seen += text;
  if (armed !== null && seen.includes(armed)) {
    if (perTimer) clearTimeout(perTimer);
    rtts.push(Number(process.hrtime.bigint() - t0) / 1e6);
    armed = null; i += 1; next();
  }
});
ws.on('close', () => { if (!done) { clearTimeout(wall); finish({ok:false, error:'closed', rtts}, 3); } });
ws.on('error', err => { if (!done) { clearTimeout(wall); finish({ok:false, error:err.message, rtts}, 4); } });
""" % {"name": json.dumps(name), "samples": int(samples), "ready": json.dumps(READY_TOKEN),
       "per": int(per_sample_timeout * 1000), "wall": wall}
    result = smoke.remote_node_json(script, timeout=wall)
    if not result.get("ok"):
        raise ProbeError("relay series failed on the VPS: %s (got %d samples)"
                         % (result.get("error"), len(result.get("rtts") or [])), EXIT_ERROR)
    return [float(v) for v in result.get("rtts") or []]


# --------------------------------------------------------------------------- signed series

PRINCIPAL_AUTH_CANDIDATES = (
    os.path.join("relay", "public", "principal-auth.js"),
    os.path.join("relay", "principal-auth.js"),
    os.path.join("app", "data", "web", "principal-auth.js"),
    os.path.join("muxd", "principal-auth.js"),
)


def find_principal_auth(root=REPO_ROOT):
    """Locate the principal-auth.js the enforced path signs with, or None."""
    override = os.environ.get("MUXD_PRINCIPAL_AUTH", "").strip()
    if override:
        return override if os.path.isfile(override) else None
    for rel in PRINCIPAL_AUTH_CANDIDATES:
        path = os.path.join(root, rel)
        if os.path.isfile(path):
            return path
    return None


async def run_signed_series(port, samples, per_sample_timeout, deadline):
    auth = find_principal_auth()
    if auth is None:
        raise ProbeError(
            "signed-path series requested but the trust plumbing is not on this branch: no "
            "principal-auth.js found under %s (looked at %s; override with MUXD_PRINCIPAL_AUTH). "
            "muxd here advertises no signature-verification capability, so a signed channel "
            "cannot be opened and no signed output can be verified. Re-run --signed once the "
            "authorized-principal workstream merges; until then the unsigned series are "
            "diagnostic only." % (REPO_ROOT, ", ".join(PRINCIPAL_AUTH_CANDIDATES)),
            EXIT_SIGNED_UNAVAILABLE)
    info = await request_json(port, {"t": "info"}, timeout=8.0)
    caps = set(info.get("caps") or [])
    if not ({"principal", "signedInput", "signedOutput"} & caps):
        raise ProbeError(
            "principal-auth.js is present at %s but the running muxd (protocol %s) advertises no "
            "signature-verification capability: caps=%s. Restart MuxdSessionHost onto a muxd that "
            "enforces authorized-principal proof before measuring the enforced path."
            % (auth, info.get("protocol", "?"), sorted(caps)),
            EXIT_SIGNED_UNAVAILABLE)
    raise ProbeError(
        "signed-path measurement is wired to principal-auth.js at %s and a signature-capable "
        "muxd, but the signed channel-open handshake has no implementation on this branch."
        % auth, EXIT_SIGNED_UNAVAILABLE)


# --------------------------------------------------------------------------- cli

def write_json_artifact(path, signed_available, exit_code, series):
    """Write the machine-readable run record for CI/dashboards.

    Written on every path that measured at least one series - including the over-budget and
    signed-unavailable exits - so a failing run is still inspectable without scraping stdout.
    """
    parent = os.path.dirname(os.path.abspath(path))
    if parent:
        os.makedirs(parent, exist_ok=True)
    payload = {
        "signed_available": bool(signed_available),
        "exit_code": int(exit_code),
        "series": series,
    }
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(payload, fh, indent=2)
        fh.write("\n")


def build_parser():
    p = argparse.ArgumentParser(
        prog="latency_probe",
        description="input->echo round-trip latency probe for muxd (measure-only)")
    p.add_argument("--local", action="store_true", help="measure the local muxd ws path")
    p.add_argument("--relay", action="store_true",
                   help="also measure the relay ws path (needs MUXD_VPS_TESTS=1)")
    p.add_argument("--signed", action="store_true",
                   help="measure the SIGNED/enforced path (authoritative when available)")
    p.add_argument("--samples", type=int, default=DEFAULT_SAMPLES,
                   help="samples per series (default %d)" % DEFAULT_SAMPLES)
    p.add_argument("--budget-p95-ms", type=float, default=None,
                   help="p95 budget in ms for the local and signed series only "
                        "(default %.0f); the relay has its own knob"
                        % DEFAULT_LOCAL_BUDGET_MS)
    p.add_argument("--relay-budget-p95-ms", type=float, default=None,
                   help="p95 budget in ms for the relay series (default %.0f)"
                        % DEFAULT_RELAY_BUDGET_MS)
    p.add_argument("--timeout", type=float, default=120.0,
                   help="hard wall clamp in seconds for the whole probe (default 120)")
    p.add_argument("--per-sample-timeout", type=float, default=3.0,
                   help="give up on one sample's echo after this many seconds (default 3)")
    p.add_argument("--port", type=int, default=MUXCTL_PORT,
                   help="local muxd port (default $MUXCTL_PORT or 7699)")
    p.add_argument("--json", action="store_true", help="emit the summaries as JSON too")
    p.add_argument("--json-out", metavar="PATH", default=None,
                   help="write the run record (series + exit code) as JSON to PATH "
                        "(parent directories are created)")
    return p


async def run(args):
    if args.samples < 1:
        raise ProbeError("--samples must be >= 1", EXIT_ERROR)
    if args.timeout <= 0:
        raise ProbeError("--timeout must be > 0", EXIT_ERROR)
    deadline = time.monotonic() + args.timeout
    local_budget = args.budget_p95_ms if args.budget_p95_ms is not None else DEFAULT_LOCAL_BUDGET_MS
    # --budget-p95-ms is the local/signed budget ONLY. Letting it fall through to the relay judged
    # an internet round trip against a loopback number: a 50ms local budget fails any WAN hop.
    relay_budget = (args.relay_budget_p95_ms if args.relay_budget_p95_ms is not None
                    else DEFAULT_RELAY_BUDGET_MS)

    want_local = args.local or not (args.relay or args.signed)
    reports = []
    signed_available = False
    pending_code = None

    if args.signed:
        # authoritative series first: if the enforced path cannot be measured, say so loudly - but
        # keep going. An unmeasurable signed path is a verdict about the signed path, not a reason
        # to throw away the diagnostic series the user explicitly asked for.
        try:
            signed = await run_signed_series(args.port, args.samples, args.per_sample_timeout,
                                             deadline)
        except ProbeError as e:
            if e.code != EXIT_SIGNED_UNAVAILABLE:
                raise
            pending_code = EXIT_SIGNED_UNAVAILABLE
            sys.stderr.write("[probe] signed (enforced) series UNAVAILABLE - reporting the "
                             "remaining series as DIAGNOSTIC ONLY and exiting %d: %s\n"
                             % (EXIT_SIGNED_UNAVAILABLE, e))
        else:
            signed_available = True
            reports.append((summarize("signed (enforced)", signed), local_budget, True))

    if want_local:
        samples = await run_local_series(args.port, args.samples, args.per_sample_timeout, deadline)
        reports.append((summarize("local muxd ws", samples), local_budget, not args.signed))

    if args.relay:
        if not relay_enabled():
            sys.stderr.write("[probe] --relay skipped: set MUXD_VPS_TESTS=1 to enable it\n")
        else:
            samples = await run_relay_series(args.port, args.samples, args.per_sample_timeout,
                                             deadline)
            reports.append((summarize("relay ws", samples), relay_budget, not args.signed))

    if not reports:
        if pending_code is not None:
            return pending_code
        raise ProbeError("no series ran", EXIT_ERROR)

    print("latency_probe - input->echo round trip (measure-only; no tuning in this probe)")
    for summary, budget, enforced in reports:
        print(format_summary(summary, budget, enforced))
    if signed_available:
        print("  note: the signed series is authoritative; unsigned series are diagnostic only.")
    elif args.signed:
        print("  note: the signed series could NOT be measured here - everything above is "
              "DIAGNOSTIC ONLY and this run exits %d." % EXIT_SIGNED_UNAVAILABLE)
    else:
        print("  note: no signed series in this run - these numbers are DIAGNOSTIC ONLY; the "
              "enforced (signed) path is the shipping path.")
    series = [{"budget_p95_ms": b, "enforced": e, **s} for s, b, e in reports]
    if args.json:
        print(json.dumps(series, indent=2))

    over = [s for s, b, e in reports if e and (not s["count"] or s["p95"] > b)]
    for s in over:
        print("BUDGET FAILURE: %s p95=%s exceeds budget" % (s["label"], s["p95"]),
              file=sys.stderr)

    if pending_code is not None:
        # 'unmeasurable' outranks 'slow': a diagnostic series blowing its budget must not rewrite
        # the verdict that the enforced path could not be measured at all.
        code = pending_code
    elif over:
        code = EXIT_OVER_BUDGET
    else:
        code = EXIT_OK
    if args.json_out:
        write_json_artifact(args.json_out, signed_available, code, series)
    return code


def main(argv=None):
    args = build_parser().parse_args(argv)
    try:
        return asyncio.run(run(args))
    except ProbeError as e:
        sys.stderr.write("[probe] %s\n" % e)
        return e.code
    except KeyboardInterrupt:
        sys.stderr.write("[probe] interrupted\n")
        return EXIT_ERROR


if __name__ == "__main__":
    sys.exit(main())
