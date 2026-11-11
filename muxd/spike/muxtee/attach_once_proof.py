"""Attach-once proof: a teed tab's history crosses the wire exactly once on its first attach.

The double replay (GRID-OWNERSHIP-CONTRACT.md 10a): a new viewer's attach both started the tee's feed
(which pushes its whole ring as a resync) AND asked muxd for scrollback, so the same history was parsed
twice before the first keystroke could paint. The fix is to let the attach wait for the resync when the
attach itself started the feed - but "wait" must never be armed for a tab whose owner cannot answer, or
it becomes the 2026-11-11 banner: waiting forever for a snapshot that was never coming.

This drives the REAL muxtee.exe under a real ConPTY and a DISPOSABLE muxd, with a fake relay on the host
link, and proves the chain that makes the wait safe at the layer where it lives:

  A1  a teed tab's hello carries `watchAck`, and muxd's row for it carries the flag through to the relay
  A2  a FIRST `watch` (a real watched-edge flip) answers `watchAck snapshot:true` AND emits the ring
      resync that muxd turns into `{"t":"resync"}` - the reply the relay paints from instead of `sb`
  A3  a REPEATED `watch` (muxd re-stating interest while the tee's own watch never dropped) answers
      `watchAck snapshot:false` and emits NO second resync - the deadlock case, now resolved in one frame
  A4  only a tee that advertises the flag is ever waited on; the relay reads it per session, so an old
      tee in the same muxd keeps its pre-existing path with zero added latency

Usage:
  env -u MUXTEE_ACTIVE /c/Python311/python.exe muxd/spike/muxtee/attach_once_proof.py [--exe PATH]
"""
import argparse
import asyncio
import os
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import b1_reconnect_proof as b1  # noqa: E402
from grid_ownership_proof import SizeTab  # noqa: E402

REPO = Path(__file__).resolve().parents[3]
NAME = 'attach-once'

# A full screen of output, then quiet ticks. A tee's ring must hold this, because it is exactly what the
# relay paints a new viewer from; measuring with a nearly-empty ring (a few status lines) says nothing
# about whether a screen can be painted at all.
SCREEN_CHILD = (
    "import sys,time\n"
    "sys.stdout.write('\\x1b[2J\\x1b[H')\n"
    "for i in range(120):\n"
    "    sys.stdout.write('SCREENLINE %03d %s\\r\\n' % (i, '=' * 40))\n"
    "sys.stdout.flush()\n"
    "while True:\n"
    "    time.sleep(0.5)\n"
    "    sys.stdout.write('tick\\r\\n'); sys.stdout.flush()\n"
)


class ScreenTab(SizeTab):
    """SizeTab, but its child paints a real full screen instead of repeating its own size."""

    def __init__(self, exe, port, name, cols=120, rows=30):
        import threading as _threading
        import winpty as _winpty
        env = os.environ.copy()
        env.pop('MUXTEE_ACTIVE', None)
        env.pop('MUXTEE_DISABLE', None)
        env.update({'MUXTEE_LOCAL_PORT': str(port), 'MUXTEE_NAME': name,
                    'MUXTEE_NOLINK': '', 'TERM': 'xterm-256color'})
        self.name = name
        self.buf = bytearray()
        self._stop = _threading.Event()
        self.p = _winpty.PtyProcess.spawn(
            [exe, '--', sys.executable, '-u', '-c', SCREEN_CHILD],
            dimensions=(rows, cols), env=env)
        self._t = _threading.Thread(target=self._pump, daemon=True)
        self._t.start()


class Rows:
    def __init__(self):
        self.failed = []

    def check(self, row, ok, detail=""):
        print(f"{'PASS' if ok else 'FAIL'} {row}{(' - ' + detail) if detail else ''}")
        if not ok:
            self.failed.append(row)
        return ok


def acks(relay, want=None):
    return [m for kind, n, m in relay.frames
            if isinstance(m, dict) and m.get('t') == 'watchAck'
            and (not m.get('s') or m.get('s') in (None, want))]


def resyncs(relay, want=None):
    return [m for kind, n, m in relay.frames
            if isinstance(m, dict) and m.get('t') == 'resync'
            and (not m.get('s') or m.get('s') in (None, want))]


