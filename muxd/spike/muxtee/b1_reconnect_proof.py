"""B1 proof: a stream owner re-registers after muxd restarts, watched AND unwatched.

Reviewer's bar: "on stagemux, with 2 tabs (one watched, one unwatched), restart muxd. Both must
re-register within a bounded time, accept remote input, and respond to watch. Show it red on the
current code first."

This drives the REAL muxtee.exe (not a stub) under a real ConPTY, against a disposable muxd on a free
port, so it exercises the exact path the review flagged: RecvLoop's Close `return`, SendLoop's
`_outWake` block on an unwatched tab, and the `_watched` that never reset.

Run:  C:/Python311/python.exe muxd/spike/muxtee/b1_reconnect_proof.py [--exe PATH] [--edge]
      --edge  inject a Close frame right after registration to stand in for a muxd restart without
              waiting for a real process bounce (deterministic; no port race).
"""
import argparse
import asyncio
import base64
import json
import os
import socket
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

try:
    import winpty
except ImportError:
    print("pywinpty missing"); sys.exit(2)

REPO = Path(__file__).resolve().parents[3]
MUXD = REPO / "muxd" / "muxd.py"
CREATE_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)

try:
    import websockets
except ImportError:
    print("websockets missing"); sys.exit(2)


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return int(s.getsockname()[1])


class DisposableMuxd:
    def __init__(self, relay_port=1):
        self.root = Path(tempfile.mkdtemp(prefix="b1-proof-"))
        self.port = free_port()
        # Mirror the integration harness exactly: a private profile with its OWN runtime root, off
        # production. Without this a second muxd shares the live state_root and hangs on it at boot.
        rt = self.root / "runtime"
        rt.mkdir(parents=True, exist_ok=True)
        state = self.root / "state"
        state.mkdir(parents=True, exist_ok=True)
        cwd = self.root / "cwd"          # NOT self.root: DEFAULT_CWD may not contain the runtime root
        cwd.mkdir(parents=True, exist_ok=True)
        # Under a profile, muxd's LOCAL_PORT *is* the control port, so this single port is both the
        # attach listener and what muxtee's MUXTEE_LOCAL_PORT must point at.
        control_port = free_port()
        self.port = control_port
        env_file = self.root / "profile.env"
        env_file.write_text("\n".join(f"{k}={v}" for k, v in {
            "MUXD_STATE_ROOT": state,
            "MUXD_CONTROL_PORT": control_port,
            "MUXD_MUTEX_NAME": f"Local\\MuxdB1-{control_port}",
            "MUXD_PRINCIPAL_REGISTRY": self.root / "principal.dpapi",
            "MUXD_PRINCIPAL_INSTANCE_ID": f"b1-principal-{control_port}",
            "MUXD_HOST_IDENTITY": f"b1-host-{control_port}",
            "MUXD_RELAY_URLS": f"ws://127.0.0.1:{relay_port}/host",
            "MUXD_TOKEN_SOURCE": "env:MUX_HOST_TOKEN",
            "MUXD_LAUNCH_CLAIM_ROOT": self.root / "claims",
            "MUXD_TASK_NAME": f"MuxB1-{control_port}",
            "MUXD_RESTART_TASK_NAME": f"MuxB1Restart-{control_port}",
            "MUXD_WATCHDOG_TASK_NAME": f"MuxB1Watchdog-{control_port}",
            "MUXD_GUARDIAN_DISABLED": "1",
            "DEFAULT_CWD": cwd,
            f"LOCAL_PORT": self.port,
        }.items()), encoding="utf-8")
        self.env = os.environ.copy()
        self.env.update({
            "MUX_HOST_TOKEN": "test-token",
            "MUXD_PROFILE": "browser-it",
            "MUXD_RUNTIME_ROOT": str(rt),
            "MUXD_ENV_FILE": str(env_file),
            "MUXCTL_AUTOSTART": "0",
            "MUXCTL_PORT": str(control_port),
            "PYTHONUNBUFFERED": "1",
            "HOME": str(self.root), "USERPROFILE": str(self.root),
            "HOMEDRIVE": self.root.drive or "C:",
            "HOMEPATH": str(self.root)[len(self.root.drive):] if self.root.drive else str(self.root),
        })
        self.proc = None
        self.start()

    def start(self):
        self.logpath = self.root / "muxd.log"
        self._log = open(self.logpath, "w")
        self.proc = subprocess.Popen([sys.executable, str(MUXD), "--profile", "browser-it"],
                                     cwd=str(REPO), env=self.env, stdout=self._log,
                                     stderr=subprocess.STDOUT, creationflags=CREATE_NO_WINDOW)
        self.wait_ready()

    def dump_log(self):
        # muxd logs through LOG_Q to <state_root>/muxd.log, not stdout, so read the state log; the
        # redirected stdout is only a crash backstop and is usually empty.
        for p in (self.root / "state" / "muxd.log", self.logpath):
            try:
                if p.exists():
                    text = p.read_text(encoding="utf-8", errors="replace")
                    if text.strip():
                        return f"[{p.name}]\n" + text[-4000:]
            except Exception:
                continue
        return "<no log>"

    def stop(self):
        if self.proc and self.proc.poll() is None:
            self.proc.terminate()
            try: self.proc.wait(timeout=6)
            except subprocess.TimeoutExpired:
                self.proc.kill(); self.proc.wait(timeout=6)

    def restart(self):
        self.stop(); self.start()

    def wait_ready(self):
        deadline = time.time() + 20
        while time.time() < deadline:
            if self.proc.poll() is not None:
                raise AssertionError("muxd exited before ready rc=" + str(self.proc.returncode)
                                     + "\n" + self.dump_log())
            try:
                with socket.create_connection(("127.0.0.1", self.port), timeout=0.5):
                    return
            except OSError:
                time.sleep(0.2)
        raise AssertionError("muxd local port never opened\n" + self.dump_log())


