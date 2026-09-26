import asyncio
import base64
import ctypes
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import unittest
import urllib.request
from pathlib import Path
from ctypes import wintypes

try:
    import websockets
except ImportError:  # pragma: no cover - covered by unittest skip
    websockets = None


REPO = Path(__file__).resolve().parents[1]
MUXD = REPO / "muxd.py"
MUXRUN = REPO / "muxrun.py"
CREATE_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)
CREATE_NEW_CONSOLE = getattr(subprocess, "CREATE_NEW_CONSOLE", 0x00000010)
DETACHED_PROCESS = getattr(subprocess, "DETACHED_PROCESS", 0x00000008)


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def process_alive(pid):
    k = ctypes.windll.kernel32
    k.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    k.OpenProcess.restype = wintypes.HANDLE
    k.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    k.GetExitCodeProcess.restype = wintypes.BOOL
    k.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = k.OpenProcess(0x1000, False, int(pid))  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return False
    try:
        exit_code = wintypes.DWORD()
        if not k.GetExitCodeProcess(handle, ctypes.byref(exit_code)):
            return False
        return exit_code.value == 259  # STILL_ACTIVE
    finally:
        k.CloseHandle(handle)


def claude_registry_command(session_id, marker):
    """PowerShell fake agent that publishes a verifiably-live Claude registry record for its own PID.

    muxd fences registry records against PID recycling by comparing the record's startedAt with the
    live process creation time, so the record must carry this process's real start time; a hard-coded
    stamp would be rejected as a recycled PID.
    """
    return (
        "$d=Join-Path $env:USERPROFILE '.claude/sessions'; "
        "New-Item -ItemType Directory -Force $d | Out-Null; "
        "$s=[DateTimeOffset]::new((Get-Process -Id $PID).StartTime).ToUnixTimeMilliseconds(); "
        f"@{{pid=$PID;sessionId='{session_id}';startedAt=$s}} | ConvertTo-Json | "
        "Set-Content (Join-Path $d \"$PID.json\"); "
        f"Write-Output '{marker}'; "
        "while($true){Start-Sleep -Milliseconds 200}"
    )


async def request_json(port, payload, timeout=8):
    async with websockets.connect(f"ws://127.0.0.1:{port}", open_timeout=timeout, close_timeout=1, ping_interval=None) as ws:
        await asyncio.wait_for(ws.send(json.dumps(payload)), timeout)
        raw = await asyncio.wait_for(ws.recv(), timeout)
        if isinstance(raw, (bytes, bytearray)):
            raw = bytes(raw).decode("utf-8", "replace")
        return json.loads(raw)


def run_request(port, payload, timeout=8):
    return asyncio.run(request_json(port, payload, timeout=timeout))


async def browser_origin_close_code(port, timeout=8):
    try:
        async with websockets.connect(
            f"ws://127.0.0.1:{port}",
            origin="https://untrusted.example",
            open_timeout=timeout,
            close_timeout=1,
            ping_interval=None,
        ) as ws:
            await asyncio.wait_for(ws.send(json.dumps({"t": "info"})), timeout)
            await asyncio.wait_for(ws.recv(), timeout)
    except websockets.exceptions.ConnectionClosed as exc:
        if exc.rcvd is not None:
            return exc.rcvd.code
        return exc.sent.code if exc.sent is not None else None
    raise AssertionError("browser-origin websocket was allowed to use the local muxd protocol")


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


async def info_during_slow_create_persistence(port, fault_path, name, cmd, timeout=18):
    create_task = asyncio.create_task(
        request_json(port, {"t": "create", "s": name, "cmd": cmd}, timeout=timeout)
    )
    deadline = time.perf_counter() + timeout
    while fault_path.exists() and time.perf_counter() < deadline:
        await asyncio.sleep(0.01)
    if fault_path.exists():
        raise TimeoutError("slow persistence fault was not consumed")

    async def timed_info():
        started = time.perf_counter()
        try:
            result = await request_json(port, {"t": "info"}, timeout=2)
        except Exception as exc:
            result = exc
        return time.perf_counter() - started, result

    samples = await asyncio.gather(*(timed_info() for _ in range(4)))
    created = await asyncio.wait_for(create_task, timeout=timeout)
    return created, samples


async def concurrent_requests(port, payloads, timeout=14):
    return await asyncio.gather(
        *(request_json(port, payload, timeout=timeout) for payload in payloads)
    )

async def input_during_slow_create(port, fault_path, create_payload, input_payload, timeout=18):
    create_task = asyncio.create_task(
        request_json(port, create_payload, timeout=timeout)
    )
    deadline = time.perf_counter() + timeout
    while fault_path.exists() and time.perf_counter() < deadline:
        await asyncio.sleep(0.01)
    if fault_path.exists():
        raise TimeoutError("slow persistence fault was not consumed")

    started = time.perf_counter()
    input_result = await request_json(port, input_payload, timeout=timeout)
    input_elapsed = time.perf_counter() - started
    created = await asyncio.wait_for(create_task, timeout=timeout)
    return created, input_result, input_elapsed


async def attach_during_slow_create(port, fault_path, create_payload, target, timeout=18):
    create_task = asyncio.create_task(
        request_json(port, create_payload, timeout=timeout)
    )
    deadline = time.perf_counter() + timeout
    while fault_path.exists() and time.perf_counter() < deadline:
        await asyncio.sleep(0.01)
    if fault_path.exists():
        raise TimeoutError("slow persistence fault was not consumed")

    started = time.perf_counter()
    async with websockets.connect(
        f"ws://127.0.0.1:{port}",
        open_timeout=timeout,
        close_timeout=1,
        ping_interval=None,
    ) as ws:
        await ws.send(json.dumps({"t": "attach", "s": target, "sb": 60000}))
        initial = await asyncio.wait_for(ws.recv(), timeout=timeout)
    attach_elapsed = time.perf_counter() - started
    created = await asyncio.wait_for(create_task, timeout=timeout)
    return created, initial, attach_elapsed


async def owner_collision(port, name, cmd, timeout=14):
    first = await websockets.connect(
        f"ws://127.0.0.1:{port}",
        open_timeout=timeout,
        close_timeout=1,
        ping_interval=None,
    )
    try:
        await first.send(json.dumps({
            "t": "owner",
            "s": name,
            "cmd": cmd,
            "ownerKey": "a" * 32,
        }))
        first_response = json.loads(await asyncio.wait_for(first.recv(), timeout))
        second_response = await request_json(
            port,
            {
                "t": "owner",
                "s": name,
                "cmd": cmd,
                "ownerKey": "b" * 32,
            },
            timeout=timeout,
        )
        await first.send(json.dumps({"t": "dead"}))
        return first_response, second_response
    finally:
        await first.close()


