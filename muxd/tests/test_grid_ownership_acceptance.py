"""Grid ownership acceptance: the muxd state machine of GRID-OWNERSHIP-CONTRACT.md (2026-11-09).

Base: 39d5efa (leaf17/p2-b-positional-cut, frozen).
LANDED-ON: implementation head sha recorded when #17's grid-ownership commits land.

Rows are the contract's pytest rows:
    T3  a request whose epoch <= current is ignored; committed G does not move
    T4  muxd commits only after sizeAck; an unacked or failed resize leaves G unchanged
    T5  the committed geometry (owner, epoch, G) persists in the session record
    T6  a local `activity` reclaims a device-owned grid
    T7  two devices: last pin wins; the first becomes stale and is rejected
    T8  owner release (disconnect / TTL) returns to local and the pane geometry

Red-first: these name the surfaces the contract requires. #17 chooses the internal function names;
the `_resolve` helper accepts any of the listed candidates and fails with the contract clause when
none exists, so the red names the missing capability rather than a guessed symbol. When the
implementation lands, the candidate lists are narrowed to the name it actually provides - the
assertions (owner, epoch, committed G, resize calls) do not change.

The fake session is deliberately minimal: `resize` records what muxd asked the tee for, and
`cols`/`rows` are the COMMITTED geometry. A request may call `resize` (it is how the tee is asked);
it must not move `cols`/`rows` before the ack.
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
        self.local_sizes = {}
        self.remote_size_active = False
        # Legacy OwnerSession fields: a boolean `owner` means "a visible local terminal owns the
        # agent", NOT the contract's grid owner. The contract's reader must therefore return None
        # here until the implementation carries the string state.
        self.owner = True
        self.expected_owner = True

    def resize(self, cols, rows):
        self.resizes.append((int(cols), int(rows)))

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


def grid_owner(session_or_record):
    """The contract's owner value, from an object or a session record dict.

    Accepts `gridOwner`, or `owner` when it is a string. A boolean `owner` is the legacy
    stream-owner flag and is NOT this value.
    """
    if isinstance(session_or_record, dict):
        for key in ("gridOwner", "ownerState", "owner"):
            value = session_or_record.get(key)
            if isinstance(value, str) and value:
                return value
        return None
    for key in ("grid_owner", "owner_state", "owner"):
        value = getattr(session_or_record, key, None)
        if isinstance(value, str) and value:
            return value
    return None


def grid_epoch(session_or_record):
    keys = ("ownerEpoch", "gridEpoch", "epoch") if isinstance(session_or_record, dict) \
        else ("owner_epoch", "grid_epoch", "epoch")
    for key in keys:
        value = (session_or_record.get(key) if isinstance(session_or_record, dict)
                 else getattr(session_or_record, key, None))
        if isinstance(value, int):
            return value
    return None


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
        _ack(s, cols=100, rows=30, epoch=5, ok=True)
        self.assertEqual(committed(s), (100, 30), "the ack commits the applied size")
        self.assertEqual(grid_epoch(s), 5)
        self.assertEqual(grid_owner(s), "dev-a", "the pinning device owns the grid")

    def test_t4_failed_ack_leaves_g_and_epoch_unchanged(self):
        s = FakeGridSession()
        _request(s, cols=100, rows=30, epoch=5, owner="dev-a")
        _ack(s, cols=100, rows=30, epoch=5, ok=False)
        self.assertEqual(committed(s), PANE, "ok:false leaves G unchanged (§4)")
        self.assertEqual(grid_epoch(s), None, "and commits no epoch")
        self.assertEqual(grid_owner(s), "local")

    # --- T3: epoch monotonicity -------------------------------------------------------------
    def test_t3_stale_request_is_ignored(self):
        s = FakeGridSession()
        _request(s, cols=100, rows=30, epoch=5, owner="dev-a")
        _ack(s, cols=100, rows=30, epoch=5, ok=True)
        asked = len(s.resizes)
        _request(s, cols=120, rows=36, epoch=5, owner="dev-b")
        _request(s, cols=120, rows=36, epoch=4, owner="dev-b")
        self.assertEqual(len(s.resizes), asked, "a request with epoch <= current is ignored (G4)")
        self.assertEqual(committed(s), (100, 30))

    def test_t3_stale_ack_cannot_roll_the_geometry_back(self):
        s = FakeGridSession()
        _request(s, cols=100, rows=30, epoch=5, owner="dev-a")
        _ack(s, cols=100, rows=30, epoch=5, ok=True)
        _ack(s, cols=209, rows=51, epoch=5, ok=True)   # a late ack for the superseded request
        self.assertEqual(committed(s), (100, 30), "a late ack must never roll G back (G4)")
        self.assertEqual(grid_epoch(s), 5)

    # --- T5: persistence --------------------------------------------------------------------
    def test_t5_committed_geometry_is_in_the_session_record(self):
        s = FakeGridSession()
        _request(s, cols=100, rows=30, epoch=5, owner="dev-a")
        _ack(s, cols=100, rows=30, epoch=5, ok=True)

        record = muxd.session_records_payload({s.name: s})[s.name]
        self.assertEqual(grid_owner(record), "dev-a", "the record carries the grid owner")
        self.assertEqual(grid_epoch(record), 5, "and the epoch")
        self.assertEqual((record.get("cols"), record.get("rows")), (100, 30),
                         "cols/rows are the committed G, not the pane")

        blob = json.dumps(muxd.manifest_payload({s.name: s}))
        restored = json.loads(blob)["sessions"][s.name]
        self.assertEqual(grid_owner(restored), "dev-a", "the values survive serialization")
        self.assertEqual(grid_epoch(restored), 5)

    # --- T6: local activity reclaims --------------------------------------------------------
    def test_t6_activity_reclaims_a_device_owned_grid(self):
        s = FakeGridSession()
        _request(s, cols=100, rows=30, epoch=5, owner="dev-a")
        _ack(s, cols=100, rows=30, epoch=5, ok=True)
        asked = len(s.resizes)

        _activity(s)

        self.assertEqual(grid_owner(s), "local", "a local key/mouse reclaims the grid (G5)")
        self.assertEqual(committed(s), PANE, "and restores the pane geometry")
        self.assertEqual(grid_epoch(s), 6, "with a new epoch")
        self.assertEqual(s.resizes[-1], PANE, "the tee is asked for the pane")
        self.assertEqual(len(s.resizes), asked + 1, "exactly one restore request")

    # --- T7: last pin wins ------------------------------------------------------------------
    def test_t7_last_pin_wins_and_the_first_is_stale(self):
        s = FakeGridSession()
        _request(s, cols=100, rows=30, epoch=5, owner="dev-a")
        _ack(s, cols=100, rows=30, epoch=5, ok=True)
        _request(s, cols=110, rows=34, epoch=6, owner="dev-b")
        _ack(s, cols=110, rows=34, epoch=6, ok=True)
        self.assertEqual(grid_owner(s), "dev-b", "last explicit pin wins (§3)")
        self.assertEqual(committed(s), (110, 34))

        asked = len(s.resizes)
        _request(s, cols=100, rows=30, epoch=6, owner="dev-a")   # A's late attempt
        self.assertEqual(len(s.resizes), asked, "the superseded pinner is stale and rejected (G4)")
        self.assertEqual(committed(s), (110, 34))

    # --- T8: release to local ---------------------------------------------------------------
    def test_t8_owner_disconnect_releases_to_local(self):
        s = FakeGridSession()
        _request(s, cols=100, rows=30, epoch=5, owner="dev-a")
        _ack(s, cols=100, rows=30, epoch=5, ok=True)

        _release(s, "disconnect")

        self.assertEqual(grid_owner(s), "local", "a disconnected owner releases the grid (G5)")
        self.assertEqual(committed(s), PANE, "G returns to the pane geometry")
        self.assertEqual(grid_epoch(s), 6)
        self.assertEqual(s.resizes[-1], PANE)

    def test_t8_ttl_expiry_releases_to_local(self):
        s = FakeGridSession()
        _request(s, cols=100, rows=30, epoch=5, owner="dev-a")
        _ack(s, cols=100, rows=30, epoch=5, ok=True)

        _release(s, "ttl")

        self.assertEqual(grid_owner(s), "local", "TTL expiry releases the grid (G5)")
        self.assertEqual(committed(s), PANE)


if __name__ == "__main__":
    unittest.main()
