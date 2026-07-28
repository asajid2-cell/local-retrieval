import importlib
import unittest
from unittest import mock


muxd = importlib.import_module("muxd")


def _reset_custody_emission():
    """The emission filter is module state and several tests reuse the same custody keys —
    without this a later test's first line looks like a repeat of an earlier test's."""
    for slot in muxd._LAST_CUSTODY_EMISSION.values():
        slot.update({"sig": None, "at": 0.0, "repeats": 0})


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
        _reset_custody_emission()

    def tearDown(self):
        muxd._ORPHANED_CONPTY_HOSTS.clear()
        muxd._PENDING_CONPTY_BASELINES.clear()
        _reset_custody_emission()

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
        _reset_custody_emission()

    def tearDown(self):
        muxd._ORPHANED_CONPTY_HOSTS.clear()
        muxd._PENDING_CONPTY_BASELINES.clear()
        _reset_custody_emission()

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


class ConptyQuarantineExpiryTests(unittest.TestCase):
    """A baseline whose enumeration never recovers must stop blocking spawns."""

    def setUp(self):
        muxd._ORPHANED_CONPTY_HOSTS.clear()
        muxd._PENDING_CONPTY_BASELINES.clear()
        muxd._ABANDONED_CONPTY_BASELINES.clear()
        _reset_custody_emission()

    def tearDown(self):
        muxd._ORPHANED_CONPTY_HOSTS.clear()
        muxd._PENDING_CONPTY_BASELINES.clear()
        muxd._ABANDONED_CONPTY_BASELINES.clear()
        _reset_custody_emission()

    def _reap_with_failing_enumeration(self, times, logs):
        with mock.patch.object(
            muxd,
            "_new_conpty_host_processes",
            side_effect=OSError("toolhelp snapshot failed"),
        ), mock.patch.object(muxd, "log", side_effect=logs.append):
            for _ in range(times):
                muxd._reap_orphaned_conpty_hosts(timeout=1)

    def test_permanent_enumeration_failure_expires_the_quarantine(self):
        muxd._retain_pending_conpty_baseline({7: "a"}, "failed spawn for tab-1")
        self.assertEqual(1, len(muxd._PENDING_CONPTY_BASELINES))
        logs = []
        self._reap_with_failing_enumeration(5, logs)
        self.assertEqual({}, muxd._PENDING_CONPTY_BASELINES)
        expiry = [line for line in logs if "quarantine expired" in line]
        self.assertEqual(1, len(expiry), logs)
        self.assertIn("5 attempt(s)", expiry[0])
        self.assertIn("resuming spawns (orphan hosts may have leaked)", expiry[0])
        self.assertIn("failed spawn for tab-1", expiry[0])
        self.assertEqual(1, len(muxd._ABANDONED_CONPTY_BASELINES))
        abandoned = muxd._ABANDONED_CONPTY_BASELINES[0]
        self.assertEqual(((7, "a"),), abandoned["baseline"])
        self.assertEqual("failed spawn for tab-1", abandoned["reason"])
        self.assertEqual(5, abandoned["attempts"])
        self.assertIn("toolhelp snapshot failed", abandoned["error"])

    def test_quarantine_holds_until_the_attempt_budget_is_spent(self):
        muxd._retain_pending_conpty_baseline({7: "a"}, "failed spawn for tab-1")
        logs = []
        self._reap_with_failing_enumeration(4, logs)
        self.assertEqual(1, len(muxd._PENDING_CONPTY_BASELINES))
        self.assertEqual([], muxd._ABANDONED_CONPTY_BASELINES)
        # The 4 attempts observe identical state, so only the first one narrates it.
        self.assertEqual(1, len([line for line in logs if "still pending" in line]))

    def test_age_alone_expires_the_quarantine_before_the_attempt_budget(self):
        with mock.patch.object(muxd.time, "monotonic", return_value=1000.0):
            muxd._retain_pending_conpty_baseline({7: "a"}, "aborted spawn for tab-2")
        logs = []
        with mock.patch.object(
            muxd.time,
            "monotonic",
            return_value=1000.0 + muxd.CONPTY_QUARANTINE_TTL + 1.0,
        ):
            self._reap_with_failing_enumeration(1, logs)
        self.assertEqual({}, muxd._PENDING_CONPTY_BASELINES)
        expiry = [line for line in logs if "quarantine expired" in line]
        self.assertEqual(1, len(expiry), logs)
        self.assertIn("1 attempt(s)", expiry[0])
        self.assertEqual(1, len(muxd._ABANDONED_CONPTY_BASELINES))

    def test_baseline_that_resolves_is_not_recorded_as_abandoned(self):
        muxd._retain_pending_conpty_baseline({7: "a"}, "failed spawn for tab-3")
        attempts = {"n": 0}

        def flaky(previous_records, timeout=1.0):
            attempts["n"] += 1
            if attempts["n"] < 2:
                raise OSError("toolhelp snapshot failed")
            return [(9, "spawned")]

        logs = []
        with mock.patch.object(
            muxd, "_new_conpty_host_processes", side_effect=flaky
        ), mock.patch.object(
            # The synthetic pid is not a live host of ours, so the dormant-record GC
            # would drop it before the terminate path; keep it LIVE for this test.
            muxd,
            "_same_process_instance",
            return_value=True,
        ), mock.patch.object(
            muxd,
            "_terminate_conhost_records",
            side_effect=lambda owned, timeout=3: (list(owned), ["still running"]),
        ), mock.patch.object(muxd, "log", side_effect=logs.append):
            muxd._reap_orphaned_conpty_hosts(timeout=1)
            muxd._reap_orphaned_conpty_hosts(timeout=1)
        self.assertEqual({}, muxd._PENDING_CONPTY_BASELINES)
        self.assertEqual([], muxd._ABANDONED_CONPTY_BASELINES)
        self.assertEqual([], [line for line in logs if "quarantine expired" in line])
        # The success path still hands the baseline's reason to the orphan record.
        self.assertEqual(
            "failed spawn for tab-3",
            muxd._ORPHANED_CONPTY_HOSTS[(9, "spawned")]["reason"],
        )

    def test_abandoned_baselines_are_bounded(self):
        limit = muxd.CONPTY_ABANDONED_BASELINE_LIMIT
        for index in range(limit + 3):
            muxd._retain_pending_conpty_baseline({index: "a"}, f"spawn {index}")
            self._reap_with_failing_enumeration(5, [])
        self.assertEqual(limit, len(muxd._ABANDONED_CONPTY_BASELINES))
        self.assertEqual(
            f"spawn {limit + 2}",
            muxd._ABANDONED_CONPTY_BASELINES[-1]["reason"],
        )