async def owner_snapshot(port, want_name, timeout=12):
    """Ask muxd's local attach who it knows as a stream owner."""
    async with websockets.connect(f"ws://127.0.0.1:{port}", open_timeout=5,
                                  close_timeout=1, ping_interval=None) as ws:
        await ws.send(json.dumps({"t": "ls"}))
        raw = await asyncio.wait_for(ws.recv(), timeout=timeout)
        msg = json.loads(raw)
        for row in msg.get("list", []):
            if row.get("name") == want_name:
                return row
        return None


HOST_BIN_KIND_REVERSE = {0: "r", 1: "i", 2: "o"}


def decode_host_binary(raw, names):
    """[ver=1][kind][idx_be16][payload] -> (kind, name, payload), or None if malformed.

    The same layout muxd's own decode_host_binary reads; inlined here so the proof decodes what muxd
    actually put on the wire rather than trusting a JSON fallback that only fires when a slot is missing.
    """
    data = bytes(raw)
    if len(data) < 4 or data[0] != 1:
        return None
    kind = HOST_BIN_KIND_REVERSE.get(data[1])
    idx = (data[2] << 8) | data[3]
    if kind is None or idx >= len(names):
        return None
    return kind, names[idx], data[4:]


class FakeRelay:
    """A stand-in for the real relay. muxd dials OUT to it as a websocket client, so this is a server.

    The local attach port has no `watch` verb at all - watch and remote input only ever arrive over the
    host link - so the reviewer's two questions ("respond to watch", "accept remote input") can only be
    asked of the relay path. This speaks just enough of the host link to ask them, and records every
    frame muxd sends back so the proof can attribute output to a named tab.
    """

    def __init__(self):
        self.port = free_port()
        self.names = []                 # session-index -> name, from the hello muxd sends on connect
        self.frames = []                # (kind, name, payload) for output; ("json", None, msg) otherwise
        self.ws = None
        self.connected = asyncio.Event()
        self.server = None

    async def start(self):
        async def conn(ws):
            self.ws = ws
            self.connected.set()
            try:
                async for raw in ws:
                    if isinstance(raw, (bytes, bytearray, memoryview)):
                        dec = decode_host_binary(raw, self.names)
                        if dec is not None:
                            self.frames.append(dec)
                    else:
                        try:
                            msg = json.loads(raw)
                        except Exception:
                            continue
                        if msg.get("t") == "hello":
                            self.names = [str(s.get("name", "")) for s in msg.get("sessions", [])]
                        self.frames.append(("json", None, msg))
            except Exception:
                pass
            finally:
                # Clear ONLY if we still own the slot. A muxd restart makes the old socket's teardown
                # race the new socket's connect: an unconditional `self.ws = None` here lands after the
                # new connection has already claimed it and leaves a LIVE link looking dead.
                if self.ws is ws:
                    self.ws = None
        self.server = await websockets.serve(conn, "127.0.0.1", self.port, ping_interval=None)
        return self

    async def wait_connected(self, timeout=20):
        try:
            await asyncio.wait_for(self.connected.wait(), timeout=timeout)
            return True
        except asyncio.TimeoutError:
            return False

    async def send(self, obj):
        if self.ws is None:
            raise AssertionError("relay not connected to muxd")
        await self.ws.send(json.dumps(obj))

    def output_for(self, name):
        # muxd puts a session's output on the wire in one of two shapes: a BINARY frame (lever 4) when
        # the name holds a slot in the hello it sent, else the JSON `t:o` fallback. A tab that registers
        # AFTER the host link is up has no slot until the next hello, so the fallback is the common case
        # in this proof - read both, or the proof would call a working link silent.
        out = b"".join(p for k, n, p in self.frames if k == "o" and n == name)
        for k, n, p in self.frames:
            if k == "json" and isinstance(p, dict) and p.get("t") == "o" and p.get("s") == name:
                try:
                    out += base64.b64decode(p.get("d", ""))
                except Exception:
                    pass
        return out

    async def watch_and_type(self, name, marker, timeout=10):
        """Watch a tab by name (relay shape), type a line into it, and report whether it drew the echo.

        Both of the reviewer's questions ride this one call: the watch must open the gate (or nothing
        comes back), and the input must reach muxtee's ConPTY (or the child never echoes). The echo is
        the marker, so it can only appear if the keystroke really reached the child.
        """
        # After a muxd restart the host link can be mid-redial; wait for it rather than crash on a None
        # ws. A None here is a link problem, not a watch problem, so returning False is honest.
        if self.ws is None and not await self.wait_connected(15):
            return False
        await self.send({"t": "watch", "s": name})
        await asyncio.sleep(0.6)          # let the watch gate open on muxd before the keystroke
        await self.send({"t": "i", "s": name, "d": base64.b64encode((marker + "\r\n").encode()).decode()})
        deadline = time.time() + timeout
        while time.time() < deadline:
            if marker.encode() in self.output_for(name):
                return True
            await asyncio.sleep(0.1)
        return False

    async def stop(self):
        try:
            if self.server is not None:
                self.server.close()
                await self.server.wait_closed()
        except Exception:
            pass


