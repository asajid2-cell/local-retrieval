"""Grid-ownership viewer proof: real relay + real muxd + real muxtee + a real browser, all local.

No deployment and no credential. The relay is started in its documented test posture (MUX_TEST_MODE=1
with MUX_TEST_FIXTURE=1 and a 127.0.0.1 bind, which is the only combination that grants loopback
viewer trust), muxd runs under a disposable profile, and the tab is a real muxtee under a real ConPTY.
The browser then drives the size chip and we read what the relay actually published.

Rows:
  V0  the page attaches to the session and paints a terminal
  V1  owner=local: gridOwner is local, no sx/sy transform, the term grid equals the session grid
  V2  flex tap: gridOwner becomes mine, a resize commits, the term grid becomes the device's grid
  V3  no distortion: the screen transform stays identity while the pin is held
  V4  second tap: ownership returns to local and the term grid returns to the pane grid

Usage: python muxd/spike/muxtee/grid_viewer_proof.py [--exe PATH] [--repo PATH] [--headful]
"""
import argparse
import asyncio
import os
import socket
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

# The page's chip text carries emoji; a cp1252 console must not turn a PASS into a traceback.
for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

sys.path.insert(0, str(Path(__file__).resolve().parent))
import b1_reconnect_proof as b1  # noqa: E402

try:
    import winpty  # noqa: F401
except ImportError:
    print("pywinpty missing")
    sys.exit(2)

TOKEN = "[REDACTED:MUX_HOST_TOKEN]"
CREATE_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return int(s.getsockname()[1])


class Relay:
    """The real relay, booted the way its own tests boot it."""

    def __init__(self, repo):
        self.repo = Path(repo) / "relay"
        self.port = free_port()
        self.tmp = Path(tempfile.mkdtemp(prefix="viewer-proof-"))
        self.proc = None
        self.out = ""

    def start(self):
        env = os.environ.copy()
        env.update({
            "PORT": str(self.port),
            "MUX_HOST_TOKEN": TOKEN,
            "MUX_TEST_MODE": "1",
            "MUX_TEST_FIXTURE": "1",
            "MUX_BIND_HOST": "127.0.0.1",
            "MUX_AUTOHEAL": "0",
            "MUX_STATE_DIR": str(self.tmp),
            "MUX_HOST_SB_WAIT_MS": "40",
            "HLAUTH_BASE": "http://127.0.0.1:1",
            # A browser always sends Origin; the deployed allow-list is the real host, so the loopback
            # origin has to be added explicitly or every viewer attach answers 1008.
            "ALLOWED_WS_ORIGINS": f"http://127.0.0.1:{self.port}",
            "MUX_GRID_DEBUG": "1",
        })
        self.proc = subprocess.Popen([os.environ.get("NODE_BIN", "node"), "server.js"],
                                     cwd=str(self.repo), env=env,
                                     stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                     creationflags=CREATE_NO_WINDOW)
        end = time.time() + 25
        while time.time() < end:
            line = self.proc.stdout.readline()
            if not line:
                if self.proc.poll() is not None:
                    break
                continue
            self.out += line.decode("utf-8", "replace")
            if f"multiplex-app on 127.0.0.1:{self.port}" in self.out:
                threading.Thread(target=self._drain, daemon=True).start()
                return True
        return False

    def _drain(self):
        try:
            for line in self.proc.stdout:
                self.out += line.decode("utf-8", "replace")
        except Exception:
            pass

    def stop(self):
        if self.proc and self.proc.poll() is None:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=8)
            except subprocess.TimeoutExpired:
                self.proc.kill()


class Muxd(b1.DisposableMuxd):
    """Disposable muxd that dials our relay with the token it expects."""

    def __init__(self, relay_port, token):
        os.environ["MUX_HOST_TOKEN"] = token
        super().__init__(relay_port=relay_port)
        # The base class starts the process inside __init__, before we can change its env; restart so the
        # relay's token (not the harness's fixed one) is what the host link presents.
        self.env["MUX_HOST_TOKEN"] = token
        self.restart()