class ConptySpawnGateTests(unittest.TestCase):
    """The spawn gate stays a cheap truthiness check, but names what is blocking."""

    def setUp(self):
        muxd._PENDING_CONPTY_BASELINES.clear()
        _reset_custody_emission()

    def tearDown(self):
        muxd._PENDING_CONPTY_BASELINES.clear()
        _reset_custody_emission()

    def test_gate_message_names_the_blocking_reason(self):
        muxd._retain_pending_conpty_baseline({7: "a"}, "failed spawn for tab-9")
        session = muxd.Session.__new__(muxd.Session)
        session.name = "tab-10"
        session.cmd = ""
        session.rows = 24
        session.cols = 80
        session.cwd = None
        with self.assertRaises(RuntimeError) as caught:
            session.spawn()
        message = str(caught.exception)
        self.assertIn("ConPTY custody is quarantined pending orphan-host discovery", message)
        self.assertIn("failed spawn for tab-9", message)

    def test_gate_opens_once_the_baseline_is_gone(self):
        muxd._retain_pending_conpty_baseline({7: "a"}, "failed spawn for tab-9")
        logs = []
        with mock.patch.object(
            muxd,
            "_new_conpty_host_processes",
            side_effect=OSError("toolhelp snapshot failed"),
        ), mock.patch.object(muxd, "log", side_effect=logs.append):
            for _ in range(5):
                muxd._reap_orphaned_conpty_hosts(timeout=1)
        self.assertEqual({}, muxd._PENDING_CONPTY_BASELINES)
        # Gate is now open: reaching PtyProcess.spawn proves the RuntimeError no longer fires.
        session = muxd.Session.__new__(muxd.Session)
        session.name = "tab-10"
        session.cmd = ""
        session.rows = 24
        session.cols = 80
        session.cwd = None
        with mock.patch.object(
            muxd.PtyProcess, "spawn", side_effect=RuntimeError("reached the real spawn")
        ), mock.patch.object(muxd, "_conpty_host_process_records", return_value={}), \
                mock.patch.object(
                    muxd, "_new_conpty_host_processes", return_value=[]
                ), mock.patch.object(
                    muxd, "_terminate_conhost_records", return_value=([], [])
                ), mock.patch.object(muxd, "log", side_effect=logs.append):
            with self.assertRaises(RuntimeError) as caught:
                session.spawn()
        self.assertEqual("reached the real spawn", str(caught.exception))


