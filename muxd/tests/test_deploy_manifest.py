"""The deploy manifest must carry every local module the runtime entrypoints import.

py_compile (the old preflight) never resolves imports, so muxd.py can compile perfectly in the live
runtime dir and still die at startup on a sibling module nobody copied. These tests read the
sources as text — no muxd import, muxd.py has module-level side effects — and hold
scripts/deploy-muxd.ps1 to what muxd.py/muxctl.py actually import.
"""

import ast
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

MUXD_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO_ROOT = os.path.dirname(MUXD_DIR)
DEPLOY_SCRIPT = os.path.join(REPO_ROOT, "scripts", "deploy-muxd.ps1")
ENTRYPOINTS = ("muxd.py", "muxctl.py")


def read(path):
    with open(path, "r", encoding="utf-8") as fh:
        return fh.read()


def extract_preflight_source(script_path=DEPLOY_SCRIPT):
    """Return the Python source embedded in deploy-muxd.ps1's here-string."""
    lines = read(script_path).splitlines()
    opener = next(
        (index for index, line in enumerate(lines) if re.match(r"\s*\$preflight\s*=\s*@'\s*$", line)),
        None,
    )
    assert opener is not None, "deploy-muxd.ps1 is missing the preflight here-string opener"
    closer = next(
        (index for index in range(opener + 1, len(lines)) if lines[index].strip() == "'@"),
        None,
    )
    assert closer is not None, "deploy-muxd.ps1 is missing the preflight here-string closer"
    return "\n".join(lines[opener + 1:closer]) + "\n"


