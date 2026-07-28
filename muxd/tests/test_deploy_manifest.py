"""The deploy manifest must carry every local module the runtime entrypoints import.

py_compile (the old preflight) never resolves imports, so muxd.py can compile perfectly in the live
runtime dir and still die at startup on a sibling module nobody copied. These tests read the
sources as text — no muxd import, muxd.py has module-level side effects — and hold
scripts/deploy-muxd.ps1 to what muxd.py/muxctl.py actually import.
"""

import ast
import os
import re
import tempfile
import unittest

MUXD_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO_ROOT = os.path.dirname(MUXD_DIR)
DEPLOY_SCRIPT = os.path.join(REPO_ROOT, "scripts", "deploy-muxd.ps1")
ENTRYPOINTS = ("muxd.py", "muxctl.py")


def read(path):
    with open(path, "r", encoding="utf-8") as fh:
        return fh.read()


def imported_names(path):
    """Every module name imported by `path`, as the deploy preflight computes them."""
    names = set()
    for node in ast.walk(ast.parse(read(path), filename=path)):
        if isinstance(node, ast.Import):
            names.update(a.name.split(".")[0] for a in node.names)
        elif isinstance(node, ast.ImportFrom):
            if node.level == 0 and node.module:
                names.add(node.module.split(".")[0])
    return names


def local_sibling_modules(muxd_dir=MUXD_DIR):
    """Imported names that resolve to a sibling .py in muxd/ — the ones a deploy must carry."""
    found = {}
    for entry in ENTRYPOINTS:
        for name in imported_names(os.path.join(muxd_dir, entry)):
            if os.path.exists(os.path.join(muxd_dir, name + ".py")):
                found.setdefault(name + ".py", set()).add(entry)
    return found


def deploy_file_list(script_path=DEPLOY_SCRIPT):
    """The '<name>.py' entries inside the `$files = @(...)` literal of deploy-muxd.ps1."""
    text = read(script_path)
    match = re.search(r"\$files\s*=\s*@\(([^)]*)\)", text)
    assert match, "no `$files = @(...)` literal found in %s" % script_path
    return set(re.findall(r"'([^']+)'", match.group(1)))


def missing_from_manifest(muxd_dir=MUXD_DIR, script_path=DEPLOY_SCRIPT):
    required = local_sibling_modules(muxd_dir)
    return sorted(set(required) - deploy_file_list(script_path))


class TestDeployManifest(unittest.TestCase):
    def test_every_local_sibling_module_is_deployed(self):
        deployed = deploy_file_list()
        required = local_sibling_modules()
        missing = missing_from_manifest()
        self.assertEqual(
            missing, [],
            "scripts/deploy-muxd.ps1 $files is missing local module(s) %s, imported by %s — "
            "deploying without them leaves the live runtime dying at startup on ModuleNotFoundError"
            % (", ".join(missing), ", ".join(sorted(n for m in missing for n in required[m]))),
        )

    def test_manifest_check_catches_a_newly_imported_local_module(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            with open(os.path.join(temp_dir, "muxd.py"), "w", encoding="utf-8") as fh:
                fh.write("import os\nimport newmod\n")
            with open(os.path.join(temp_dir, "muxctl.py"), "w", encoding="utf-8") as fh:
                fh.write("import sys\n")
            with open(os.path.join(temp_dir, "newmod.py"), "w", encoding="utf-8") as fh:
                fh.write("# synthetic local module\n")
            script_path = os.path.join(temp_dir, "deploy-muxd.ps1")
            with open(script_path, "w", encoding="utf-8") as fh:
                fh.write("$files = @('muxd.py','muxctl.py')   # runtime code only\n")

            missing = missing_from_manifest(temp_dir, script_path)
            required = local_sibling_modules(temp_dir)
            self.assertEqual(missing, ["newmod.py"])
            with self.assertRaises(self.failureException) as failure:
                self.assertEqual(
                    missing, [],
                    "scripts/deploy-muxd.ps1 $files is missing local module(s) %s, imported by %s - "
                    "deploying without them leaves the live runtime dying at startup on ModuleNotFoundError"
                    % (", ".join(missing), ", ".join(
                        sorted(n for n in missing for n in required[n])
                    )),
                )
            self.assertIn("newmod.py", str(failure.exception))
            self.assertIn("muxd.py", str(failure.exception))

    def test_manifest_check_ignores_stdlib_and_third_party_imports(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            with open(os.path.join(temp_dir, "muxd.py"), "w", encoding="utf-8") as fh:
                fh.write(
                    "import os\nimport json\nimport asyncio\n"
                    "from websockets import connect\n"
                )
            with open(os.path.join(temp_dir, "muxctl.py"), "w", encoding="utf-8") as fh:
                fh.write("import sys\n")
            script_path = os.path.join(temp_dir, "deploy-muxd.ps1")
            with open(script_path, "w", encoding="utf-8") as fh:
                fh.write("$files = @('muxd.py','muxctl.py')   # runtime code only\n")

            self.assertEqual(missing_from_manifest(temp_dir, script_path), [])

    def test_host_input_intent_is_covered(self):
        required = local_sibling_modules()
        self.assertIn(
            "host_input_intent.py", required,
            "muxd.py/muxctl.py no longer import host_input_intent — this test guards the wrong module",
        )
        self.assertIn(
            "host_input_intent.py", deploy_file_list(),
            "host_input_intent.py is imported by the runtime but absent from deploy-muxd.ps1 $files",
        )

    def test_deploy_script_has_missing_module_preflight(self):
        text = read(DEPLOY_SCRIPT)
        self.assertIn(
            "PREFLIGHT-LOCAL-MODULE-MANIFEST", text,
            "deploy-muxd.ps1 lost its missing-local-module preflight marker",
        )
        self.assertIsNone(
            re.search(r"(?m)^\s*(import muxd|from muxd import)\b", text),
            "the preflight must not import muxd — muxd.py runs module-level side effects",
        )
        restart = text.index("Start-ScheduledTask")
        preflight = text.index("PREFLIGHT-LOCAL-MODULE-MANIFEST")
        self.assertLess(
            preflight, restart,
            "the local-module preflight must run before Start-ScheduledTask",
        )
        refusal = re.search(
            r"missing local modules[\s\S]*?exit 1[\s\S]*?Start-ScheduledTask", text)
        self.assertIsNotNone(
            refusal,
            "deploy-muxd.ps1 must exit 1 on missing local modules before Start-ScheduledTask",
        )


if __name__ == "__main__":
    unittest.main()
