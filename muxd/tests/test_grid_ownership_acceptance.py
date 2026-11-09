"""Grid ownership acceptance: the muxd state machine of GRID-OWNERSHIP-CONTRACT.md (2026-11-09).

Base: 39d5efa (leaf17/p2-b-positional-cut, frozen).
LANDED-ON: implementation head sha recorded when #17's grid-ownership commits land.

Rows are the contract's pytest rows:
    T3  a request whose epoch <= current is ignored; committed G does not move
    T4  muxd commits only after sizeAck; an unacked or failed resize leaves G unchanged
    T5  the committed geometry (gridOwner, gridEpoch, G) persists in the session record
    T6  a local `activity` reclaims a device-owned grid
    T7  two devices: last pin wins; the first becomes stale and is rejected
    T8  owner release (disconnect / TTL) returns to local and the pane geometry

Settled names (contract §4 ruling, 2026-11-09): `gridOwner` ("local"|"pin"), `gridEpoch`,
`gridOwnerLabel`. The legacy boolean `owner` is the stream-owner flag and is never read as the grid
owner here. Release is the restore frame the relay sends: {"t":"resize","epoch":E,"owner":"local"}
with cols/rows OMITTED, because only the tee knows the pane; the tee restores and acks with the pane
geometry (which may also carry paneCols/paneRows for observability).

Red-first: these name the surfaces the contract requires. #17 chooses the internal function names;
the `_resolve` helper accepts any listed candidate and fails with the contract clause when none
exists, so the red names a missing capability rather than a guessed symbol. When the implementation
lands, the candidate lists are narrowed to the name it provides - the assertions (owner, epoch,
committed G, restore frame) do not change.

The fake session is deliberately minimal: `resize` records what muxd asked the tee for,
`_send_owner` records every owner-link frame, and `cols`/`rows` are the COMMITTED geometry. A request
may reach the tee; it must not move `cols`/`rows` before the ack.
"""
import importlib
import json
import unittest


muxd = importlib.import_module("muxd")

PANE = (209, 51)


class FakeGridSession:
    """Enough of OwnerSession for the grid state machine. `cols`/`rows` are committed, not requested."""

    def __init__(self, name="grid", cols=PANE[0], rows=PANE[1]):
        self.name = name
        self.cols, self.rows = int(cols), int(rows)
        self.pane = tuple(PANE)
        self.resizes = []
        self.owner_frames = []
        self.local_sizes = {}
        self.remote_size_active = False
        # Contract state. The label is what the UI shows; the epoch is the only ordering token.
        self.grid_owner = "local"
        self.grid_epoch = 1
        self.grid_owner_label = "PC"
        # Legacy OwnerSession fields: boolean `owner` means "a visible local terminal owns the agent",
        # NOT the grid owner. The contract's reader must never return it.
        self.owner = True
        self.expected_owner = True

    def resize(self, cols, rows):
        self.resizes.append((int(cols), int(rows)))

    def _send_owner(self, obj):
        self.owner_frames.append(dict(obj))

    def alive(self):
        return True

    def tail_text(self):
        return ""


def _resolve(*names):
    for name in names:
        fn = getattr(muxd, name, None)
        if callable(fn):
            return fn
    raise AssertionError(
        "contract §4/§5: muxd exposes no handler for this transition; expected one of "
        + ", ".join(names)
    )


def _request(session, **message):
    """A relay->muxd resize request (the frame set calls it `resize`)."""
    muxd.apply_remote_session_size(session, message)


def _ack(session, **message):
    """A tee->muxd sizeAck: the only point at which a remote geometry becomes committed (G2)."""
    ack = _resolve(
        "apply_session_size_ack",
        "apply_size_ack",
        "commit_remote_size",
        "apply_grid_size_ack",
    )
    return ack(session, message)


def _activity(session):
    """A tee->muxd `activity` frame: local key/mouse seen; a device-owned grid returns to local (G5)."""
    fn = _resolve(
        "apply_owner_activity",
        "apply_grid_activity",
        "handle_owner_activity",
        "reclaim_local_grid",
    )
    return fn(session)


def _release(session, reason):
    """Relay/muxd release of a pin (owner viewer gone, or TTL expiry)."""
    fn = _resolve(
        "release_grid_owner",
        "release_device_owner",
        "apply_grid_release",
        "release_remote_size",
    )
    try:
        return fn(session, reason)
    except TypeError:
        return fn(session)


