"""Grid ownership acceptance: the muxd state machine of GRID-OWNERSHIP-CONTRACT.md (2026-11-09).

Base: 39d5efa (leaf17/p2-b-positional-cut, frozen). Implementation under test: 8379e13, the muxd
layer of leaf17/grid-ownership. LANDED-ON: 8379e1365cbe002d9705ee2072edde252a0cf8dd.

Rows drive the production API directly - no aliases, no test-only seams:
    owner_request_grid(owner, cols, rows, epoch, device, label)
    owner_size_ack(owner, message)
    owner_reclaim_local(owner, reason)
    owner_local_size(owner, cols, rows)
    owner_grid_tick(owner, now)

Contract rows:
    T3  a request whose epoch <= current is ignored; committed G does not move
    T4  muxd commits only after sizeAck; an unacked or failed resize leaves G unchanged
    T5  the committed geometry and epoch persist; a restart returns the owner to local
    T6  a local `activity` reclaims a device-owned grid
    T7  two devices: last pin wins; the first becomes stale and is rejected
    T8  owner release (disconnect / TTL) returns to local and the pane geometry

State model as implemented: `grid_owner` is "" while the local pane holds the grid and a device id
while pinned; `grid_epoch` starts at 0 and only moves on a commit; `grid_pending` is the in-flight
request. A local pane `size` is applied before it is reported (owner_local_size).

Settled names: gridOwner ("local"|"pin" on the list), gridOwnerLabel, gridEpoch. The legacy boolean
`owner` is the stream-owner flag and is never read as the grid owner here.
"""
import importlib
import json
import time
import unittest


muxd = importlib.import_module("muxd")

PANE = (209, 51)


class FakeOutq:
    def __init__(self):
        self.items = []

    def put_nowait(self, item):
        self.items.append(item)


class FakeGridSession:
    """Enough of OwnerSession for the grid state machine. `cols`/`rows` are committed, not requested."""

    def __init__(self, name="grid", cols=PANE[0], rows=PANE[1]):
        self.name = name
        self.cmd, self.cwd, self.heal = "", "", False
        self.created = time.time(); self.last_out = self.created
        self.local = set(); self.owner_connected = False
        self.cols, self.rows = int(cols), int(rows)
        self.pane_cols, self.pane_rows = tuple(PANE)
        self.grid_owner = ""
        self.grid_owner_label = ""
        self.grid_epoch = 0
        self.grid_pending = None
        self.grid_last_at = 0.0
        self.outq = FakeOutq()
        self.owner_frames = []
        # Legacy OwnerSession field: boolean `owner` means "a visible local terminal owns the agent",
        # NOT the grid owner.
        self.owner = True
        self.expected_owner = True
        # restore_manifest_sessions builds its own objects; these keep the record shape honest.
        self.session_id = name + "-id"
        self.session_uuid = name + "-uuid"
        self.aliases, self.ids = [], []
        self.stream = True
        self.adopted = False
        self.identity_pending = False
        self.lifecycle = "active"
        self.child_pid = 0
        self.child_start_token = ""
        self.stop_disposition = ""
        self.user_killed = False
        self.owner_key = ""
        self.generation_id = "gen"
        self.deaths = []

    def _send_owner(self, obj):
        self.owner_frames.append(dict(obj))

    def alive(self):
        return True

    def tail_text(self):
        return ""


def restore_frames(session):
    return [f for f in session.owner_frames if f.get("t") == "resize"]


def grid_owner(session):
    """The contract's owner for a session: 'pin' when a device holds it, else 'local'."""
    return "pin" if getattr(session, "grid_owner", "") else "local"


def committed(session):
    return (int(session.cols), int(session.rows))


def grid_reports(session):
    return [item for item in session.outq.items if item and item[0] == "gridAck"]


