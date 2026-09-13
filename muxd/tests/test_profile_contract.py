import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

MUXD = Path(__file__).resolve().parents[1]


def profile_env(root, name="test"):
    runtime = root / "runtime"
    env_file = root / "profile.env"
    state = root / "state"
    guardian = root / "guardian"
    claude = root / "transcripts" / "claude"
    codex = root / "transcripts" / "codex"
    principal = root / "principal.dpapi"
    claims = root / "claims"
    token = root / "token.txt"
    root.mkdir(parents=True, exist_ok=True)
    token.write_text("disposable-token", encoding="utf-8")
    values = {
        "MUXD_STATE_ROOT": str(state),
        "MUXD_CONTROL_PORT": "17769",
        "MUXD_MUTEX_NAME": "Local\\CodexMuxdTest",
        "MUXD_PRINCIPAL_REGISTRY": str(principal),
        "MUXD_PRINCIPAL_INSTANCE_ID": "test-principal",
        "MUXD_HOST_IDENTITY": "test-host",
        "MUXD_RELAY_URLS": "ws://127.0.0.1:17770",
        "MUXD_TOKEN_SOURCE": "file:" + str(token),
        "MUXD_LAUNCH_CLAIM_ROOT": str(claims),
        "MUXD_TASK_NAME": "MuxdTest",
        "MUXD_RESTART_TASK_NAME": "MuxdTestRestart",
        "MUXD_WATCHDOG_TASK_NAME": "MuxdTestWatchdog",
        "DEFAULT_CWD": str(root / "workspace"),
        "MUXD_GUARDIAN_DIR": str(guardian),
        "MUXD_GUARDIAN_LOCK_PORT": "17690",
        "MUXD_TRANSCRIPT_CLAUDE_ROOT": str(claude),
        "MUXD_TRANSCRIPT_CODEX_ROOT": str(codex),
    }
    env_file.write_text("\n".join(f"{k}={v}" for k, v in values.items()), encoding="utf-8")
    env = os.environ.copy()
    env.update({"MUXD_PROFILE": name, "MUXD_RUNTIME_ROOT": str(runtime), "MUXD_ENV_FILE": str(env_file)})
    return env, values