class ConptyStaleEmissionFilterTests(unittest.TestCase):
    """A dead record must stop narrating state that has not changed."""

    def setUp(self):
        muxd._ORPHANED_CONPTY_HOSTS.clear()
        muxd._PENDING_CONPTY_BASELINES.clear()
        _reset_custody_emission()

    def tearDown(self):
        muxd._ORPHANED_CONPTY_HOSTS.clear()
        muxd._PENDING_CONPTY_BASELINES.clear()
        _reset_custody_emission()

    def _reap(self, clock, times, logs):
        """Run the reaper `times` times at a frozen clock against a permanently stuck host."""
        with mock.patch.object(
            muxd.time, "monotonic", return_value=clock
        ), mock.patch.object(
            muxd, "_same_process_instance", return_value=True
        ), mock.patch.object(
            muxd,
            "_terminate_conhost_records",
            side_effect=lambda owned, timeout=3: (list(owned), ["still running"]),
        ), mock.patch.object(muxd, "log", side_effect=logs.append):
            for _ in range(times):
                muxd._reap_orphaned_conpty_hosts(timeout=1)

    @staticmethod
    def _pending(logs):
        return [line for line in logs if "still pending" in line]

    def test_unchanged_state_emits_exactly_one_line_across_five_reaps(self):
        with mock.patch.object(muxd.time, "monotonic", return_value=500.0):
            muxd._retain_orphaned_conpty_hosts([(1234, "tok")], "why")
        logs = []
        self._reap(500.0, 5, logs)
        pending = self._pending(logs)
        self.assertEqual(1, len(pending), logs)
        self.assertEqual(
            "[conpty] supervised orphan cleanup still pending: still running", pending[0]
        )
        self.assertEqual(4, muxd._LAST_CUSTODY_EMISSION["cleanup"]["repeats"])

    def test_elapsed_interval_re_emits_once_carrying_the_repeat_count(self):
        with mock.patch.object(muxd.time, "monotonic", return_value=500.0):
            muxd._retain_orphaned_conpty_hosts([(1234, "tok")], "why")
        logs = []
        self._reap(500.0, 5, logs)
        later = []
        self._reap(500.0 + muxd.CONPTY_EMISSION_INTERVAL + 1.0, 1, later)
        pending = self._pending(later)
        self.assertEqual(1, len(pending), later)
        self.assertEqual(
            "[conpty] supervised orphan cleanup still pending: still running (repeated 4x)",
            pending[0],
        )
        # And the freshly emitted line restarts the window, not a second one right after.
        again = []
        self._reap(500.0 + muxd.CONPTY_EMISSION_INTERVAL + 2.0, 1, again)
        self.assertEqual([], self._pending(again), again)

    def test_changed_retained_set_emits_immediately(self):
        with mock.patch.object(muxd.time, "monotonic", return_value=500.0):
            muxd._retain_orphaned_conpty_hosts([(1234, "tok")], "why")
        logs = []
        self._reap(500.0, 3, logs)
        self.assertEqual(1, len(self._pending(logs)), logs)
        with mock.patch.object(muxd.time, "monotonic", return_value=500.0):
            muxd._retain_orphaned_conpty_hosts([(5678, "tok2")], "another")
        changed = []
        self._reap(500.0, 1, changed)
        pending = self._pending(changed)
        # Same instant, same interval — the state itself changed, so it speaks now.
        self.assertEqual(1, len(pending), changed)
        self.assertIn("(repeated 2x)", pending[0])

    def test_transition_lines_bypass_the_filter(self):
        with mock.patch.object(muxd.time, "monotonic", return_value=1000.0):
            muxd._retain_orphaned_conpty_hosts([(2, "b")], "stuck host")
        logs = []
        self._reap(1000.0, 2, logs)
        self.assertEqual(1, len(self._pending(logs)), logs)
        # Seed an already-expired record: its one-shot drop must not be swallowed by the
        # suppression that is currently silencing the unchanged 'still pending' line.
        muxd._ORPHANED_CONPTY_HOSTS[(1, "a")] = muxd._custody_record(
            "long dead", now=1000.0 - muxd.CONPTY_CUSTODY_TTL - 10.0
        )
        third = []
        self._reap(1000.0, 1, third)
        self.assertEqual([], self._pending(third), third)
        abandoning = [line for line in third if "abandoning orphan host record" in line]
        self.assertEqual(1, len(abandoning), third)
        self.assertIn("pid=1", abandoning[0])
        self.assertIn("long dead", abandoning[0])

    def test_quarantine_and_cleanup_lines_suppress_independently(self):
        # Both stale emitters fire in the same pass; a shared slot would see their
        # signatures alternate and suppress neither.
        with mock.patch.object(muxd.time, "monotonic", return_value=2000.0):
            muxd._retain_orphaned_conpty_hosts([(1234, "tok")], "why")
            muxd._retain_pending_conpty_baseline({7: "a"}, "failed spawn")
        logs = []
        with mock.patch.object(
            muxd.time, "monotonic", return_value=2000.0
        ), mock.patch.object(
            muxd, "_same_process_instance", return_value=True
        ), mock.patch.object(
            muxd, "_new_conpty_host_processes", side_effect=OSError("snapshot failed")
        ), mock.patch.object(
            muxd,
            "_terminate_conhost_records",
            side_effect=lambda owned, timeout=3: (list(owned), ["still running"]),
        ), mock.patch.object(muxd, "log", side_effect=logs.append):
            for _ in range(4):
                muxd._reap_orphaned_conpty_hosts(timeout=1)
        self.assertEqual(
            1, len([line for line in logs if "orphan discovery still pending" in line]), logs
        )
        self.assertEqual(
            1, len([line for line in logs if "cleanup still pending" in line]), logs
        )