def _restore_frames(session):
    """Owner-link frames that ask the tee to restore the pane: t=resize, owner=local, no cols."""
    return [
        frame for frame in getattr(session, "owner_frames", [])
        if frame.get("t") == "resize" and frame.get("owner") == "local"
    ]


def grid_owner(session_or_record):
    """The contract's grid owner. Only `gridOwner`/`grid_owner` is accepted.

    The legacy boolean `owner` is the stream-owner flag and is deliberately not a candidate: reading
    it here would make the collision invisible and pass rows that never implemented the field.
    """
    if isinstance(session_or_record, dict):
        value = session_or_record.get("gridOwner")
        return value if isinstance(value, str) and value else None
    value = getattr(session_or_record, "grid_owner", None)
    return value if isinstance(value, str) and value else None


def grid_epoch(session_or_record):
    if isinstance(session_or_record, dict):
        value = session_or_record.get("gridEpoch")
    else:
        value = getattr(session_or_record, "grid_epoch", None)
    return value if isinstance(value, int) else None


def grid_label(session_or_record):
    if isinstance(session_or_record, dict):
        return session_or_record.get("gridOwnerLabel")
    return getattr(session_or_record, "grid_owner_label", None)


def committed(session):
    return (int(session.cols), int(session.rows))


class GridOwnershipTests(unittest.TestCase):
    # --- T4: no commit before the ack -------------------------------------------------------
    def test_t4_a_request_asks_the_tee_but_does_not_commit(self):
        s = FakeGridSession()
        _request(s, cols=100, rows=30, epoch=5, owner="dev-a")
        self.assertEqual(s.resizes, [(100, 30)], "the request must reach the tee")
        self.assertEqual(committed(s), PANE, "but G must not move before sizeAck (G2)")
        self.assertEqual(grid_owner(s), "local", "and the owner stays local until the ack")

    def test_t4_ack_commits_the_acked_geometry_and_epoch(self):
        s = FakeGridSession()
        _request(s, cols=100, rows=30, epoch=5, owner="dev-a")
        _ack(s, cols=100, rows=30, epoch=5, ok=True, owner="dev-a")
        self.assertEqual(committed(s), (100, 30), "the ack commits the applied size")
        self.assertEqual(grid_epoch(s), 5)
        self.assertEqual(grid_owner(s), "pin", "the pinning device owns the grid")
        self.assertEqual(grid_label(s), "dev-a", "and the list names it")

    def test_t4_failed_ack_leaves_g_and_epoch_unchanged(self):
        s = FakeGridSession()
        _request(s, cols=100, rows=30, epoch=5, owner="dev-a")
        _ack(s, cols=100, rows=30, epoch=5, ok=False, owner="dev-a")
        self.assertEqual(committed(s), PANE, "ok:false leaves G unchanged (§4)")
        self.assertEqual(grid_epoch(s), 1, "and commits no epoch")
        self.assertEqual(grid_owner(s), "local")

    # --- T3: epoch monotonicity -------------------------------------------------------------
    def test_t3_stale_request_is_ignored(self):
        s = FakeGridSession()
        _request(s, cols=100, rows=30, epoch=5, owner="dev-a")
        _ack(s, cols=100, rows=30, epoch=5, ok=True, owner="dev-a")
        asked = len(s.resizes)
        _request(s, cols=120, rows=36, epoch=5, owner="dev-b")
        _request(s, cols=120, rows=36, epoch=4, owner="dev-b")
        self.assertEqual(len(s.resizes), asked, "a request with epoch <= current is ignored (G4)")
        self.assertEqual(committed(s), (100, 30))

    def test_t3_stale_ack_cannot_roll_the_geometry_back(self):
        s = FakeGridSession()
        _request(s, cols=100, rows=30, epoch=5, owner="dev-a")
        _ack(s, cols=100, rows=30, epoch=5, ok=True, owner="dev-a")
        _ack(s, cols=209, rows=51, epoch=5, ok=True, owner="dev-a")   # a late ack for the superseded request
        self.assertEqual(committed(s), (100, 30), "a late ack must never roll G back (G4)")
        self.assertEqual(grid_epoch(s), 5)

    # --- T5: persistence --------------------------------------------------------------------
    def test_t5_committed_geometry_is_in_the_session_record(self):
        s = FakeGridSession()
        _request(s, cols=100, rows=30, epoch=5, owner="dev-a")
        _ack(s, cols=100, rows=30, epoch=5, ok=True, owner="dev-a")

        record = muxd.session_records_payload({s.name: s})[s.name]
        self.assertEqual(grid_owner(record), "pin", "the record carries the grid owner")
        self.assertEqual(grid_epoch(record), 5, "and the epoch")
        self.assertEqual(grid_label(record), "dev-a", "and the owner label")
        self.assertEqual((record.get("cols"), record.get("rows")), (100, 30),
                         "cols/rows are the committed G, not the pane")

        blob = json.dumps(muxd.manifest_payload({s.name: s}))
        restored = json.loads(blob)["sessions"][s.name]
        self.assertEqual(grid_owner(restored), "pin", "the values survive serialization")
        self.assertEqual(grid_epoch(restored), 5)

    # --- T6: local activity reclaims --------------------------------------------------------
    def test_t6_activity_asks_for_the_restore_and_commits_on_ack(self):
        s = FakeGridSession()
        _request(s, cols=100, rows=30, epoch=5, owner="dev-a")
        _ack(s, cols=100, rows=30, epoch=5, ok=True, owner="dev-a")

        _activity(s)

        restores = _restore_frames(s)
        self.assertTrue(restores, "activity must ask the tee to restore the pane (G5)")
        self.assertNotIn("cols", restores[-1], "the restore frame must not invent a geometry")
        self.assertTrue(s.grid_epoch > 5, "with a superseding epoch")

        _ack(s, cols=PANE[0], rows=PANE[1], epoch=s.grid_epoch, ok=True, owner="local",
             paneCols=PANE[0], paneRows=PANE[1])
        self.assertEqual(grid_owner(s), "local", "the pane ack commits local ownership")
        self.assertEqual(committed(s), PANE, "and the pane geometry")

    # --- T7: last pin wins ------------------------------------------------------------------
    def test_t7_last_pin_wins_and_the_first_is_stale(self):
        s = FakeGridSession()
        _request(s, cols=100, rows=30, epoch=5, owner="dev-a")
        _ack(s, cols=100, rows=30, epoch=5, ok=True, owner="dev-a")
        _request(s, cols=110, rows=34, epoch=6, owner="dev-b")
        _ack(s, cols=110, rows=34, epoch=6, ok=True, owner="dev-b")
        self.assertEqual(grid_owner(s), "pin", "the grid is pinned")
        self.assertEqual(grid_label(s), "dev-b", "last explicit pin wins (§3)")
        self.assertEqual(committed(s), (110, 34))

        asked = len(s.resizes)
        _request(s, cols=100, rows=30, epoch=6, owner="dev-a")   # A's late attempt
        self.assertEqual(len(s.resizes), asked, "the superseded pinner is stale and rejected (G4)")
        self.assertEqual(committed(s), (110, 34))

    # --- T8: release to local ---------------------------------------------------------------
    def test_t8_owner_disconnect_releases_to_local(self):
        s = FakeGridSession()
        _request(s, cols=100, rows=30, epoch=5, owner="dev-a")
        _ack(s, cols=100, rows=30, epoch=5, ok=True, owner="dev-a")

        _release(s, "disconnect")

        restores = _restore_frames(s)
        self.assertTrue(restores, "a disconnected owner releases the grid (G5)")
        self.assertNotIn("cols", restores[-1], "the release carries no geometry (ruling)")
        self.assertEqual(committed(s), (100, 30), "G is unchanged until the tee acks the pane")

        _ack(s, cols=PANE[0], rows=PANE[1], epoch=s.grid_epoch, ok=True, owner="local",
             paneCols=PANE[0], paneRows=PANE[1])
        self.assertEqual(grid_owner(s), "local")
        self.assertEqual(committed(s), PANE)

    def test_t8_ttl_expiry_releases_to_local(self):
        s = FakeGridSession()
        _request(s, cols=100, rows=30, epoch=5, owner="dev-a")
        _ack(s, cols=100, rows=30, epoch=5, ok=True, owner="dev-a")

        _release(s, "ttl")

        self.assertTrue(_restore_frames(s), "TTL expiry uses the same release semantics (G5)")


if __name__ == "__main__":
    unittest.main()