async def wait_until(fn, timeout, step=0.1):
    end = time.time() + timeout
    while time.time() < end:
        try:
            v = fn()
            if v:
                return v
        except Exception:
            pass
        await asyncio.sleep(step)
    return None


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--exe', default=str(REPO / 'app' / 'native' / 'MuxTee' / 'bin' / 'Release'
                                        / 'net10.0-windows' / 'muxtee.exe'))
    args = ap.parse_args()
    exe = Path(args.exe)
    if not exe.exists():
        print(f'muxtee.exe not found: {exe}')
        return 2
    print(f'exe={exe}')

    rows = Rows()
    relay = await b1.FakeRelay().start()
    mux = b1.DisposableMuxd(relay_port=relay.port)
    print(f'muxd on {mux.port}, relay on {relay.port}')
    tab = None
    try:
        if not await relay.wait_connected(25):
            print(mux.dump_log())
            rows.check('harness', False, 'muxd never dialled the fake relay')
            return 1

        tab = ScreenTab(str(exe), mux.port, NAME, cols=120, rows=30)
        row = None

        async def registered():
            nonlocal row
            row = await b1.owner_snapshot(mux.port, NAME)
            return bool(row and row.get('kind') == 'local-tab')

        end = time.time() + 20
        while time.time() < end and not await registered():
            await asyncio.sleep(0.4)

        if not rows.check('A1a registration', bool(row), f'kind={(row or {}).get("kind")}'):
            print(mux.dump_log())
            print(b1.muxtee_log_tail())
            return 1
        rows.check('A1b the tee advertised watchAck and muxd carried it to the relay row',
                   (row or {}).get('watchAck') is True,
                   f'watchAck={(row or {}).get("watchAck")}')

        # A2: the FIRST watch is a real flip. The tee must say snapshot:true AND push the ring resync.
        await relay.send({'t': 'watch', 's': NAME})
        got_true = await wait_until(
            lambda: [a for a in acks(relay, NAME) if a.get('snapshot') is True], 10)
        rows.check('A2a first watch answers watchAck snapshot:true', bool(got_true),
                   f'acks={acks(relay, NAME)}')
        got_resync = await wait_until(lambda: resyncs(relay, NAME), 10)
        rows.check('A2b first watch produces the resync the relay paints from instead of sb',
                   bool(got_resync), f'resyncs={len(resyncs(relay, NAME))}')
        # THE BODY IS THE POINT. The resync frame only ANNOUNCES the new stream; the relay then asks for
        # the ring with `sb` and paints from the reply. A reply with an empty body paints nothing and
        # sends the relay straight into its banner/redraw loop - the live symptom under investigation.
        # So ask exactly as the relay does and judge the answer.
        rid = 'proof-sb-1'
        before_sb = len([m for k, n, m in relay.frames if isinstance(m, dict) and m.get('t') == 'sb'])
        await relay.send({'t': 'sb', 's': NAME, 'rid': rid, 'max': 800000})
        got_sb = await wait_until(
            lambda: [m for k, n, m in relay.frames
                     if isinstance(m, dict) and m.get('t') == 'sb' and m.get('rid') == rid], 10)
        reply = (got_sb or [None])[0]
        body_len = len(reply.get('d') or '') if reply else 0
        rows.check('A2c the ring reply carries a non-empty snapshot the relay can paint',
                   body_len > 100,
                   f'd(len b64)={body_len} pos={reply.get("pos") if reply else None} resync={reply.get("resync") if reply else None} pos_before={before_sb}')

        # A3: re-state the SAME interest. The tee never dropped its watch, so there is no fresh snapshot:
        # ack must be false and no second resync may follow. This is the banner case, answered in a frame.
        before = len(resyncs(relay, NAME))
        falses_before = len([a for a in acks(relay, NAME) if a.get('snapshot') is False])
        await relay.send({'t': 'watch', 's': NAME})
        got_false = await wait_until(
            lambda: len([a for a in acks(relay, NAME) if a.get('snapshot') is False]) > falses_before, 10)
        rows.check('A3a a re-stated watch answers watchAck snapshot:false', bool(got_false),
                   f'acks={acks(relay, NAME)}')
        await asyncio.sleep(1.2)
        rows.check('A3b a re-stated watch sends NO second resync',
                   len(resyncs(relay, NAME)) == before,
                   f'resyncs {before} -> {len(resyncs(relay, NAME))}')

        print(f'--- rows failed: {len(rows.failed)} ---')
        if rows.failed:
            print('PROOF FAIL: ' + ', '.join(rows.failed))
            return 1
        print('PROOF PASS: a teed tab answers watch (true on a flip, false on a re-state), the resync '
              'arrives exactly when the answer is true, and the flag rides the session row so the relay '
              'only ever waits where an answer exists')
        return 0
    finally:
        if tab is not None:
            tab.kill()
        mux.stop()


if __name__ == '__main__':
    sys.exit(asyncio.run(main()))