class ConptyReaperBackoffTests(unittest.TestCase):
    """A reap that changes nothing must not re-run every 5 seconds forever."""

    def setUp(self):
        muxd._ORPHANED_CONPTY_HOSTS.clear()
        muxd._PENDING_CONPTY_BASELINES.clear()

    def tearDown(self):
        muxd._ORPHANED_CONPTY_HOSTS.clear()
        muxd._PENDING_CONPTY_BASELINES.clear()

    def test_first_pass_uses_the_base_interval(self):
        self.assertEqual(
            muxd.CONPTY_REAPER_INTERVAL,
            muxd._reaper_backoff(None, (1, (), ()), muxd.CONPTY_REAPER_MAX_INTERVAL),
        )

    def test_stalled_passes_grow_toward_the_ceiling_and_stop(self):
        sig = (1, (((1234, "tok"),)), ())
        delay = muxd.CONPTY_REAPER_INTERVAL
        seen = []
        for _ in range(6):
            delay = muxd._reaper_backoff(sig, sig, delay)
            seen.append(delay)
        self.assertEqual([10.0, 20.0, 30.0, 30.0, 30.0, 30.0], seen)
        self.assertLessEqual(max(seen), muxd.CONPTY_REAPER_MAX_INTERVAL)

    def test_changed_signature_resets_to_the_base_interval(self):
        self.assertEqual(
            muxd.CONPTY_REAPER_INTERVAL,
            muxd._reaper_backoff((1, (), ()), (2, (), ()), 30.0),
        )

    def test_a_newly_retained_record_changes_the_tick_signature(self):
        stalled = muxd._reaper_tick_signature(0)
        muxd._retain_orphaned_conpty_hosts([(1234, "tok")], "why")
        fresh = muxd._reaper_tick_signature(1)
        self.assertNotEqual(stalled, fresh)
        self.assertEqual(
            muxd.CONPTY_REAPER_INTERVAL, muxd._reaper_backoff(stalled, fresh, 30.0)
        )
        # ...and an identical follow-up pass backs off again from that reset.
        self.assertEqual(10.0, muxd._reaper_backoff(fresh, fresh, 5.0))


if __name__ == "__main__":
    unittest.main()
