"""Grid ownership, muxd layer (GRID-OWNERSHIP-CONTRACT.md rows T3-T8).

A teed local tab has one committed geometry. The local pane holds it by default; a device may take it
with an epoch, and only the tee's sizeAck commits. These drive the state machine directly - no socket,
no PTY - so a row that fails is the state machine, not the harness.
"""
import unittest

import muxd


class FakeOut:
    def __init__(self):
        self.items = []

    def put_nowait(self, item):
        self.items.append(item)


def make_stream(name="tab", cols=120, rows=30):
    out = FakeOut()
    s = muxd.OwnerSession(name, "", None, cols, rows, None, out, None,
                          owner_key="k" * 24, stream=True)
    s.sent = []
    s._send_owner = lambda obj: s.sent.append(obj)   # no owner socket in a unit test
    s.frames = out
    return s


def commit(s, cols, rows, epoch, device="devA", label=""):
    assert muxd.owner_request_grid(s, cols, rows, epoch, device, label)
    assert muxd.owner_size_ack(s, {"epoch": epoch, "cols": cols, "rows": rows, "ok": True})


class EpochTest(unittest.TestCase):
    def test_t3_a_request_at_or_behind_the_current_epoch_never_moves_the_grid(self):
        s = make_stream()
        s.grid_epoch = 4
        self.assertFalse(muxd.owner_request_grid(s, 100, 40, 4, "devA"))
        self.assertFalse(muxd.owner_request_grid(s, 100, 40, 3, "devA"))
        self.assertIsNone(s.grid_pending, "a rejected request must not leave a pending resize")
        self.assertEqual((s.cols, s.rows), (120, 30))
        self.assertEqual(s.sent, [], "a rejected request sends nothing to the tee")
        self.assertTrue(muxd.owner_request_grid(s, 100, 40, 5, "devA"))

    def test_a_stale_request_is_reported_so_the_relay_can_retry(self):
        # The relay's pin record persists across its own restart; muxd's epoch has moved. Without the
        # report the relay never learns its request lost, and the pin looks like a dead button.
        s = make_stream()
        s.grid_epoch = 9
        self.assertFalse(muxd.owner_request_grid(s, 100, 40, 9, "devA"))
        muxd._grid_report(s, 9, False, "stale epoch")
        reports = [f for kind, _n, f in s.frames.items if kind == "gridAck"]
        self.assertEqual(len(reports), 1)
        self.assertIn('"epoch": 9', reports[0])
        self.assertIn('"ok": false', reports[0])

    def test_g4_a_late_ack_for_an_older_request_cannot_roll_the_grid_back(self):
        s = make_stream()
        commit(s, 100, 40, 2)
        self.assertFalse(muxd.owner_size_ack(s, {"epoch": 1, "cols": 200, "rows": 60, "ok": True}))
        self.assertEqual((s.cols, s.rows), (100, 40))
        self.assertEqual(s.grid_epoch, 2)


class AckTest(unittest.TestCase):
    def test_t4_only_an_ack_commits_and_an_unacked_resize_leaves_the_grid(self):
        s = make_stream()
        self.assertTrue(muxd.owner_request_grid(s, 100, 40, 1, "devA"))
        self.assertEqual((s.cols, s.rows), (120, 30), "the request alone must not move G")
        self.assertEqual(s.grid_owner, "", "a device owns the grid only once the tee has applied it")
        self.assertEqual(s.grid_epoch, 0, "the epoch advances on commit only, never on a request")
        self.assertEqual(s.sent[-1], {"t": "resize", "epoch": 1, "cols": 100, "rows": 40})

        self.assertFalse(muxd.owner_size_ack(s, {"epoch": 9, "cols": 100, "rows": 40, "ok": True}),
                         "an ack for a different request is ignored")
        self.assertEqual((s.cols, s.rows), (120, 30))

        self.assertTrue(muxd.owner_size_ack(s, {"epoch": 1, "cols": 100, "rows": 40, "ok": True}))
        self.assertEqual((s.cols, s.rows), (100, 40))
        self.assertEqual(s.grid_owner, "devA")
        self.assertEqual(s.grid_epoch, 1)

    def test_t4_a_refusal_leaves_the_grid_and_reports_failure(self):
        s = make_stream()
        commit(s, 100, 40, 1)
        self.assertTrue(muxd.owner_request_grid(s, 130, 50, 2, "devB"))
        self.assertFalse(muxd.owner_size_ack(s, {"epoch": 2, "cols": 130, "rows": 50, "ok": False}))
        self.assertEqual((s.cols, s.rows), (100, 40), "a refused resize leaves G unchanged")
        self.assertEqual(s.grid_owner, "devA", "and the previous owner keeps the grid")
        self.assertEqual(s.grid_epoch, 1, "a refused request never burns the epoch")
        reports = [f for kind, _name, f in s.frames.items if kind == "gridAck"]
        self.assertEqual(len(reports), 1)
        self.assertIn('"ok": false', reports[0])

    def test_t4_an_unanswered_request_times_out_without_moving_the_grid(self):
        s = make_stream()
        self.assertTrue(muxd.owner_request_grid(s, 100, 40, 1, "devA"))
        at = s.grid_pending["at"]
        self.assertTrue(muxd.owner_grid_tick(s, now=at + muxd.GRID_ACK_TIMEOUT_S + 1))
        self.assertIsNone(s.grid_pending)
        self.assertEqual((s.cols, s.rows), (120, 30))
        self.assertTrue(any(kind == "gridAck" for kind, _n, _f in s.frames.items))


