"""P2-B positional replay: the forwarded-raw coordinate and the ground-state cut, at the source.

The relay cuts a replay on `pos`/`start` in the coordinate of the bytes muxd actually forwarded.
That only works if muxd's own accounting is exact, so these pin it directly against a ring, with no
relay and no PTY:

  * `stream_bytes` advances with the bytes handed to the forward queue, and every ring chunk records
    the raw span it was taken from (the sum invariant a straddling cut depends on);
  * a dropped forward-queue prefix is a hole the relay can never receive, so it marks the stream
    dirty and `scrollback_view` refuses a position (`pos: null`, distinct from an old muxd that omits
    the key);
  * a cut is only ever reported on a ground-state boundary, snapped back over an open escape
    sequence, and never inside a multi-byte UTF-8 character - the cut splits RAW bytes, so a
    mid-character boundary would hand the relay a broken sequence;
  * `start` is the first boundary at or after the oldest chunk in the reply, which is what makes
    "bytes below start are unavailable" true;
  * parked owner bytes mark the stream dirty on the next watch (they reached the ring, never the
    relay), and `replace_history` re-bases the coordinate and serves the truth snapshot exactly once.
"""
import unittest

import muxd


def make_stream(name="tab"):
    return muxd.OwnerSession(name, "", None, 120, 30, None, muxd.RelayFanout(), None,
                             owner_key="k" * 24, stream=True)


def make_owner(name="shell"):
    return muxd.OwnerSession(name, "", None, 120, 30, None, muxd.RelayFanout(), None,
                             owner_key="k" * 24, stream=False)


def make_hosted(name="hosted"):
    # A muxd-owned PTY session that is never spawned: the ring is the only thing under test.
    return muxd.Session(name, "", None, 120, 30, None, muxd.RelayFanout(), spawn_now=False)


class ForwardedCoordinateTest(unittest.TestCase):
    def test_the_counter_follows_the_forward_queue_and_spans_stay_contiguous(self):
        s = make_owner()
        s.ingest(b"abc")
        s.ingest(b"de")
        self.assertEqual(s.stream_bytes, 5)
        self.assertEqual([m[:2] for m in s.ring_meta], [(0, 3), (3, 5)])
        self.assertEqual(sum(len(c) for c in s.ring), 5)
        self.assertFalse(s.stream_dirty)
        self.assertEqual(s.drain(), b"abcde", "the same bytes the relay receives leave through the queue")

    def test_a_hosted_session_keeps_the_same_accounting(self):
        s = make_hosted()
        s._append_ring(b"hello")
        s._append_ring(b"world")
        self.assertEqual(s.stream_bytes, 10)
        self.assertEqual([m[:2] for m in s.ring_meta], [(0, 5), (5, 10)])
        view = s.scrollback_view()
        self.assertEqual((view["pos"], view["start"]), (10, 0))

    def test_a_dropped_queue_prefix_marks_the_stream_dirty(self):
        s = make_owner()
        s.ingest(b"a" * 260_000)
        s.ingest(b"b" * 260_000)          # 520_000 > the 512_000 forward cap
        self.assertTrue(s.stream_dirty)
        view = s.scrollback_view()
        self.assertIsNone(view["pos"], "a hole in the forwarded stream has no position")
        self.assertIsNone(view["start"])
        self.assertFalse(view["resync"])

    def test_the_payload_exposes_the_base_for_the_relay_anchor(self):
        s = make_owner()
        s.ingest(b"abcdef")
        self.assertEqual(muxd.session_payload("x", s)["streamBytes"], 6)


