"""The ring-only strip of OSC 21338 (the gateway's terminal element map).

The map is a control channel: one base64url JSON frame, re-sent every 10 s with the same `gen` while
the screen is unchanged. The live path must keep it (the relay decodes it and hands browsers the map
as its own `E` frame), but history must not, or ~4.2 KB/10 s fills the 800 KB ring with half an hour
of base64 that nothing renders. These pin the split: `pending`/the live ws gets the chunk verbatim,
the ring gets the stripped text, with a bounded per-session carry so a chunk boundary inside an
introducer or body cannot print base64 into a replay. A stream with no frame must be byte-identical.
"""
import base64
import json
import os
import unittest

import muxd

VECTORS = os.path.join(os.path.dirname(__file__), "..", "..", "docs", "element-map-vectors.json")

MAP = {
    "v": 1, "gen": 7, "cols": 120, "rows": 30,
    "els": [
        {"id": "btn-%d" % i, "role": "button", "r": [i, i, 10, 2], "label": "Element %d" % i,
         "act": {"t": "ptr", "x": i, "y": i, "b": 0}}
        for i in range(40)
    ],
}
BODY = base64.urlsafe_b64encode(json.dumps(MAP).encode("utf-8")).decode("ascii").rstrip("=")
BODY_B = BODY.encode("ascii")
ESC_SEQ = b"\x1b]21338;" + BODY_B + b"\x1b\\"
C1_SEQ = b"\x9d21338;" + BODY_B + b"\x9c"
# A slice of the body that must never surface in a replay; mixed enough not to collide with screen text.
BODY_SLICE = BODY_B[200:280]


def make_stream(name="tab", outq=None):
    return muxd.OwnerSession(name, "", None, 120, 30, None,
                             outq if outq is not None else muxd.RelayFanout(), None,
                             owner_key="k" * 24, stream=True)


def make_owner(name="shell", outq=None):
    return muxd.OwnerSession(name, "", None, 120, 30, None,
                             outq if outq is not None else muxd.RelayFanout(), None,
                             owner_key="k" * 24, stream=False)


def make_hosted(name="hosted"):
    # A muxd-owned PTY session that is never spawned: the ring is the only thing under test.
    return muxd.Session(name, "", None, 120, 30, None, muxd.RelayFanout(), spawn_now=False)