def run_preflight(src, dst):
    """Run the extracted preflight through stdin, matching deploy-muxd.ps1."""
    result = subprocess.run(
        [sys.executable, "-", src, dst],
        input=extract_preflight_source(),
        capture_output=True,
        text=True,
    )
    return result.returncode, result.stdout + result.stderr


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
    @unittest.skipIf(os.name != "nt", "PowerShell deploy preflight requires Windows")
    def test_enforce_deploy_refuses_an_empty_principal_registry_before_copy(self):
        with tempfile.TemporaryDirectory() as profile, tempfile.TemporaryDirectory() as runtime:
            ops = os.path.join(runtime, "ops")
            os.mkdir(ops)
            with open(os.path.join(ops, "restart_muxd.ps1"), "w", encoding="utf-8") as fh:
                fh.write("param([switch]$CheckOnly)\nexit 0\n")
            env = os.environ.copy()
            env["HOME"] = profile
            env["USERPROFILE"] = profile
            result = subprocess.run(
                [
                    "powershell.exe",
                    "-NoProfile",
                    "-ExecutionPolicy", "Bypass",
                    "-File", DEPLOY_SCRIPT,
                    "-AuthzMode", "enforce",
                    "-RuntimeDir", runtime,
                ],
                cwd=REPO_ROOT,
                env=env,
                capture_output=True,
                text=True,
                timeout=30,
            )
        self.assertNotEqual(result.returncode, 0)
        self.assertRegex(
            result.stdout + result.stderr,
            # Windows PowerShell may wrap the error stream between "requires" and "a".
            r"enforce deployment requires\s+a\s+DPAPI-protected provisioned principal",
        )

    def test_preflight_refuses_when_a_local_module_is_missing_from_dst(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            src = os.path.join(temp_dir, "src")
            dst = os.path.join(temp_dir, "dst")
            os.mkdir(src)
            os.mkdir(dst)
            for name, content in (
                ("muxd.py", "import os\nimport sidecar\n"),
                ("muxctl.py", "import sys\n"),
                ("sidecar.py", "# synthetic local module\n"),
            ):
                with open(os.path.join(src, name), "w", encoding="utf-8") as fh:
                    fh.write(content)
            for name in ("muxd.py", "muxctl.py"):
                shutil.copyfile(os.path.join(src, name), os.path.join(dst, name))

            returncode, output = run_preflight(src, dst)

            self.assertNotEqual(returncode, 0)
            self.assertIn("sidecar.py", output)

    def test_preflight_passes_when_dst_is_complete(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            src = os.path.join(temp_dir, "src")
            dst = os.path.join(temp_dir, "dst")
            os.mkdir(src)
            os.mkdir(dst)
            for name, content in (
                ("muxd.py", "import os\nimport sidecar\n"),
                ("muxctl.py", "import sys\n"),
                ("sidecar.py", "# synthetic local module\n"),
            ):
                with open(os.path.join(src, name), "w", encoding="utf-8") as fh:
                    fh.write(content)
                shutil.copyfile(os.path.join(src, name), os.path.join(dst, name))

            returncode, _ = run_preflight(src, dst)

            self.assertEqual(returncode, 0)

    def test_preflight_ignores_stdlib_imports(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            src = os.path.join(temp_dir, "src")
            dst = os.path.join(temp_dir, "dst")
            os.mkdir(src)
            os.mkdir(dst)
            for name, content in (
                ("muxd.py", "import os\nimport json\nimport asyncio\n"),
                ("muxctl.py", "import sys\n"),
            ):
                with open(os.path.join(src, name), "w", encoding="utf-8") as fh:
                    fh.write(content)
                shutil.copyfile(os.path.join(src, name), os.path.join(dst, name))

            returncode, _ = run_preflight(src, dst)

            self.assertEqual(returncode, 0)

    def test_preflight_refuses_when_an_entrypoint_is_missing_from_dst(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            src = os.path.join(temp_dir, "src")
            dst = os.path.join(temp_dir, "dst")
            os.mkdir(src)
            os.mkdir(dst)
            for name, content in (
                ("muxd.py", "import os\n"),
                ("muxctl.py", "import sys\n"),
            ):
                with open(os.path.join(src, name), "w", encoding="utf-8") as fh:
                    fh.write(content)
            shutil.copyfile(os.path.join(src, "muxd.py"), os.path.join(dst, "muxd.py"))

            returncode, output = run_preflight(src, dst)

            self.assertNotEqual(returncode, 0)
            self.assertIn("muxctl.py", output)

    def test_deploy_script_propagates_preflight_failure(self):
        text = read(DEPLOY_SCRIPT)
        refusal = re.search(
            r"\$preflight\s*\|\s*python\s+-\s+\$src\s+\$stage\s*\r?\n"
            r"\s*if\s*\(\$LASTEXITCODE\s*-ne\s*0\)[\s\S]*?\bthrow\b",
            text,
        )
        self.assertIsNotNone(
            refusal,
            "the staged preflight invocation must be followed by a LASTEXITCODE check that throws",
        )
        self.assertLess(
            refusal.end(),
            text.index("$liveChanged = $true"),
            "preflight refusal must appear before any live runtime mutation",
        )

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
        restart = text.index("Invoke-RestartAndVerify", text.index("$restartRequested = $true"))
        preflight = text.index("PREFLIGHT-LOCAL-MODULE-MANIFEST")
        self.assertLess(
            preflight, restart,
            "the local-module preflight must run before the restart",
        )
        refusal = re.search(
            r"missing local modules[\s\S]*?throw[\s\S]*?\$liveChanged = \$true", text)
        self.assertIsNotNone(
            refusal,
            "deploy-muxd.ps1 must reject missing local modules before changing the live runtime",
        )

    def test_deploy_stages_before_live_copy_and_restores_on_failure(self):
        text = read(DEPLOY_SCRIPT)
        self.assertRegex(text, r"(?m)^\$ErrorActionPreference\s*=\s*['\"]Stop['\"]")
        stage_compile = text.index("& python -m py_compile $stagedFile")
        live_copy = text.index(
            "Copy-Item -LiteralPath (Join-Path $stage $f) -Destination $liveFile -Force"
        )
        self.assertLess(stage_compile, live_copy)
        self.assertIn("function Restore-Runtime", text)
        self.assertRegex(
            text,
            r"catch\s*\{[\s\S]*?Restore-Runtime[\s\S]*?throw \$deploymentError",
        )

    def test_deploy_persists_authz_mode_and_verifies_restart_health(self):
        text = read(DEPLOY_SCRIPT)
        self.assertNotIn(
            "[string]$AuthzMode = $(if ($env:MUX_AUTHZ_MODE)",
            text,
            "an omitted mode must preserve the current runtime setting instead of defaulting to audit",
        )
        self.assertIn(
            "Get-MuxdEnvValue -Path $runtimeEnv -Name 'MUX_AUTHZ_MODE'",
            text,
        )
        self.assertIn(
            "Set-MuxdEnvValue -Path $runtimeEnv -Name 'MUX_AUTHZ_MODE' -Value $AuthzMode",
            text,
        )
        self.assertIn("Get-ScheduledTaskInfo -TaskName $restartTask", text)
        self.assertIn("& python (Join-Path $dst 'muxctl.py') status", text)

    def test_enforce_requires_the_trusted_browser_signer_before_copy(self):
        text = read(DEPLOY_SCRIPT)
        signer_guard = text.index(
            "enforce deployment requires the trusted browser principal-auth.js signer"
        )
        live_copy = text.index(
            "Copy-Item -LiteralPath (Join-Path $stage $f) -Destination $liveFile -Force"
        )
        self.assertLess(signer_guard, live_copy)
        self.assertIn("Join-Path $relay 'public\\principal-auth.js'", text)
        self.assertIn("principal-auth\\.js", text)
        self.assertIn("<script\\b", text)

    def test_rollback_uses_recovery_restart_that_can_start_an_absent_muxd(self):
        deploy = read(DEPLOY_SCRIPT)
        restart = read(os.path.join(MUXD_DIR, "ops", "restart_muxd.ps1"))
        self.assertRegex(
            deploy,
            r"Restore-Runtime[\s\S]*?Invoke-RestartAndVerify -Recovery",
        )
        self.assertIn("'ops\\restart_muxd.ps1'", deploy)
        self.assertRegex(
            deploy,
            r"foreach \(\$f in \$files\)[\s\S]*?Copy-Item -LiteralPath "
            r"\(Join-Path \$stage \$f\) -Destination \$liveFile -Force",
        )
        self.assertIn("[switch]$Recovery", restart)
        self.assertIn("[string]$RecoveryToken", restart)
        self.assertIn('$fence -ne "recovery:$RecoveryToken"', restart)
        self.assertIn("recovery restart token expired", restart)
        self.assertRegex(
            restart,
            r"if \(-not \$skipPreflight\)\s*\{\s*\$sessions = @\(Get-HostedSessions\)",
        )
        fence_create = deploy.index('"recovery:$recoveryToken`n"')
        restart_request = deploy.index("$restartRequested = $true")
        self.assertLess(fence_create, restart_request)
        self.assertRegex(
            deploy,
            r"Invoke-RestartAndVerify -Recovery[\s\S]*?"
            r"Remove-Item -LiteralPath \$deployFence -Force",
        )


if __name__ == "__main__":
    unittest.main()
