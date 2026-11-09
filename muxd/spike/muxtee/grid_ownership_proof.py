"""Grid-ownership proof: the whole contract on a disposable stack, no deployment, no live tabs.

Drives the REAL muxtee.exe under a real ConPTY, against a disposable muxd on a free port, with a
fake relay on the muxd host link. Reuses the B1 harness (DisposableMuxd / FakeRelay / owner_snapshot)
so the isolation recipe is identical to the one that already runs green.

Contract: C:\\Users\\Ahmed\\mux-track\\local-fixtures-20261102\\GRID-OWNERSHIP-CONTRACT.md
Rows proven here (each prints PASS/FAIL; the process exits non-zero if any row fails):
  T0  baseline: a local-tab registers at the pane's geometry (120x30) and the child agrees
  G3  zero resize traffic while local ownership holds
  T2  a remote resize reaches the PTY (the child reports the new size), and muxd commits it
  T2b the ring is cleared on the commit (a resync is emitted) - the empty 0x03 aftermath
  T3  a stale epoch cannot move the geometry
  T6  a local keystroke reclaims: the PTY returns to the pane size and muxd reports owner=local
  T7  last pin wins (a second device's higher epoch commits; the first is stale)

Usage:
  python muxd/spike/muxtee/grid_ownership_proof.py [--exe PATH] [--repo PATH]

RED today: the installed tee has no inbound resize case and muxd's OwnerSession.resize is a no-op,
so T2 fails at "the child still reports 120x30". That is the intended red before the campaign lands.
"""
import argparse
import asyncio
import os
import sys
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import b1_reconnect_proof as b1  # noqa: E402  (the disposable-profile + fake-relay harness)

try:
    import winpty
except ImportError:
    print("pywinpty missing")
    sys.exit(2)

# A child that reports the console size it actually sees, so a PTY resize is observable as data and not
# as an inference: if the pty did not move, this line never changes.
CHILD = (
    "import os,time\n"
    "while True:\n"
    "    c,l = os.get_terminal_size()\n"
    "    print('SIZE=%dx%d' % (c,l), flush=True)\n"
    "    time.sleep(0.2)\n"
)


class SizeTab:
    """A real muxtee owning its own ConPTY, running a child that prints its console size."""

    def __init__(self, exe, port, name, cols=120, rows=30):
        env = os.environ.copy()
        # This session may itself be running under a muxtee, which exports MUXTEE_ACTIVE. The tee treats
        # that as "an outer muxtee already owns a ConPTY" and drops to passthrough, so it must be cleared
        # or the proof silently tests nothing.
        env.pop("MUXTEE_ACTIVE", None)
        env.pop("MUXTEE_DISABLE", None)
        env.update({
            "MUXTEE_LOCAL_PORT": str(port),
            "MUXTEE_NAME": name,
            "MUXTEE_NOLINK": "",
        })
        self.name = name
        self.buf = bytearray()
        self._stop = threading.Event()
        self.p = winpty.PtyProcess.spawn(
            [exe, "--", sys.executable, "-u", "-c", CHILD],
            dimensions=(rows, cols), env=env)
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

    def saw(self, needle):
        return needle.encode() in self.output()

    def write(self, s):
        try:
            self.p.write(s)
        except Exception:
            pass

    def kill(self):
        self._stop.set()
        try:
            self.p.terminate(force=True)
        except Exception:
            pass


class Rows:
    def __init__(self):
        self.failed = []

    def check(self, row, ok, detail=""):
        print(f"{'PASS' if ok else 'FAIL'} {row}{(' - ' + detail) if detail else ''}")
        if not ok:
            self.failed.append(row)
        return ok


async def wait_for(fn, timeout, step=0.15):
    end = time.time() + timeout
    while time.time() < end:
        try:
            if fn():
                return True
        except Exception:
            pass
        await asyncio.sleep(step)
    return False