class DisposableMuxd:
    def __init__(self, relay_port=1):
        self.root = Path(tempfile.mkdtemp(prefix="muxd-it-"))
        self.port = free_port()
        mux_home = self.root / "muxd"
        mux_home.mkdir(parents=True, exist_ok=True)
        (mux_home / "muxd.env").write_text(
            "\n".join(
                [
                    "MUX_HOST_TOKEN=test-token",
                    f"RELAY_LAN=ws://127.0.0.1:{int(relay_port)}/host",
                    f"LOCAL_PORT={self.port}",
                    f"INSTANCE_MUTEX_NAME=Local\\CodexMuxdTest-{self.port}",
                    f"DEFAULT_CWD={self.root}",
                    f"LAUNCH_CLAIM_ROOT={self.root / 'launch-claims'}",
                    "LOCAL_FIRST_TIMEOUT=5",
                    "LOOP_WATCHDOG_WARN=120",
                    "",
                ]
            ),
            encoding="utf-8",
        )
        self.env = os.environ.copy()
        self.env["MUX_HOST_TOKEN"] = "test-token"
        self.env["HOME"] = str(self.root)
        self.env["USERPROFILE"] = str(self.root)
        self.env["HOMEDRIVE"] = self.root.drive or "C:"
        self.env["HOMEPATH"] = str(self.root)[len(self.root.drive) :] if self.root.drive else str(self.root)
        self.env["PYTHONUNBUFFERED"] = "1"
        self.env["INSTANCE_MUTEX_NAME"] = f"Local\\CodexMuxdTest-{self.port}"
        self.env["MUXCTL_PORT"] = str(self.port)
        self.env["MUXD_TEST_PERSIST_FAULT_FILE"] = str(self.root / "persist-fault.json")
        self.start_process()

    def start_process(self):
        self.proc = subprocess.Popen(
            [sys.executable, str(MUXD)],
            cwd=str(REPO),
            env=self.env,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=CREATE_NO_WINDOW,
        )
        self.wait_ready()

    def stop_process(self):
        if self.proc.poll() is None:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=6)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(timeout=6)

    def restart(self):
        self.stop_process()
        self.start_process()

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

    def fail_persistence(self, stage, file="sessions.json", after=0):
        (self.root / "persist-fault.json").write_text(
            json.dumps({"file": file, "stage": stage, "after": after}),
            encoding="utf-8",
        )

    def delay_persistence(self, stage, delay_ms, file="sessions.json"):
        (self.root / "persist-fault.json").write_text(
            json.dumps({
                "file": file,
                "stage": stage,
                "delayMs": delay_ms,
                "fail": False,
            }),
            encoding="utf-8",
        )

    def close(self):
        self.stop_process()
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

    def test_local_control_rejects_browser_origin_before_first_frame(self):
        self.assertEqual(1008, asyncio.run(browser_origin_close_code(self.muxd.port)))
        info = run_request(self.muxd.port, {"t": "info"})
        self.assertEqual("info", info.get("t"))

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
            self.assertNotIn("cmdSig", relaunched)
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
            self.assertNotIn("cmdSig", after)
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
            self.assertTrue(after.get("hasCommand"))
            self.assertNotIn("cmdSig", after)
        finally:
            self.kill(name)

    def test_replayed_relaunch_intent_returns_prior_result_without_restarting_again(self):
        name = "it-idempotent-relaunch"
        intent_id = f"relaunch-{int(time.time() * 1000)}"
        counter = self.muxd.root / "relaunch-count.txt"
        marker = f"MUXD_IT_IDEMPOTENT_{int(time.time() * 1000)}"
        quoted_counter = str(counter).replace("'", "''")
        cmd = f"Add-Content -LiteralPath '{quoted_counter}' -Value launch; Write-Output '{marker}'"
        self.kill(name)
        try:
            first = run_request(self.muxd.port, {"t": "create", "s": name, "cmd": cmd}, timeout=12)
            self.assertTrue(first.get("created"))
            self.wait_for_tail(name, marker)

            request = {"t": "create", "s": name, "relaunch": True, "intentId": intent_id}
            relaunched = run_request(self.muxd.port, request, timeout=12)
            self.assertTrue(relaunched.get("created"))
            self.wait_for_tail(name, marker)

            self.muxd.restart()
            replayed = run_request(self.muxd.port, request, timeout=12)
            self.assertEqual(relaunched, replayed)
            time.sleep(1.5)
            self.assertEqual(counter.read_text(encoding="utf-8").splitlines(), ["launch", "launch"])

            conflict = run_request(
                self.muxd.port,
                {"t": "create", "s": name, "relaunch": True, "heal": True, "intentId": intent_id},
                timeout=12,
            )
            self.assertEqual("err", conflict.get("t"))
            self.assertIn("different payload", conflict.get("m", ""))
        finally:
            self.kill(name)

    def test_abandoned_accepted_create_intent_does_not_brick_the_session_name(self):
        name = "it-abandoned-intent"
        self.kill(name)
        self.muxd.stop_process()
        manifest_path = self.muxd.root / "muxd" / "sessions.json"
        backup_path = Path(str(manifest_path) + ".bak")
        if backup_path.exists():
            backup_path.unlink()
        old_key = "local:create:abandoned-intent"
        manifest_path.write_text(json.dumps({
            "version": 2,
            "sessions": {},
            "intents": {
                old_key: {
                    "kind": "create",
                    "session": name,
                    "fingerprint": "a" * 64,
                    "status": "accepted",
                    "result": {},
                    "createdAt": time.time() - 10,
                    "updatedAt": time.time() - 10,
                },
            },
        }), encoding="utf-8")
        self.muxd.start_process()
        try:
            result = run_request(
                self.muxd.port,
                {"t": "create", "s": name, "intentId": "replacement-intent"},
                timeout=12,
            )
            self.assertEqual("created", result.get("t"), result)
            persisted = json.loads(manifest_path.read_text(encoding="utf-8"))
            self.assertEqual("failed", persisted["intents"][old_key]["status"])
            self.assertEqual(
                "completed",
                persisted["intents"]["local:create:replacement-intent"]["status"],
            )
        finally:
            self.kill(name)

    def test_distinct_concurrent_create_intents_serialize_and_converge(self):
        name = "it-distinct-intents"
        self.kill(name)
        try:
            results = asyncio.run(concurrent_requests(
                self.muxd.port,
                [
                    {"t": "create", "s": name, "intentId": "distinct-intent-a"},
                    {"t": "create", "s": name, "intentId": "distinct-intent-b"},
                ],
                timeout=12,
            ))
            self.assertTrue(all(result.get("t") == "created" for result in results), results)
            self.assertEqual(1, sum(bool(result.get("created")) for result in results), results)
        finally:
            self.kill(name)

    def test_protected_resume_preserves_occupied_generation(self):
        name = "it-protected-resume"
        self.kill(name)
        try:
            original = {"t": "create", "s": name, "cmd": "Write-Output original; # codex resume original-chat", "sessionId": "original-chat"}
            run_request(self.muxd.port, original, timeout=15)
            before = self.session(name)
            refused = run_request(self.muxd.port, {**original, "resumeOnly": True,
                "cmd": "Write-Output replacement; # codex resume different-chat", "sessionId": "different-chat"}, timeout=15)
            self.assertEqual("err", refused.get("t"), refused)
            after = self.session(name)
            self.assertEqual(before["generationId"], after["generationId"])
            self.assertEqual("original-chat", after["sessionId"])
            self.assertTrue(after["alive"])
            accepted = run_request(self.muxd.port, {**original, "resumeOnly": True}, timeout=15)
            self.assertEqual("created", accepted.get("t"), accepted)
            self.assertEqual(before["generationId"], self.session(name)["generationId"])
        finally:
            self.kill(name)

    def test_fenced_input_refuses_wrong_generation_and_preserves_replay(self):
        name = "it-fenced-input"
        self.kill(name)
        try:
            created = run_request(self.muxd.port, {"t": "create", "s": name, "cmd": "Write-Output ready; # codex resume input-chat", "ids": ["input-chat"]}, timeout=15)
            self.assertNotEqual("err", created.get("t"), created)
            rows = run_request(self.muxd.port, {"t": "ls"})["list"]
            row = next(row for row in rows if row["name"] == name)
            payload = {"t": "input", "s": name, "sessionId": row["sessionId"],
                       "generationId": "wrong-generation", "intentId": "fenced-input-test",
                       "d": base64.b64encode(b"# fenced input").decode("ascii")}
            refused = run_request(self.muxd.port, payload, timeout=15)
            self.assertEqual("err", refused.get("t"), refused)
            self.assertIn("identity changed", refused.get("m", ""))
            payload["generationId"] = row["generationId"]
            applied = run_request(self.muxd.port, payload, timeout=15)
            self.assertEqual("input-ok", applied.get("t"), applied)
            replay = run_request(self.muxd.port, payload, timeout=15)
            self.assertEqual(applied, replay)
        finally:
            self.kill(name)

    def test_replayed_input_intent_is_at_most_once(self):
        name = "it-idempotent-input"
        intent_id = f"input-{int(time.time() * 1000)}"
        ready = f"MUXD_INPUT_READY_{int(time.time() * 1000)}"
        counter = self.muxd.root / "input-count.txt"
        quoted_counter = str(counter).replace("'", "''")
        command = f"Add-Content -LiteralPath '{quoted_counter}' -Value hit\r"
        payload = {
            "t": "input",
            "s": name,
            "d": base64.b64encode(command.encode("utf-8")).decode("ascii"),
            "intentId": intent_id,
        }
        self.kill(name)
        try:
            run_request(
                self.muxd.port,
                {"t": "create", "s": name, "cmd": f"Write-Output '{ready}'"},
                timeout=12,
            )
            self.wait_for_tail(name, ready)
            first = run_request(self.muxd.port, payload, timeout=12)
            self.assertEqual("input-ok", first.get("t"))
            deadline = time.time() + 5
            while time.time() < deadline and not counter.exists():
                time.sleep(0.05)
            self.assertTrue(counter.exists())

            self.muxd.restart()
            replayed = run_request(self.muxd.port, payload, timeout=12)
            self.assertEqual(first, replayed)
            time.sleep(0.2)
            self.assertEqual(counter.read_text(encoding="utf-8").splitlines(), ["hit"])

            conflicting = dict(payload)
            conflicting["d"] = base64.b64encode(b"Write-Output conflict\r").decode("ascii")
            conflict = run_request(self.muxd.port, conflicting, timeout=12)
            self.assertEqual("err", conflict.get("t"))
            self.assertIn("different payload", conflict.get("m", ""))
        finally:
            self.kill(name)

    def test_concurrent_create_has_exactly_one_winner(self):
        name = "it-concurrent-create"
        marker = f"MUXD_IT_CONCURRENT_{int(time.time() * 1000)}"
        cmd = f"Write-Output '{marker}'; # codex resume concurrent-create-session"
        self.kill(name)
        try:
            results = asyncio.run(
                concurrent_requests(
                    self.muxd.port,
                    [
                        {"t": "create", "s": name, "cmd": cmd, "ids": ["concurrent-create-session"]},
                        {"t": "create", "s": name, "cmd": cmd, "ids": ["concurrent-create-session"]},
                    ],
                )
            )

            winners = [r for r in results if r.get("created") is True]
            self.assertEqual(1, len(winners), f"concurrent create must have one winner: {results!r}")
            self.wait_for_tail(name, marker)
        finally:
            self.kill(name)

    def test_visible_owner_real_browser_gestures_and_recovery(self):
        try:
            from playwright.sync_api import sync_playwright
        except ImportError:
            self.skipTest("Playwright is required for the real-browser integration")
        relay_root = REPO.parent / "relay"
        relay_port = free_port()
        name = "it-owner-browser-interaction"
        session_id = f"browser-footer-{int(time.time() * 1000)}"
        target = None
        sidecar = None
        relay = None
        private_muxd = None
        root = Path(tempfile.mkdtemp(prefix="mux-owner-browser-"))
        screenshot = root.with_suffix('.png')
        mobile_screenshot = root.with_name(root.name + '-mobile.png')
        bottom_screenshot = root.with_name(root.name + '-mobile-bottom.png')
        try:
            relay_state = root / "relay-state"
            relay_state.mkdir()
            relay_env = dict(os.environ)
            relay_env.update(PORT=str(relay_port), MUX_HOST_TOKEN="test-token",
                             MUX_TEST_MODE="1", MUX_TEST_FIXTURE="1", MUX_BIND_HOST="127.0.0.1",
                             MUX_AUTOHEAL="0", MUX_STATE_DIR=str(relay_state),
                             ALLOWED_WS_ORIGINS=f"http://127.0.0.1:{relay_port}",
                             HLAUTH_BASE="http://127.0.0.1:1")
            relay = subprocess.Popen(["node", "server.js"], cwd=relay_root, env=relay_env,
                                     stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
                                     creationflags=CREATE_NO_WINDOW)
            deadline = time.monotonic() + 20
            while time.monotonic() < deadline:
                if relay.poll() is not None:
                    self.fail(f"relay exited early: {relay.stderr.read()}")
                try:
                    urllib.request.urlopen(f"http://127.0.0.1:{relay_port}/api/health", timeout=1).close()
                    break
                except Exception:
                    time.sleep(0.1)
            else:
                self.fail("test relay did not start")

            profile_root = root / "profile"
            profile_root.mkdir()
            state = profile_root / "state"
            state.mkdir()
            control_port = free_port()
            env_file = profile_root / "profile.env"
            env_file.write_text("\n".join(f"{key}={value}" for key, value in {
                "MUXD_STATE_ROOT": state,
                "MUXD_CONTROL_PORT": control_port,
                "MUXD_MUTEX_NAME": f"Local\\MuxOwnerBrowser-{control_port}",
                "MUXD_PRINCIPAL_REGISTRY": profile_root / "principal.dpapi",
                "MUXD_PRINCIPAL_INSTANCE_ID": f"browser-principal-{control_port}",
                "MUXD_HOST_IDENTITY": f"browser-host-{control_port}",
                "MUXD_RELAY_URLS": f"ws://127.0.0.1:{relay_port}/host",
                "MUXD_TOKEN_SOURCE": "env:MUX_HOST_TOKEN",
                "MUXD_LAUNCH_CLAIM_ROOT": profile_root / "claims",
                "MUXD_TASK_NAME": f"MuxBrowser-{control_port}",
                "MUXD_RESTART_TASK_NAME": f"MuxBrowserRestart-{control_port}",
                "MUXD_WATCHDOG_TASK_NAME": f"MuxBrowserWatchdog-{control_port}",
                "MUXD_GUARDIAN_DISABLED": "1",
                "DEFAULT_CWD": root,
            }.items()), encoding="utf-8")
            env = dict(os.environ)
            env.update(MUXD_PROFILE="browser-it", MUXD_RUNTIME_ROOT=str(profile_root / "runtime"),
                       MUXD_ENV_FILE=str(env_file), MUX_HOST_TOKEN="test-token",
                       MUXCTL_AUTOSTART="0")
            private_muxd = subprocess.Popen([sys.executable, str(MUXD), "--profile", "browser-it"],
                                            cwd=REPO, env=env, stdout=subprocess.DEVNULL,
                                            stderr=subprocess.DEVNULL, creationflags=CREATE_NO_WINDOW)
            deadline = time.monotonic() + 20
            while time.monotonic() < deadline:
                if private_muxd.poll() is not None:
                    self.fail(f"isolated muxd exited: {private_muxd.returncode}")
                try:
                    if run_request(control_port, {"t": "info"}, timeout=1).get("t") == "info":
                        break
                except Exception:
                    time.sleep(0.1)
            else:
                self.fail("isolated muxd did not start")
            fake_codex = root / "codex.exe"
            shutil.copy2(sys.executable, fake_codex)
            console_app = r"""import ctypes,sys
sys.path.insert(0,sys.argv[1]); import muxrun
k=ctypes.windll.kernel32
k.CreateFileW.restype=ctypes.c_void_p
out=k.CreateFileW('CONOUT$',0xC0000000,3,None,3,0,None)
hin=k.CreateFileW('CONIN$',0xC0000000,3,None,3,0,None)
mode=ctypes.c_uint()
if not k.GetConsoleMode(out,ctypes.byref(mode)): raise ctypes.WinError()
if not k.SetConsoleMode(out,mode.value|muxrun.ENABLE_VIRTUAL_TERMINAL_PROCESSING): raise ctypes.WinError()
if not k.GetConsoleMode(hin,ctypes.byref(mode)): raise ctypes.WinError()
if not k.SetConsoleMode(hin,(mode.value|0x0010|0x0080)&~0x0040): raise ctypes.WinError()
def write(text):
    n=ctypes.c_uint()
    if not k.WriteConsoleW(out,text,len(text),ctypes.byref(n),None): raise ctypes.WinError()
write('\x1b[?1049h\x1b[?1000h\x1b[?1006h'+'VISIBLE FOOTER\x1b[8;1HWHEEL=0 CLICK=0 KEY=0')
wheel=click=release=key=0
bottom=False
last='-'
records=(muxrun.INPUT_RECORD*16)(); count=ctypes.c_uint()
while k.ReadConsoleInputW(hin,records,16,ctypes.byref(count)):
    for i in range(count.value):
        r=records[i]
        if r.EventType==muxrun.MOUSE_EVENT_TYPE:
            m=r.Event.MouseEvent
            if m.dwEventFlags==muxrun.MOUSE_WHEELED: wheel+=1
            elif m.dwButtonState==1:
                click+=1
                last='%d,%d'%(m.dwMousePosition.X+1,m.dwMousePosition.Y+1)
            elif m.dwButtonState==0: release+=1
        elif r.EventType==muxrun.KEY_EVENT and r.Event.KeyEvent.bKeyDown:
            key+=1
            if r.Event.KeyEvent.uChar.UnicodeChar=='b':
                bottom=True
                write('\x1b[2J\x1b[H')
                write('\x1b[51;1HBOTTOM FOOTER')
    write('\x1b[8;1HWHEEL=%d CLICK=%d KEY=%d REL=%d %s\x1b[K'%(wheel,click,key,release,last))
    if bottom: write('\x1b[51;1HBOTTOM FOOTER')

"""
            target = subprocess.Popen([str(fake_codex), "-c", console_app, str(REPO),
                                       "resume", session_id], cwd=root, creationflags=CREATE_NEW_CONSOLE)
            sidecar = subprocess.Popen([sys.executable,
                                        str(MUXRUN), name, "--attach-pid", str(target.pid),
                                        "--cwd", str(root), "--cmd", f"codex resume {session_id}",
                                        "--session-id", session_id, "--profile", "browser-it"],
                                       cwd=REPO, env=env, stdout=subprocess.DEVNULL,
                                       stderr=subprocess.PIPE, text=True,
                                       creationflags=DETACHED_PROCESS | CREATE_NO_WINDOW, close_fds=True)
            deadline = time.monotonic() + 20
            last_row = None
            while time.monotonic() < deadline:
                if sidecar.poll() is not None:
                    self.fail(f"owner exited: {sidecar.returncode}: {sidecar.stderr.read()}")
                row = next((s for s in run_request(control_port, {"t": "ls"}).get("list", [])
                            if s.get("name") == name and s.get("owner")), None)
                last_row = row or run_request(control_port, {"t": "ls"}).get("list", [])
                if row:
                    break
                time.sleep(0.1)
            else:
                sidecar.kill(); sidecar.wait(timeout=5)
                self.fail(f"visible owner did not register; last={last_row}; sidecar={sidecar.poll()}, stderr="
                          f"{sidecar.stderr.read() if sidecar.poll() is not None else 'still running'}, muxd="
                          f"{(state / 'muxd.log').read_text(encoding='utf-8', errors='replace')[-2500:] if (state / 'muxd.log').exists() else 'no log'}")
            # The console app itself enables alternate screen and mouse reporting, and
            # records which input events it actually receives after browser gestures.
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline:
                if target.poll() is not None:
                    self.fail(f"console app exited before displaying footer: {target.returncode}")
                try:
                    if "VISIBLE FOOTER" in run_request(control_port, {"t": "ls"}).get("list", [])[0].get("tail", ""):
                        break
                except Exception:
                    pass
                time.sleep(0.1)
            else:
                self.fail("app console footer never reached isolated muxd")
            # The owner has now forwarded the active screen; the browser must show the
            # same source cells after relay scrollback and live output have settled.
            with sync_playwright() as playwright:
                browser = playwright.chromium.launch(headless=False, args=["--no-proxy-server"])
                try:
                    page = browser.new_page(viewport={"width": 1440, "height": 900})
                    page.goto(f"http://127.0.0.1:{relay_port}/", timeout=30000)
                    page.evaluate("name => connect(name)", name)
                    page.wait_for_function("() => [...document.querySelectorAll('.xterm-rows > div')].some(r => r.textContent.includes('VISIBLE FOOTER'))", timeout=20000)
                    self.assertIn("VISIBLE FOOTER", page.locator(".xterm-rows").inner_text())
                    # The browser never saw the app's pre-attach DECSET, but session ownership
                    # remains explicit; real pointer gestures must reach the visible console.
                    page.wait_for_function("() => isVisibleOwner()")
                    self.assertEqual(page.evaluate("() => term.buffer.active.type"), "normal")
                    self.assertFalse(page.evaluate("() => window.__muxMouseGuard.mouseActive()"))
                    centre = page.locator('#term .xterm-screen').bounding_box()
                    self.assertIsNotNone(centre)
                    x = centre['x'] + centre['width'] / 2
                    y = centre['y'] + centre['height'] / 2
                    page.mouse.move(x, y)
                    page.mouse.wheel(0, -480)
                    page.mouse.click(x, y)
                    self.assertFalse(page.evaluate('() => isFrozen()'), 'a plain owner click must not freeze live output')
                    page.mouse.move(x, y)
                    page.mouse.down()
                    page.mouse.move(x + 70, y + 15, steps=5)
                    page.mouse.up()
                    self.assertFalse(page.evaluate('() => term.hasSelection()'), 'a plain owner drag belongs to the app')
                    self.assertFalse(page.evaluate('() => isFrozen()'), 'a plain owner drag must not freeze live output')
                    page.keyboard.press('k')
                    page.wait_for_function("() => [...document.querySelectorAll('.xterm-rows > div')].some(r => /WHEEL=[1-9]/.test(r.textContent) && /CLICK=[1-9]/.test(r.textContent) && /KEY=[1-9]/.test(r.textContent))", timeout=10000)
                    # Pointer fidelity at the LEFT EDGE of the grid: the cell the app receives must be
                    # the cell that was clicked. A clamped or off-by-one report would silently put every
                    # click on the wrong cell of a TUI - and a dropped release would leave it stuck in a
                    # drag. Both are invisible to a test that only counts that SOME click arrived.
                    before = page.evaluate("() => document.querySelector('.xterm-rows').textContent.match(/ (\\d+),(\\d+)/).slice(1).map(Number)")
                    counter_box = page.locator('.xterm-rows > div').filter(has_text='WHEEL=').first.bounding_box()
                    self.assertIsNotNone(counter_box)
                    page.mouse.click(centre['x'] + 5, counter_box['y'] + counter_box['height'] / 2)
                    page.wait_for_function("prev => { const m=document.querySelector('.xterm-rows').textContent.match(/ (\\d+),(\\d+)/); return m && (Number(m[1])!==prev[0] || Number(m[2])!==prev[1]); }", arg=before, timeout=10000)
                    reported = page.evaluate("() => document.querySelector('.xterm-rows').textContent.match(/ (\\d+),(\\d+)/).slice(1).map(Number)")
                    self.assertEqual(reported, [1, 8], f'owner click reported the wrong cell: {reported}')
                    counters = page.evaluate("() => document.querySelector('.xterm-rows').textContent.match(/WHEEL=(\\d+) CLICK=(\\d+) KEY=(\\d+) REL=(\\d+)/).slice(1).map(Number)")
                    self.assertEqual(counters[3], counters[1], f'every owner press must be released: {counters}')
                    self.assertGreaterEqual(counters[1], 3, counters)
                    footer = page.locator('.xterm-rows > div').filter(has_text='VISIBLE FOOTER').first
                    footer_box = footer.bounding_box()
                    self.assertIsNotNone(footer_box)
                    page.mouse.move(footer_box['x'] + 5, footer_box['y'] + footer_box['height'] / 2)
                    page.keyboard.down('Shift')
                    page.mouse.down()
                    page.mouse.move(footer_box['x'] + 90, footer_box['y'] + footer_box['height'] / 2, steps=5)
                    page.mouse.up()
                    page.keyboard.up('Shift')
                    page.screenshot(path=str(screenshot), full_page=True)
                    observed = {
                        'key': page.evaluate("() => [...document.querySelectorAll('.xterm-rows > div')].some(r => /KEY=[1-9]/.test(r.textContent))"),
                        'wheel': page.evaluate("() => [...document.querySelectorAll('.xterm-rows > div')].some(r => /WHEEL=[1-9]/.test(r.textContent))"),
                        'click': page.evaluate("() => [...document.querySelectorAll('.xterm-rows > div')].some(r => /CLICK=[1-9]/.test(r.textContent))"),
                        'selection': page.evaluate("() => term.hasSelection() && !!term.getSelection().trim()"),
                    }
                    phone = browser.new_page(viewport={'width': 390, 'height': 700}, is_mobile=True, has_touch=True)
                    phone.goto(f'http://127.0.0.1:{relay_port}/', timeout=30000)
                    phone.evaluate('name => connect(name)', name)
                    phone.wait_for_function("() => [...document.querySelectorAll('.xterm-rows > div')].some(r => /WHEEL=[1-9]/.test(r.textContent))", timeout=20000)
                    phone.get_by_role('button', name='Sessions', exact=True).click()
                    self.assertTrue(phone.evaluate("() => document.querySelector('#app').classList.contains('mobile-view-sessions')"))
                    phone.get_by_role('button', name='Terminal', exact=True).click()
                    observed['nav'] = not phone.evaluate("() => document.querySelector('#app').classList.contains('mobile-view-sessions')")
                    phone.wait_for_function("""() => { const host=document.querySelector('#term'); const row=[...host.querySelectorAll('.xterm-rows > div')].find(r=>r.textContent.includes('VISIBLE FOOTER')); if(!row) return false; const a=row.getBoundingClientRect(), b=host.getBoundingClientRect(); return a.bottom>b.top && a.top<b.bottom; }""")
                    mobile_surface = phone.evaluate("""() => { const host=document.querySelector('#term'); const xt=host.querySelector('.xterm'); const rows=host.querySelector('.xterm-rows'); return {
                        hostTop:host.scrollTop, hostHeight:host.clientHeight, hostScrollHeight:host.scrollHeight,
                        xtermHeight:xt.clientHeight, xtermScrollHeight:xt.scrollHeight,
                        classes:host.className, rowsText:rows.textContent.slice(0,250),
                        footerRow:[...rows.children].findIndex(r=>r.textContent.includes('VISIBLE FOOTER')),
                        counterRow:[...rows.children].findIndex(r=>r.textContent.includes('WHEEL=')),
                        winSize, viewportFit
                    }; }""")
                    phone.screenshot(path=str(mobile_screenshot), full_page=True)
                    try:
                        from PIL import Image
                    except ImportError:
                        Image = None
                    if Image is not None:
                        with Image.open(mobile_screenshot) as image:
                            # DOM rows can exist even when the phone pans into blank space.
                            text_pixels = sum(1 for y in range(115, 155) for x in range(3, 160)
                                              if max(image.getpixel((x, y))[:3]) > 100)
                        self.assertGreater(text_pixels, 25, f'phone screenshot has no visible terminal text: {mobile_surface}; screenshot={mobile_screenshot}')
                    touch = phone.context.new_cdp_session(phone)
                    touch.send('Input.dispatchTouchEvent', {'type': 'touchStart', 'touchPoints': [{'x': 90, 'y': 330, 'id': 1}]})
                    touch.send('Input.dispatchTouchEvent', {'type': 'touchMove', 'touchPoints': [{'x': 90, 'y': 250, 'id': 1}]})
                    touch.send('Input.dispatchTouchEvent', {'type': 'touchEnd', 'touchPoints': []})
                    phone.wait_for_function("() => [...document.querySelectorAll('.xterm-rows > div')].some(r => /WHEEL=[5-9]/.test(r.textContent))", timeout=10000)
                    observed['touch_scroll'] = True
                    print(f'owner browser gesture outcome: {observed}; mobile={mobile_surface}; screenshots={screenshot}, {mobile_screenshot}', flush=True)
                    self.assertTrue(observed['nav'], observed)
                    self.assertTrue(observed['selection'], observed)
                    self.assertTrue(observed['key'], observed)
                    self.assertTrue(observed['wheel'], observed)
                    self.assertTrue(observed['click'], observed)
                    # The app then redraws its actual footer at row 51. The phone must
                    # follow content that moved after attach, without the user reloading.
                    page.keyboard.press('b')
                    phone.wait_for_function("() => [...document.querySelectorAll('.xterm-rows > div')].some(r=>r.textContent.includes('BOTTOM FOOTER'))", timeout=15000)
                    phone.wait_for_function("""() => { const host=document.querySelector('#term'); const row=[...host.querySelectorAll('.xterm-rows > div')].find(r=>r.textContent.includes('BOTTOM FOOTER'));
                      if(!row) return false; const a=row.getBoundingClientRect(), b=host.getBoundingClientRect(); return a.bottom>b.top && a.top<b.bottom; }""", timeout=5000)
                    bottom_surface = phone.evaluate("""() => { const host=document.querySelector('#term'); const row=[...host.querySelectorAll('.xterm-rows > div')].find(r=>r.textContent.includes('BOTTOM FOOTER'));
                      const a=row.getBoundingClientRect(), b=host.getBoundingClientRect(); return { top:host.scrollTop, visible:a.bottom>b.top && a.top<b.bottom }; }""")
                    print(f'owner bottom redraw: {bottom_surface}; screenshot={bottom_screenshot}', flush=True)
                    self.assertTrue(bottom_surface['visible'], bottom_surface)
                    phone.screenshot(path=str(bottom_screenshot), full_page=True)
                    # Damage only the browser mirror; after a selection gesture the
                    # viewer is frozen until the user explicitly resumes live output.
                    # The app's CURRENT content is BOTTOM FOOTER plus the counters, so the
                    # repair has to restore those - VISIBLE FOOTER is gone from the app too.
                    page.evaluate("() => new Promise(resolve => term.write('\\x1b[2J\\x1b[H', resolve))")
                    page.wait_for_function("() => ![...document.querySelectorAll('.xterm-rows > div')].some(r => r.textContent.includes('BOTTOM FOOTER'))")
                    page.evaluate("() => send('R', '')")
                    page.evaluate("() => forceJumpBottom()")
                    page.wait_for_function("() => [...document.querySelectorAll('.xterm-rows > div')].some(r => r.textContent.includes('BOTTOM FOOTER'))", timeout=20000)
                    self.assertIn("WHEEL=", page.locator(".xterm-rows").inner_text())
                finally:
                    browser.close()
        finally:
            if private_muxd is not None and private_muxd.poll() is None:
                try:
                    run_request(control_port, {"t": "kill", "s": name}, timeout=10)
                except Exception:
                    pass
            if sidecar is not None and sidecar.poll() is None:
                sidecar.kill()
                sidecar.wait(timeout=5)
            if sidecar is not None and sidecar.stderr is not None:
                sidecar.stderr.close()
            if target is not None and target.poll() is None:
                target.kill()
                target.wait(timeout=5)
            if private_muxd is not None and private_muxd.poll() is None:
                private_muxd.terminate()
                private_muxd.wait(timeout=10)
            if relay is not None and relay.poll() is None:
                relay.terminate()
                relay.wait(timeout=10)
            if relay is not None and relay.stderr is not None:
                relay.stderr.close()
            shutil.rmtree(root, ignore_errors=True)

    def test_visible_owner_registration_has_one_identity_owner(self):
        name = "it-owner-claim"
        cmd = "codex resume it-owner-identity"
        self.kill(name)
        try:
            first, second = asyncio.run(owner_collision(self.muxd.port, name, cmd))

            self.assertEqual("owner-ok", first.get("t"), first)
            self.assertEqual("err", second.get("t"), second)
            self.assertIn("visible local owner", second.get("m", ""))
        finally:
            self.kill(name)

    def test_visible_owner_reclaims_manifest_after_muxd_restart(self):
        name = "it-owner-restart"
        cmd = "while($true){Start-Sleep -Milliseconds 200}"
        self.kill(name)
        owner = subprocess.Popen(
            [sys.executable, str(MUXRUN), name, "--cwd", str(self.muxd.root), "--cmd", cmd],
            cwd=str(REPO),
            env=self.muxd.env,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            text=True,
            creationflags=CREATE_NO_WINDOW,
        )
        try:
            deadline = time.time() + 12
            before = None
            while time.time() < deadline:
                before = self.session(name)
                if before and before.get("owner"):
                    break
                time.sleep(0.2)
            self.assertIsNotNone(before)
            self.assertTrue(before.get("owner"), before)

            manifest = json.loads((self.muxd.root / "muxd" / "sessions.json").read_text(encoding="utf-8"))
            self.assertTrue(manifest["sessions"][name]["owner"])
            self.assertGreaterEqual(len(manifest["sessions"][name]["ownerKey"]), 24)

            self.muxd.restart()

            deadline = time.time() + 15
            after = None
            while time.time() < deadline:
                after = self.session(name)
                if after and after.get("owner"):
                    break
                time.sleep(0.25)
            if owner.poll() is not None:
                stderr = owner.stderr.read() if owner.stderr is not None else ""
                self.fail(self.muxd.diagnostics(
                    f"muxrun child owner exited during muxd restart with {owner.returncode}: {stderr}"
                ))
            self.assertIsNotNone(after)
            self.assertTrue(after.get("owner"), after)
        finally:
            self.kill(name)
            try:
                owner.wait(timeout=5)
            except subprocess.TimeoutExpired:
                subprocess.run(
                    ["taskkill.exe", "/PID", str(owner.pid), "/T", "/F"],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    timeout=10,
                    creationflags=CREATE_NO_WINDOW,
                )
                owner.wait(timeout=5)
            if owner.stderr is not None:
                owner.stderr.close()

    def test_visible_owner_sidecar_death_cannot_orphan_its_child(self):
        name = "it-owner-contained"
        cmd = "while($true){Start-Sleep -Milliseconds 200}"
        self.kill(name)
        owner = subprocess.Popen(
            [sys.executable, str(MUXRUN), name, "--cwd", str(self.muxd.root), "--cmd", cmd],
            cwd=str(REPO),
            env=self.muxd.env,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=CREATE_NO_WINDOW,
        )
        child_pid = 0
        try:
            deadline = time.time() + 12
            while time.time() < deadline:
                row = self.session(name)
                child_pid = int((row or {}).get("childPid") or 0)
                if row and row.get("owner") and child_pid > 0:
                    break
                time.sleep(0.2)
            self.assertGreater(child_pid, 0, "visible owner never published its child pid")
            self.assertTrue(process_alive(child_pid), "visible owner child was not alive before sidecar termination")

            owner.kill()
            owner.wait(timeout=5)
            deadline = time.time() + 8
            while process_alive(child_pid) and time.time() < deadline:
                time.sleep(0.1)
            self.assertFalse(process_alive(child_pid), "visible owner child survived sidecar death")
        finally:
            self.kill(name)
            if owner.poll() is None:
                subprocess.run(
                    ["taskkill.exe", "/PID", str(owner.pid), "/T", "/F"],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    timeout=10,
                    creationflags=CREATE_NO_WINDOW,
                )

    def test_visible_owner_kill_ack_waits_for_contained_child_exit(self):
        name = "it-owner-kill-verified"
        cmd = "while($true){Start-Sleep -Milliseconds 211}"
        self.kill(name)
        owner = subprocess.Popen(
            [sys.executable, str(MUXRUN), name, "--cwd", str(self.muxd.root), "--cmd", cmd],
            cwd=str(REPO),
            env=self.muxd.env,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=CREATE_NO_WINDOW,
        )
        child_pid = 0
        try:
            deadline = time.time() + 12
            while time.time() < deadline:
                row = self.session(name)
                child_pid = int((row or {}).get("childPid") or 0)
                if row and row.get("owner") and child_pid > 0:
                    break
                time.sleep(0.2)
            self.assertGreater(child_pid, 0)
            self.assertTrue(process_alive(child_pid))

            killed = run_request(self.muxd.port, {"t": "kill", "s": name}, timeout=18)

            self.assertEqual("killed", killed.get("t"), killed)
            self.assertFalse(
                process_alive(child_pid),
                "visible-owner kill acknowledged before its contained child exited",
            )
            owner.wait(timeout=5)
        finally:
            self.kill(name)
            if owner.poll() is None:
                subprocess.run(
                    ["taskkill.exe", "/PID", str(owner.pid), "/T", "/F"],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    timeout=10,
                    creationflags=CREATE_NO_WINDOW,
                )

    def test_adopted_local_console_mirrors_input_reconnects_and_survives_sidecar_exit(self):
        name = "it-adopted-local"
        session_id = f"adopted-{int(time.time() * 1000)}"
        marker = "MUX_ADOPTED_SCREEN_READY"
        input_marker = "MUX_ADOPTED_REMOTE_INPUT"
        fake_codex = self.muxd.root / "codex.exe"
        shutil.copy2(os.environ.get("ComSpec", r"C:\Windows\System32\cmd.exe"), fake_codex)
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = 0
        target = subprocess.Popen(
            [
                str(fake_codex),
                "/d",
                "/q",
                "/k",
                f"echo {marker}",
                "resume",
                session_id,
            ],
            cwd=str(self.muxd.root),
            startupinfo=startup,
            creationflags=CREATE_NEW_CONSOLE,
        )
        sidecars = []
        command = base64.b64encode(f"codex resume {session_id}".encode("utf-8")).decode("ascii")

        def start_sidecar():
            env = dict(self.muxd.env)
            env["MUXCTL_PORT"] = str(self.muxd.port)
            env["MUXD_TASK"] = f"MuxdNonexistentTest-{self.muxd.port}"
            pythonw = Path(sys.executable).with_name("pythonw.exe")
            executable = pythonw if pythonw.exists() else Path(sys.executable)
            proc = subprocess.Popen(
                [
                    str(executable),
                    str(MUXRUN),
                    name,
                    "--attach-pid",
                    str(target.pid),
                    "--cwd",
                    str(self.muxd.root),
                    "--cmd-b64",
                    command,
                    "--session-id",
                    session_id,
                ],
                cwd=str(REPO),
                env=env,
                creationflags=DETACHED_PROCESS,
                close_fds=True,
            )
            sidecars.append(proc)
            return proc

        def wait_adopted(sidecar, timeout=15):
            deadline = time.time() + timeout
            last = None
            while time.time() < deadline:
                if sidecar.poll() is not None:
                    self.fail(self.muxd.diagnostics(
                        f"adopted sidecar exited with {sidecar.returncode}; last session={last}"
                    ))
                last = self.session(name)
                if (
                    last
                    and last.get("alive")
                    and last.get("adopted")
                    and last.get("externalOwner")
                    and int(last.get("childPid") or 0) == target.pid
                ):
                    return last
                time.sleep(0.2)
            self.fail(self.muxd.diagnostics(f"adopted session did not become live; last={last}"))

        self.kill(name)
        try:
            first_sidecar = start_sidecar()
            first = wait_adopted(first_sidecar)
            self.assertEqual("adopted-local", first.get("kind"))
            self.assertFalse(first.get("heal"))
            self.wait_for_tail(name, marker)

            sent = run_request(
                self.muxd.port,
                {
                    "t": "input",
                    "s": name,
                    "d": base64.b64encode(f"echo {input_marker}\r".encode("utf-8")).decode("ascii"),
                },
                timeout=8,
            )
            self.assertEqual("input-ok", sent.get("t"), sent)
            self.wait_for_tail(name, input_marker)

            first_sidecar.kill()
            first_sidecar.wait(timeout=5)
            time.sleep(0.5)
            self.assertTrue(process_alive(target.pid), "adopted target died with its mirror sidecar")

            second_sidecar = start_sidecar()
            wait_adopted(second_sidecar)
            self.assertTrue(process_alive(target.pid))

            killed = run_request(self.muxd.port, {"t": "kill", "s": name}, timeout=18)
            self.assertEqual("killed", killed.get("t"), killed)
            deadline = time.time() + 8
            while process_alive(target.pid) and time.time() < deadline:
                time.sleep(0.1)
            self.assertFalse(process_alive(target.pid), "explicit mux Stop left the adopted target alive")
        finally:
            self.kill(name)
            for sidecar in sidecars:
                if sidecar.poll() is None:
                    sidecar.kill()
                    sidecar.wait(timeout=5)
            if target.poll() is None:
                target.kill()
                target.wait(timeout=5)

    def test_boot_never_resurrects_user_killed_armed_session(self):
        name = "it-user-killed-boot"
        self.kill(name)
        self.muxd.stop_process()
        manifest_path = self.muxd.root / "muxd" / "sessions.json"
        manifest_path.write_text(
            json.dumps({
                name: {
                    "cmd": "while($true){Start-Sleep -Milliseconds 200}; # codex resume user-killed-boot",
                    "cwd": str(self.muxd.root),
                    "cols": 100,
                    "rows": 30,
                    "heal": True,
                    "ids": ["user-killed-boot"],
                    "sessionId": "user-killed-boot",
                    "aliases": [],
                    "owner": False,
                    "ownerKey": "",
                    "identityPending": False,
                    "lifecycle": "active",
                    "childPid": 0,
                    "stopDisposition": "",
                    "userKilled": True,
                    "deaths": [],
                },
            }),
            encoding="utf-8",
        )

        self.muxd.start_process()

        self.assertIsNone(self.session(name))
        persisted = json.loads(manifest_path.read_text(encoding="utf-8"))
        self.assertNotIn(name, persisted)

    def test_boot_reconciles_interrupted_replacement_to_dormant(self):
        name = "it-replace-boot"
        self.kill(name)
        self.muxd.stop_process()
        manifest_path = self.muxd.root / "muxd" / "sessions.json"
        manifest_path.write_text(
            json.dumps({
                name: {
                    "cmd": "while($true){Start-Sleep -Milliseconds 200}",
                    "cwd": str(self.muxd.root),
                    "cols": 100,
                    "rows": 30,
                    "heal": False,
                    "ids": [],
                    "sessionId": "",
                    "aliases": [],
                    "owner": False,
                    "ownerKey": "",
                    "identityPending": False,
                    "lifecycle": "stopping",
                    "childPid": 0,
                    "stopDisposition": "replace",
                    "userKilled": False,
                    "deaths": [],
                },
            }),
            encoding="utf-8",
        )

        self.muxd.start_process()

        restored = self.session(name)
        self.assertIsNotNone(restored)
        self.assertFalse(restored.get("alive"))
        self.assertEqual("dormant", restored.get("lifecycle"))
        self.kill(name)

    def test_boot_durably_demotes_stale_active_session_to_dormant(self):
        name = "it-active-boot"
        self.kill(name)
        self.muxd.stop_process()
        manifest_path = self.muxd.root / "muxd" / "sessions.json"
        manifest_path.write_text(
            json.dumps({
                name: {
                    "cmd": "Write-Output preserved",
                    "cwd": str(self.muxd.root),
                    "cols": 100,
                    "rows": 30,
                    "heal": False,
                    "ids": [],
                    "sessionId": "",
                    "aliases": [],
                    "owner": False,
                    "ownerKey": "",
                    "identityPending": False,
                    "lifecycle": "active",
                    "childPid": 424242,
                    "childStartToken": "0000000000001234",
                    "stopDisposition": "",
                    "userKilled": False,
                    "deaths": [],
                },
            }),
            encoding="utf-8",
        )

        self.muxd.start_process()

        restored = self.session(name)
        self.assertIsNotNone(restored)
        self.assertFalse(restored.get("alive"))
        self.assertEqual("dormant", restored.get("lifecycle"))
        persisted = json.loads(manifest_path.read_text(encoding="utf-8"))
        record = persisted.get("sessions", persisted)[name]
        self.assertEqual("dormant", record.get("lifecycle"))
        self.assertEqual(0, record.get("childPid"))
        self.assertEqual("", record.get("childStartToken"))
        self.kill(name)

    def test_boot_reconciliation_never_drops_later_manifest_records(self):
        removed_name = "it-boot-remove-first"
        preserved_name = "it-boot-preserve-later"
        self.kill(removed_name)
        self.kill(preserved_name)
        self.muxd.stop_process()
        manifest_path = self.muxd.root / "muxd" / "sessions.json"

        def record(cmd, lifecycle, *, user_killed=False, stop_disposition=""):
            return {
                "cmd": cmd,
                "cwd": str(self.muxd.root),
                "cols": 100,
                "rows": 30,
                "heal": False,
                "ids": [],
                "sessionId": "",
                "aliases": [],
                "owner": False,
                "ownerKey": "",
                "identityPending": False,
                "lifecycle": lifecycle,
                "childPid": 0,
                "childStartToken": "",
                "stopDisposition": stop_disposition,
                "userKilled": user_killed,
                "deaths": [],
            }

        manifest_path.write_text(
            json.dumps({
                removed_name: record("", "stopping", user_killed=True, stop_disposition="remove"),
                preserved_name: record("Write-Output preserved", "dormant"),
            }),
            encoding="utf-8",
        )

        self.muxd.start_process()

        persisted = json.loads(manifest_path.read_text(encoding="utf-8"))
        self.assertNotIn(removed_name, persisted["sessions"])
        self.assertIn(preserved_name, persisted["sessions"])
        self.assertIsNotNone(self.session(preserved_name))
        self.muxd.restart()
        self.assertIsNotNone(self.session(preserved_name))
        self.kill(preserved_name)

    def test_boot_never_kills_live_process_with_recycled_pid(self):
        name = "it-stale-pid-boot"
        self.kill(name)
        sentinel = subprocess.Popen(
            [sys.executable, "-c", "import time; time.sleep(60)"],
            creationflags=CREATE_NO_WINDOW,
        )
        try:
            self.muxd.stop_process()
            manifest_path = self.muxd.root / "muxd" / "sessions.json"
            manifest_path.write_text(
                json.dumps({
                    name: {
                        "cmd": "",
                        "cwd": str(self.muxd.root),
                        "cols": 100,
                        "rows": 30,
                        "heal": False,
                        "ids": [],
                        "sessionId": "",
                        "aliases": [],
                        "owner": False,
                        "ownerKey": "",
                        "identityPending": False,
                        "lifecycle": "starting",
                        "childPid": sentinel.pid,
                        "childStartToken": "stale-process-instance",
                        "stopDisposition": "",
                        "userKilled": False,
                        "deaths": [],
                    },
                }),
                encoding="utf-8",
            )

            self.muxd.start_process()

            self.assertIsNone(sentinel.poll(), "boot killed an unrelated process that reused a persisted PID")
            restored = self.session(name)
            self.assertIsNotNone(restored)
            self.assertEqual("failed", restored.get("lifecycle"))
            self.kill(name)
        finally:
            if sentinel.poll() is None:
                sentinel.terminate()
                sentinel.wait(timeout=5)

    def test_boot_quarantines_unproven_matching_live_agent_without_killing_it(self):
        name = "it-unproven-live-boot"
        session_id = "unproven-live-boot"
        self.kill(name)
        node = shutil.which("node")
        self.assertIsNotNone(node)
        sentinel = subprocess.Popen(
            [
                node,
                "-e",
                "setTimeout(() => {}, 60000)",
                "codex",
                "resume",
                session_id,
            ],
            creationflags=CREATE_NO_WINDOW,
        )
        try:
            self.muxd.stop_process()
            manifest_path = self.muxd.root / "muxd" / "sessions.json"
            manifest_path.write_text(
                json.dumps({
                    name: {
                        "cmd": f"codex resume {session_id}",
                        "cwd": str(self.muxd.root),
                        "cols": 100,
                        "rows": 30,
                        "heal": True,
                        "ids": [session_id],
                        "sessionId": session_id,
                        "aliases": [],
                        "owner": False,
                        "ownerKey": "",
                        "identityPending": False,
                        "lifecycle": "starting",
                        "childPid": 0,
                        "childStartToken": "",
                        "stopDisposition": "",
                        "userKilled": False,
                        "deaths": [],
                    },
                }),
                encoding="utf-8",
            )

            self.muxd.start_process()

            self.assertIsNone(sentinel.poll(), "boot killed a matching process without custody proof")
            restored = self.session(name)
            self.assertIsNotNone(restored)
            self.assertEqual("starting", restored.get("lifecycle"))
            persisted = json.loads(manifest_path.read_text(encoding="utf-8"))
            self.assertEqual("starting", persisted[name]["lifecycle"])
            self.kill(name)
        finally:
            if sentinel.poll() is None:
                sentinel.terminate()
                sentinel.wait(timeout=5)

    def test_relaunch_waits_until_previous_process_exits(self):
        name = "it-relaunch-exit-barrier"
        marker = f"MUXD_IT_EXIT_BARRIER_{int(time.time() * 1000)}"
        pid_file = self.muxd.root / "previous-pid.txt"
        overlap_file = self.muxd.root / "overlap.txt"
        pid_ps = pid_file.as_posix().replace("'", "''")
        overlap_ps = overlap_file.as_posix().replace("'", "''")
        cmd = (
            f"$old=0; if(Test-Path -LiteralPath '{pid_ps}'){{"
            f"$old=[int](Get-Content -LiteralPath '{pid_ps}');"
            f"if(Get-Process -Id $old -ErrorAction SilentlyContinue){{"
            f"Set-Content -LiteralPath '{overlap_ps}' -Value 'overlap'}}}};"
            f"Set-Content -LiteralPath '{pid_ps}' -Value $PID;"
            f"Write-Output '{marker}'; while($true){{Start-Sleep -Milliseconds 200}}"
        )
        self.kill(name)
        for path in (pid_file, overlap_file):
            try:
                path.unlink()
            except FileNotFoundError:
                pass
        try:
            first = run_request(self.muxd.port, {"t": "create", "s": name, "cmd": cmd}, timeout=12)
            self.assertTrue(first.get("created"))
            self.wait_for_tail(name, marker)
            deadline = time.time() + 5
            while time.time() < deadline and not pid_file.exists():
                time.sleep(0.05)
            self.assertTrue(pid_file.exists(), "first writer did not publish its PID")

            second = run_request(self.muxd.port, {"t": "create", "s": name, "relaunch": True}, timeout=12)
            self.assertTrue(second.get("created"))
            self.wait_for_tail(name, marker)

            self.assertFalse(
                overlap_file.exists(),
                "replacement started while the previous PowerShell writer was still alive",
            )
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

    def test_pending_fresh_identity_cannot_relaunch_until_bound(self):
        name = "it-pending-identity"
        marker = f"MUXD_IT_PENDING_{int(time.time() * 1000)}"
        self.kill(name)
        try:
            first = run_request(
                self.muxd.port,
                {
                    "t": "create",
                    "s": name,
                    "cmd": claude_registry_command("captured-id", marker),
                    "identityPending": True,
                },
                timeout=12,
            )
            self.assertTrue(first.get("created"))
            pending = self.wait_for_tail(name, marker)
            projection_path = self.muxd.root / "muxd" / "live-tabs.json"
            deadline = time.monotonic() + 12
            projected = None
            while time.monotonic() < deadline:
                if projection_path.exists():
                    projected = json.loads(projection_path.read_text(encoding="utf-8")).get(name)
                    if projected and projected.get("generationId") == first.get("generationId"):
                        break
                time.sleep(0.05)
            self.assertIsNotNone(projected, "Local identity projection must not require a relay connection")
            self.assertEqual(first["generationId"], projected["generationId"])
            self.assertGreater(projected["pid"], 0)
            self.assertTrue(projected["identityPending"])
            self.assertTrue(pending.get("identityPending"))
            self.assertEqual("", pending.get("sessionId"))

            refused = run_request(
                self.muxd.port,
                {"t": "create", "s": name, "relaunch": True},
                timeout=12,
            )
            self.assertEqual("err", refused.get("t"))
            self.assertIn("identity has not been captured", refused.get("m", ""))

            resume_marker = marker + "_RESUMED"
            for generation in ("", "replacement-generation"):
                rejected = run_request(self.muxd.port, {
                    "t": "bind", "s": name, "generationId": generation,
                    "cmd": "# codex resume wrong-id", "sessionId": "wrong-id",
                }, timeout=12)
                self.assertEqual("err", rejected.get("t"), rejected)
                self.assertIn("generation mismatch", rejected.get("m", ""))
                self.assertTrue(self.session(name).get("identityPending"))
                self.assertEqual("", self.session(name).get("sessionId"))
            unowned = run_request(self.muxd.port, {
                "t": "bind", "s": name,
                "generationId": self.session(name)["generationId"],
                "cmd": "# codex resume absent-id", "sessionId": "absent-id",
            }, timeout=12)
            self.assertEqual("err", unowned.get("t"), unowned)
            self.assertIn("verified live descendant", unowned.get("m", ""))
            self.assertTrue(self.session(name).get("identityPending"))
            bound = run_request(
                self.muxd.port,
                {
                    "t": "bind",
                    "s": name,
                    "generationId": self.session(name)["generationId"],
                    "cmd": f"Write-Output '{resume_marker}'; # codex resume captured-id",
                    "sessionId": "captured-id",
                    "aliases": [],
                },
                timeout=12,
            )
            self.assertEqual("bind-ok", bound.get("t"), bound)
            after = self.session(name)
            self.assertFalse(after.get("identityPending"))
            self.assertEqual("captured-id", after.get("sessionId"))

            relaunched = run_request(
                self.muxd.port,
                {"t": "create", "s": name, "relaunch": True},
                timeout=12,
            )
            self.assertTrue(relaunched.get("created"))
            self.wait_for_tail(name, resume_marker)
        finally:
            self.kill(name)

    def test_bind_accepts_a_plain_shell_running_an_agent_inside_it(self):
        """An agent started INSIDE a mux tab (`cc --resume <id>` typed at the shell) leaves muxd with
        kind="shell", hasCommand=false, sessionId="" and identity_pending FALSE. That is a session with NO
        identity, not one with a conflicting identity, so a bind must not be refused as "already has a
        different canonical identity" - refusing it wedges the tab permanently: the desktop bridge re-proposes
        the binding on every tick (alive && !hasCommand) and the tab can never become resumable. The bind must
        instead go through the pending path, which verifies the tab's own live descendant before binding."""
        name = "it-bare-shell-bind"
        marker = f"MUXD_IT_BARE_{int(time.time() * 1000)}"
        sid = "bare-shell-resume-id"
        self.kill(name)
        try:
            created = run_request(self.muxd.port, {"t": "create", "s": name}, timeout=12)
            self.assertTrue(created.get("created"), created)
            shell = self.session(name)
            self.assertTrue(shell.get("shellOnly"))
            self.assertFalse(shell.get("hasCommand"))
            self.assertFalse(shell.get("identityPending"))
            self.assertEqual("", shell.get("sessionId"))

            # Start a live descendant whose command line carries the resume id, exactly as the gateway does:
            # node.exe is one of muxd's agent-cmdline executables and `--resume <id>` is what _parse_resume_id
            # reads. It stays alive on the setInterval so the ownership check has something to find.
            asyncio.run(attach_and_roundtrip(
                self.muxd.port,
                name,
                f"node -e \"console.log('{marker}');setInterval(function(){{}},1000)\" -- --resume {sid}\r",
                marker,
            ))

            # The agent-cmdline probe is TTL-cached, so allow a couple of refreshes before judging.
            deadline = time.time() + 15
            bound = None
            while time.time() < deadline:
                bound = run_request(self.muxd.port, {
                    "t": "bind",
                    "s": name,
                    "generationId": self.session(name)["generationId"],
                    "cmd": f"# cc --resume {sid}",
                    "sessionId": sid,
                    "aliases": [],
                }, timeout=12)
                if bound.get("t") == "bind-ok":
                    break
                self.assertNotIn(
                    "different canonical identity", bound.get("m", ""),
                    "a session holding no identity must never be reported as holding a different one")
                time.sleep(0.5)
            self.assertEqual("bind-ok", bound.get("t"), bound)
            after = self.session(name)
            self.assertFalse(after.get("identityPending"))
            self.assertEqual(sid, after.get("sessionId"))
        finally:
            self.kill(name)

    def test_bind_still_refuses_a_session_that_truly_has_a_different_identity(self):
        """The conflict guard must survive the fix above: a session already bound to one identity must still
        refuse a bind proposing another."""
        name = "it-real-identity-conflict"
        self.kill(name)
        try:
            run_request(self.muxd.port, {
                "t": "create", "s": name,
                "cmd": f"Write-Output '{name}'; # cc --resume real-identity-id",
            }, timeout=12)
            self.wait_for_tail(name, name)
            current = self.session(name)
            self.assertEqual("real-identity-id", current.get("sessionId"), current)

            refused = run_request(self.muxd.port, {
                "t": "bind",
                "s": name,
                "generationId": current["generationId"],
                "cmd": "# cc --resume some-other-id",
                "sessionId": "some-other-id",
                "aliases": [],
            }, timeout=12)
            self.assertEqual("err", refused.get("t"), refused)
            self.assertIn("different canonical identity", refused.get("m", ""))
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
            self.assertNotIn(old_marker, new.get("tail") or "")
            self.assertNotIn("cmdSig", new)
        finally:
            self.kill(name)

    def test_attach_missing_session_is_refused_without_spawning(self):
        name = "it-missing-attach"
        self.kill(name)

        result = run_request(self.muxd.port, {"t": "attach", "s": name})

        self.assertEqual(result.get("t"), "err")
        self.assertIn("no such session", result.get("m", ""))
        self.assertIsNone(self.session(name))

    def test_relay_kill_fences_shell_generation_and_correlates_result(self):
        async def scenario():
            connected = asyncio.get_running_loop().create_future()
            finished = asyncio.Event()

            async def relay(ws):
                hello = json.loads(await ws.recv())
                if not connected.done():
                    connected.set_result((ws, hello))
                await finished.wait()

            async with websockets.serve(relay, '127.0.0.1', 0) as listener:
                fixture = await asyncio.to_thread(DisposableMuxd, listener.sockets[0].getsockname()[1])
                try:
                    ws, hello = await asyncio.wait_for(connected, 20)
                    self.assertIn('killFence', hello['caps'])
                    name = 'it-relay-kill'
                    created = await request_json(fixture.port, {'t': 'create', 's': name, 'cols': 80, 'rows': 24})
                    self.assertTrue(created.get('created'), created)
                    listing = await request_json(fixture.port, {'t': 'ls'})
                    original = next(row for row in listing['list'] if row['name'] == name)
                    self.assertEqual('', original.get('sessionId', ''))
                    for invalid_id in [None, 'invalid/identity']:
                        invalid = await request_json(fixture.port, {'t': 'kill', 's': name,
                            'sessionId': invalid_id, 'generationId': original['generationId']})
                        self.assertEqual('err', invalid.get('t'), invalid)

                    async def send_kill(rid, generation):
                        await ws.send(json.dumps({'t': 'kill', 's': name, 'rid': rid,
                                                  'sessionId': '', 'generationId': generation}))
                        async def result():
                            while True:
                                frame = json.loads(await ws.recv())
                                if frame.get('rid') == rid or frame.get('t') == 'killed':
                                    return frame
                        return await asyncio.wait_for(result(), 15)

                    refused = await send_kill('stale-stop', 'obsolete-generation')
                    self.assertEqual('killResult', refused.get('t'), refused)
                    self.assertFalse(refused.get('ok'))
                    alive = next(row for row in (await request_json(fixture.port, {'t': 'ls'}))['list'] if row['name'] == name)
                    self.assertEqual(original['generationId'], alive['generationId'])
                    self.assertTrue(alive['alive'])
                    killed = await send_kill('exact-stop', original['generationId'])
                    self.assertEqual('killed', killed.get('t'), killed)
                    self.assertEqual('exact-stop', killed.get('rid'))
                    self.assertEqual(original['generationId'], killed.get('generationId'))
                    self.assertEqual('', killed.get('sessionId'))
                    self.assertNotIn(name, [row['name'] for row in (await request_json(fixture.port, {'t': 'ls'}))['list']])
                finally:
                    finished.set()
                    await asyncio.to_thread(fixture.close)

        asyncio.run(scenario())

    def test_fenced_kill_requires_matching_identity_and_generation(self):
        name = "it-fenced-kill"
        self.kill(name)
        created = run_request(self.muxd.port, {
            "t": "create", "s": name, "cmd": "while($true){Start-Sleep -Milliseconds 200}",
            "sessionId": "fenced-session",
        }, timeout=18)
        self.assertEqual("created", created.get("t"), created)
        row = next(item for item in run_request(self.muxd.port, {"t": "ls"})["list"] if item["name"] == name)
        self.assertTrue(row.get("generationId"), row)
        wrong_id = run_request(self.muxd.port, {"t": "kill", "s": name,
                                                "sessionId": "wrong", "generationId": row["generationId"]})
        self.assertEqual("err", wrong_id.get("t"), wrong_id)
        self.assertIsNotNone(self.session(name))
        wrong_generation = run_request(self.muxd.port, {"t": "kill", "s": name,
                                                        "sessionId": "fenced-session", "generationId": "wrong"})
        self.assertEqual("err", wrong_generation.get("t"), wrong_generation)
        self.assertIsNotNone(self.session(name))
        killed = run_request(self.muxd.port, {"t": "kill", "s": name,
                                              "sessionId": "fenced-session", "generationId": row["generationId"]}, timeout=18)
        self.assertEqual("killed", killed.get("t"), killed)

    def test_kill_removes_session_from_manifest_and_listing(self):
        name = "it-kill"
        self.kill(name)
        run_request(
            self.muxd.port,
            {
                "t": "create",
                "s": name,
                "cmd": "$env:MUX_KILL_PROBE='it-kill'; while($true){Start-Sleep -Milliseconds 237}",
            },
            timeout=20,
        )
        current = self.session(name)
        self.assertIsNotNone(current)
        child_pid = int(current.get("childPid") or 0)
        self.assertGreater(child_pid, 0)
        self.assertTrue(process_alive(child_pid))

        killed = run_request(self.muxd.port, {"t": "kill", "s": name}, timeout=6)

        self.assertEqual(killed.get("t"), "killed")
        self.assertIsNone(self.session(name))
        self.assertFalse(process_alive(child_pid), "kill acknowledged while the child process was still alive")

    def test_create_is_not_acknowledged_and_spawn_is_reaped_when_manifest_commit_fails(self):
        name = "it-create-persist-fail"
        self.kill(name)
        self.muxd.fail_persistence("before_write")

        result = run_request(
            self.muxd.port,
            {
                "t": "create",
                "s": name,
                "cmd": "while($true){Start-Sleep -Milliseconds 200}",
            },
            timeout=12,
        )

        self.assertEqual("err", result.get("t"), result)
        self.assertIn("durably reserve session start", result.get("m", ""))
        self.assertIsInstance(result.get("retryable"), bool, "local create errors must retain retry classification")
        self.assertIsNone(self.session(name))
        self.muxd.restart()
        self.assertIsNone(self.session(name))

    def test_create_persists_starting_intent_before_spawn_and_keeps_failed_record(self):
        name = "it-create-active-persist-fail"
        self.kill(name)
        self.muxd.fail_persistence("before_write", after=1)

        result = run_request(
            self.muxd.port,
            {
                "t": "create",
                "s": name,
                "cmd": "while($true){Start-Sleep -Milliseconds 200}",
            },
            timeout=20,
        )

        self.assertEqual("err", result.get("t"), result)
        current = self.session(name)
        self.assertIsNotNone(current)
        self.assertFalse(current.get("alive"))
        self.assertEqual("failed", current.get("lifecycle"))
        self.muxd.restart()
        restored = self.session(name)
        self.assertIsNotNone(restored)
        self.assertFalse(restored.get("alive"))
        self.assertEqual("failed", restored.get("lifecycle"))
        self.kill(name)

    def test_create_retries_transient_post_replace_uncertainty(self):
        name = "it-create-post-replace"
        self.kill(name)
        self.muxd.fail_persistence("before_directory_fsync")

        result = run_request(
            self.muxd.port,
            {
                "t": "create",
                "s": name,
                "cmd": "while($true){Start-Sleep -Milliseconds 200}",
            },
            timeout=20,
        )

        self.assertTrue(result.get("created"), result)
        current = self.session(name)
        self.assertTrue(current.get("alive"))
        self.assertEqual("active", current.get("lifecycle"))
        self.kill(name)

    def test_kill_does_not_stop_or_ack_when_tombstone_commit_fails(self):
        name = "it-kill-persist-fail"
        self.kill(name)
        created = run_request(
            self.muxd.port,
            {
                "t": "create",
                "s": name,
                "cmd": "while($true){Start-Sleep -Milliseconds 200}",
            },
            timeout=12,
        )
        self.assertTrue(created.get("created"), created)
        self.muxd.fail_persistence("before_write")

        result = run_request(self.muxd.port, {"t": "kill", "s": name}, timeout=8)

        self.assertEqual("err", result.get("t"), result)
        self.assertIn("durably record session stop", result.get("m", ""))
        self.assertTrue(self.session(name).get("alive"))
        self.kill(name)

    def test_kill_retries_transient_post_replace_uncertainty(self):
        name = "it-kill-post-replace"
        self.kill(name)
        created = run_request(
            self.muxd.port,
            {
                "t": "create",
                "s": name,
                "cmd": "while($true){Start-Sleep -Milliseconds 200}",
            },
            timeout=12,
        )
        self.assertTrue(created.get("created"), created)
        self.muxd.fail_persistence("before_directory_fsync")

        result = run_request(self.muxd.port, {"t": "kill", "s": name}, timeout=12)

        self.assertEqual("killed", result.get("t"), result)
        self.assertIsNone(self.session(name))
        self.muxd.restart()
        self.assertIsNone(self.session(name))

    def test_bind_rolls_back_identity_when_manifest_commit_fails(self):
        name = "it-bind-persist-fail"
        self.kill(name)
        try:
            created = run_request(
                self.muxd.port,
                {
                    "t": "create",
                    "s": name,
                    "cmd": claude_registry_command("durable-bind-id", "BIND_ID_READY"),
                    "identityPending": True,
                },
                timeout=12,
            )
            self.assertTrue(created.get("created"), created)
            self.wait_for_tail(name, "BIND_ID_READY")
            self.muxd.fail_persistence("before_write")

            result = run_request(
                self.muxd.port,
                {
                    "t": "bind",
                    "s": name,
                    "generationId": self.session(name)["generationId"],
                    "cmd": "Write-Output bound; # codex resume durable-bind-id",
                    "sessionId": "durable-bind-id",
                },
                timeout=12,
            )

            self.assertEqual("err", result.get("t"), result)
            self.assertIn("durably bind session identity", result.get("m", ""))
            current = self.session(name)
            self.assertTrue(current.get("identityPending"))
            self.assertEqual("", current.get("sessionId"))
        finally:
            self.kill(name)

    def test_bind_retries_transient_post_replace_uncertainty(self):
        name = "it-bind-post-replace"
        self.kill(name)
        try:
            created = run_request(
                self.muxd.port,
                {
                    "t": "create",
                    "s": name,
                    "cmd": claude_registry_command("durable-bind-post-replace", "BIND_ID_READY"),
                    "identityPending": True,
                },
                timeout=12,
            )
            self.assertTrue(created.get("created"), created)
            self.wait_for_tail(name, "BIND_ID_READY")
            self.muxd.fail_persistence("before_directory_fsync")

            result = run_request(
                self.muxd.port,
                {
                    "t": "bind",
                    "s": name,
                    "generationId": self.session(name)["generationId"],
                    "cmd": "Write-Output bound; # codex resume durable-bind-post-replace",
                    "sessionId": "durable-bind-post-replace",
                },
                timeout=12,
            )

            self.assertEqual("bind-ok", result.get("t"), result)
            current = self.session(name)
            self.assertFalse(current.get("identityPending"))
            self.assertEqual("durable-bind-post-replace", current.get("sessionId"))
        finally:
            self.kill(name)

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

    def test_slow_manifest_persistence_does_not_block_info_requests(self):
        name = "it-nonblocking-persist"
        self.kill(name)
        try:
            self.muxd.delay_persistence("before_write", 1500)
            created, samples = asyncio.run(
                info_during_slow_create_persistence(
                    self.muxd.port,
                    self.muxd.root / "persist-fault.json",
                    name,
                    "while($true){Start-Sleep -Milliseconds 200}",
                    timeout=18,
                )
            )

            self.assertTrue(created.get("created"), created)
            self.assertGreaterEqual(len(samples), 3, samples)
            slow = [
                round(elapsed, 3)
                for elapsed, result in samples
                if elapsed > 0.75 or isinstance(result, Exception)
            ]
            self.assertEqual(
                [],
                slow,
                f"info requests stalled or failed during durable manifest write; samples={samples!r}",
            )
        finally:
            self.kill(name)

    def test_slow_manifest_persistence_does_not_block_unrelated_input(self):
        target = "it-input-during-persist"
        creating = "it-input-during-persist-create"
        marker = f"MUXD_INPUT_DURING_PERSIST_{int(time.time() * 1000)}"
        self.kill(target)
        self.kill(creating)
        try:
            target_result = run_request(
                self.muxd.port,
                {
                    "t": "create",
                    "s": target,
                },
                timeout=18,
            )
            self.assertTrue(target_result.get("created"), target_result)

            self.muxd.delay_persistence("before_write", 1500)
            created, input_result, input_elapsed = asyncio.run(
                input_during_slow_create(
                    self.muxd.port,
                    self.muxd.root / "persist-fault.json",
                    {
                        "t": "create",
                        "s": creating,
                        "cmd": "while($true){Start-Sleep -Milliseconds 200}",
                    },
                    {
                        "t": "input",
                        "s": target,
                        "d": base64.b64encode(
                            f"Write-Output '{marker}'\r".encode("utf-8")
                        ).decode("ascii"),
                    },
                )
            )

            self.assertTrue(created.get("created"), created)
            self.assertEqual("input-ok", input_result.get("t"), input_result)
            self.assertLess(
                input_elapsed,
                0.75,
                f"unrelated input waited for durable state transaction: {input_elapsed:.3f}s",
            )
            self.wait_for_tail(target, marker)
        finally:
            self.kill(target)
            self.kill(creating)

    def test_slow_manifest_persistence_does_not_block_read_only_attach(self):
        target = "it-attach-during-persist"
        creating = "it-attach-during-persist-create"
        self.kill(target)
        self.kill(creating)
        try:
            target_result = run_request(
                self.muxd.port,
                {"t": "create", "s": target},
                timeout=18,
            )
            self.assertTrue(target_result.get("created"), target_result)

            self.muxd.delay_persistence("before_write", 1500)
            created, initial, attach_elapsed = asyncio.run(
                attach_during_slow_create(
                    self.muxd.port,
                    self.muxd.root / "persist-fault.json",
                    {
                        "t": "create",
                        "s": creating,
                        "cmd": "while($true){Start-Sleep -Milliseconds 200}",
                    },
                    target,
                )
            )

            self.assertTrue(created.get("created"), created)
            self.assertIsInstance(initial, bytes)
            self.assertLess(
                attach_elapsed,
                0.75,
                f"read-only attach waited for durable state transaction: {attach_elapsed:.3f}s",
            )
        finally:
            self.kill(target)
            self.kill(creating)

    def test_concurrent_distinct_creates_survive_restart_without_snapshot_loss(self):
        first = "it-distinct-persist-a"
        second = "it-distinct-persist-b"
        self.kill(first)
        self.kill(second)
        try:
            self.muxd.delay_persistence("before_write", 750)
            results = asyncio.run(concurrent_requests(
                self.muxd.port,
                [
                    {
                        "t": "create",
                        "s": first,
                        "cmd": "while($true){Start-Sleep -Milliseconds 200}",
                    },
                    {
                        "t": "create",
                        "s": second,
                        "cmd": "while($true){Start-Sleep -Milliseconds 200}",
                    },
                ],
                timeout=18,
            ))

            self.assertTrue(all(result.get("created") for result in results), results)
            self.muxd.restart()
            self.assertIsNotNone(self.session(first))
            self.assertIsNotNone(self.session(second))
        finally:
            self.kill(first)
            self.kill(second)
