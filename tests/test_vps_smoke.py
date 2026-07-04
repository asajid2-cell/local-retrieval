import asyncio
import base64
import json
import os
import subprocess
import time
import unittest

try:
    import websockets
except ImportError:  # pragma: no cover - covered by unittest skip
    websockets = None


SSH_TARGET = os.environ.get("MUXD_VPS_SSH", "harmonizer@192.168.1.142")
RELAY_BASE = os.environ.get("MUXD_VPS_RELAY", "http://127.0.0.1:7682")
LOCAL_PORT = int(os.environ.get("MUXD_LIVE_PORT", "7699"))
CREATE_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)


def enabled():
    return os.environ.get("MUXD_VPS_TESTS", "").lower() in ("1", "true", "yes", "on")


async def local_request(payload, timeout=8):
    async with websockets.connect(f"ws://127.0.0.1:{LOCAL_PORT}", open_timeout=timeout, close_timeout=1, ping_interval=None) as ws:
        await asyncio.wait_for(ws.send(json.dumps(payload)), timeout)
        raw = await asyncio.wait_for(ws.recv(), timeout)
        if isinstance(raw, (bytes, bytearray)):
            raw = bytes(raw).decode("utf-8", "replace")
        return json.loads(raw)


def local_json(payload, timeout=8):
    return asyncio.run(local_request(payload, timeout=timeout))


def relay_json(method, path, body=None, timeout=20):
    script = f"""
import json
import sys
import urllib.error
import urllib.request

url = {json.dumps(RELAY_BASE + path)}
body = json.loads({json.dumps(json.dumps(body))})
data = None if body is None else json.dumps(body).encode("utf-8")
req = urllib.request.Request(url, data=data, method={json.dumps(method)})
if data is not None:
    req.add_header("Content-Type", "application/json")
try:
    with urllib.request.urlopen(req, timeout={int(timeout)}) as r:
        print(r.read().decode("utf-8"))
except urllib.error.HTTPError as e:
    detail = e.read().decode("utf-8", "replace")
    print(json.dumps({{"_http_status": e.code, "_body": detail}}))
    sys.exit(0)
"""
    encoded = base64.b64encode(script.encode("utf-8")).decode("ascii")
    remote = "python3 - <<'PY'\nimport base64\nexec(base64.b64decode('%s'))\nPY" % encoded
    proc = subprocess.run(
        ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", SSH_TARGET, remote],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        timeout=timeout + 15,
        creationflags=CREATE_NO_WINDOW,
    )
    if proc.returncode != 0:
        raise AssertionError(f"ssh relay request failed rc={proc.returncode}\nstdout={proc.stdout}\nstderr={proc.stderr}")
    out = (proc.stdout or "").strip()
    if not out:
        raise AssertionError("relay request returned empty output")
    return json.loads(out.splitlines()[-1])


@unittest.skipUnless(enabled(), "set MUXD_VPS_TESTS=1 to run VPS-backed smoke tests")
@unittest.skipIf(websockets is None, "websockets package is required for VPS smoke tests")
class VpsSmokeTests(unittest.TestCase):
    def local_session(self, name):
        listing = local_json({"t": "ls"})
        for sess in listing.get("list", []):
            if sess.get("name") == name:
                return sess
        return None

    def relay_session(self, name):
        sessions = relay_json("GET", "/api/sessions")
        for sess in sessions:
            if sess.get("name") == name:
                return sess
        return None

    def wait_for(self, fn, timeout=18, label="condition"):
        deadline = time.time() + timeout
        last = None
        while time.time() < deadline:
            last = fn()
            if last:
                return last
            time.sleep(0.4)
        self.fail(f"timed out waiting for {label}; last={last}")

    def kill(self, name):
        try:
            local_json({"t": "kill", "s": name}, timeout=6)
        except Exception:
            pass
        try:
            relay_json("DELETE", f"/api/sessions/{name}", timeout=10)
        except Exception:
            pass

    def test_relay_relaunches_existing_shell_only_session_with_requested_command(self):
        suffix = int(time.time() * 1000)
        name = f"vps-shell-{suffix}"
        marker = f"MUXD_VPS_RELAUNCH_{suffix}"
        cmd = f"Write-Output '{marker}'"
        self.kill(name)
        try:
            created = local_json({"t": "create", "s": name, "cols": 100, "rows": 28}, timeout=12)
            self.assertTrue(created.get("created"))
            shell = self.wait_for(lambda: self.relay_session(name) if (self.relay_session(name) or {}).get("shellOnly") else None, label="shell-only relay row")
            self.assertTrue(shell.get("alive"))
            self.assertFalse(shell.get("hasCommand"))

            result = relay_json("POST", "/api/sessions", {"name": name, "command": cmd}, timeout=25)
            self.assertTrue(result.get("ok"), result)
            self.assertTrue(result.get("created"), result)

            local = self.wait_for(
                lambda: self.local_session(name) if marker in ((self.local_session(name) or {}).get("tail") or "") else None,
                label="command marker in local muxd tail",
            )
            self.assertTrue(local.get("hasCommand"))
            self.assertFalse(local.get("shellOnly"))

            relay = self.wait_for(
                lambda: self.relay_session(name) if (self.relay_session(name) or {}).get("hasCommand") else None,
                label="command-backed relay row",
            )
            self.assertFalse(relay.get("shellOnly"))
            self.assertTrue(relay.get("cmdSig"))
        finally:
            self.kill(name)

    def test_relay_reuses_same_command_session_without_restarting(self):
        suffix = int(time.time() * 1000)
        name = f"vps-same-{suffix}"
        marker = f"MUXD_VPS_SAME_{suffix}"
        cmd = f"Write-Output '{marker}'"
        self.kill(name)
        try:
            first = relay_json("POST", "/api/sessions", {"name": name, "command": cmd}, timeout=25)
            self.assertTrue(first.get("ok"), first)
            self.assertTrue(first.get("created"), first)
            before = self.wait_for(
                lambda: self.local_session(name) if marker in ((self.local_session(name) or {}).get("tail") or "") else None,
                label="first command marker",
            )

            second = relay_json("POST", "/api/sessions", {"name": name, "command": cmd}, timeout=25)
            after = self.local_session(name)
            self.assertTrue(second.get("ok"), second)
            self.assertFalse(second.get("created"), second)
            self.assertEqual(before.get("created"), after.get("created"))
            self.assertEqual(before.get("cmdSig"), after.get("cmdSig"))
        finally:
            self.kill(name)


if __name__ == "__main__":
    unittest.main()