def mux_log(mux):
    for p in (mux.root / "state" / "muxd.log", mux.root / "muxd.log"):
        try:
            if p.exists():
                return p.read_text(encoding="utf-8", errors="replace")
        except Exception:
            pass
    return ""


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=r"C:\Users\Ahmed\muxtee\muxtee.exe")
    ap.add_argument("--repo", default=str(Path(__file__).resolve().parents[3]))
    ap.add_argument("--keep", action="store_true", help="leave the temp dir for inspection")
    args = ap.parse_args()
    exe, repo = Path(args.exe), Path(args.repo)
    if not exe.exists():
        print(f"muxtee.exe not found: {exe}")
        return 2
    b1.REPO = repo
    b1.MUXD = repo / "muxd" / "muxd.py"
    print(f"exe={exe}\nrepo={repo}")

    rows = Rows()
    relay = await b1.FakeRelay().start()
    mux = b1.DisposableMuxd(relay_port=relay.port)
    port = mux.port
    print(f"muxd on {port}, relay on {relay.port}")
    tab = None
    try:
        if not await relay.wait_connected(25):
            print(mux.dump_log())
            print("FAIL T0 - muxd never dialed the relay")
            return 1

        tab = SizeTab(str(exe), port, "grid-proof")
        row = None

        async def registered():
            nonlocal row
            row = await b1.owner_snapshot(port, "grid-proof")
            return bool(row and row.get("kind") == "local-tab")
        end = time.time() + 20
        while time.time() < end and not await registered():
            await asyncio.sleep(0.4)
        if not rows.check("T0 registration", bool(row), f"kind={row.get('kind') if row else None}"):
            print(mux.dump_log())
            print(b1.muxtee_log_tail())
            return 1

        rows.check("T0 registered geometry",
                   (row.get("cols"), row.get("rows")) == (120, 30),
                   f"{row.get('cols')}x{row.get('rows')}")

        if not await wait_for(lambda: tab.saw("SIZE=120x30"), 12):
            print(repr(tab.output()[-400:]))
            rows.check("T0 child sees 120x30", False)
            return 1
        rows.check("T0 child sees 120x30", True)

        # G3: with no pin and no remote resize, nothing may emit a resize request.
        await asyncio.sleep(2.5)
        log = mux_log(mux)
        rows.check("G3 zero resize traffic", "resize req" not in log,
                   "no 'resize req' in muxd.log")

        def resyncs():
            return sum(1 for k, n, p in relay.frames
                       if isinstance(p, dict) and p.get("t") == "resync")
        resync_before = resyncs()

        # T2: the relay asks for a device-owned grid.
        await relay.send({"t": "resize", "s": "grid-proof", "cols": 60, "rows": 20, "epoch": 1})
        moved = await wait_for(lambda: tab.saw("SIZE=60x20"), 12)
        rows.check("T2 pty follows the remote resize", moved,
                   "child reports 60x20" if moved else "child never reported 60x20")

        end = time.time() + 8
        committed = False
        while time.time() < end:
            r = await b1.owner_snapshot(port, "grid-proof")
            if r and (r.get("cols"), r.get("rows")) == (60, 20):
                row = r
                committed = True
                break
            await asyncio.sleep(0.3)
        rows.check("T2 muxd commits 60x20", committed,
                   f"gridOwner={row.get('gridOwner') if row else None} epoch={row.get('gridEpoch') if row else None}")

        # T2b: the ring was cleared, so a resync went out to viewers *because of this commit*.
        got_resync = resyncs() > resync_before
        rows.check("T2b resync after commit", got_resync,
                   f"{resync_before} -> {resyncs()} resync frame(s)")

        # T3: a stale epoch must not move anything.
        await relay.send({"t": "resize", "s": "grid-proof", "cols": 90, "rows": 25, "epoch": 1})
        await asyncio.sleep(2.0)
        r = await b1.owner_snapshot(port, "grid-proof")
        stale_ok = (r.get("cols"), r.get("rows")) == (60, 20) and tab.saw("SIZE=60x20")
        rows.check("T3 stale epoch rejected", stale_ok,
                   f"still {r.get('cols')}x{r.get('rows')}" if r else "no row")

        # T7: a newer epoch from another device wins.
        await relay.send({"t": "resize", "s": "grid-proof", "cols": 100, "rows": 28, "epoch": 2})
        rows.check("T7 newer epoch commits",
                   await wait_for(lambda: tab.saw("SIZE=100x28"), 12),
                   "child reports 100x28")

        # T6: a local keystroke reclaims the pane's grid.
        tab.write("x")
        reclaimed = await wait_for(lambda: tab.saw("SIZE=120x30"), 12)
        rows.check("T6 local key restores the pane", reclaimed,
                   "child back at 120x30" if reclaimed else "child stuck off-pane")
        end = time.time() + 8
        owner_local = False
        while time.time() < end:
            r = await b1.owner_snapshot(port, "grid-proof")
            if r and r.get("gridOwner") == "local":
                owner_local = True
                break
            await asyncio.sleep(0.3)
        rows.check("T6 muxd reports local ownership", owner_local,
                   f"gridOwner={r.get('gridOwner') if r else None}")

        if rows.failed:
            print("\n--- muxd log ---\n" + mux_log(mux)[-3000:])
            print("--- muxtee log ---\n" + b1.muxtee_log_tail())
            print(f"PROOF FAIL: {len(rows.failed)} row(s): {', '.join(rows.failed)}")
            return 1
        print("PROOF PASS: the grid is owned, committed, refused when stale, and reclaimed locally")
        return 0
    finally:
        if tab:
            tab.kill()
        mux.stop()
        await relay.stop()


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
