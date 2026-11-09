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
        """Historical: did this string EVER appear. Not proof of the current geometry - see now_size."""
        return needle.encode() in self.output()

    def now_size(self):
        """The last SIZE the child printed: the pty's size NOW, not the best size it ever had.

        A monotonic `in buffer` check is how a restore that lands on the wrong size still passes - the
        old SIZE=120x30 from startup is still in the scrollback. The proof must read the tail.
        """
        import re
        hits = re.findall(rb"SIZE=(\d+)x(\d+)", self.output())
        return (int(hits[-1][0]), int(hits[-1][1])) if hits else None

    def pane_size(self):
        """The harness terminal's own dimensions, to separate "the pty moved" from "the pane moved"."""
        try:
            dims = self.p.getwinsize()          # pywinpty: (rows, cols)
            return (int(dims[1]), int(dims[0]))
        except Exception:
            return None

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
    # The tee logs to %LOCALAPPDATA%\muxtee\muxtee.log, shared by every run on this box. Truncate it so
    # the tail below is THIS run's evidence and not a paragraph from yesterday.
    tee_log = Path(os.environ.get("LOCALAPPDATA", "")) / "muxtee" / "muxtee.log"
    try:
        tee_log.write_text("")
    except Exception:
        pass
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

        async def epoch_now():
            r = await b1.owner_snapshot(port, "grid-proof")
            return int((r or {}).get("gridEpoch") or 0)

        # A request must speak the relay's language: it names the device taking the grid and asks for an
        # epoch past the one committed. A hard-coded epoch is either stale (a rejection that looks like
        # a product fault) or skips the ordering the contract is proving.
        async def pin(cols, rows, device="device-a", label="Phone A"):
            e = await epoch_now()
            await relay.send({"t": "resize", "s": "grid-proof", "cols": cols, "rows": rows,
                              "epoch": e + 1, "owner": device, "label": label})
            return e + 1

        # T2: the relay asks for a device-owned grid.
        await pin(60, 20)
        moved = await wait_for(lambda: tab.now_size() == (60, 20), 12)
        rows.check("T2 pty follows the remote resize", moved,
                   f"child now reports {tab.now_size()}")
        # The mirror must take the phone's grid WITHOUT the local pane reclaiming it. The tee's own
        # backstop is what would do that, so the assertion is on the tee's statements, not on the
        # harness window (pywinpty reports the size it set, not the host's live console).
        await asyncio.sleep(1.0)
        no_reclaim = "owner=local reason=size" not in mux_log(mux)
        rows.check("T2 no local size reclaim while pinned", no_reclaim,
                   "no 'owner=local reason=size' in muxd.log")

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
        # Geometry alone does not prove the handover: a `size` frame from the tee would set the same
        # numbers while leaving the pane as the owner, which is the divergence this contract exists to
        # end. The committed owner must be the device that asked.
        rows.check("T2 muxd commits the device as owner",
                   bool(row) and row.get("gridOwner") == "pin"
                   and row.get("gridOwnerLabel") == "Phone A",
                   f"gridOwner={row.get('gridOwner') if row else None} "
                   f"label={row.get('gridOwnerLabel') if row else None}")

        # T2b: the ring was cleared, so a resync went out to viewers *because of this commit*.
        got_resync = resyncs() > resync_before
        rows.check("T2b resync after commit", got_resync,
                   f"{resync_before} -> {resyncs()} resync frame(s)")

        # T3: re-asserting the epoch that just committed must not move anything.
        committed_epoch = await epoch_now()
        await relay.send({"t": "resize", "s": "grid-proof", "cols": 90, "rows": 25,
                          "epoch": committed_epoch, "owner": "device-b"})
        await asyncio.sleep(2.0)
        r = await b1.owner_snapshot(port, "grid-proof")
        stale_ok = ((r.get("cols"), r.get("rows")) == (60, 20)
                    and int(r.get("gridEpoch") or 0) == committed_epoch)
        rows.check("T3 stale epoch rejected", stale_ok and tab.now_size() == (60, 20),
                   f"still {r.get('cols')}x{r.get('rows')} epoch={r.get('gridEpoch')} "
                   f"pty={tab.now_size()}" if r else "no row")

        # T7: a newer epoch from another device wins.
        await pin(100, 28, device="device-b", label="Phone B")
        rows.check("T7 newer epoch commits",
                   await wait_for(lambda: tab.now_size() == (100, 28), 12),
                   f"child now reports {tab.now_size()}")
        r = await b1.owner_snapshot(port, "grid-proof")
        rows.check("T7 last pin wins ownership",
                   bool(r) and r.get("gridOwner") == "pin"
                   and r.get("gridOwnerLabel") == "Phone B"
                   and (r.get("cols"), r.get("rows")) == (100, 28),
                   f"gridOwner={r.get('gridOwner') if r else None} "
                   f"label={r.get('gridOwnerLabel') if r else None} "
                   f"{r.get('cols') if r else None}x{r.get('rows') if r else None}")

        # T6: a local keystroke reclaims the pane's grid.
        tab.write("x")
        reclaimed = await wait_for(lambda: tab.now_size() == (120, 30), 12)
        rows.check("T6 local key restores the pane", reclaimed,
                   f"child now reports {tab.now_size()} pane={tab.pane_size()}")
        end = time.time() + 8
        owner_local = False
        while time.time() < end:
            r = await b1.owner_snapshot(port, "grid-proof")
            if r and r.get("gridOwner") == "local":
                owner_local = True
                break
            await asyncio.sleep(0.3)
        rows.check("T6 muxd reports local ownership", owner_local,
                   f"gridOwner={r.get('gridOwner') if r else None} "
                   f"{r.get('cols') if r else None}x{r.get('rows') if r else None}")
        rows.check("T6 committed grid is the pane again",
                   bool(r) and (r.get("cols"), r.get("rows")) == (120, 30),
                   f"{r.get('cols') if r else None}x{r.get('rows') if r else None}")

        grid_lines = [ln for ln in mux_log(mux).splitlines()
                      if any(k in ln for k in ("owner=", "resize", "grid", "size"))]
        print("\n--- muxd grid log ---")
        print("\n".join(grid_lines[-25:]) or "(no grid lines)")
        try:
            tee_text = tee_log.read_text(encoding="utf-8", errors="replace")
        except Exception:
            tee_text = ""
        tee_lines = [ln for ln in tee_text.splitlines()
                     if any(k in ln for k in ("resize", "size", "activity", "remote", "pane"))]
        print("--- muxtee grid log ---")
        print("\n".join(tee_lines[-20:]) or "(no grid lines)")
        # ConPTY answers a pseudoconsole resize by asking the TERMINAL to resize (CSI 8 ; rows ; cols t).
        # A host that honours it moves the user's own pane, which would make "the pane is a view" a lie,
        # so the sequence is evidence either way and is reported rather than assumed.
        import re as _re
        wreq = _re.findall(rb"\x1b\[8;(\d+);(\d+)t", tab.output())
        print("--- upstream window-resize requests ---")
        print([(int(a), int(b)) for a, b in wreq[-6:]] or "(none)")

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
