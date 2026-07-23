import importlib
import unittest
from unittest import mock


muxd = importlib.import_module("muxd")


class ConptyCustodyRecordTests(unittest.TestCase):
    """Custody entries carry an age so a later pass can expire them."""

    def setUp(self):
        muxd._ORPHANED_CONPTY_HOSTS.clear()
        muxd._PENDING_CONPTY_BASELINES.clear()

    def tearDown(self):
        muxd._ORPHANED_CONPTY_HOSTS.clear()
        muxd._PENDING_CONPTY_BASELINES.clear()

    def test_retain_orphan_stores_record_with_reason_and_first_seen(self):
        muxd._retain_orphaned_conpty_hosts([(1234, "tok")], "why")
        record = muxd._ORPHANED_CONPTY_HOSTS[(1234, "tok")]
        self.assertEqual("why", record["reason"])
        self.assertIsInstance(record["first_seen"], float)
        self.assertEqual(record["first_seen"], record["last_attempt"])
        self.assertEqual(0, record["attempts"])

    def test_reretaining_same_key_keeps_first_seen_and_first_reason(self):
        with mock.patch.object(muxd.time, "monotonic", return_value=100.0):
            muxd._retain_orphaned_conpty_hosts([(1234, "tok")], "first")
        with mock.patch.object(muxd.time, "monotonic", return_value=900.0):
            muxd._retain_orphaned_conpty_hosts([(1234, "tok")], "second")
        record = muxd._ORPHANED_CONPTY_HOSTS[(1234, "tok")]
        self.assertEqual(100.0, record["first_seen"])
        self.assertEqual("first", record["reason"])
        self.assertEqual(1, len(muxd._ORPHANED_CONPTY_HOSTS))

    def test_pending_baseline_stores_record_and_keeps_first_seen(self):
        with mock.patch.object(muxd.time, "monotonic", return_value=10.0):
            muxd._retain_pending_conpty_baseline({7: "a"}, "quarantined")
        with mock.patch.object(muxd.time, "monotonic", return_value=70.0):
            muxd._retain_pending_conpty_baseline({7: "a"}, "again")
        record = muxd._PENDING_CONPTY_BASELINES[((7, "a"),)]
        self.assertEqual("quarantined", record["reason"])
        self.assertEqual(10.0, record["first_seen"])

    def test_custody_age_grows_with_the_clock(self):
        with mock.patch.object(muxd.time, "monotonic", return_value=50.0):
            record = muxd._custody_record("aging")
            self.assertEqual(0.0, muxd._custody_age(record))
        with mock.patch.object(muxd.time, "monotonic", return_value=125.5):
            self.assertAlmostEqual(75.5, muxd._custody_age(record))

    def test_custody_touch_bumps_attempts_without_moving_first_seen(self):
        record = muxd._custody_record("touched", now=5.0)
        muxd._custody_touch(record, now=11.0)
        muxd._custody_touch(record, now=12.0)
        self.assertEqual(2, record["attempts"])
        self.assertEqual(12.0, record["last_attempt"])
        self.assertEqual(5.0, record["first_seen"])


class ConptyCustodyReapTests(unittest.TestCase):
    """The reaper still drains custody on success — representation change only."""

    def setUp(self):
        muxd._ORPHANED_CONPTY_HOSTS.clear()
        muxd._PENDING_CONPTY_BASELINES.clear()

    def tearDown(self):
        muxd._ORPHANED_CONPTY_HOSTS.clear()
        muxd._PENDING_CONPTY_BASELINES.clear()

    def test_reap_empties_orphans_when_termination_succeeds(self):
        muxd._retain_orphaned_conpty_hosts([(1234, "tok"), (5678, "tok2")], "why")
        self.assertEqual(2, len(muxd._ORPHANED_CONPTY_HOSTS))
        with mock.patch.object(
            muxd,
            "_same_process_instance",
            return_value=True,
        ), mock.patch.object(
            muxd,
            "_terminate_conhost_records",
            return_value=([], []),
        ):
            retained = muxd._reap_orphaned_conpty_hosts(timeout=1)
        self.assertEqual(0, retained)
        self.assertEqual({}, muxd._ORPHANED_CONPTY_HOSTS)

    def test_reap_carries_baseline_reason_into_the_orphan_record(self):
        muxd._retain_pending_conpty_baseline({7: "a"}, "quarantined")
        with mock.patch.object(
            muxd,
            "_same_process_instance",
            return_value=True,
        ), mock.patch.object(
            muxd,
            "_new_conpty_host_processes",
            return_value=[(9, "spawned")],
        ), mock.patch.object(
            muxd,
            "_terminate_conhost_records",
            side_effect=lambda owned, timeout=3: (list(owned), ["still running"]),
        ):
            retained = muxd._reap_orphaned_conpty_hosts(timeout=1)
        self.assertEqual(1, retained)
        self.assertEqual({}, muxd._PENDING_CONPTY_BASELINES)
        self.assertEqual("quarantined", muxd._ORPHANED_CONPTY_HOSTS[(9, "spawned")]["reason"])