class GroundBoundaryTest(unittest.TestCase):
    def pos(self, s):
        return s.scrollback_view()["pos"]

    def test_a_clean_tail_is_the_tail_offset(self):
        s = make_owner()
        data = b"abc\x1b[1mXY"
        s.ingest(data)
        self.assertEqual(self.pos(s), len(data))

    def test_an_open_escape_snaps_the_cut_back_to_the_last_ground(self):
        s = make_owner()
        s.ingest(b"abc\x1b[")
        self.assertEqual(self.pos(s), 3, "a cut inside a CSI sequence is not a boundary")
        s.ingest(b"1mZ")
        self.assertEqual(self.pos(s), 8, "the cut returns to ground once the sequence closes")

    def test_a_utf8_continuation_byte_is_data_not_an_introducer(self):
        s = make_owner()
        s.ingest(b"A\xc2\x9bB")           # U+009B: a C1 code point, but a UTF-8 continuation here
        self.assertEqual(self.pos(s), 4, "0x9B as a continuation must not open a sequence")

    def test_c1_introducers_are_honoured_when_standalone(self):
        s = make_owner()
        s.ingest(b"A\x9b1mB")             # a raw 8-bit CSI introducer is a real control
        self.assertEqual(self.pos(s), 5)
        osc = make_owner()
        osc.ingest(b"A\x9dtitle\x9cB")    # a raw 8-bit OSC introducer, closed by 8-bit ST
        self.assertEqual(self.pos(osc), len(b"A\x9dtitle\x9cB"))

    def test_a_cut_never_falls_inside_a_split_multi_byte_character(self):
        s = make_owner()
        s.ingest(b"\xc3")                 # first half of "é": no boundary at 1
        s.ingest(b"\xa9Z")
        self.assertEqual(self.pos(s), 3)
        meta = list(s.ring_meta)
        self.assertNotIn(1, [meta[0][3], meta[1][3]], "a mid-character offset was reported as ground")

    def test_start_is_the_first_boundary_at_or_after_the_oldest_retained_chunk(self):
        s = make_hosted()
        # The chunk that OPENED the CSI (and ended inside it) is trimmed away; the oldest chunk the
        # reply still carries begins mid-sequence, so its exact start is not a ground boundary.
        s._append_ring(b"z" * muxd.RING_CAP + b"\x1b[")
        s._append_ring(b"31mXY")
        view = s.scrollback_view()
        self.assertEqual(view["pos"], muxd.RING_CAP + 7)
        self.assertEqual(view["start"], muxd.RING_CAP + 5,
                         "bytes below the first ground of the oldest retained chunk are unavailable")
        self.assertLess(view["start"], view["pos"])

    def test_start_falls_back_to_pos_when_the_oldest_span_has_no_exact_start(self):
        s = make_hosted()
        big = b"z" * (muxd.RING_CAP + 10)  # one oversized chunk is trimmed to its tail
        s._append_ring(big)
        view = s.scrollback_view()
        self.assertEqual(view["pos"], len(big))
        self.assertEqual(view["start"], view["pos"],
                         "the stripped tail has no exact raw start, so nothing below pos is offered")

    def test_the_ring_strip_does_not_move_the_raw_position(self):
        osc = b"\x1b]21338;" + b"QUJD" + b"\x1b\\"
        s = make_owner()
        s.ingest(b"A" + osc + b"B")
        view = s.scrollback_view()
        self.assertNotIn(b"21338", view["d"], "history must not keep the element map")
        self.assertEqual(view["d"], b"AB")
        self.assertEqual(view["pos"], 1 + len(osc) + 1,
                         "the position counts forwarded raw bytes, including the stripped sequence")


class DirtyAndResyncTest(unittest.TestCase):
    def test_parked_owner_bytes_mark_the_stream_dirty_on_the_next_watch(self):
        s = make_stream()
        self.assertFalse(s.watched)
        s.ingest(b"abc")                  # reaches the ring, never the relay
        s.note_parked(3)
        self.assertFalse(s.stream_dirty, "parking alone is not yet a hole in the forwarded stream")
        s.set_watched(True)
        self.assertTrue(s.stream_dirty)
        self.assertIsNone(s.scrollback_view()["pos"])

    def test_replace_history_rebases_the_coordinate_and_carries_the_truth_once(self):
        s = make_owner()
        s.ingest(b"OLD-STREAM")
        self.assertEqual(s.stream_bytes, 10)
        s.replace_history(b"HISTORY")

        self.assertEqual(s.stream_bytes, 0, "the new base is zero")
        self.assertFalse(s.stream_dirty)
        truth = s.scrollback_view()
        self.assertTrue(truth["resync"])
        self.assertEqual((truth["pos"], truth["start"]), (0, 0))
        self.assertIn(b"HISTORY", truth["d"])

        self.assertFalse(s.scrollback_view()["resync"],
                         "the truth snapshot is served once, not on every reply")

        s.ingest(b"NEW")
        view = s.scrollback_view()
        self.assertEqual(view["pos"], 3, "post-resync bytes count from the new base")
        self.assertEqual(s.drain(), b"NEW", "the forward queue re-bases with it")


if __name__ == "__main__":
    unittest.main()
