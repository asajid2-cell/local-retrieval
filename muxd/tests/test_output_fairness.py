"""r.1.15 — one flooding session must not starve, gap, or resync any other session.

Before this leaf, relay egress was ONE shared asyncio.Queue (maxsize 64) with silent tail-drop:
`yes` in tab A filled every slot each 12ms flush tick, so tab B's output was discarded with
nothing but a counter to show for it. These tests pin the replacement — per-session byte-bounded
queues drained round-robin, with an explicit `resync` frame instead of silence on overflow.
"""

import asyncio
import importlib
import unittest


muxd = importlib.import_module("muxd")

CHUNK = b"x" * 131072          # 128KiB — 8 of these overrun the 1MiB per-session budget


def drain_all(q):
    """Pop everything currently queued, in egress order."""
    out = []
    while True:
        try:
            out.append(q.get_nowait())
        except asyncio.QueueEmpty:
            return out


class OutputFairnessTest(unittest.TestCase):
    def test_flood_never_gaps_or_reorders_a_quiet_session(self):
        """A floods 10MiB while B emits 100 numbered lines: all 100 egress, in order, intact."""
        q = muxd.RelayOutQueue()
        egress = []
        # Lockstep: each tick the producer emits one A chunk + one B line, the consumer drains a
        # single item. The consumer is 2x slower than the producer, so backlog growth is forced.
        for i in range(80):                       # 80 * 128KiB = 10MiB of flood from A
            q.put_nowait(("o", "A", CHUNK))
            if i < 100:
                q.put_nowait(("o", "B", b"line %d\n" % i))
            item = q.get_nowait()
            egress.append(item)
        for i in range(80, 100):                  # B keeps talking after the flood stops
            q.put_nowait(("o", "B", b"line %d\n" % i))
        egress.extend(drain_all(q))

        b_lines = [item[2] for item in egress if item[1] == "B" and item[0] == "o"]
        self.assertEqual(len(b_lines), 100, "a flood in A dropped output belonging to B")
        self.assertEqual(b_lines, [b"line %d\n" % i for i in range(100)],
                         "B's frames egressed out of order or were corrupted")
        self.assertEqual([item for item in egress if item[0] == "resync" and item[1] == "B"], [],
                         "B was told to resync for a flood it did not cause")

    def test_overflow_emits_exactly_one_resync_for_the_offender(self):
        """Sustained overflow yields ONE pending resync for A and none for B."""
        q = muxd.RelayOutQueue()
        for _ in range(200):                      # ~25MiB, wildly past A's 1MiB budget
            q.put_nowait(("o", "A", CHUNK))
            q.put_nowait(("o", "B", b"quiet\n"))
        egress = drain_all(q)

        resyncs = [item for item in egress if item[0] == "resync"]
        self.assertEqual(len(resyncs), 1, "a sustained flood must not spam resync frames")
        self.assertEqual(resyncs[0][1], "A", "resync named the wrong session")
        self.assertGreater(q.dropped, 0, "overflow did not record dropped output")
        self.assertEqual(len([i for i in egress if i[1] == "B" and i[0] == "o"]), 200,
                         "B lost output to A's overflow")

    def test_resync_rearms_once_the_previous_one_has_egressed(self):
        """Suppression lasts only while a resync is still queued — not forever."""
        q = muxd.RelayOutQueue()
        for _ in range(20):
            q.put_nowait(("o", "A", CHUNK))
        self.assertEqual(q.resyncs, 1)
        drain_all(q)                              # the pending resync egresses
        for _ in range(20):
            q.put_nowait(("o", "A", CHUNK))
        self.assertEqual(q.resyncs, 2, "a later overflow must be able to raise a new resync")

    def test_overflow_drops_only_whole_frames_and_never_a_lifecycle_frame(self):
        """Drops happen at coalesced-frame boundaries, so no half escape sequence can egress."""
        q = muxd.RelayOutQueue()
        chunks = [b"A" * 200000 + (b"%d" % i) for i in range(12)]
        q.put_nowait(("dead", "A", ""))           # must survive: a lost 'dead' leaves a zombie tab
        for chunk in chunks:
            q.put_nowait(("o", "A", chunk))
        egress = drain_all(q)

        self.assertIn(("dead", "A", ""), egress, "overflow discarded a session-lifecycle frame")
        for kind, _name, data in egress:
            if kind == "o":
                self.assertIn(data, chunks, "an egressed frame was a fragment of a producer chunk")

    def test_drain_is_round_robin_across_sessions(self):
        """No session can be served twice while another has output waiting."""
        q = muxd.RelayOutQueue()
        for i in range(5):
            for name in ("A", "B", "C"):
                q.put_nowait(("o", name, b"%s%d" % (name.encode(), i)))
        order = [item[1] for item in drain_all(q)]

        self.assertEqual(len(order), 15)
        self.assertEqual(order, ["A", "B", "C"] * 5, f"egress was not round-robin: {order}")

    def test_get_wakes_a_blocked_consumer(self):
        """pump_out awaits get(); a later put must wake it rather than hang the link."""
        async def scenario():
            q = muxd.RelayOutQueue()
            waiter = asyncio.ensure_future(q.get())
            await asyncio.sleep(0)
            self.assertFalse(waiter.done(), "get() returned before anything was queued")
            q.put_nowait(("o", "A", b"hello"))
            return await asyncio.wait_for(waiter, 2)

        self.assertEqual(asyncio.run(scenario()), ("o", "A", b"hello"))


if __name__ == "__main__":
    unittest.main()