class StripUnit(unittest.TestCase):
    """The stripper itself: both wire forms, both terminators, both aborts, and the carry rules."""

    def strip_all(self, *chunks):
        s = muxd.ElementMapStrip()
        return b"".join(s.strip(c) for c in chunks), s

    def test_the_escape_form_is_removed_and_text_is_kept(self):
        text, st = self.strip_all(b"before" + ESC_SEQ + b"after")
        self.assertEqual(text, b"beforeafter")
        self.assertEqual(st.carry, b"")

    def test_the_8_bit_form_is_removed_the_same_way(self):
        text, _ = self.strip_all(b"A" + C1_SEQ + b"B")
        self.assertEqual(text, b"AB")

    def test_a_bel_terminator_is_consumed(self):
        seq = b"\x1b]21338;" + BODY_B + b"\x07"
        self.assertEqual(self.strip_all(b"x" + seq + b"y")[0], b"xy")

    def test_a_can_or_sub_abort_is_consumed(self):
        for abort in (b"\x18", b"\x1a"):
            seq = b"\x1b]21338;" + BODY_B + abort
            self.assertEqual(self.strip_all(b"x" + seq + b"y")[0], b"xy", abort)

    def test_an_escape_abort_leaves_the_escape_as_text(self):
        # ESC followed by anything but backslash aborts the sequence and the ESC survives.
        self.assertEqual(self.strip_all(b"\x1b]21338;AC\x1b[1m")[0], b"\x1b[1m")

    def test_a_non_introducer_escape_is_text(self):
        payload = b"\x1b[?25h plain \x1b]0;title\x07"
        self.assertEqual(self.strip_all(payload)[0], payload)

    def test_a_chunk_boundary_in_the_introducer_is_held_not_printed(self):
        text, st = self.strip_all(b"a\x1b]21", b"338;" + BODY_B + b"\x1b\\b")
        self.assertEqual(text, b"ab", "the split introducer must not leak")
        self.assertEqual(st.carry, b"")

    def test_a_chunk_boundary_in_the_body_is_held_not_printed(self):
        text, st = self.strip_all(b"a\x1b]21338;" + BODY_B[:100], BODY_B[100:] + b"\x1b\\b")
        self.assertEqual(text, b"ab")
        self.assertEqual(st.carry, b"")

    def test_a_trailing_escape_that_could_still_be_a_terminator_is_held(self):
        s = muxd.ElementMapStrip()
        seq = b"\x1b]21338;" + BODY_B
        self.assertEqual(s.strip(seq + b"\x1b"), b"")
        self.assertEqual(s.carry, seq + b"\x1b", "the open sequence is held whole, not just its ESC")
        self.assertEqual(s.strip(b"\\tail"), b"tail")
        self.assertEqual(s.carry, b"")

    def test_a_stream_that_starts_mid_body_passes_through_as_text(self):
        # Contract 2a: the stripper was never inside a string, so orphaned body bytes are text.
        orphan = BODY_B[:60] + b" tail"
        self.assertEqual(self.strip_all(orphan)[0], orphan)
        self.assertEqual(self.strip_all(orphan)[1].dropped, 0)

    def test_a_resync_snapshot_tail_is_dropped_where_it_is_known_to_be_a_tail(self):
        # muxtee's ring evicts by byte, so a snapshot can begin inside a body. The stripper must keep a
        # mid-body stream as text (2a), so the snapshot path removes the tail before it is stripped.
        tail = BODY_B[:900] + b"\x1b\\"
        self.assertEqual(muxd.drop_leading_element_map_tail(tail + b"after"), b"after")
        self.assertEqual(muxd.drop_leading_element_map_tail(BODY_B[:900] + b"\x9c" + b"after"), b"after")
        self.assertEqual(muxd.drop_leading_element_map_tail(BODY_B[:900] + b"\x07" + b"after"), b"after")

    def test_a_snapshot_that_is_real_text_is_not_touched(self):
        for data in (b"", b"prompt$ ls\r\n", b"SCREEN-LINE-000001 " + b"." * 40, b"-" * 200 + b"\x07"):
            self.assertEqual(muxd.drop_leading_element_map_tail(data), data)

    def test_a_short_or_unterminated_leading_run_is_left_alone(self):
        # Too short to be a body, and a long run with no terminator behind it is indistinguishable from
        # text - guessing there would eat real output, so it is kept.
        self.assertEqual(muxd.drop_leading_element_map_tail(b"abc" + b"\x1b\\" + b"tail"), b"abc\x1b\\tail")
        self.assertEqual(muxd.drop_leading_element_map_tail(BODY_B[:900]), BODY_B[:900])

    def test_a_low_entropy_run_is_a_rule_not_a_body(self):
        # Terminal output does produce long runs of one glyph, and a snapshot may well begin in one.
        for rule in (b"-" * 200, b"=" * 200, b"A" * 300):
            self.assertEqual(muxd.drop_leading_element_map_tail(rule + b"\x07next"), rule + b"\x07next")

    def test_an_intact_frame_at_the_head_is_still_stripped_normally(self):
        # The tail remover must not claim a snapshot that starts with its own introducer.
        self.assertEqual(muxd.drop_leading_element_map_tail(ESC_SEQ + b"tail"), ESC_SEQ + b"tail")

    def test_carry_overflow_discards_and_resumes_at_the_next_introducer(self):
        # A runaway ESC-form body past the cap is abandoned. Its bytes are base64url text, so the only
        # introducer that can hide inside it is the 8-bit form - and the strict reading keeps the frame
        # behind it rather than discarding the rest of the chunk with the garbage.
        head = b"\x1b]21338;" + b"A" * 62000 + C1_SEQ[:-1]
        text, st = self.strip_all(head, b"\x9ctail")
        self.assertEqual(text, b"tail")
        self.assertEqual(st.carry, b"")
        self.assertEqual(st.dropped, 1)

    def test_carry_overflow_with_no_later_introducer_drops_everything(self):
        text, st = self.strip_all(b"keep\x1b]21338;" + b"A" * 70000)
        self.assertEqual(text, b"keep")
        self.assertEqual(st.carry, b"")
        self.assertEqual(st.dropped, 1)


