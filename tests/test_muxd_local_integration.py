import asyncio
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

try:
    import websockets
except ImportError:  # pragma: no cover - covered by unittest skip
    websockets = None


REPO = Path(__file__).resolve().parents[1]
MUXD = REPO / "muxd.py"
CREATE_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


async def request_json(port, payload, timeout=8):
    async with websockets.connect(f"ws://127.0.0.1:{port}", open_timeout=timeout, close_timeout=1, ping_interval=None) as ws:
        await asyncio.wait_for(ws.send(json.dumps(payload)), timeout)
        raw = await asyncio.wait_for(ws.recv(), timeout)
        if isinstance(raw, (bytes, bytearray)):
            raw = bytes(raw).decode("utf-8", "replace")
        return json.loads(raw)


def run_request(port, payload, timeout=8):
    return asyncio.run(request_json(port, payload, timeout=timeout))


async def attach_and_roundtrip(port, name, command, marker, timeout=12):
    async with websockets.connect(f"ws://127.0.0.1:{port}", open_timeout=timeout, close_timeout=1, ping_interval=None) as ws:
        await asyncio.wait_for(ws.send(json.dumps({"t": "attach", "s": name, "cols": 100, "rows": 28, "sb": 0})), timeout)
        await asyncio.wait_for(ws.send(command.encode("utf-8")), timeout)
        deadline = time.time() + timeout
        seen = bytearray()
        while time.time() < deadline:
            raw = await asyncio.wait_for(ws.recv(), max(0.1, deadline - time.time()))
            if isinstance(raw, str):
                raw = raw.encode("utf-8", "replace")
            seen += raw
            if marker.encode("utf-8") in seen:
                return bytes(seen)
    raise AssertionError(f"marker {marker!r} not seen through attach; saw={seen[-1000:]!r}")


async def create_while_hammering_info(port, name, cmd, timeout=14):
    create_task = asyncio.create_task(request_json(port, {"t": "create", "s": name, "cmd": cmd}, timeout=timeout))
    samples = []
    deadline = time.perf_counter() + timeout
    while not create_task.done() and time.perf_counter() < deadline:
        started = time.perf_counter()
        try:
            info = await request_json(port, {"t": "info"}, timeout=2)
            samples.append((time.perf_counter() - started, info))
        except Exception as exc:
            samples.append((time.perf_counter() - started, exc))
        await asyncio.sleep(0.05)
    created = await asyncio.wait_for(create_task, timeout=timeout)
    return created, samples


class DisposableMuxd:
    def __init__(self):
        self.root = Path(tempfile.mkdtemp(prefix="muxd-it-"))
        self.port = free_port()
        mux_home = self.root / "muxd"
        mux_home.mkdir(parents=True, exist_ok=True)
        (mux_home / "muxd.env").write_text(
            "\n".join(
                [
                    "MUX_HOST_TOKEN=test-token",
                    "RELAY_LAN=ws://127.0.0.1:1/host",
                    f"LOCAL_PORT={self.port}",
                    f"INSTANCE_MUTEX_NAME=Local\\CodexMuxdTest-{self.port}",
                    f"DEFAULT_CWD={self.root}",
                    "LOCAL_FIRST_TIMEOUT=1",
                    "LOOP_WATCHDOG_WARN=120",
                    "",
                ]
            ),
            encoding="utf-8",
        )
        env = os.environ.copy()
        env["HOME"] = str(self.root)
        env["USERPROFILE"] = str(self.root)
        env["HOMEDRIVE"] = self.root.drive or "C:"
        env["HOMEPATH"] = str(self.root)[len(self.root.drive) :] if self.root.drive else str(self.root)
        env["PYTHONUNBUFFERED"] = "1"
        env["INSTANCE_MUTEX_NAME"] = f"Local\\CodexMuxdTest-{self.port}"
        self.proc = subprocess.Popen(
            [sys.executable, str(MUXD)],
            cwd=str(REPO),
            env=env,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=CREATE_NO_WINDOW,
        )
        self.wait_ready()

    def wait_ready(self):
        deadline = time.time() + 18
        last_error = None
        while time.time() < deadline:
            if self.proc.poll() is not None:
                raise AssertionError(self.diagnostics("muxd exited before local server became ready"))
            try:
                info = run_request(self.port, {"t": "info"}, timeout=1)
                if info.get("t") == "info":
                    return
            except Exception as exc:
                last_error = exc
                time.sleep(0.2)
        raise AssertionError(self.diagnostics(f"muxd local server was not ready: {last_error}"))

    def diagnostics(self, reason):
        log_path = self.root / "muxd" / "muxd.log"
        log_tail = ""
        if log_path.exists():
            log_tail = "\n".join(log_path.read_text(encoding="utf-8", errors="replace").splitlines()[-40:])
        return f"{reason}\nlog:\n{log_tail}"

    def close(self):
        if self.proc.poll() is None:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=6)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(timeout=6)
        shutil.rmtree(self.root, ignore_errors=True)