class GridOwnershipTests(unittest.TestCase):
    # --- T4: no commit before the ack -------------------------------------------------------
    def test_t4_a_request_asks_the_tee_but_does_not_commit(self):
        s = FakeGridSession()
        self.assertTrue(muxd.owner_request_grid(s, 100, 30, 5, "dev-a", "laptop"))
        self.assertEqual(restore_frames(s), [
            {"t": "resize", "epoch": 5, "cols": 100, "rows": 30},
        ], "the request reaches the tee with the requested epoch and size")
        self.assertEqual(committed(s), PANE, "but G must not move before sizeAck (G2)")
        self.assertEqual(grid_owner(s), "local", "and the owner stays local until the ack")
        self.assertEqual(s.grid_epoch, 0, "and the epoch is not burned by a request")

    def test_t4_ack_commits_the_acked_geometry_epoch_and_owner(self):
        s = FakeGridSession()
        muxd.owner_request_grid(s, 100, 30, 5, "dev-a", "laptop")
        self.assertTrue(muxd.owner_size_ack(s, {"epoch": 5, "cols": 100, "rows": 30, "ok": True}))
        self.assertEqual(committed(s), (100, 30), "the ack commits the applied size")
        self.assertEqual(s.grid_epoch, 5)
        self.assertEqual(grid_owner(s), "pin")
        self.assertEqual(s.grid_owner, "dev-a", "the pinning device holds the grid")
        self.assertEqual(s.grid_owner_label, "laptop")
        self.assertIsNone(s.grid_pending)

    def test_t4_failed_ack_leaves_g_epoch_and_owner_unchanged(self):
        s = FakeGridSession()
        muxd.owner_request_grid(s, 100, 30, 5, "dev-a", "laptop")
        self.assertFalse(muxd.owner_size_ack(s, {"epoch": 5, "cols": 100, "rows": 30, "ok": False}))
        self.assertEqual(committed(s), PANE, "ok:false leaves G unchanged (§4)")
        self.assertEqual(s.grid_epoch, 0, "and burns no epoch")
        self.assertEqual(grid_owner(s), "local")
        self.assertIsNone(s.grid_pending, "the refusal ends the request")
        reports = grid_reports(s)
        self.assertTrue(reports and json.loads(reports[-1][2])["ok"] is False,
                        "and the refusal is reported to the requesting viewer")

    def test_t4_unacked_request_times_out_without_moving_g(self):
        s = FakeGridSession()
        muxd.owner_request_grid(s, 100, 30, 5, "dev-a", "laptop")
        at = s.grid_pending["at"]
        self.assertTrue(muxd.owner_grid_tick(s, now=at + muxd.GRID_ACK_TIMEOUT_S + 1))
        self.assertEqual(committed(s), PANE, "a request the tee never applied changes nothing")
        self.assertEqual(s.grid_epoch, 0)
        self.assertIsNone(s.grid_pending)
        reports = grid_reports(s)
        self.assertTrue(reports and json.loads(reports[-1][2])["ok"] is False)

    # --- T3: epoch monotonicity -------------------------------------------------------------
    def test_t3_stale_request_is_ignored(self):
        s = FakeGridSession()
        muxd.owner_request_grid(s, 100, 30, 5, "dev-a", "laptop")
        muxd.owner_size_ack(s, {"epoch": 5, "cols": 100, "rows": 30, "ok": True})
        sent = len(restore_frames(s))
        self.assertFalse(muxd.owner_request_grid(s, 120, 36, 5, "dev-b", "phone"),
                         "a request at the committed epoch is refused (G4)")
        self.assertFalse(muxd.owner_request_grid(s, 120, 36, 4, "dev-b", "phone"))
        self.assertEqual(len(restore_frames(s)), sent, "and never reaches the tee")
        self.assertEqual(committed(s), (100, 30))

    def test_t3_a_request_behind_an_inflight_reservation_is_ignored(self):
        s = FakeGridSession()
        muxd.owner_request_grid(s, 100, 30, 6, "dev-a", "laptop")
        self.assertFalse(muxd.owner_request_grid(s, 120, 36, 5, "dev-b", "phone"),
                         "an in-flight epoch also supersedes (G4)")
        self.assertEqual(s.grid_pending["device"], "dev-a", "the reservation is unchanged")

    def test_t3_stale_ack_cannot_roll_the_geometry_back(self):
        s = FakeGridSession()
        muxd.owner_request_grid(s, 100, 30, 5, "dev-a", "laptop")
        muxd.owner_size_ack(s, {"epoch": 5, "cols": 100, "rows": 30, "ok": True})
        muxd.owner_request_grid(s, 110, 34, 6, "dev-a", "laptop")
        self.assertFalse(muxd.owner_size_ack(s, {"epoch": 5, "cols": 209, "rows": 51, "ok": True}),
                         "an ack for a superseded request is refused (G4)")
        self.assertEqual(committed(s), (100, 30), "and never rolls G back")
        self.assertEqual(s.grid_epoch, 5)

    # --- T5: persistence and restart --------------------------------------------------------
    def test_t5_geometry_and_epoch_persist_and_restore_returns_local(self):
        s = FakeGridSession()
        muxd.owner_request_grid(s, 100, 30, 5, "dev-a", "laptop")
        muxd.owner_size_ack(s, {"epoch": 5, "cols": 100, "rows": 30, "ok": True})

        record = muxd.session_records_payload({s.name: s})[s.name]
        self.assertEqual((record.get("cols"), record.get("rows")), (100, 30),
                         "the durable record carries the committed geometry")
        self.assertEqual(record.get("gridEpoch"), 5, "and the ordering token")
        self.assertNotIn("gridOwner", record,
                         "the durable record must not resurrect a device pin: authority is local after restart")

        relay_view = muxd.session_payload(s.name, s)
        self.assertEqual(relay_view["gridOwner"], "pin", "the live list names the owner")
        self.assertEqual(relay_view["gridOwnerLabel"], "laptop")
        self.assertEqual(relay_view["gridEpoch"], 5)

        restored = {}
        restored_names = muxd.restore_manifest_sessions(
            {s.name: record}, target=restored, loop=None, outq=FakeOutq())
        self.assertEqual(restored_names, [s.name])
        row = restored[s.name]
        self.assertEqual((row.cols, row.rows), (100, 30), "the geometry survives the restart")
        self.assertGreaterEqual(row.grid_epoch, int(time.time()) - 5,
                                "the restored epoch is ahead of any pre-restart epoch")
        self.assertEqual(row.grid_owner, "", "and a restart returns the grid to the local pane")

    # --- T6: local activity reclaims --------------------------------------------------------
    def test_t6_activity_restores_the_pane_and_commits_on_ack(self):
        s = FakeGridSession()
        muxd.owner_request_grid(s, 100, 30, 5, "dev-a", "laptop")
        muxd.owner_size_ack(s, {"epoch": 5, "cols": 100, "rows": 30, "ok": True})

        self.assertTrue(muxd.owner_reclaim_local(s, "activity"))
        self.assertEqual(committed(s), (100, 30), "nothing moves until the tee applies the restore")
        self.assertEqual(grid_owner(s), "pin", "the pin is still the committed owner mid-restore")
        self.assertTrue(s.grid_pending["local"])
        self.assertGreater(s.grid_pending["epoch"], 5)

        self.assertTrue(muxd.owner_size_ack(s, {
            "epoch": s.grid_pending["epoch"], "cols": PANE[0], "rows": PANE[1], "ok": True,
        }))
        self.assertEqual(grid_owner(s), "local", "the pane ack commits local ownership")
        self.assertEqual(committed(s), PANE, "and the pane geometry")

    def test_t6_a_second_reclaim_while_one_is_in_flight_is_a_no_op(self):
        s = FakeGridSession()
        muxd.owner_request_grid(s, 100, 30, 5, "dev-a", "laptop")
        muxd.owner_size_ack(s, {"epoch": 5, "cols": 100, "rows": 30, "ok": True})
        self.assertTrue(muxd.owner_reclaim_local(s, "activity"))
        sent = len(restore_frames(s))
        self.assertFalse(muxd.owner_reclaim_local(s, "activity"), "a typing burst is one reclaim")
        self.assertEqual(len(restore_frames(s)), sent)

    def test_t6_local_pane_size_commits_local_ownership_without_an_ack(self):
        s = FakeGridSession()
        muxd.owner_request_grid(s, 100, 30, 5, "dev-a", "laptop")
        muxd.owner_size_ack(s, {"epoch": 5, "cols": 100, "rows": 30, "ok": True})

        self.assertTrue(muxd.owner_local_size(s, 180, 45),
                        "the tee already applied a local pane resize, so it commits at once")
        self.assertEqual(grid_owner(s), "local")
        self.assertEqual(committed(s), (180, 45))
        self.assertEqual((s.pane_cols, s.pane_rows), (180, 45))
        self.assertGreater(s.grid_epoch, 5)

    def test_t6_a_redundant_local_size_is_not_a_commit(self):
        s = FakeGridSession()
        self.assertFalse(muxd.owner_local_size(s, PANE[0], PANE[1]),
                         "the connect-time announcement of an unchanged pane changes nothing")
        self.assertEqual(s.grid_epoch, 0)
        self.assertEqual(committed(s), PANE)

    # --- T7: last pin wins ------------------------------------------------------------------
    def test_t7_last_pin_wins_and_the_first_is_stale(self):
        s = FakeGridSession()
        muxd.owner_request_grid(s, 100, 30, 5, "dev-a", "laptop")
        muxd.owner_size_ack(s, {"epoch": 5, "cols": 100, "rows": 30, "ok": True})
        muxd.owner_request_grid(s, 110, 34, 6, "dev-b", "phone")
        muxd.owner_size_ack(s, {"epoch": 6, "cols": 110, "rows": 34, "ok": True})
        self.assertEqual(s.grid_owner, "dev-b", "last explicit pin wins (§3)")
        self.assertEqual(s.grid_owner_label, "phone")
        self.assertEqual(committed(s), (110, 34))

        sent = len(restore_frames(s))
        self.assertFalse(muxd.owner_request_grid(s, 100, 30, 6, "dev-a", "laptop"),
                         "the superseded pinner is stale and rejected (G4)")
        self.assertEqual(len(restore_frames(s)), sent)
        self.assertEqual(committed(s), (110, 34))

    # --- T8: release to local ---------------------------------------------------------------
    def test_t8_owner_disconnect_restores_the_pane(self):
        s = FakeGridSession()
        muxd.owner_request_grid(s, 100, 30, 5, "dev-a", "laptop")
        muxd.owner_size_ack(s, {"epoch": 5, "cols": 100, "rows": 30, "ok": True})

        self.assertTrue(muxd.owner_reclaim_local(s, "disconnect"))
        self.assertTrue(s.grid_pending["local"])
        self.assertEqual(committed(s), (100, 30), "G is unchanged until the tee acks the pane")
        muxd.owner_size_ack(s, {"epoch": s.grid_pending["epoch"], "cols": PANE[0], "rows": PANE[1], "ok": True})
        self.assertEqual(grid_owner(s), "local")
        self.assertEqual(committed(s), PANE)

    def test_t8_ttl_releases_a_silent_pin(self):
        s = FakeGridSession()
        muxd.owner_request_grid(s, 100, 30, 5, "dev-a", "laptop")
        muxd.owner_size_ack(s, {"epoch": 5, "cols": 100, "rows": 30, "ok": True})
        self.assertTrue(muxd.owner_grid_tick(s, now=s.grid_last_at + muxd.GRID_PIN_TTL_S + 1),
                        "a pin with no traffic for the TTL releases (G5)")
        self.assertTrue(s.grid_pending["local"])

    def test_t8_device_traffic_keeps_the_pin_alive(self):
        s = FakeGridSession()
        muxd.owner_request_grid(s, 100, 30, 5, "dev-a", "laptop")
        muxd.owner_size_ack(s, {"epoch": 5, "cols": 100, "rows": 30, "ok": True})
        before = s.grid_last_at
        time.sleep(0.01)
        muxd.owner_touch(s)
        self.assertGreater(s.grid_last_at, before, "activity from the owner renews the TTL")
        self.assertFalse(muxd.owner_grid_tick(s, now=s.grid_last_at + 1),
                         "so the pin does not expire while the device is talking")

    # --- contract §4 compliance: the restore frame ------------------------------------------
    def test_contract_restore_frame_is_local_without_geometry(self):
        """§4 ruling: release is {"t":"resize","epoch":E,"owner":"local"} with cols/rows OMITTED.

        Only the tee knows the pane; muxd must not invent a geometry. 8379e13 sends the stored
        pane_cols/pane_rows and omits `owner`, so this row is the compliance red for that gap.
        """
        s = FakeGridSession()
        muxd.owner_request_grid(s, 100, 30, 5, "dev-a", "laptop")
        muxd.owner_size_ack(s, {"epoch": 5, "cols": 100, "rows": 30, "ok": True})
        muxd.owner_reclaim_local(s, "disconnect")
        frame = restore_frames(s)[-1]
        self.assertEqual(frame.get("owner"), "local", "the restore frame names the local pane")
        self.assertNotIn("cols", frame, "and carries no geometry")
        self.assertNotIn("rows", frame)


if __name__ == "__main__":
    unittest.main()