class ProfileContractTests(unittest.TestCase):
    def test_task_names_reject_case_insensitive_collisions(self):
        with tempfile.TemporaryDirectory(prefix="muxd-task-case-") as temp:
            env, _ = profile_env(Path(temp))
            for overrides in (
                {"MUXD_TASK_NAME": "muxdsessionhost"},
                {"MUXD_RESTART_TASK_NAME": "MUXDTEST"},
                {"MUXD_WATCHDOG_TASK_NAME": "muxdtestrestart"},
            ):
                with self.subTest(overrides=overrides):
                    result = subprocess.run(
                        [sys.executable, str(MUXD / "profile.py"), "--json"],
                        env={**env, **overrides}, capture_output=True, text=True,
                    )
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn("task names", result.stderr)

    def test_nonproduction_rejects_production_and_credentialed_relay_urls(self):
        with tempfile.TemporaryDirectory(prefix="muxd-relay-fence-") as temp:
            env, _ = profile_env(Path(temp))
            for url in ("ws://127.0.0.1:7682", "wss://example.test/multiplex/host", "ws://user:secret@127.0.0.1:17770"):
                with self.subTest(url=url):
                    env["MUXD_RELAY_URLS"] = url
                    result = subprocess.run([sys.executable, str(MUXD / "profile.py"), "--json"], env=env, capture_output=True, text=True)
                    self.assertNotEqual(result.returncode, 0)


    def test_nonproduction_is_fail_closed_for_missing_explicit_values(self):
        env = os.environ.copy()
        env.update({"MUXD_PROFILE": "test", "MUXD_RUNTIME_ROOT": tempfile.gettempdir()})
        env.pop("MUXD_ENV_FILE", None)
        result = subprocess.run(
            [sys.executable, str(MUXD / "profile.py"), "--json"], env=env,
            capture_output=True, text=True,
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("MUXD_ENV_FILE", result.stderr)

    def test_two_disposable_profiles_are_distinct_and_complete(self):
        with tempfile.TemporaryDirectory(prefix="muxd-profiles-") as temp:
            root = Path(temp)
            env_a, values_a = profile_env(root / "a", "alpha")
            env_b, values_b = profile_env(root / "b", "beta")
            for env, values, name in ((env_a, values_a, "alpha"), (env_b, values_b, "beta")):
                result = subprocess.run([sys.executable, str(MUXD / "profile.py"), "--json"], env=env, capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                data = json.loads(result.stdout)
                self.assertEqual(name, data["Name"])
                self.assertEqual(values["MUXD_CONTROL_PORT"], str(data["ControlPort"]))
                self.assertEqual(values["MUXD_PRINCIPAL_INSTANCE_ID"], data["PrincipalInstanceId"])
                self.assertNotIn("MuxdSessionHost", json.dumps(data))
            self.assertNotEqual(env_a["MUXD_RUNTIME_ROOT"], env_b["MUXD_RUNTIME_ROOT"])

    def test_production_defaults_remain_historical(self):
        env = os.environ.copy()
        env["MUXD_PROFILE"] = "production"
        env.pop("MUXD_ENV_FILE", None)
        result = subprocess.run([sys.executable, str(MUXD / "profile.py"), "--json"], env=env, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        data = json.loads(result.stdout)
        self.assertEqual(7699, data["ControlPort"])
        self.assertEqual(r"Local\CodexMuxdSessionHost", data["MutexName"])
        self.assertEqual("MuxdSessionHost", data["TaskName"])

    def test_profile_process_matching_requires_runtime_and_identity(self):
        with mock.patch.dict(os.environ, {"MUXD_PROFILE": "production"}, clear=False):
            import importlib.util
            spec = importlib.util.spec_from_file_location("muxd_profile_test", MUXD / "profile.py")
            module = importlib.util.module_from_spec(spec)
            sys.modules[spec.name] = module
            spec.loader.exec_module(module)

            self.assertFalse(module._same_path("/a", "/b"))
            self.assertFalse(module.profile_matches_command("python muxd.py"))
            self.assertFalse(module.profile_matches_command(
                "python /tmp/muxd.py --profile production", module.PROFILE))
            self.assertTrue(module.profile_matches_command(
                f"python {module.PROFILE.runtime_root}/muxd.py --profile production", module.PROFILE))
            self.assertTrue(module.profile_matches_command(
                f'"C:/Program Files/Python/python.exe" "{module.PROFILE.runtime_root}/muxd.py" --profile "production"'))
            for command in (
                f'python "{module.PROFILE.runtime_root}-other/muxd.py" --profile production',
                f'python unrelated.py --path "{module.PROFILE.runtime_root}" --profile production',
                f'python "{module.PROFILE.runtime_root}/muxd.py" --profile production --profile test',
                f'python "{module.PROFILE.runtime_root}/muxd.py" --profile production-extra',
            ):
                with self.subTest(command=command):
                    self.assertFalse(module.profile_matches_command(command))

    def test_process_filter_cli_selects_only_exact_profile_entrypoint(self):
        with tempfile.TemporaryDirectory(prefix="muxd-pid-filter-") as temp:
            env, _ = profile_env(Path(temp))
            runtime = env["MUXD_RUNTIME_ROOT"]
            processes = [
                {"ProcessId": 101, "CommandLine": f'python "{runtime}/muxd.py" --profile test'},
                {"ProcessId": 102, "CommandLine": f'python "{runtime}-other/muxd.py" --profile test'},
                {"ProcessId": 103, "CommandLine": f'python "{runtime}/muxd.py" --profile test-other'},
                {"ProcessId": 104, "CommandLine": None},
            ]
            result = subprocess.run([sys.executable, str(MUXD / "profile.py"), "--matching-pids"],
                                    input=json.dumps(processes), env=env, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(json.loads(result.stdout), [101])

    @unittest.skipUnless(os.name == "nt", "requires Windows PowerShell")
    def test_operations_process_filter_runs_actual_powershell_pipeline(self):
        script = r'''
$ErrorActionPreference = 'Stop'
$python = $env:TEST_PYTHON
$root = $env:TEST_MUXD
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($env:TEST_OPS_SCRIPT, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'script parse failed' }
$function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-MuxdProcesses' }, $true)
Invoke-Expression $function.Extent.Text
function Get-CimInstance {
    [pscustomobject]@{ ProcessId=101; CommandLine=('python "' + $env:MUXD_RUNTIME_ROOT + '/muxd.py" --profile test') }
    [pscustomobject]@{ ProcessId=102; CommandLine=('python "' + $env:MUXD_RUNTIME_ROOT + '-other/muxd.py" --profile test') }
    [pscustomobject]@{ ProcessId=103; CommandLine=$null }
}
$matches = @(Get-MuxdProcesses)
if ($matches.Count -ne 1 -or $matches[0].ProcessId -ne 101) { throw 'incorrect process custody selection' }
function Get-CimInstance { }
if (@(Get-MuxdProcesses).Count -ne 0) { throw 'empty process list must stay empty' }
function Get-CimInstance {
    [pscustomobject]@{ ProcessId=101; CommandLine=('python "' + $env:MUXD_RUNTIME_ROOT + '/muxd.py" --profile test') }
}
$single = @(Get-MuxdProcesses)
if ($single.Count -ne 1 -or $single[0].ProcessId -ne 101) { throw 'single process input failed' }
Write-Output 'exact ownership selected'
'''
        with tempfile.TemporaryDirectory(prefix="muxd-ps-filter-") as temp:
            env, _ = profile_env(Path(temp) / "runtime-é-测试")
            env.update(TEST_PYTHON=sys.executable, TEST_MUXD=str(MUXD))
            for name in ("restart_muxd.ps1", "watch_muxd.ps1"):
                with self.subTest(script=name):
                    env["TEST_OPS_SCRIPT"] = str(MUXD / "ops" / name)
                    result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
                                            env=env, capture_output=True, text=True, timeout=20)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertIn("exact ownership selected", result.stdout)

    @unittest.skipUnless(os.name == "nt", "requires Windows PowerShell")
    def test_ops_profile_admission_rejects_failure_and_wrong_identity(self):
        script = r'''
$ErrorActionPreference = 'Stop'
$Profile = 'test'
$source = [IO.File]::ReadAllText($env:TEST_OPS_SCRIPT)
$start = $source.IndexOf('if ($LASTEXITCODE -ne 0)')
$end = $source.IndexOf('$profileJson.Name -cne $Profile)', $start)
$end = $source.IndexOf('}', $end) + 1
if ($start -lt 0 -or $end -le $start) { throw 'admission guard missing' }
$guard = $source.Substring($start, $end - $start)
foreach ($case in @(
    @{ code=1; json='{"Name":"test"}'; accepted=$false },
    @{ code=0; json='{"Name":"production"}'; accepted=$false },
    @{ code=0; json='null'; accepted=$false },
    @{ code=0; json='{"Name":"test"}'; accepted=$true }
)) {
    $LASTEXITCODE = $case.code
    $profileRaw = $case.json
    $accepted = $false
    try { Invoke-Expression $guard; $accepted = $true } catch { }
    if ($accepted -ne $case.accepted) { throw 'incorrect profile admission' }
}
Write-Output 'profile admission cases passed'
'''
        for name in ("install_muxd_tasks.ps1", "restart_muxd.ps1", "watch_muxd.ps1"):
            with self.subTest(script=name):
                env = {**os.environ, "TEST_OPS_SCRIPT": str(MUXD / "ops" / name)}
                result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
                                        env=env, capture_output=True, text=True, timeout=20)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn("profile admission cases passed", result.stdout)

    @unittest.skipUnless(os.name == "nt", "requires Windows PowerShell")
    def test_launcher_quotes_space_and_apostrophe_paths(self):
        script = r'''
$ErrorActionPreference = 'Stop'
$python = "C:\Program Files\Python\pythonw.exe"
$entry = "C:\Users\O'Brien\mux runtime\muxd.py"
$source = [IO.File]::ReadAllText($env:TEST_OPS_SCRIPT)
$start = $source.IndexOf('    $quotedPython =')
$end = $source.IndexOf('    $keysafe =', $start)
Invoke-Expression $source.Substring($start, $end - $start)
function Start-Process {
    param($FilePath, $ArgumentList, [switch]$Wait)
    if ($FilePath -ne $python -or -not $Wait) { throw 'wrong executable or wait policy' }
    if ($ArgumentList -ne ('"' + $entry + '" --profile production')) { throw 'script path was not preserved' }
    Write-Output 'quoted launch preserved'
}
Invoke-Expression $child
'''
        env = {**os.environ, "TEST_OPS_SCRIPT": str(MUXD / "ops" / "launch_muxd.ps1")}
        result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
                                env=env, capture_output=True, text=True, timeout=20)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("quoted launch preserved", result.stdout)

    @unittest.skipUnless(os.name == "nt", "requires Windows PowerShell")
    def test_restart_preflight_preserves_runtime_path_and_session_rows(self):
        script = r'''
$ErrorActionPreference = 'Stop'
$python = $env:TEST_PYTHON
$root = $env:TEST_RUNTIME
$env:MUXD_PREFLIGHT_ROOT = 'previous-root'
$env:MUXCTL_AUTOSTART = 'previous-autostart'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($env:TEST_OPS_SCRIPT, [ref]$tokens, [ref]$errors)
$function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-HostedSessions' }, $true)
Invoke-Expression $function.Extent.Text
$rows = @(Get-HostedSessions)
if ($rows.Count -ne 2 -or $rows[0].name -ne 'active' -or $rows[1].name -ne 'idle') { throw 'session rows changed' }
$active = @($rows | Where-Object { @($_.alive) -contains $true })
if ($active.Count -ne 1) { throw 'active session preflight detection failed' }
if ($env:MUXD_PREFLIGHT_ROOT -ne 'previous-root' -or $env:MUXCTL_AUTOSTART -ne 'previous-autostart') { throw 'environment not restored' }
Write-Output 'preflight verified'
'''
        with tempfile.TemporaryDirectory(prefix="muxd-preflight-") as temp:
            root = Path(temp) / "O'Brien runtime"
            root.mkdir()
            (root / "muxctl.py").write_text(
                "import os\nasync def fetch_info():\n    return {}\nasync def request_json(payload):\n    assert payload == {'t': 'ls'}\n    assert os.environ['MUXCTL_AUTOSTART'] == '0'\n    return {'list': [{'name': 'active', 'alive': True}, {'name': 'idle', 'alive': False}]}\n",
                encoding="utf-8")
            env = {**os.environ, "TEST_RUNTIME": str(root), "TEST_PYTHON": sys.executable,
                   "TEST_OPS_SCRIPT": str(MUXD / "ops" / "restart_muxd.ps1")}
            result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
                                    env=env, capture_output=True, text=True, timeout=20)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("preflight verified", result.stdout)
            for index, response in enumerate(({}, {'list': None}, {'list': [{'name': 'unknown'}]},
                                               {'list': [{'name': 'unknown', 'alive': 'false'}]})):
                with self.subTest(response=response):
                    candidate = root / str(index)
                    candidate.mkdir()
                    (candidate / "muxctl.py").write_text(
                        f"async def fetch_info():\n    return {{}}\nasync def request_json(payload):\n    return {response!r}\n", encoding="utf-8")
                    env["TEST_RUNTIME"] = str(candidate)
                    rejected = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
                                              env=env, capture_output=True, text=True, timeout=20)
                    self.assertNotEqual(rejected.returncode, 0)
                    self.assertIn("muxd preflight", rejected.stderr)

    def test_muxctl_autostart_targets_only_selected_task(self):
        import ast
        import types
        tree = ast.parse((MUXD / "muxctl.py").read_text(encoding="utf-8"))
        selected = ast.Module(body=[node for node in tree.body if isinstance(node, ast.FunctionDef)
                                   and node.name in {"_profile_args", "ensure_muxd_started"}], type_ignores=[])
        run = mock.Mock(side_effect=[types.SimpleNamespace(returncode=0, stdout='Ready', stderr=''),
                                     types.SimpleNamespace(returncode=0, stdout='', stderr='')])
        pause = mock.Mock()
        scope = {"os": types.SimpleNamespace(name="nt"), "AUTOSTART": True,
                 "TASK_NAME": "MuxdIsolatedAutostart", "_run_quiet": run,
                 "sys": types.SimpleNamespace(stderr=mock.Mock()), "time": types.SimpleNamespace(sleep=pause)}
        exec(compile(selected, "muxctl-autostart", "exec"), scope)
        self.assertTrue(scope["ensure_muxd_started"]())
        self.assertEqual(run.call_args_list, [
            mock.call(["schtasks", "/Query", "/TN", "MuxdIsolatedAutostart", "/FO", "CSV", "/NH"]),
            mock.call(["schtasks", "/Run", "/TN", "MuxdIsolatedAutostart"])])
        run.reset_mock()
        scope["AUTOSTART"] = False
        self.assertFalse(scope["ensure_muxd_started"]())
        run.assert_not_called()
        scope["AUTOSTART"] = True
        run.side_effect = [types.SimpleNamespace(returncode=0, stdout='Running', stderr='')]
        self.assertFalse(scope["ensure_muxd_started"]())
        self.assertEqual(run.call_count, 1)

    def test_muxctl_info_requires_selected_nonproduction_identity(self):
        import ast
        import asyncio
        import types
        tree = ast.parse((MUXD / "muxctl.py").read_text(encoding="utf-8"))
        selected = ast.Module(body=[node for node in tree.body if isinstance(node, ast.AsyncFunctionDef)
                                   and node.name == "fetch_info"], type_ignores=[])
        request = mock.AsyncMock()
        scope = {"PROFILE": types.SimpleNamespace(name="test", principal_instance_id="expected"),
                 "request_json": request}
        exec(compile(selected, "muxctl-health", "exec"), scope)
        for response in ({"t": "info"}, {"t": "info", "profile": "production", "instanceId": "expected"},
                         {"t": "info", "profile": "test", "instanceId": "other"}):
            with self.subTest(response=response):
                request.return_value = response
                with self.assertRaisesRegex(RuntimeError, "identity"):
                    asyncio.run(scope["fetch_info"]())
        request.return_value = {"t": "info", "profile": "test", "instanceId": "expected"}
        self.assertEqual(asyncio.run(scope["fetch_info"]()), request.return_value)

    def test_path_overlap_detection_is_symmetric(self):
        with mock.patch.dict(os.environ, {"MUXD_PROFILE": "production"}, clear=False):
            import importlib.util
            spec = importlib.util.spec_from_file_location("muxd_profile_overlap_test", MUXD / "profile.py")
            module = importlib.util.module_from_spec(spec)
            sys.modules[spec.name] = module
            spec.loader.exec_module(module)

            parent = str(Path(tempfile.gettempdir()) / "muxd-overlap")
            child = str(Path(parent) / "child")
            sibling = str(Path(tempfile.gettempdir()) / "muxd-overlap-sibling")
            self.assertTrue(module._overlaps_path(parent, child))
            self.assertTrue(module._overlaps_path(child, parent))
            self.assertTrue(module._overlaps_path(parent, parent))
            self.assertFalse(module._overlaps_path(parent, sibling))

    def test_nonproduction_rejects_both_nesting_directions_for_production_custody(self):
        with tempfile.TemporaryDirectory(prefix="muxd-overlap-profile-") as temp:
            root = Path(temp)
            env, _ = profile_env(root)
            production_runtime = Path.home() / "muxd"
            cases = (
                ("MUXD_RUNTIME_ROOT", production_runtime.parent),
                ("MUXD_RUNTIME_ROOT", production_runtime / "test"),
                ("MUXD_STATE_ROOT", production_runtime.parent),
                ("MUXD_STATE_ROOT", production_runtime / "test-state"),
                ("MUXD_PRINCIPAL_REGISTRY", production_runtime / "test-principal.dpapi"),
            )
            for key, path in cases:
                with self.subTest(key=key, path=path):
                    candidate = env.copy()
                    if key == "MUXD_RUNTIME_ROOT":
                        candidate[key] = str(path)
                    else:
                        text = Path(env["MUXD_ENV_FILE"]).read_text(encoding="utf-8")
                        lines = [
                            f"{key}={path}" if line.startswith(key + "=") else line
                            for line in text.splitlines()
                        ]
                        profile_file = root / (key.lower() + ".env")
                        profile_file.write_text("\n".join(lines), encoding="utf-8")
                        candidate["MUXD_ENV_FILE"] = str(profile_file)
                    result = subprocess.run(
                        [sys.executable, str(MUXD / "profile.py"), "--json"],
                        env=candidate, capture_output=True, text=True,
                    )
                    self.assertNotEqual(result.returncode, 0, result.stdout)
                    self.assertIn("overlaps", result.stderr)

    def test_nonproduction_requires_explicit_default_working_directory(self):
        with tempfile.TemporaryDirectory(prefix="muxd-cwd-required-") as temp:
            root = Path(temp)
            env, _ = profile_env(root)
            profile_file = Path(env["MUXD_ENV_FILE"])
            profile_file.write_text(
                "\n".join(
                    line for line in profile_file.read_text(encoding="utf-8").splitlines()
                    if not line.startswith("DEFAULT_CWD=")
                ),
                encoding="utf-8",
            )
            result = subprocess.run(
                [sys.executable, str(MUXD / "profile.py"), "--json"],
                env=env, capture_output=True, text=True,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("DEFAULT_CWD", result.stderr)

    def test_nonproduction_default_working_directory_cannot_overlap_production_runtime(self):
        with tempfile.TemporaryDirectory(prefix="muxd-cwd-overlap-") as temp:
            root = Path(temp)
            env, _ = profile_env(root)
            profile_file = Path(env["MUXD_ENV_FILE"])
            lines = [
                f"DEFAULT_CWD={Path.home() / 'muxd' / 'workspace'}"
                if line.startswith("DEFAULT_CWD=") else line
                for line in profile_file.read_text(encoding="utf-8").splitlines()
            ]
            profile_file.write_text("\n".join(lines), encoding="utf-8")
            result = subprocess.run(
                [sys.executable, str(MUXD / "profile.py"), "--json"],
                env=env, capture_output=True, text=True,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("DEFAULT_CWD overlaps", result.stderr)

    def test_nonproduction_principal_instance_identity_cannot_match_production(self):
        with tempfile.TemporaryDirectory(prefix="muxd-principal-instance-") as temp:
            root = Path(temp)
            env, _ = profile_env(root)
            env["COMPUTERNAME"] = "test-machine"
            profile_file = Path(env["MUXD_ENV_FILE"])
            lines = [
                "MUXD_PRINCIPAL_INSTANCE_ID=test-machine"
                if line.startswith("MUXD_PRINCIPAL_INSTANCE_ID=") else line
                for line in profile_file.read_text(encoding="utf-8").splitlines()
            ]
            profile_file.write_text("\n".join(lines), encoding="utf-8")
            result = subprocess.run(
                [sys.executable, str(MUXD / "profile.py"), "--json"],
                env=env, capture_output=True, text=True,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("MUXD_PRINCIPAL_INSTANCE_ID overlaps", result.stderr)

    def test_guardian_disable_is_explicit(self):
        with tempfile.TemporaryDirectory(prefix="muxd-disabled-") as temp:
            root = Path(temp)
            env, values = profile_env(root)
            text = Path(env["MUXD_ENV_FILE"]).read_text(encoding="utf-8")
            Path(env["MUXD_ENV_FILE"]).write_text(text + "\nMUXD_GUARDIAN_DISABLED=true\n", encoding="utf-8")
            result = subprocess.run([sys.executable, str(MUXD / "profile.py"), "--json"], env=env, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertFalse(json.loads(result.stdout)["GuardianEnabled"])

    def test_muxrun_consumes_matching_profile_switch_before_argument_parsing(self):
        source = (MUXD / "muxrun.py").read_text(encoding="utf-8")
        self.assertIn("p.parse_args(_consume_profile_arg(sys.argv[1:]))", source)
        self.assertNotIn('_profile_task_args(["schtasks"', source)

    def test_operations_scripts_use_profile_muxd_task_not_watchdog_task(self):
        installer = (MUXD / "ops" / "install_muxd_tasks.ps1").read_text(encoding="utf-8")
        restart = (MUXD / "ops" / "restart_muxd.ps1").read_text(encoding="utf-8")
        watchdog = (MUXD / "ops" / "watch_muxd.ps1").read_text(encoding="utf-8")
        self.assertIn("$hostTask = $profileJson.TaskName", installer)
        self.assertIn("-TaskName $hostTask", installer)
        self.assertIn("$taskName = $profileJson.TaskName", restart)
        self.assertIn("$taskName = $profileJson.TaskName", watchdog)
        self.assertNotIn("$taskName = $profileJson.WatchdogTaskName", watchdog)

    def test_profile_scripts_export_identity_before_loading_profile(self):
        for name in ("launch_muxd.ps1", "install_muxd_tasks.ps1", "restart_muxd.ps1", "watch_muxd.ps1"):
            source = (MUXD / "ops" / name).read_text(encoding="utf-8")
            self.assertLess(source.index("$env:MUXD_PROFILE = $Profile"), source.index("profile.py") if "profile.py" in source else source.index("$entry"))
            if "profile.py" in source:
                self.assertLess(source.index("$env:MUXD_ENV_FILE"), source.index("profile.py"))


if __name__ == "__main__":
    unittest.main()
