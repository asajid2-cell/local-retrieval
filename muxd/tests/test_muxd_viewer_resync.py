"""LocalViewerQueue: a dropped chunk for a local viewer must be an OBSERVABLE fact.

Before this, a slow local viewer's overflow was handled inline in fanout_local_output: pop the
oldest chunk, push the new one, call redraw_nudge. Nothing recorded that a gap had happened, and
redraw_nudge returns immediately unless the session holds the alternate screen (private modes
47/1047/1049) — so for an ordinary shell the viewer silently rendered a screen with a hole in it.

These tests pin the accounting the resync wiring is built on:
  * lossless offers never touch the counters,
  * an overflowing offer evicts exactly the OLDEST chunk and bills its length,
  * gap_seq counts gap EPISODES, not evictions — one contiguous burst is one gap,
  * gap_seq is strictly monotonic: it advances again only after an offer has succeeded,
  * one viewer's overflow never costs a healthy sibling viewer a byte.
"""
import asyncio
import importlib
import unittest

muxd = importlib.import_module("muxd")


def make_session():
    """A real muxd.Session with no spawned pty — same construction tests/test_local_scroll_forwarding.py uses."""
    return muxd.Session("t", "", None, 120, 30, None, None, spawn_now=False)


def drain(q, n):
    for _ in range(n):
        q.get_nowait()


class LocalViewerQueueAccounting(unittest.TestCase):
    def test_fresh_queue_accepts_maxsize_chunks_losslessly(self):
        q = muxd.LocalViewerQueue()
        self.assertEqual(q.maxsize, muxd.LOCAL_VIEWER_QUEUE_MAX)
        for i in range(q.maxsize):
            self.assertTrue(q.offer(b"chunk-%d" % i), "offer %d should be lossless" % i)
        self.assertEqual(q.qsize(), q.maxsize)
        self.assertEqual(q.gap_seq, 0)
        self.assertEqual(q.dropped_chunks, 0)
        self.assertEqual(q.dropped_bytes, 0)

    def test_overflow_evicts_oldest_and_opens_one_gap(self):
        q = muxd.LocalViewerQueue(maxsize=4)
        oldest = b"oldest-chunk"
        q.offer(oldest)
        for i in range(3):
            q.offer(b"filler-%d" % i)
        self.assertTrue(q.full())

        self.assertFalse(q.offer(b"newest"), "an overflowing offer must report the gap")
        self.assertEqual(q.qsize(), 4, "the queue stays exactly at maxsize")
        self.assertEqual(q.dropped_chunks, 1)
        self.assertEqual(q.dropped_bytes, len(oldest))
        self.assertEqual(q.gap_seq, 1)

        # The OLDEST chunk is what went; the newest is what survived.
        self.assertEqual(q.get_nowait(), b"filler-0")
        drain(q, 2)
        self.assertEqual(q.get_nowait(), b"newest")

    def test_contiguous_burst_of_drops_is_one_gap_episode(self):
        q = muxd.LocalViewerQueue(maxsize=4)
        for i in range(4):
            q.offer(b"a-%d" % i)
        self.assertFalse(q.offer(b"first-drop"))
        self.assertEqual(q.gap_seq, 1)

        for i in range(5):
            self.assertFalse(q.offer(b"burst-%d" % i))
        self.assertEqual(q.gap_seq, 1, "fifty drops in a row are ONE hole in the stream, not fifty")
        self.assertEqual(q.dropped_chunks, 6)
        self.assertEqual(q.qsize(), 4)

    def test_gap_seq_advances_again_only_after_a_lossless_offer(self):
        q = muxd.LocalViewerQueue(maxsize=4)
        for i in range(4):
            q.offer(b"a-%d" % i)
        self.assertFalse(q.offer(b"drop-1"))
        self.assertEqual(q.gap_seq, 1)

        # The viewer catches up: draining below maxsize lets the next offer succeed, closing the gap.
        drain(q, 2)
        self.assertTrue(q.offer(b"caught-up"))
        self.assertEqual(q.gap_seq, 1, "a successful offer closes the episode without inventing a new one")

        self.assertTrue(q.offer(b"still-fine"))
        self.assertFalse(q.offer(b"drop-2"), "queue is full again")
        self.assertEqual(q.gap_seq, 2, "a NEW burst after recovery is a new gap episode")
        self.assertEqual(q.dropped_chunks, 2)

    def test_gap_seq_never_decreases_across_the_queue_life(self):
        q = muxd.LocalViewerQueue(maxsize=2)
        seen = [q.gap_seq]
        for i in range(40):
            q.offer(b"x-%d" % i)
            if i % 7 == 0:
                try:
                    q.get_nowait()
                except Exception:
                    pass
            seen.append(q.gap_seq)
        for prev, cur in zip(seen, seen[1:]):
            self.assertGreaterEqual(cur, prev, "gap_seq must be monotonic: %r" % (seen,))
        self.assertGreater(q.gap_seq, 0)


