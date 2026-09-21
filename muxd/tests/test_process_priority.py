"""The scheduling-priority guarantee: muxd must not run at the priority its task gives it.

Background: the MuxdSessionHost scheduled task carries no <Priority> element, so Task Scheduler
uses its default of 7, which maps to BELOW_NORMAL_PRIORITY_CLASS. wscript -> powershell -> pythonw
all inherit it. A BelowNormal process is descheduled whenever anything at Normal wants a core, and
the failure that produces is not "slow": the event loop stops being scheduled, the terminal paints
nothing, and kills time out against the relay's fence.

Measured on the live box with a game resident at ~3.9 cores: BelowNormal rtt p90=452ms p99=1769ms
and loop lag max=2094ms, versus AboveNormal p90=6ms p99=8ms and lag max=15ms - the same process and
the same code, with priority the only variable. So the fix is asserted here as behaviour, not as a
comment: the resolver must default to AboveNormal, and the applied class must never be lowered.
"""
import ctypes
import os
import unittest
from unittest import mock

import muxd


class PriorityResolutionTests(unittest.TestCase):
    def test_the_default_is_abovenormal_not_the_inherited_class(self):
        # None means "unset", which is the state on every host that has not been told otherwise.
        # If this ever resolves to BelowNormal, muxd is vulnerable again by default.
        self.assertEqual(muxd.resolve_priority_class(None),
                         muxd.PRIORITY_CLASSES["abovenormal"])
        self.assertEqual(muxd.resolve_priority_class(""),
                         muxd.PRIORITY_CLASSES["abovenormal"])
        self.assertEqual(muxd.resolve_priority_class("default"),
                         muxd.PRIORITY_CLASSES["abovenormal"])

    def test_names_are_accepted_in_the_spellings_an_operator_would_actually_type(self):
        expected = muxd.PRIORITY_CLASSES["abovenormal"]
        for spelling in ("AboveNormal", "abovenormal", "ABOVE_NORMAL", "above-normal", "above normal"):
            with self.subTest(spelling=spelling):
                self.assertEqual(muxd.resolve_priority_class(spelling), expected)

    def test_numeric_priority_is_accepted_in_decimal_and_hex(self):
        normal = muxd.PRIORITY_CLASSES["normal"]
        self.assertEqual(muxd.resolve_priority_class("32"), normal)
        self.assertEqual(muxd.resolve_priority_class("0x20"), normal)

    def test_an_unrecognised_value_resolves_to_none_rather_than_a_silent_default(self):
        # None is the sentinel apply_process_priority reports; a typo must not be mistaken for
        # "use the default", or an operator who asked for High and typo'd it gets AboveNormal
        # and no signal that their setting was ignored.
        for bad in ("veryhigh", "99", "-1"):
            with self.subTest(value=bad):
                self.assertIsNone(muxd.resolve_priority_class(bad))

    def test_the_raw_constants_are_not_numerically_ordered_so_ranking_is_explicit(self):
        # HIGH is 0x80 and ABOVE_NORMAL is 0x8000. Any comparison on the raw values inverts these
        # two, which is how a process an operator pinned at High would get *raised* to AboveNormal.
        self.assertLess(muxd.PRIORITY_CLASSES["high"], muxd.PRIORITY_CLASSES["abovenormal"])
        self.assertLess(muxd.priority_rank(muxd.PRIORITY_CLASSES["abovenormal"]),
                        muxd.priority_rank(muxd.PRIORITY_CLASSES["high"]))


class FakeKernel32:
    """Records what was asked of the process-priority API without touching the real process."""

    def __init__(self, current, set_succeeds=True):
        self.current = current
        self.set_succeeds = set_succeeds
        self.applied = []

    def GetCurrentProcess(self):
        return "handle"

    def GetPriorityClass(self, handle):
        return self.current

    def SetPriorityClass(self, handle, value):
        self.applied.append(value)
        if self.set_succeeds:
            self.current = value
        return 1 if self.set_succeeds else 0