class PersistTest(unittest.TestCase):
    def test_t5_the_committed_geometry_is_what_the_manifest_records(self):
        s = make_stream()
        commit(s, 132, 44, 1, "devA")
        record = muxd.session_records_payload({"tab": s})["tab"]
        self.assertEqual((record["cols"], record["rows"]), (132, 44))
        restored = {}
        muxd.restore_manifest_sessions({"tab": record}, target=restored, loop=None, outq=None)
        self.assertEqual((restored["tab"].cols, restored["tab"].rows), (132, 44),
                         "a restart restores the committed grid, not the construction size")

    def test_a_restart_returns_ownership_to_local_and_never_rewinds_the_epoch(self):
        # A pin is runtime state: the tee re-announces its pane at connect, which is a local statement.
        # The epoch, however, must survive: a relay carrying a pre-restart epoch must never win.
        s = make_stream()
        commit(s, 132, 44, 1, "devA")
        record = muxd.session_records_payload({"tab": s})["tab"]
        self.assertEqual(record["gridEpoch"], 1)
        restored = {}
        muxd.restore_manifest_sessions({"tab": record}, target=restored, loop=None, outq=None)
        self.assertEqual(getattr(restored["tab"], "grid_owner", ""), "")
        self.assertGreaterEqual(restored["tab"].grid_epoch, int(__import__("time").time()) - 1,
                                "a restore starts at or above wall time so old epochs lose")