@unittest.skipIf(websockets is None, "websockets package is required for muxd integration tests")
@unittest.skipIf(os.name != "nt", "muxd ConPTY integration tests require Windows")
class MuxdLocalIntegrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.muxd = DisposableMuxd()

    @classmethod
    def tearDownClass(cls):
        cls.muxd.close()

    def session(self, name):
        listing = run_request(self.muxd.port, {"t": "ls"})
        self.assertEqual(listing.get("t"), "ls")
        for sess in listing.get("list", []):
            if sess.get("name") == name:
                return sess
        return None

    def wait_for_tail(self, name, marker, timeout=12):
        deadline = time.time() + timeout
        last = None
        while time.time() < deadline:
            last = self.session(name)
            if last and marker in (last.get("tail") or ""):
                return last
            time.sleep(0.25)
        self.fail(f"marker {marker!r} not found in {name}; last session={last}")

    def kill(self, name):
        try:
            run_request(self.muxd.port, {"t": "kill", "s": name}, timeout=4)
        except Exception:
            pass

    def test_shell_only_session_relaunches_when_command_arrives(self):
        name = "it-shell-relaunch"
        marker = f"MUXD_IT_RELAUNCH_{int(time.time() * 1000)}"
        self.kill(name)
        try:
            first = run_request(self.muxd.port, {"t": "create", "s": name, "cols": 90, "rows": 24})
            self.assertTrue(first.get("created"))
            shell = self.session(name)
            self.assertTrue(shell.get("alive"))
            self.assertTrue(shell.get("shellOnly"))
            self.assertFalse(shell.get("hasCommand"))

            cmd = f"Write-Output '{marker}'"
            second = run_request(self.muxd.port, {"t": "create", "s": name, "cmd": cmd, "cols": 90, "rows": 24}, timeout=12)
            self.assertTrue(second.get("created"))
            relaunched = self.wait_for_tail(name, marker)
            self.assertTrue(relaunched.get("alive"))
            self.assertTrue(relaunched.get("hasCommand"))
            self.assertFalse(relaunched.get("shellOnly"))
            self.assertEqual(relaunched.get("kind"), "command")
            self.assertTrue(relaunched.get("cmdSig"))
        finally:
            self.kill(name)

    def test_same_command_reuses_existing_live_session(self):
        name = "it-same-command"
        marker = f"MUXD_IT_SAME_{int(time.time() * 1000)}"
        cmd = f"Write-Output '{marker}'"
        self.kill(name)
        try:
            first = run_request(self.muxd.port, {"t": "create", "s": name, "cmd": cmd}, timeout=12)
            self.assertTrue(first.get("created"))
            before = self.wait_for_tail(name, marker)

            second = run_request(self.muxd.port, {"t": "create", "s": name, "cmd": cmd}, timeout=12)
            after = self.session(name)
            self.assertFalse(second.get("created"))
            self.assertEqual(before.get("created"), after.get("created"))
            self.assertEqual(before.get("cmdSig"), after.get("cmdSig"))
        finally:
            self.kill(name)

    def test_explicit_relaunch_reuses_saved_command(self):
        name = "it-explicit-relaunch"
        marker = f"MUXD_IT_EXPLICIT_{int(time.time() * 1000)}"
        cmd = f"Write-Output '{marker}'"
        self.kill(name)
        try:
            first = run_request(self.muxd.port, {"t": "create", "s": name, "cmd": cmd}, timeout=12)
            self.assertTrue(first.get("created"))
            before = self.wait_for_tail(name, marker)
            time.sleep(0.05)

            second = run_request(self.muxd.port, {"t": "create", "s": name, "relaunch": True}, timeout=12)
            after = self.wait_for_tail(name, marker)

            self.assertTrue(second.get("created"))
            self.assertNotEqual(before.get("created"), after.get("created"))
            self.assertEqual(before.get("cmdSig"), after.get("cmdSig"))
            self.assertTrue(after.get("hasCommand"))
        finally:
            self.kill(name)

    def test_explicit_relaunch_without_saved_command_is_refused(self):
        name = "it-relaunch-no-command"
        self.kill(name)
        try:
            first = run_request(self.muxd.port, {"t": "create", "s": name}, timeout=12)
            self.assertTrue(first.get("created"))
            before = self.session(name)

            second = run_request(self.muxd.port, {"t": "create", "s": name, "relaunch": True}, timeout=12)
            after = self.session(name)

            self.assertEqual(second.get("t"), "err")
            self.assertIn("no saved command", second.get("m", ""))
            self.assertEqual(before.get("created"), after.get("created"))
            self.assertTrue(after.get("shellOnly"))
        finally:
            self.kill(name)

    def test_different_command_replaces_wrong_live_session(self):
        name = "it-different-command"
        old_marker = f"MUXD_IT_OLD_{int(time.time() * 1000)}"
        new_marker = f"MUXD_IT_NEW_{int(time.time() * 1000)}"
        self.kill(name)
        try:
            run_request(self.muxd.port, {"t": "create", "s": name, "cmd": f"Write-Output '{old_marker}'"}, timeout=12)
            old = self.wait_for_tail(name, old_marker)

            second = run_request(self.muxd.port, {"t": "create", "s": name, "cmd": f"Write-Output '{new_marker}'"}, timeout=12)
            new = self.wait_for_tail(name, new_marker)
            self.assertTrue(second.get("created"))
            self.assertNotEqual(old.get("created"), new.get("created"))
            self.assertNotEqual(old.get("cmdSig"), new.get("cmdSig"))
            self.assertNotIn(old_marker, new.get("tail") or "")
        finally:
            self.kill(name)

    def test_attach_missing_session_is_refused_without_spawning(self):
        name = "it-missing-attach"
        self.kill(name)

        result = run_request(self.muxd.port, {"t": "attach", "s": name})

        self.assertEqual(result.get("t"), "err")
        self.assertIn("no such session", result.get("m", ""))
        self.assertIsNone(self.session(name))

    def test_kill_removes_session_from_manifest_and_listing(self):
        name = "it-kill"
        self.kill(name)
        run_request(self.muxd.port, {"t": "create", "s": name}, timeout=12)
        self.assertIsNotNone(self.session(name))

        killed = run_request(self.muxd.port, {"t": "kill", "s": name}, timeout=6)

        self.assertEqual(killed.get("t"), "killed")
        self.assertIsNone(self.session(name))

    def test_attach_stream_accepts_input_and_returns_output(self):
        name = "it-attach-io"
        marker = f"MUXD_IT_ATTACH_{int(time.time() * 1000)}"
        self.kill(name)
        try:
            run_request(self.muxd.port, {"t": "create", "s": name}, timeout=12)

            seen = asyncio.run(attach_and_roundtrip(self.muxd.port, name, f"Write-Output '{marker}'\r", marker))

            self.assertIn(marker.encode("utf-8"), seen)
            tail = self.wait_for_tail(name, marker)
            self.assertTrue(tail.get("shellOnly"))
        finally:
            self.kill(name)

    def test_create_does_not_block_info_requests(self):
        name = "it-nonblocking-create"
        marker = f"MUXD_IT_NONBLOCK_{int(time.time() * 1000)}"
        self.kill(name)
        try:
            created, samples = asyncio.run(
                create_while_hammering_info(self.muxd.port, name, f"Start-Sleep -Milliseconds 250; Write-Output '{marker}'")
            )

            self.assertTrue(created.get("created"))
            self.assertGreaterEqual(len(samples), 1)
            slow = [round(elapsed, 3) for elapsed, result in samples if elapsed > 1.0 or isinstance(result, Exception)]
            self.assertEqual([], slow, f"info requests stalled or failed during create; samples={samples!r}")
            self.wait_for_tail(name, marker)
        finally:
            self.kill(name)
