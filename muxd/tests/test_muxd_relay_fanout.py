"""One noisy session must not silently starve every other session's relay link.

The queue behind `outq` shards per session and drains them round-robin, with control frames
(`("dead", ...)`, `("resync", ...)`) on their own shard. These tests pin the four properties that
make a flood survivable: a loud tab cannot consume a quiet tab's capacity, a session-list refresh
cannot be lost or delayed behind terminal bytes, no session can monopolize the drain, and the
reconnect reset still empties everything.
"""

import asyncio
import importlib
import unittest


muxd = importlib.import_module("muxd")


class RelayFanoutTests(unittest.TestCase):
    def setUp(self):
        # Small bounds so saturation is cheap; semantics are identical at the 64/1MiB defaults.
        self.q = muxd.RelayFanout(budget=4096, maxsize=8)

    def test_fanout_is_the_relay_out_queue(self):
        self.assertIs(muxd.RelayFanout, muxd.RelayOutQueue)
        # The frame bound is a backstop behind the byte budget, not the primary policy.
        self.assertGreaterEqual(muxd.RELAY_SESSION_QUEUE_MAX, 4096)

    def test_loud_session_cannot_starve_a_quiet_one(self):
        for i in range(200):
            self.q.put_nowait(("o", "loud", b"flood-%d" % i))
        self.assertTrue(self.q.put_nowait(("o", "quiet", b"hello")))

        # The quiet session's single chunk is still retrievable, verbatim.
        drained = []
        while True:
            try:
                drained.append(self.q.get_nowait())
            except asyncio.QueueEmpty:
                break
        self.assertIn(("o", "quiet", b"hello"), drained)

        # And only the flooding session is billed for the loss.
        self.assertEqual(["loud"], sorted(self.q.per_session_dropped()))
        self.assertGreater(self.q.per_session_dropped()["loud"], 0)

    def test_dead_frame_survives_a_saturated_session_shard(self):
        for i in range(200):
            self.q.put_nowait(("o", "loud", b"flood-%d" % i))
        self.assertTrue(self.q.put_nowait(("dead", "loud", "")))

        drained = []
        while True:
            try:
                drained.append(self.q.get_nowait())
            except asyncio.QueueEmpty:
                break
        self.assertIn(("dead", "loud", ""), drained)
        # Control frames outrank terminal bytes, so the session-list refresh is not merely
        # present but ahead of the flood it was queued behind.
        self.assertLess(drained.index(("dead", "loud", "")),
                        max(i for i, item in enumerate(drained) if item[0] == "o"))

    def test_get_alternates_between_two_backed_up_sessions(self):
        for i in range(4):
            self.q.put_nowait(("o", "a", b"a-%d" % i))
            self.q.put_nowait(("o", "b", b"b-%d" % i))

        async def drain(n):
            return [await self.q.get() for _ in range(n)]

        names = [item[1] for item in asyncio.new_event_loop().run_until_complete(drain(6))]
        # Round-robin, not drain-one-then-the-other.
        self.assertEqual(["a", "b", "a", "b", "a", "b"], names)

    def test_reconnect_drain_empties_every_shard_and_zeroes_the_count(self):
        for i in range(200):
            self.q.put_nowait(("o", "loud", b"flood-%d" % i))
        self.q.put_nowait(("o", "quiet", b"hello"))
        self.q.put_nowait(("dead", "quiet", ""))
        self.assertGreater(self.q.dropped, 0)

        # Verbatim reconnect-reset pattern from the relay client.
        stale_frames = 0
        while True:
            try:
                self.q.get_nowait()
                stale_frames += 1
            except asyncio.QueueEmpty:
                break
        if stale_frames or self.q.dropped:
            self.q.dropped = 0

        self.assertGreater(stale_frames, 0)
        self.assertEqual(0, self.q.qsize())
        self.assertTrue(self.q.empty())
        self.assertEqual(0, self.q.dropped)
        self.assertEqual({}, self.q.per_session_dropped())
        with self.assertRaises(asyncio.QueueEmpty):
            self.q.get_nowait()

    def test_forget_drops_a_gone_sessions_shard(self):
        self.q.put_nowait(("o", "gone", b"x"))
        self.q.put_nowait(("o", "stays", b"y"))
        self.q.forget("gone")

        self.assertEqual(1, self.q.qsize())
        self.assertEqual(("o", "stays", b"y"), self.q.get_nowait())
        self.assertNotIn("gone", self.q.per_session_dropped())

    def test_frame_bound_holds_when_the_byte_budget_cannot(self):
        # A million 1-byte chunks fit inside any sane byte budget; the frame bound is what stops
        # the deque from growing without limit.
        for i in range(500):
            self.q.put_nowait(("o", "tiny", b"x"))
        self.assertLessEqual(self.q.qsize(), self.q.maxsize + 1)
        self.assertGreater(self.q.per_session_dropped().get("tiny", 0), 0)


if __name__ == "__main__":
    unittest.main()