class PriorityApplicationTests(unittest.TestCase):
    def setUp(self):
        self.logs = []
        self._original_log = muxd.log
        muxd.log = self.logs.append
        self.addCleanup(lambda: setattr(muxd, "log", self._original_log))
        self._original_priority = muxd.MUXD_PRIORITY
        self.addCleanup(lambda: setattr(muxd, "MUXD_PRIORITY", self._original_priority))
        self._original_nt = muxd.os.name
        self.addCleanup(lambda: setattr(muxd.os, "name", self._original_nt))

    def _apply(self, fake):
        with mock.patch.object(muxd, "_priority_kernel32", return_value=fake):
            muxd.apply_process_priority()

    def test_a_belownormal_process_is_raised_and_says_so(self):
        fake = FakeKernel32(muxd.PRIORITY_CLASSES["belownormal"])
        self._apply(fake)
        self.assertEqual(fake.applied, [muxd.PRIORITY_CLASSES["abovenormal"]])
        self.assertTrue(any("raised" in m for m in self.logs),
                        f"the raise was not reported: {self.logs}")

    def test_an_already_higher_priority_is_never_lowered(self):
        # An operator running muxd under High on purpose must not be demoted back to AboveNormal.
        fake = FakeKernel32(muxd.PRIORITY_CLASSES["high"])
        self._apply(fake)
        self.assertEqual(fake.applied, [], "muxd lowered a priority an operator had raised")
        self.assertEqual(self.logs, [], f"a no-op should be quiet: {self.logs}")

    def test_a_failed_set_is_reported_rather_than_assumed(self):
        fake = FakeKernel32(muxd.PRIORITY_CLASSES["belownormal"], set_succeeds=False)
        self._apply(fake)
        self.assertTrue(any("failed" in m for m in self.logs),
                        f"a refused SetPriorityClass was silent: {self.logs}")

    def test_an_unrecognised_setting_is_reported_not_silently_defaulted(self):
        muxd.MUXD_PRIORITY = None
        fake = FakeKernel32(muxd.PRIORITY_CLASSES["belownormal"])
        self._apply(fake)
        self.assertEqual(fake.applied, [])
        self.assertTrue(any("not a known priority class" in m for m in self.logs),
                        f"an ignored setting was silent: {self.logs}")

    def test_the_effective_class_is_reported_by_name(self):
        fake = FakeKernel32(muxd.PRIORITY_CLASSES["abovenormal"])
        with mock.patch.object(muxd, "_priority_kernel32", return_value=fake):
            self.assertEqual(muxd.current_priority_class(), "abovenormal")

    def test_the_reported_class_is_never_a_bare_number_for_a_known_class(self):
        # `info` is read by operators and by the relay health path; an opaque hex class would
        # hide exactly the condition this reporting exists to expose.
        for name, value in muxd.PRIORITY_CLASSES.items():
            with self.subTest(name=name):
                fake = FakeKernel32(value)
                with mock.patch.object(muxd, "_priority_kernel32", return_value=fake):
                    self.assertEqual(muxd.current_priority_class(), name)


class EntrypointWiringTests(unittest.TestCase):
    """The priority must be applied when muxd RUNS, and only then.

    An import-time call was tried first and was wrong: this is a process-global mutation, and
    muxd.py is imported by this very test suite, so importing it rescheduled the test runner and
    appended to the live muxd.log. Both halves are asserted here - wired into __main__, and absent
    from module scope - because either one alone is satisfied by the broken version.
    """

    @classmethod
    def setUpClass(cls):
        import pathlib
        cls.source = pathlib.Path(muxd.__file__).read_text(encoding="utf-8")

    def test_the_entrypoint_applies_the_priority(self):
        entry = self.source.split('if __name__ == "__main__":', 1)
        self.assertEqual(len(entry), 2, "muxd.py has no __main__ block")
        self.assertIn("apply_process_priority()", entry[1],
                      "muxd never applies the priority it resolves, so it runs at whatever the "
                      "MuxdSessionHost task's default gave it (BelowNormal)")

    def test_the_priority_is_not_applied_at_import_time(self):
        module_scope = self.source.split('if __name__ == "__main__":', 1)[0]
        stray = [ln for ln in module_scope.splitlines()
                 if ln.strip() == "apply_process_priority()"]
        self.assertEqual(stray, [], "the priority is applied at import, so every importer "
                                    "(the test suite, tooling) is rescheduled and logged")

    def test_the_effective_class_is_surfaced_through_info(self):
        # Without this the operator sees lag and a black terminal but not the cause.
        self.assertIn('"priorityClass": current_priority_class()', self.source)


if __name__ == "__main__":
    unittest.main()