class CanonicalVectors(unittest.TestCase):
    """The canonical strip vectors, driven through the ring stripper.

    The ring and the relay must agree on what survives a chunk: text_hex and carry_hex are the
    contract, and the ring side adopts them wholesale. `dropped` is not compared - the relay also
    counts undecodable bodies, which the ring never inspects because it drops every sequence.
    """

    def test_every_strip_vector_matches_on_text_and_carry(self):
        with open(VECTORS, encoding="utf-8") as handle:
            entries = json.load(handle)["strip"]["entries"]
        self.assertGreater(len(entries), 100)
        for entry in entries:
            state = muxd.ElementMapStrip()
            with self.subTest(entry["name"]):
                for chunk_hex, expect in zip(entry["chunks"], entry["expect"]):
                    text = state.strip(bytes.fromhex(chunk_hex))
                    self.assertEqual(text.hex(), expect["text_hex"])
                    self.assertEqual(state.carry.hex(), expect["carry_hex"])


class RingOnlySplit(unittest.TestCase):
    """The split the whole change rests on: one chunk in, two different outputs."""

    def test_a_stream_owner_keeps_the_osc_live_and_strips_the_ring(self):
        s = make_stream()
        payload = b"screen" + ESC_SEQ + b"tail"
        s.ingest(payload)
        self.assertEqual(s.drain(), payload, "the live ws path must forward the chunk unmodified")
        self.assertEqual(b"".join(s._ring_snapshot()), b"screentail", "history must not hold the frame")

    def test_the_replay_carries_neither_the_sequence_nor_a_base64_tail(self):
        s = make_stream()
        s.ingest(b"screen" + ESC_SEQ + b"tail")
        replay = s.scrollback()
        self.assertIn(b"screen", replay)
        self.assertNotIn(b"\x1b]21338;", replay)
        self.assertNotIn(BODY_SLICE, replay)

    def test_a_regular_owner_gets_the_same_split(self):
        s = make_owner()
        s.ingest(b"x" + C1_SEQ + b"y")
        self.assertIn(b"\x9d21338;", s.drain())
        self.assertEqual(b"".join(s._ring_snapshot()), b"xy")

    def test_a_hosted_session_is_byte_identical_with_no_frame_in_the_stream(self):
        s = make_hosted()
        payload = (b"PS C:\\Users\\me> git status\r\n"
                   b"\x1b]0;title\x07\x1b[?25h\x1b[1mbold\x1b[0m \x1b]10;?\x07 done\r\n")
        s._append_ring(payload)
        self.assertEqual(b"".join(s._ring_snapshot()), payload,
                         "a stream with no element-map frame must pass through unchanged")

    def test_a_regular_owner_is_byte_identical_with_no_frame_in_the_stream(self):
        s = make_owner()
        payload = b"plain \x1b[32mgreen\x1b[0m text \x07 \x9b no frame\r\n"
        s.ingest(payload)
        self.assertEqual(b"".join(s._ring_snapshot()), payload)

    def test_the_carry_survives_a_chunk_boundary_on_the_ring_path(self):
        s = make_stream()
        s.ingest(b"a\x1b]21338;" + BODY_B[:200])
        s.ingest(BODY_B[200:] + b"\x1b\\b")
        self.assertEqual(b"".join(s._ring_snapshot()), b"ab")
        self.assertIn(b"\x1b]21338;", s.drain(), "the live copy is whole across both ingests")

    def test_a_pure_frame_chunk_adds_nothing_to_history(self):
        s = make_stream()
        s.ingest(ESC_SEQ)
        self.assertEqual(b"".join(s._ring_snapshot()), b"")
        self.assertEqual(s.ring_len, 0)

    def test_a_resync_strips_its_ring_copy_and_starts_a_fresh_carry(self):
        s = make_stream()
        # Leave a fragment in the carry from the previous stream...
        s.ingest(b"\x1b]21338;orphaned-body")
        # ...then resync: that fragment must not be glued onto the new snapshot.
        s.replace_history(b"fresh" + ESC_SEQ + b"screen")
        self.assertEqual(b"".join(s._ring_snapshot()), b"freshscreen")
        self.assertEqual(s.emap_strip.carry, b"")

    def test_a_resync_that_starts_mid_body_does_not_replay_base64(self):
        # The live shape: muxtee's ring evicted into the middle of a 34 KB body, so the snapshot head is
        # body bytes with no introducer. Replayed, that is what a viewer paints on screen.
        s = make_stream()
        s.replace_history(BODY_B[:900] + b"\x1b\\" + b"prompt$ ")
        ring = b"".join(s._ring_snapshot())
        self.assertEqual(ring, b"prompt$ ")
        self.assertNotIn(BODY_SLICE[:40], ring)
        self.assertEqual(s.scrollback(), b"prompt$ ")