class Tab:
    """A real muxtee owning its own ConPTY, driven from a pty we answer cursor queries on.

    muxtee only tees when stdin AND stdout are a console; a redirected pipe sends it down the
    passthrough path and nothing ever registers, which is exactly what a bare Popen with stdout=DEVNULL
    produced. winpty gives it a real console, the same way the P1 acceptance harness does, and the same
    harness must answer the \\x1b[6n that INHERIT_CURSOR blocks on or the tee never renders.
    """

    def __init__(self, exe, port, name):
        env = os.environ.copy()
        env.update({
            "MUXTEE_LOCAL_PORT": str(port),
            "MUXTEE_NAME": name,
            "MUXTEE_NOLINK": "",          # link ON
            "MUXTEE_DISABLE": "",
        })
        self.name = name
        self.buf = bytearray()
        self._stop = threading.Event()
        self.p = winpty.PtyProcess.spawn([exe, "--", "cmd.exe", "/k"],
                                         dimensions=(30, 120), env=env)
        self._t = threading.Thread(target=self._pump, daemon=True)
        self._t.start()

    def _pump(self):
        answered = False
        while not self._stop.is_set():
            try:
                d = self.p.read(65536)
            except Exception:
                break
            if not d:
                time.sleep(0.001)
                continue
            self.buf += d.encode("utf-8", "surrogatepass")
            if not answered and "\x1b[6n" in d:
                try:
                    self.p.write("\x1b[1;1R")
                    answered = True
                except Exception:
                    pass

    def output(self):
        return bytes(self.buf)

    def write(self, s):
        try:
            self.p.write(s)
        except Exception:
            pass

    def alive(self):
        try:
            return self.p.isalive()
        except Exception:
            return False

    def kill(self):
        self._stop.set()
        try:
            self.p.terminate(force=True)
        except Exception:
            pass