class ReclaimTest(unittest.TestCase):
    def test_t6_local_activity_reclaims_a_pinned_grid(self):
        s = make_stream()
        s.pane_cols, s.pane_rows = 120, 30
        commit(s, 100, 40, 1, "devA")
        self.assertEqual(s.grid_owner, "devA")

        self.assertTrue(muxd.owner_reclaim_local(s, reason="activity"))
        # Until the tee has applied the pane geometry, the record must still say the device owns a
        # device-sized grid: publishing "local" over cols=100 would paint a remote grid as local.
        self.assertEqual(s.grid_owner, "devA")
        self.assertEqual((s.cols, s.rows), (100, 40))
        self.assertEqual(s.grid_epoch, 1, "a reclaim does not burn an epoch before the ack")
        # A release carries no geometry: only the tee can read the pane (contract section 4).
        self.assertEqual(s.sent[-1], {"t": "resize", "epoch": 2, "owner": "local"})

        self.assertTrue(muxd.owner_size_ack(s, {"epoch": 2, "cols": 120, "rows": 30, "ok": True}))
        self.assertEqual(s.grid_owner, "")
        self.assertEqual((s.cols, s.rows), (120, 30))
        self.assertEqual(s.grid_epoch, 2)

    def test_t6_the_restore_frame_is_local_and_carries_no_geometry(self):
        s = make_stream()
        s.pane_cols, s.pane_rows = 120, 30
        commit(s, 100, 40, 1, "devA")
        self.assertTrue(muxd.owner_reclaim_local(s, reason="release"))
        frame = s.sent[-1]
        self.assertEqual(frame, {"t": "resize", "epoch": 2, "owner": "local"},
                         "a release must not dictate the pane size; only the tee knows it")
        self.assertNotIn("cols", frame)
        self.assertNotIn("rows", frame)

    def test_t6_a_second_activity_before_the_ack_is_a_no_op(self):
        s = make_stream()
        s.pane_cols, s.pane_rows = 120, 30
        commit(s, 100, 40, 1, "devA")
        self.assertTrue(muxd.owner_reclaim_local(s, reason="activity"))
        sent = len(s.sent)
        self.assertFalse(muxd.owner_reclaim_local(s, reason="activity"),
                         "a reclaim already in flight is not re-requested")
        self.assertEqual(len(s.sent), sent)

    def test_t6_a_second_activity_on_a_local_grid_is_inert(self):
        s = make_stream()
        before = (s.cols, s.rows, s.grid_epoch)
        self.assertFalse(muxd.owner_reclaim_local(s, reason="activity"))
        self.assertEqual(s.sent, [], "no resize frame is emitted while the local pane already owns it")
        self.assertEqual((s.cols, s.rows, s.grid_epoch), before)

    def test_t8_the_pin_ttl_releases_to_local(self):
        s = make_stream()
        s.pane_cols, s.pane_rows = 120, 30
        commit(s, 100, 40, 1, "devA")
        at = s.grid_last_at
        self.assertFalse(muxd.owner_grid_tick(s, now=at + muxd.GRID_PIN_TTL_S - 1))
        self.assertEqual(s.grid_owner, "devA")
        self.assertTrue(muxd.owner_grid_tick(s, now=at + muxd.GRID_PIN_TTL_S + 1))
        self.assertTrue(s.grid_pending.get("local"), "a silent device starts a reclaim")
        self.assertEqual(s.grid_owner, "devA", "and the device still owns it until the tee applies")
        self.assertTrue(muxd.owner_size_ack(s, {"epoch": s.grid_pending["epoch"], "cols": 120, "rows": 30, "ok": True}))
        self.assertEqual(s.grid_owner, "")

    def test_t8_an_explicit_release_returns_to_local(self):
        s = make_stream()
        s.pane_cols, s.pane_rows = 120, 30
        commit(s, 100, 40, 1, "devA")
        self.assertTrue(muxd.owner_reclaim_local(s, reason="release"))
        self.assertEqual(s.grid_pending["local"], True)
        self.assertTrue(muxd.owner_size_ack(s, {"epoch": 2, "cols": 120, "rows": 30, "ok": True}))
        self.assertEqual(s.grid_owner, "")


class TwoDeviceTest(unittest.TestCase):
    def test_t7_last_pin_wins_and_the_first_device_becomes_stale(self):
        s = make_stream()
        commit(s, 100, 40, 1, "devA")
        commit(s, 130, 50, 2, "devB")
        self.assertEqual(s.grid_owner, "devB")
        self.assertFalse(muxd.owner_request_grid(s, 90, 20, 2, "devA"),
                         "devA's stale epoch is rejected")
        self.assertEqual((s.cols, s.rows), (130, 50))
        self.assertTrue(muxd.owner_request_grid(s, 90, 20, 3, "devA"))
        self.assertTrue(muxd.owner_size_ack(s, {"epoch": 3, "cols": 90, "rows": 20, "ok": True}))
        self.assertEqual(s.grid_owner, "devA")


class LocalDefaultTest(unittest.TestCase):
    def test_g3_a_local_tab_emits_no_resize_traffic_before_any_pin(self):
        s = make_stream()
        self.assertEqual(s.grid_owner, "")
        self.assertEqual(s.sent, [])
        self.assertEqual(s.grid_pending, None)
        payload = muxd.session_payload("tab", s)
        self.assertEqual(payload["gridOwner"], "local")
        self.assertEqual(payload["gridEpoch"], 0)

    def test_a_tee_size_frame_is_a_local_statement(self):
        s = make_stream()
        s.pane_cols, s.pane_rows = 120, 30
        commit(s, 100, 40, 1, "devA")
        self.assertTrue(muxd.owner_local_size(s, 140, 36))
        self.assertEqual(s.grid_owner, "")
        self.assertEqual((s.cols, s.rows), (140, 36), "the pane's size is already applied by the tee")
        self.assertEqual(s.grid_epoch, 2)
        self.assertFalse(muxd.owner_size_ack(s, {"epoch": 1, "cols": 100, "rows": 40, "ok": True}),
                         "a late ack for the old pin cannot roll the pane back")
        self.assertEqual((s.cols, s.rows), (140, 36))

    def test_a_redundant_size_announcement_is_not_a_commit(self):
        s = make_stream()
        frames = len(s.frames.items)
        self.assertFalse(muxd.owner_local_size(s, 120, 30))
        self.assertEqual(s.grid_epoch, 0, "an unchanged connect-time announcement must not bump the epoch")
        self.assertEqual(len(s.frames.items), frames, "nor wake the relay")