class RingEvictionAndReplay(unittest.IsolatedAsyncioTestCase):
    """The case the directive names: more than RING_CAP through the real ingest path, then evict+replay."""

    async def test_eviction_never_leaves_a_sequence_or_a_base64_tail_in_the_replay(self):
        s = make_stream()
        chunks = []
        total = 0
        for i in range(400):                      # ~2.9 KB of real text per chunk
            line = (b"SCREEN-LINE-%06d " % (i,)) + b"." * 40 + b"\r\n"
            chunks.append(line * 40)
            total += len(chunks[-1])
            if i % 4 == 3:                        # a map frame every fourth text chunk
                chunks.append(ESC_SEQ)
                total += len(ESC_SEQ)
        self.assertGreater(total, muxd.RING_CAP,
                           "the stream must exceed the ring so eviction and replay are exercised")

        for chunk in chunks:
            await muxd.handle_owner_binary(s, bytes((muxd.OWNER_FRAME_INGEST,)) + chunk)

        ring = b"".join(s._ring_snapshot())
        self.assertLessEqual(len(ring), muxd.RING_CAP)
        self.assertGreater(len(ring), muxd.RING_CAP - 12000, "the ring must be full, not truncated")
        for bad in (b"\x1b]21338;", b"\x9d21338;", BODY_SLICE):
            self.assertNotIn(bad, ring, "history holds stripped text only")

        replay = s.scrollback()
        self.assertGreater(len(replay), 0)
        for bad in (b"\x1b]21338;", b"\x9d21338;", BODY_SLICE):
            self.assertNotIn(bad, replay, "the replay must carry neither the OSC nor a base64 tail")
        self.assertIn(b"SCREEN-LINE", replay, "real screen text still replays")
        # The whole point of stripping: nearly the entire ring is the screen, not the map.
        self.assertGreater(ring.count(b"SCREEN-LINE"), 1000)

    async def test_a_hosted_session_with_no_frames_survives_eviction_byte_for_byte(self):
        s = make_hosted()
        chunk = b"hosted output line\r\n"
        for _ in range(60000):                    # 1.26 MB, so the oldest chunks must be evicted
            s._append_ring(chunk)
        self.assertTrue(all(part == chunk for part in s._ring_snapshot()),
                        "no chunk may be altered or merged on the no-frame path")
        ring = b"".join(s._ring_snapshot())
        held = (muxd.RING_CAP // len(chunk)) * len(chunk)
        self.assertEqual(len(ring), held, "eviction pops whole chunks and stops at the cap")
        self.assertEqual(ring, chunk * (held // len(chunk)))


if __name__ == "__main__":
    unittest.main()