class ConptyCustodyGarbageCollectionTests(unittest.TestCase):
    """A custody record must not outlive its subject."""

    def setUp(self):
        muxd._ORPHANED_CONPTY_HOSTS.clear()
        muxd._PENDING_CONPTY_BASELINES.clear()

    def tearDown(self):
        muxd._ORPHANED_CONPTY_HOSTS.clear()
        muxd._PENDING_CONPTY_BASELINES.clear()

    def test_dormant_record_is_dropped_without_any_terminate_attempt(self):
        muxd._retain_orphaned_conpty_hosts([(1234, "tok")], "why")
        with mock.patch.object(
            muxd,
            "_same_process_instance",
            return_value=False,
        ) as same, mock.patch.object(
            muxd,
            "_terminate_conhost_records",
        ) as terminate, mock.patch.object(muxd, "log") as logger:
            retained = muxd._reap_orphaned_conpty_hosts(timeout=1)
        self.assertEqual(0, retained)
        self.assertEqual({}, muxd._ORPHANED_CONPTY_HOSTS)
        terminate.assert_not_called()
        same.assert_called_once_with(1234, "tok")
        self.assertEqual(
            [],
            [c for c in logger.call_args_list if "abandoning" in str(c)],
        )

    def test_live_record_past_the_ttl_is_abandoned_with_exactly_one_log(self):
        with mock.patch.object(muxd.time, "monotonic", return_value=100.0):
            muxd._retain_orphaned_conpty_hosts([(1234, "tok")], "stubborn host")
        expired = 100.0 + muxd.CONPTY_CUSTODY_TTL + 1.0
        with mock.patch.object(
            muxd.time, "monotonic", return_value=expired
        ), mock.patch.object(
            muxd,
            "_same_process_instance",
            return_value=True,
        ), mock.patch.object(
            muxd,
            "_terminate_conhost_records",
        ) as terminate, mock.patch.object(muxd, "log") as logger:
            retained = muxd._reap_orphaned_conpty_hosts(timeout=1)
        self.assertEqual(0, retained)
        self.assertEqual({}, muxd._ORPHANED_CONPTY_HOSTS)
        terminate.assert_not_called()
        abandon_logs = [
            c.args[0] for c in logger.call_args_list if "abandoning" in str(c)
        ]
        self.assertEqual(1, len(abandon_logs))
        self.assertEqual(
            "[conpty] abandoning orphan host record pid=1234 after "
            f"{muxd.CONPTY_CUSTODY_TTL + 1.0:.1f}s / 0 attempt(s): stubborn host",
            abandon_logs[0],
        )

    def test_live_record_inside_the_ttl_still_takes_the_terminate_path(self):
        with mock.patch.object(muxd.time, "monotonic", return_value=100.0):
            muxd._retain_orphaned_conpty_hosts([(1234, "tok")], "why")
        fresh = 100.0 + max(0.0, muxd.CONPTY_CUSTODY_TTL - 1.0)
        with mock.patch.object(
            muxd.time, "monotonic", return_value=fresh
        ), mock.patch.object(
            muxd,
            "_same_process_instance",
            return_value=True,
        ), mock.patch.object(
            muxd,
            "_terminate_conhost_records",
            side_effect=lambda owned, timeout=3: (list(owned), ["still running"]),
        ) as terminate, mock.patch.object(muxd, "log") as logger:
            retained = muxd._reap_orphaned_conpty_hosts(timeout=1)
        self.assertEqual(1, retained)
        terminate.assert_called_once_with([(1234, "tok")], timeout=1)
        record = muxd._ORPHANED_CONPTY_HOSTS[(1234, "tok")]
        self.assertEqual("why", record["reason"])
        self.assertEqual(100.0, record["first_seen"])
        self.assertEqual(1, record["attempts"])
        self.assertEqual(
            [],
            [c for c in logger.call_args_list if "abandoning" in str(c)],
        )


if __name__ == "__main__":
    unittest.main()