class SizeTab:
    """Real muxtee under a real ConPTY, child reports its console size (see 5a for why)."""

    CHILD = (
        "import os,time\n"
        "while True:\n"
        "    c,l = os.get_terminal_size()\n"
        "    print('SIZE=%dx%d' % (c,l), flush=True)\n"
        "    time.sleep(0.2)\n"
    )

    def __init__(self, exe, port, name, cols=120, rows=30):
        env = os.environ.copy()
        env.pop("MUXTEE_ACTIVE", None)
        env.pop("MUXTEE_DISABLE", None)
        env.update({"MUXTEE_LOCAL_PORT": str(port), "MUXTEE_NAME": name, "MUXTEE_NOLINK": ""})
        self.name, self.buf, self._stop = name, bytearray(), threading.Event()
        self.p = winpty.PtyProcess.spawn([exe, "--", sys.executable, "-u", "-c", self.CHILD],
                                         dimensions=(rows, cols), env=env)
        threading.Thread(target=self._pump, daemon=True).start()

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

    def saw(self, needle):
        return needle.encode() in bytes(self.buf)

    def kill(self):
        self._stop.set()
        try:
            self.p.terminate(force=True)
        except Exception:
            pass


async def wait_for(fn, timeout, step=0.2):
    end = time.time() + timeout
    while time.time() < end:
        try:
            if fn():
                return True
        except Exception:
            pass
        await asyncio.sleep(step)
    return False


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=r"C:\Users\Ahmed\muxtee\muxtee.exe")
    ap.add_argument("--repo", default=str(Path(__file__).resolve().parents[3]))
    ap.add_argument("--headful", action="store_true")
    args = ap.parse_args()
    repo, exe = Path(args.repo), Path(args.exe)
    if not exe.exists():
        print(f"muxtee.exe not found: {exe}")
        return 2

    relay = Relay(repo)
    if not relay.start():
        print("relay failed to boot:\n" + relay.out[-3000:])
        return 1
    print(f"relay on 127.0.0.1:{relay.port}")
    b1.REPO = repo
    b1.MUXD = repo / "muxd" / "muxd.py"
    mux = Muxd(relay_port=relay.port, token=TOKEN)
    tab = None
    try:
        tab = SizeTab(str(exe), mux.port, "viewer-proof")
        end = time.time() + 20
        row = None
        while time.time() < end:
            row = await b1.owner_snapshot(mux.port, "viewer-proof")
            if row and row.get("kind") == "local-tab":
                break
            await asyncio.sleep(0.4)
        if not row or row.get("kind") != "local-tab":
            print("tab did not register\n" + mux.dump_log())
            return 1
        print(f"tab registered at {row.get('cols')}x{row.get('rows')}")

        node = os.environ.get("NODE_BIN", "node")
        cmd = [node, str(Path(__file__).resolve().parent / "grid_viewer_assert.cjs"),
               "--port", str(relay.port), "--session", "viewer-proof",
               "--cols", str(row.get("cols")), "--rows", str(row.get("rows"))]
        if args.headful:
            cmd.append("--headful")
        env = os.environ.copy()
        env["MUX_RELAY_DIR"] = str(repo / "relay")
        proc = subprocess.run(cmd, cwd=str(repo / "relay"), capture_output=True,
                              timeout=240, env=env)
        out = (proc.stdout or b"").decode("utf-8", "replace")
        err = (proc.stderr or b"").decode("utf-8", "replace")
        sys.stdout.write(out)
        sys.stderr.write(err)
        print(f"assert exit={proc.returncode}")
        if proc.returncode != 0:
            print("--- relay log ---\n" + relay.out[-2500:])
            print("--- muxd log ---\n" + mux.dump_log()[-1500:])
        return proc.returncode
    finally:
        if tab:
            tab.kill()
        mux.stop()
        relay.stop()


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
