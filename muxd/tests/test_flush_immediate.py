"""Lever 2: the flush pump must ship a lone echo the moment it arrives, not after a fixed tick.

The old flush_out slept a flat 12ms every iteration and only then drained. On Windows a 12ms sleep
rounds up to the ~15.6ms system timer, so a single keystroke's echo waited out a whole tick — that was
most of the measured "local" latency (p50 15.5ms, of which the output half was 13.8ms).

These tests pin the SEMANTICS the new pump relies on, against a real asyncio loop and a stand-in
session, without needing a live muxd or a PTY:
  * a lone chunk is delivered in well under one old tick,
  * a burst arriving inside the window is still coalesced into few frames,
  * the wake is honoured from the reader THREAD (call_soon_threadsafe), which is the real caller.
"""
import asyncio
import sys
import time
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

# The pump's own constants, mirrored here so the test states the contract it is checking.
BURST_WINDOW = 0.004
OLD_TICK = 0.012


class FakeSession:
    """Just the surface flush_out touches: a pending buffer, a drain, a wake event, a name."""

    def __init__(self, name, loop):
        self.name = name
        self.loop = loop
        self.pending = bytearray()
        self.wake = None
        self.shipped = []

    def feed(self, data: bytes):
        self.pending += data
        self.wake_pump()

    def drain(self):
        if not self.pending:
            return None
        chunk = bytes(self.pending)
        self.pending = bytearray()
        return chunk

    def wake_pump(self):
        ev = self.wake
        if ev is None:
            return
        try:
            self.loop.call_soon_threadsafe(ev.set)
        except Exception:
            pass


async def run_pump(sessions, shipped, stop_after):
    """The new flush_out shape, verbatim in structure: write-driven, bounded wait, drain all."""
    wake = asyncio.Event()
    for s in sessions:
        s.wake = wake
    deadline = time.monotonic() + stop_after
    while time.monotonic() < deadline:
        try:
            await asyncio.wait_for(wake.wait(), BURST_WINDOW)
        except asyncio.TimeoutError:
            pass
        wake.clear()
        for s in sessions:
            if s.wake is None:
                s.wake = wake
            chunk = s.drain()
            if chunk:
                shipped.append((s.name, chunk, time.monotonic()))


def test_a_lone_echo_ships_far_under_the_old_tick():
    async def scenario():
        loop = asyncio.get_running_loop()
        s = FakeSession('solo', loop)
        shipped = []
        pump = asyncio.create_task(run_pump([s], shipped, 0.25))
        await asyncio.sleep(0.02)              # let the pump settle into its wait
        t0 = time.monotonic()
        s.feed(b'T123;')
        while not shipped and time.monotonic() - t0 < 0.2:
            await asyncio.sleep(0.0005)
        pump.cancel()
        return (shipped[0][2] - t0) if shipped else None

    latency = asyncio.run(scenario())
    assert latency is not None, 'a lone echo never shipped'
    assert latency < OLD_TICK, f'echo waited {latency*1000:.1f}ms — still paying the old tick'
    # The wake path, not the timeout path, must carry it: well under the burst window too.
    assert latency < BURST_WINDOW + 0.002, f'echo took {latency*1000:.1f}ms — the wake did not fire'


def test_a_burst_inside_the_window_still_coalesces():
    async def scenario():
        loop = asyncio.get_running_loop()
        s = FakeSession('burst', loop)
        shipped = []
        pump = asyncio.create_task(run_pump([s], shipped, 0.3))
        await asyncio.sleep(0.02)
        # Ten tiny writes inside one burst window: the first ships at once, the rest merge.
        for i in range(10):
            s.feed(b'x')
            await asyncio.sleep(0.0002)
        await asyncio.sleep(0.05)
        pump.cancel()
        return shipped

    shipped = asyncio.run(scenario())
    total = b''.join(c for _, c, _ in shipped)
    assert total == b'x' * 10, 'the burst lost or duplicated bytes'
    assert len(shipped) < 10, f'the burst was not coalesced at all: {len(shipped)} frames for 10 writes'


def test_the_wake_is_honoured_from_a_reader_thread():
    # The real caller is Session._reader, a plain threading.Thread. Prove call_soon_threadsafe wakes
    # the loop-thread pump — that is the whole mechanism this lever depends on.
    import threading

    async def scenario():
        loop = asyncio.get_running_loop()
        s = FakeSession('thread', loop)
        shipped = []
        pump = asyncio.create_task(run_pump([s], shipped, 0.3))
        await asyncio.sleep(0.02)

        def reader():
            s.feed(b'from-thread')

        t0 = time.monotonic()
        threading.Thread(target=reader, daemon=True).start()
        while not shipped and time.monotonic() - t0 < 0.2:
            await asyncio.sleep(0.0005)
        pump.cancel()
        return shipped, t0

    shipped, t0 = asyncio.run(scenario())
    assert shipped, 'a wake from the reader thread never reached the pump'
    assert shipped[0][1] == b'from-thread'
    assert shipped[0][2] - t0 < OLD_TICK, 'thread wake was slower than the old tick'


if __name__ == '__main__':
    raise SystemExit(pytest.main([__file__, '-q']))