class FanoutLocalOutput(unittest.TestCase):
    def test_a_full_viewer_never_costs_a_healthy_sibling_a_byte(self):
        session = make_session()
        slow = muxd.LocalViewerQueue(maxsize=2)
        fast = muxd.LocalViewerQueue(maxsize=2)
        for i in range(2):
            slow.offer(b"stale-%d" % i)          # slow viewer is already full
        session.local.add(slow)
        session.local.add(fast)

        muxd.fanout_local_output(session, b"live")

        self.assertEqual(fast.qsize(), 1)
        self.assertEqual(fast.get_nowait(), b"live")
        self.assertEqual(fast.dropped_chunks, 0)
        self.assertEqual(fast.dropped_bytes, 0)
        self.assertEqual(fast.gap_seq, 0, "the healthy viewer is lossless")

        self.assertEqual(slow.dropped_chunks, 1)
        self.assertEqual(slow.dropped_bytes, len(b"stale-0"))
        self.assertEqual(slow.gap_seq, 1)
        self.assertEqual(slow.qsize(), 2)

    def test_visible_owner_requests_snapshot_after_local_viewer_gap(self):
        calls = []
        session = muxd.OwnerSession("t", "", None, 120, 30, None, None, None)
        session._send_owner = lambda frame: calls.append(frame)
        slow = muxd.LocalViewerQueue(maxsize=1)
        slow.offer(b"stale")
        session.local.add(slow)

        muxd.fanout_local_output(session, b"live")
        self.assertEqual(calls, [{"t": "redraw"}])
        muxd.fanout_local_output(session, b"next")
        self.assertEqual(calls, [{"t": "redraw"}], "a contiguous gap must not flood the owner")
        slow.get_nowait()
        muxd.fanout_local_output(session, b"caught-up")
        muxd.fanout_local_output(session, b"new-gap")
        self.assertEqual(calls, [{"t": "redraw"}, {"t": "redraw"}])

    def test_fanout_still_nudges_a_redraw_on_a_dropped_chunk(self):
        session = make_session()
        slow = muxd.LocalViewerQueue(maxsize=1)
        slow.offer(b"stale")
        session.local.add(slow)

        calls = []
        original = muxd.redraw_nudge
        muxd.redraw_nudge = lambda s: calls.append(s)
        try:
            muxd.fanout_local_output(session, b"live")
            self.assertEqual(calls, [session], "a gap must still request a repaint")
            calls.clear()
            slow.get_nowait()
            muxd.fanout_local_output(session, b"lossless")
            self.assertEqual(calls, [], "a lossless fanout must not nudge")
        finally:
            muxd.redraw_nudge = original


class OwnerViewerDrain(unittest.IsolatedAsyncioTestCase):
    async def test_blocked_sender_retries_snapshot_after_its_backlog_drains(self):
        sending = asyncio.Event()
        release = asyncio.Event()

        class Socket:
            def __init__(self):
                self.sent = []

            async def send(self, data):
                if not self.sent:
                    sending.set()
                    await release.wait()
                self.sent.append(data)

        calls = []
        session = muxd.OwnerSession("t", "", None, 120, 30, None, None, None)
        session._send_owner = lambda frame: calls.append(frame)
        viewer = muxd.LocalViewerQueue(maxsize=2)
        session.local.add(viewer)
        viewer.offer(b"initial")
        socket = Socket()
        pump = asyncio.create_task(muxd.pump_local_viewer(socket, session, viewer))
        try:
            await asyncio.wait_for(sending.wait(), 2)
            muxd.fanout_local_output(session, b"stale")
            muxd.fanout_local_output(session, b"snapshot-that-will-be-evicted")
            muxd.fanout_local_output(session, b"newer")
            muxd.fanout_local_output(session, b"newest")
            self.assertEqual(calls, [{"t": "redraw"}])
            release.set()
            for _ in range(20):
                if len(socket.sent) == 3:
                    break
                await asyncio.sleep(0.01)
            self.assertEqual(socket.sent, [b"initial", b"newer", b"newest"])
            self.assertEqual(calls, [{"t": "redraw"}, {"t": "redraw"}])
        finally:
            release.set()
            pump.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await pump

    async def test_evicted_repaint_is_requested_again_after_viewer_catches_up(self):
        class Socket:
            def __init__(self):
                self.sent = []

            async def send(self, data):
                self.sent.append(data)

        calls = []
        session = muxd.OwnerSession("t", "", None, 120, 30, None, None, None)
        session._send_owner = lambda frame: calls.append(frame)
        viewer = muxd.LocalViewerQueue(maxsize=2)
        session.local.add(viewer)
        viewer.offer(b"stale-0")
        viewer.offer(b"stale-1")

        muxd.fanout_local_output(session, b"live")
        self.assertEqual(calls, [{"t": "redraw"}])
        muxd.fanout_local_output(session, b"repaint-that-will-be-evicted")
        muxd.fanout_local_output(session, b"more-output")
        muxd.fanout_local_output(session, b"latest-output")
        self.assertEqual(viewer.gap_seq, 1)
        self.assertEqual(calls, [{"t": "redraw"}])

        socket = Socket()
        pump = asyncio.create_task(muxd.pump_local_viewer(socket, session, viewer))
        try:
            for _ in range(20):
                if len(socket.sent) == 2:
                    break
                await asyncio.sleep(0.01)
            self.assertEqual(socket.sent, [b"more-output", b"latest-output"])
            self.assertEqual(calls, [{"t": "redraw"}, {"t": "redraw"}])
            muxd.fanout_local_output(session, b"recovered-snapshot")
            for _ in range(20):
                if len(socket.sent) == 3:
                    break
                await asyncio.sleep(0.01)
            self.assertEqual(socket.sent[-1], b"recovered-snapshot")
            self.assertEqual(calls, [{"t": "redraw"}, {"t": "redraw"}])
        finally:
            pump.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await pump


if __name__ == "__main__":
    unittest.main()