def muxtee_log_tail(n=2500):
    """muxtee logs to %LOCALAPPDATA%\\muxtee\\muxtee.log, not stdout - read the tail for diagnosis."""
    p = Path(os.environ.get("LOCALAPPDATA", "")) / "muxtee" / "muxtee.log"
    try:
        return p.read_text(encoding="utf-8", errors="replace")[-n:]
    except Exception as e:
        return f"<no muxtee log at {p}: {e}>"


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=str(REPO / "app" / "native" / "MuxTee" / "bin" / "Debug"
                                         / "net10.0-windows" / "muxtee.exe"))
    args = ap.parse_args()
    exe = args.exe
    if not Path(exe).exists():
        print(f"muxtee.exe not found: {exe}"); return 2
    print(f"exe={exe}")

    relay = await FakeRelay().start()
    mux = DisposableMuxd(relay_port=relay.port)
    port = mux.port
    print(f"muxd on {port} relay on {relay.port}")
    # Clear muxtee's own log so a failure tail is this run's, not a stale one's.
    try:
        (Path(os.environ.get("LOCALAPPDATA", "")) / "muxtee" / "muxtee.log").write_text("")
    except Exception:
        pass
    tabs = []
    try:
        if not await relay.wait_connected(25):
            print("--- muxd log ---\n" + mux.dump_log())
            print("PROOF FAIL: muxd never dialed the relay"); return 1

        # Two real tabs. Neither is watched yet: that is the reviewer's case.
        for nm in ("b1-watched", "b1-unwatched"):
            tabs.append(Tab(exe, port, nm))

        # Both must register. Poll `ls` for a LIVE stream owner (kind local-tab): after a restart muxd
        # restores a dormant PLACEHOLDER from its manifest, and that row is not the tab re-attaching, so
        # a kind-agnostic match would call a dead tab re-registered. Keep the last row seen for diagnosis.
        seen = {}

        async def wait_registered(name, deadline_s=15, want_kind="local-tab"):
            d = time.time() + deadline_s
            while time.time() < d:
                row = await owner_snapshot(port, name)
                if row is not None:
                    seen[name] = row
                    if row.get("kind") == want_kind:
                        return row
                await asyncio.sleep(0.4)
            return None

        for nm in ("b1-watched", "b1-unwatched"):
            row = await wait_registered(nm)
            print(f"register {nm}: {'OK kind=' + str(row.get('kind')) if row else 'MISSING'}")
            if row is None:
                print("--- last row seen ---\n" + repr(seen.get(nm)))
                print("--- muxtee log ---\n" + muxtee_log_tail())
                print("--- muxtee screen ---\n" + repr(tabs[0].output()[-400:]))
                print("--- muxd log ---\n" + mux.dump_log())
                print("PROOF FAIL: a tab never registered at all"); return 1

        # The hello carried both tabs, so the relay's slot index now maps names. Wait for it.
        deadline = time.time() + 10
        while time.time() < deadline and not ({"b1-watched", "b1-unwatched"} <= set(relay.names)):
            await asyncio.sleep(0.2)

        # Watch the first one (relay shape) and type into it. Output must come back for it.
        got = await relay.watch_and_type("b1-watched", "B1-WATCHED-OK")
        print(f"watched tab forwards its own input echo: {got}")
        if not got:
            print("--- muxtee log ---\n" + muxtee_log_tail())
            print("--- watched tab local screen ---\n" + repr(tabs[0].output()[-600:]))
            print("--- relay frames ---\n" + repr(relay.frames[-12:]))
            print("--- muxd log ---\n" + mux.dump_log())
            print("PROOF FAIL: a watched tab did not forward output"); return 1

        # The unwatched tab must have forwarded nothing: nobody is watching it.
        leaked = relay.output_for("b1-unwatched")
        print(f"unwatched tab leaked bytes: {len(leaked)}")
        if leaked:
            print("PROOF FAIL: an unwatched tab forwarded output"); return 1

        # --- the restart ---
        print("restarting muxd ...")
        mux.restart()
        print(f"muxd back on {port}")

        # The relay link must come back too, so the same watch/input path can be re-asked.
        relay.connected.clear()
        if not await relay.wait_connected(25):
            print("--- muxd log ---\n" + mux.dump_log())
            print("PROOF FAIL: muxd never re-dialed the relay after restart"); return 1

        # Both tabs must re-register as LIVE tabs within a bounded time, WITHOUT any watch traffic on the
        # second. A dormant placeholder here is a FAIL: it means the tab did not re-attach.
        for nm in ("b1-watched", "b1-unwatched"):
            row = await wait_registered(nm, deadline_s=20)
            print(f"re-register {nm}: {'OK kind=' + str(row.get('kind')) if row else 'MISSING'}")
            if row is None:
                print("--- last row seen ---\n" + repr(seen.get(nm)))
                print("--- muxtee log ---\n" + muxtee_log_tail())
                print("--- muxd log ---\n" + mux.dump_log())
                print(f"PROOF FAIL: {nm} did not re-register after muxd restart"); return 1

        deadline = time.time() + 10
        while time.time() < deadline and not ({"b1-watched", "b1-unwatched"} <= set(relay.names)):
            await asyncio.sleep(0.2)
        print(f"relay link live after restart: {relay.ws is not None} names={sorted(relay.names)}")

        # And the re-registered watched tab must answer a fresh watch and input again.
        got = await relay.watch_and_type("b1-watched", "B1-REWATCH-OK")
        print(f"re-registered tab answers watch+input: {got}")
        if not got:
            print("--- muxtee log ---\n" + muxtee_log_tail())
            print("--- muxd log ---\n" + mux.dump_log())
            print("PROOF FAIL: re-registered tab did not answer watch"); return 1

        print("PROOF PASS: both tabs re-registered after a muxd restart, watched tab live, unwatched silent")
        return 0
    finally:
        for t in tabs:
            t.kill()
        mux.stop()
        await relay.stop()


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
