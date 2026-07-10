import asyncio
import base64
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
MUXRUN = REPO / "muxrun.py"
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
                    f"LAUNCH_CLAIM_ROOT={self.root / 'launch-claims'}",
                    "LOCAL_FIRST_TIMEOUT=5",
                    "LOOP_WATCHDOG_WARN=120",
                    "",
                ]
            ),
            encoding="utf-8",
        )
        self.env = os.environ.copy()
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

    def test_replayed_input_intent_is_at_most_once(self):
        name = "it-idempotent-input"
        intent_id = f"input-{int(time.time() * 1000)}"
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
            run_request(self.muxd.port, {"t": "create", "s": name}, timeout=12)
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
            stderr=subprocess.DEVNULL,
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
            self.assertIsNone(owner.poll(), "muxrun child owner exited during muxd restart")
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
                    "cmd": f"Write-Output '{marker}'; while($true){{Start-Sleep -Milliseconds 200}}",
                    "identityPending": True,
                },
                timeout=12,
            )
            self.assertTrue(first.get("created"))
            pending = self.wait_for_tail(name, marker)
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
            bound = run_request(
                self.muxd.port,
                {
                    "t": "bind",
                    "s": name,
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

    def test_kill_removes_session_from_manifest_and_listing(self):
        name = "it-kill"
        self.kill(name)
        run_request(self.muxd.port, {"t": "create", "s": name}, timeout=12)
        self.assertIsNotNone(self.session(name))

        killed = run_request(self.muxd.port, {"t": "kill", "s": name}, timeout=6)

        self.assertEqual(killed.get("t"), "killed")
        self.assertIsNone(self.session(name))

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
                    "cmd": "while($true){Start-Sleep -Milliseconds 200}",
                    "identityPending": True,
                },
                timeout=12,
            )
            self.assertTrue(created.get("created"), created)
            self.muxd.fail_persistence("before_write")

            result = run_request(
                self.muxd.port,
                {
                    "t": "bind",
                    "s": name,
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
                    "cmd": "while($true){Start-Sleep -Milliseconds 200}",
                    "identityPending": True,
                },
                timeout=12,
            )
            self.assertTrue(created.get("created"), created)
            self.muxd.fail_persistence("before_directory_fsync")

            result = run_request(
                self.muxd.port,
                {
                    "t": "bind",
                    "s": name,
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
                create_while_hammering_info(
                    self.muxd.port,
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
