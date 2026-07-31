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


if __name__ == "__main__":
    unittest.main()
