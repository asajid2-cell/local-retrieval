"""Keep muxd/DEPLOY-local-scroll.md honest about the shipped code.

The deploy doc used to describe the PRE-flip world: page keys as "the proven mechanism" and a
web input filter that "strips ALL mouse reports". Both are now false. These tests pin the
corrected claims to the live sources so the doc cannot silently rot back into a lie.
"""

import re
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
DOC_PATH = REPO_ROOT / "muxd" / "DEPLOY-local-scroll.md"
VECTOR_PATH = REPO_ROOT / "docs" / "scroll-parity.json"
WEB_PATH = REPO_ROOT / "relay" / "public" / "index.html"

DOC = DOC_PATH.read_text(encoding="utf-8")

# Claims that were true before the sgr flip and are false now.
STALE_CLAIMS = (
    "strips ALL mouse reports",
    "strips all mouse reports",
    "KEEPS wheel reports",
    "The proven mechanism in this stack is PAGE KEYS",
    "2026-07-15-wheelscroll",
    "~line 1466",
)


class DeployDocParityTest(unittest.TestCase):
    def test_no_stale_preflip_claims_survive(self):
        for claim in STALE_CLAIMS:
            self.assertNotIn(
                claim, DOC,
                "DEPLOY-local-scroll.md still carries the pre-flip claim %r" % claim)

    def test_doc_names_contract_and_override(self):
        self.assertTrue(VECTOR_PATH.is_file(), "missing contract file %s" % VECTOR_PATH)
        self.assertIn("docs/scroll-parity.json", DOC,
                      "doc must name the authoritative vector table")
        self.assertIn("1007", DOC, "doc must mention DECSET 1007 (alternateScroll)")
        self.assertIn("MUXCTL_WHEEL=pagekeys", DOC,
                      "doc must describe the forced compatibility override")

    def test_web_filter_still_keeps_wheel_reports(self):
        """The doc claims the web filter keeps wheel reports while stripping the flood.

        Goes red if relay/public/index.html ever reverts to stripping the wheel — at which
        point the doc's Web-text-selection bullet would be a lie again.
        """
        web = WEB_PATH.read_text(encoding="utf-8", errors="replace")
        match = re.search(
            r"function stripMouseReports\s*\([^)]*\)\s*\{(.*?)\n\}", web, re.DOTALL)
        self.assertIsNotNone(match, "stripMouseReports() not found in %s" % WEB_PATH)
        body = match.group(1)
        self.assertIn("& 64", body,
                      "wheel exemption (SGR Cb bit 6) missing from stripMouseReports")
        self.assertIn(r"\x1b\[M", body, "X10 mouse-report strip missing")
        self.assertIn(r"\x1b\[[0-9;]+M", body, "urxvt/1015 mouse-report strip missing")

    def test_doc_states_x10_and_urxvt_stripped_wholesale(self):
        self.assertIn("strips X10 reports", DOC,
                      "doc must state X10 reports are stripped wholesale")
        self.assertIn("urxvt/1015", DOC,
                      "doc must state urxvt/1015 reports are stripped wholesale")
        self.assertIn("wholesale", DOC,
                      "doc must state stripping is wholesale")
        self.assertIn("only an app negotiating SGR", DOC,
                      "doc must state that only SGR wheel reports reach the pty")

    def test_doc_completes_dangling_sentence(self):
        self.assertIn("state *fidelity*", DOC,
                      "doc must complete the dangling sentence with 'state *fidelity*'")

    def test_documented_default_matches_code(self):
        import muxctl
        self.assertEqual(muxctl.DEFAULT_ALT_SCROLL, "sgr")
        self.assertIn("sgr", DOC, "doc must state the sgr default")


if __name__ == "__main__":
    unittest.main()
