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


def remote_node_json(script, timeout=25):
    encoded = base64.b64encode(script.encode("utf-8")).decode("ascii")
    remote = "cd ~/multiplex-app && node - <<'NODE'\nconst src = Buffer.from('%s', 'base64').toString('utf8');\neval(src);\nNODE" % encoded
    proc = subprocess.run(
        ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", SSH_TARGET, remote],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        timeout=timeout + 15,
        creationflags=CREATE_NO_WINDOW,
    )
    if proc.returncode != 0:
        raise AssertionError(f"ssh node probe failed rc={proc.returncode}\nstdout={proc.stdout}\nstderr={proc.stderr}")
    out = (proc.stdout or "").strip()
    if not out:
        raise AssertionError("remote node probe returned empty output")
    return json.loads(out.splitlines()[-1])


def relay_ws_marker(name, marker, timeout=25):
    script = f"""
const WebSocket = require('ws');
const name = {json.dumps(name)};
const marker = {json.dumps(marker)};
const timeout = {int(timeout) * 1000};
let seen = '';
let done = false;
function finish(obj, code) {{
  if (done) return;
  done = true;
  console.log(JSON.stringify(obj));
  try {{ ws.close(); }} catch {{}}
  process.exit(code);
}}
const ws = new WebSocket('ws://127.0.0.1:7682/ws?session=' + encodeURIComponent(name) + '&cols=100&rows=30&dev=test&label=test');
const timer = setTimeout(() => finish({{ ok:false, error:'timeout', seen: seen.slice(-1000) }}, 2), timeout);
ws.on('open', () => {{
  setTimeout(() => ws.send('iWrite-Output ' + marker + '\\r'), 800);
}});
ws.on('message', data => {{
  seen += Buffer.from(data).toString('utf8');
  if (seen.includes(marker)) {{
    clearTimeout(timer);
    finish({{ ok:true, marker, seen: seen.slice(-1000) }}, 0);
  }}
}});
ws.on('close', (code, reason) => {{
  if (!done) {{
    clearTimeout(timer);
    finish({{ ok:false, error:'closed', code, reason: String(reason), seen: seen.slice(-1000) }}, 3);
  }}
}});
ws.on('error', err => {{
  if (!done) {{
    clearTimeout(timer);
    finish({{ ok:false, error:err.message, seen: seen.slice(-1000) }}, 4);
  }}
}});
"""
    return remote_node_json(script, timeout=timeout)


@unittest.skipUnless(enabled(), "set MUXD_VPS_TESTS=1 to run VPS-backed smoke tests")
@unittest.skipIf(websockets is None, "websockets package is required for VPS smoke tests")
class VpsSmokeTests(unittest.TestCase):
    def setUp(self):
        def host_ready():
            health = relay_json("GET", "/api/health", timeout=10)
            host = health.get("host") or {}
            return health if host.get("connected") and host.get("protocolOk") else None

        self.wait_for(
            host_ready,
            timeout=45,
            label="relay host connected and protocol-current",
        )

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

    def test_relay_relaunches_dormant_session_from_muxd_saved_command(self):
        suffix = int(time.time() * 1000)
        name = f"vps-saved-{suffix}"
        marker = f"MUXD_VPS_RELAUNCH_{suffix}"
        cmd = f"Write-Output '{marker}'; Start-Sleep -Milliseconds 250; exit"
        self.kill(name)
        try:
            created = local_json({"t": "create", "s": name, "cmd": cmd, "cols": 100, "rows": 28}, timeout=12)
            self.assertTrue(created.get("created"))
            before = self.wait_for(
                lambda: self.local_session(name) if marker in ((self.local_session(name) or {}).get("tail") or "") else None,
                label="initial command marker",
            )
            dormant = self.wait_for(
                lambda: self.relay_session(name) if (self.relay_session(name) or {}).get("dormant") else None,
                label="dormant saved-command relay row",
            )
            self.assertTrue(dormant.get("hasCommand"))

            result = relay_json("POST", f"/api/sessions/{name}/relaunch", {}, timeout=25)
            self.assertTrue(result.get("ok"), result)
            self.assertTrue(result.get("created"), result)

            local = self.wait_for(
                lambda: self.local_session(name)
                if (self.local_session(name) or {}).get("created") != before.get("created")
                and marker in ((self.local_session(name) or {}).get("tail") or "")
                else None,
                label="saved command marker after relaunch",
            )
            self.assertTrue(local.get("hasCommand"))
            self.assertFalse(local.get("shellOnly"))

            relay = self.wait_for(
                lambda: self.relay_session(name) if (self.relay_session(name) or {}).get("hasCommand") else None,
                label="command-backed relay row",
            )
            self.assertFalse(relay.get("shellOnly"))
            self.assertNotIn("cmdSig", relay)
        finally:
            self.kill(name)

    def test_relay_reuses_existing_live_command_session_without_restarting(self):
        suffix = int(time.time() * 1000)
        name = f"vps-same-{suffix}"
        marker = f"MUXD_VPS_SAME_{suffix}"
        cmd = f"Write-Output '{marker}'; while($true){{Start-Sleep -Milliseconds 200}}"
        self.kill(name)
        try:
            first = local_json({"t": "create", "s": name, "cmd": cmd}, timeout=12)
            self.assertTrue(first.get("created"), first)
            before = self.wait_for(
                lambda: self.local_session(name) if marker in ((self.local_session(name) or {}).get("tail") or "") else None,
                label="first command marker",
            )

            second = relay_json("POST", "/api/sessions", {"name": name}, timeout=25)
            after = self.local_session(name)
            self.assertTrue(second.get("ok"), second)
            self.assertFalse(second.get("created"), second)
            self.assertEqual(before.get("created"), after.get("created"))
            self.assertNotIn("cmdSig", after)
        finally:
            self.kill(name)

    def test_websocket_unknown_tab_creates_shell_and_bridges_io(self):
        suffix = int(time.time() * 1000)
        name = f"vps-web-{suffix}"
        marker = f"MUXD_VPS_WEB_{suffix}"
        self.kill(name)
        try:
            probe = relay_ws_marker(name, marker, timeout=30)
            self.assertTrue(probe.get("ok"), probe)

            local = self.wait_for(
                lambda: self.local_session(name) if marker in ((self.local_session(name) or {}).get("tail") or "") else None,
                label="websocket marker in local muxd tail",
            )
            self.assertTrue(local.get("alive"))
            self.assertTrue(local.get("shellOnly"))
            relay = self.relay_session(name)
            self.assertIsNotNone(relay)
            self.assertTrue(relay.get("hosted"))
            self.assertTrue(relay.get("shellOnly"))

            deleted = relay_json("DELETE", f"/api/sessions/{name}", timeout=20)
            self.assertTrue(deleted.get("ok"), deleted)
            self.wait_for(lambda: True if self.local_session(name) is None and self.relay_session(name) is None else None,
                          label="websocket session deleted")
        finally:
            self.kill(name)


if __name__ == "__main__":
    unittest.main()
