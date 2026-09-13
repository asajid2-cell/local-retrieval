const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const launcher = path.resolve(__dirname, '../../app/scripts/start-remote-profile.ps1');
const tunnel = path.resolve(__dirname, '../../app/scripts/remote-tunnel.ps1');
test('tunnel rejects unsafe targets and ports before invoking SSH', { skip: process.platform !== 'win32' }, () => {
  for (const overrides of [
    { CLR_REMOTE_PORT: '18765; bad' },
    { CLR_REMOTE_TUNNEL_TARGET: '-oProxyCommand=bad' },
    { MUXD_PROFILE: 'test', CLR_REMOTE_PORT: '8765' },
    { CLR_REMOTE_TUNNEL_TARGET: '' },
    { CLR_REMOTE_PORT: '7682' },
    { CLR_REMOTE_PORT: '7699' },
    { MUXD_CONTROL_PORT: '18765' },
    { CLR_RELAY_PORT: '18765' },
    { MUXD_PROFILE: 'test', MUXD_STATE_ROOT: '' },
    { MUXD_PRINCIPAL_INSTANCE_ID: '' },
    { MUXD_PRINCIPAL_INSTANCE_ID: "bad'identity" },
  ]) {
    const result = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-File', tunnel], {
      encoding: 'utf8', timeout: 10000,
      env: { ...process.env, MUXD_PROFILE: 'test', MUXD_STATE_ROOT: os.tmpdir(), MUXD_PRINCIPAL_INSTANCE_ID: 'tunnel-test',
        CLR_REMOTE_PORT: '18765', CLR_REMOTE_TUNNEL_TARGET: 'test-host', ...overrides },
    });
    assert.equal(result.error, undefined, 'must reject immediately, not wait for SSH');
    assert.notEqual(result.status, 0);
  }
});
test('reverse tunnel launch bounds SSH connection attempts', () => {
  const source = fs.readFileSync(tunnel, 'utf8');
  const launch = source.slice(source.indexOf('    & ssh.exe `'));
  assert.match(launch, /-o ConnectTimeout=8/);
  assert.match(launch, /-o ConnectionAttempts=1/);
  assert.match(launch, /-o ExitOnForwardFailure=yes/);
});

test('tunnel health requires exact test service identity', { skip: process.platform !== 'win32' }, () => {
  const script = `
    $ErrorActionPreference = 'Stop'
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($env:TEST_TUNNEL_SCRIPT, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'tunnel parse failed' }
    $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-LocalServer' }, $true)
    Invoke-Expression $function.Extent.Text
    function Invoke-RestMethod { param($Uri, $TimeoutSec) return $script:health }
    $port = '18765'
    $env:MUXD_PROFILE = 'test'
    $env:MUXD_PRINCIPAL_INSTANCE_ID = 'expected-instance'
    foreach ($case in @(
      @{ ok=$true; service='codex-local-retrieval'; profile='test'; principalInstanceId='expected-instance'; expected=$true },
      @{ ok=$true; service='codex-local-retrieval'; profile='production'; principalInstanceId='expected-instance'; expected=$false },
      @{ ok=$true; service='codex-local-retrieval'; profile='test'; principalInstanceId='another-instance'; expected=$false },
      @{ ok=$true; service='other'; profile='test'; principalInstanceId='expected-instance'; expected=$false },
      @{ ok=$false; service='codex-local-retrieval'; profile='test'; principalInstanceId='expected-instance'; expected=$false },
      @{ ok=$true; expected=$false }
    )) {
      $script:health = [pscustomobject]$case
      if ((Test-LocalServer) -ne $case.expected) { throw 'incorrect health identity decision' }
    }
    Write-Output 'six identity cases passed'
  `;
  const result = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
    encoding: 'utf8', timeout: 10000, env: { ...process.env, TEST_TUNNEL_SCRIPT: tunnel },
  });
  assert.equal(result.error, undefined);
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /six identity cases passed/);
});
test('remote tunnel health predicate rejects foreign identities', { skip: process.platform !== 'win32' }, () => {
  const script = `
    $ErrorActionPreference = 'Stop'
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($env:TEST_TUNNEL_SCRIPT, [ref]$tokens, [ref]$errors)
    $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-RemoteTunnel' }, $true)
    Invoke-Expression $function.Extent.Text
    function Invoke-RemoteCheck { param($remoteCommand) Write-Output $remoteCommand }
    $port = '18765'
    $env:MUXD_PROFILE = 'test'
    $env:MUXD_PRINCIPAL_INSTANCE_ID = 'expected-instance'
    Test-RemoteTunnel
  `;
  const built = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
    encoding: 'utf8', timeout: 10000, env: { ...process.env, TEST_TUNNEL_SCRIPT: tunnel },
  });
  assert.equal(built.status, 0, built.stderr);
  const match = built.stdout.trim().match(/\| python3 -c "([^"]+)"$/);
  assert.ok(match, 'actual remote command must contain a Python identity predicate');
  const valid = { ok: true, service: 'codex-local-retrieval', profile: 'test', principalInstanceId: 'expected-instance' };
  for (const [overrides, expected] of [
    [{}, 0], [{ ok: false }, 1], [{ service: 'other' }, 1],
    [{ profile: 'production' }, 1], [{ principalInstanceId: 'other' }, 1],
  ]) {
    const checked = spawnSync('python', ['-c', match[1]], {
      input: JSON.stringify({ ...valid, ...overrides }), encoding: 'utf8', timeout: 10000,
    });
    assert.equal(checked.status, expected, checked.stderr);
  }
  const malformed = spawnSync('python', ['-c', match[1]], {
    input: 'not-json', encoding: 'utf8', timeout: 10000,
  });
  assert.notEqual(malformed.status, 0);
});

test('launcher delegates service ownership to the contained supervisor', { skip: process.platform !== 'win32' }, () => {
  const script = `
    $ErrorActionPreference = 'Stop'
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($env:TEST_LAUNCHER, [ref]$tokens, [ref]$errors)
    $loop = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.WhileStatementAst] }, $true)
    if ($null -ne $loop) { throw 'legacy uncontained restart loop remains' }
    if ([IO.File]::ReadAllText($env:TEST_LAUNCHER) -notmatch '--supervise-profile') { throw 'contained supervisor not wired' }
    Write-Output 'replacement verified'
  `;
  const result = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
    encoding: 'utf8', timeout: 10000, env: { ...process.env, TEST_LAUNCHER: launcher },
  });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /replacement verified/);
});

test('launcher releases its mutex after supervisor exit', { skip: process.platform !== 'win32' }, () => {
  const script = `
    $ErrorActionPreference = 'Stop'
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($env:TEST_LAUNCHER, [ref]$tokens, [ref]$errors)
    $try = @($ast.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.TryStatementAst] })[-1]
    $script:disposed = 0; $script:killed = 0; $script:released = 0
    $server = [pscustomobject]@{ HasExited=$false }
    $server | Add-Member ScriptMethod Kill { throw 'injected stop failure' }
    $server | Add-Member ScriptMethod Dispose { $script:disposed++; throw 'injected disposal failure' }
    $tunnel = [pscustomobject]@{ HasExited=$false }
    $tunnel | Add-Member ScriptMethod Kill { $script:killed++ }
    $tunnel | Add-Member ScriptMethod Dispose { $script:disposed++ }
    $mutex = [pscustomobject]@{}
    $mutex | Add-Member ScriptMethod ReleaseMutex { $script:released++ }
    $mutex | Add-Member ScriptMethod Dispose { $script:disposed++ }
    $owned = $true
    & ([scriptblock]::Create($try.Finally.Extent.Text.Trim().Substring(1).TrimEnd().TrimEnd('}')))
    if ($script:disposed -ne 1 -or $script:killed -ne 0 -or $script:released -ne 1) { throw 'cleanup incomplete' }
    Write-Output 'cleanup verified'
  `;
  const result = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
    encoding: 'utf8', timeout: 10000, env: { ...process.env, TEST_LAUNCHER: launcher },
  });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /cleanup verified/);
});

test('isolated launcher validates configuration without launching and rejects production overlap', { skip: process.platform !== 'win32' }, () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'remote-launcher-'));
  try {
    const envFile = path.join(root, 'profile.env');
    const settings = {
      MUXD_PROFILE: 'test', MUXD_STATE_ROOT: path.join(root, 'state'),
      MUXD_PYTHON: process.execPath, MUXD_RUNTIME_ROOT: path.resolve(__dirname, '../../muxd'),
      MUXD_CONTROL_PORT: '18769', MUXD_TASK_NAME: 'MuxdParityTest',
      MUXD_PRINCIPAL_INSTANCE_ID: 'test-launcher', CLR_RELAY_PORT: '18770',
      CLR_REMOTE_PORT: '18765', CLR_REMOTE_STORE: path.join(root, 'state', 'archive.json'),
      CLR_REMOTE_TUNNEL_TARGET: 'test-host', CLR_REMOTE_BRIDGE_TARGET: 'test-host', CLR_REMOTE_BIND: '127.0.0.1',
      CLR_REMOTE_SYNC: '0', CLR_REMOTE_HLAUTH: '0', CLR_REMOTE_TOKEN: 'disposable-test-auth-token-32-chars',
    };
    function run(overrides = {}, inherited = {}) {
      fs.writeFileSync(envFile, Object.entries({ ...settings, ...overrides }).filter(([, v]) => v !== undefined).map(([k,v]) => `${k}=${v}`).join('\n'));
      return spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-File', launcher,
        '-EnvFile', envFile, '-ServerExe', process.execPath, '-ValidateOnly'], { encoding: 'utf8', timeout: 20000, env: { ...process.env, ...inherited } });
    }
    const valid = run();
    assert.equal(valid.status, 0, valid.stderr);
    const installer = path.resolve(__dirname, '../../app/scripts/install-remote-profile.ps1');
    const registrationScript = `
      $ErrorActionPreference = 'Stop'
      $global:registered = 0; $global:started = 0
      function Get-ScheduledTask { param($TaskName, $ErrorAction) if ($env:TEST_EXISTING_TASK -eq '1') { return @{ TaskName=$TaskName } } }
      function New-ScheduledTaskAction {
        param($Execute, $Argument, $WorkingDirectory)
        if ($Execute -ne 'powershell.exe' -or $Argument -notmatch '-NoProfile -NonInteractive -WindowStyle Hidden' -or $Argument -notmatch 'start-remote-profile.ps1' -or -not $Argument.Contains($env:TEST_ENV_FILE) -or -not $Argument.Contains($env:TEST_SERVER_EXE)) { throw 'invalid action' }
        return @{ action=$true }
      }
      function New-ScheduledTaskTrigger { param([switch]$AtLogOn, $User) if (-not $AtLogOn -or -not $User) { throw 'invalid trigger' }; return @{ trigger=$true } }
      function New-ScheduledTaskPrincipal { param($UserId, $LogonType, $RunLevel) if (-not $UserId -or $LogonType -ne 'Interactive' -or $RunLevel -ne 'Limited') { throw 'invalid principal' }; return @{ principal=$true } }
      function New-ScheduledTaskSettingsSet {
        param($MultipleInstances, $RestartCount, $RestartInterval, $ExecutionTimeLimit, [switch]$StartWhenAvailable, [switch]$AllowStartIfOnBatteries, [switch]$DontStopIfGoingOnBatteries)
        if ($MultipleInstances -ne 'IgnoreNew' -or $RestartCount -ne 3 -or $RestartInterval -ne [TimeSpan]::FromMinutes(1) -or $ExecutionTimeLimit -ne [TimeSpan]::Zero -or -not $StartWhenAvailable -or -not $AllowStartIfOnBatteries -or -not $DontStopIfGoingOnBatteries) { throw 'invalid restart settings' }
        return @{ settings=$true }
      }
      function Register-ScheduledTask {
        param($TaskName, $Action, $Trigger, $Principal, $Settings)
        if ($TaskName -ne 'CodexArchiveRemote-test-regression' -or -not $Action.action -or -not $Trigger.trigger -or -not $Principal.principal -or -not $Settings.settings) { throw 'invalid registration' }
        $global:registered++
      }
      function Start-ScheduledTask { param($TaskName) if ($global:registered -ne 1 -or $TaskName -ne 'CodexArchiveRemote-test-regression') { throw 'start before registration' }; $global:started++ }
      try {
        & $env:TEST_INSTALLER -EnvFile $env:TEST_ENV_FILE -ServerExe $env:TEST_SERVER_EXE -TaskName 'CodexArchiveRemote-test-regression'
        if ($env:TEST_EXISTING_TASK -eq '1') { throw 'existing task was overwritten' }
        if ($global:registered -ne 1 -or $global:started -ne 1) { throw 'registration incomplete' }
      } catch {
        if ($env:TEST_EXISTING_TASK -ne '1' -or $_.Exception.Message -notmatch 'refusing to overwrite' -or $global:registered -ne 0 -or $global:started -ne 0) { throw }
      }
      Write-Output 'registration verified without scheduler mutation'
    `;
    for (const existing of ['0', '1']) {
      const registration = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', registrationScript], {
        encoding: 'utf8', timeout: 20000,
        env: { ...process.env, TEST_INSTALLER: installer, TEST_ENV_FILE: envFile, TEST_SERVER_EXE: process.execPath, TEST_EXISTING_TASK: existing },
      });
      assert.equal(registration.status, 0, registration.stderr);
      assert.match(registration.stdout, /registration verified without scheduler mutation/);
    }
    assert.match(valid.stdout, /configuration valid/);
    assert.equal(run({ CLR_REMOTE_SYNC: '1' }).status, 0);
    assert.notEqual(run({ CLR_REMOTE_SYNC: 'true' }).status, 0);
    assert.notEqual(run({ CLR_REMOTE_PORT: '8765' }).status, 0);
    assert.notEqual(run({ CLR_REMOTE_STORE: path.join(root, 'outside.json') }).status, 0);
    assert.notEqual(run({ MUXD_TASK_NAME: 'MuxdSessionHost' }).status, 0);
    for (const identity of ['', 'bad identity', "quote'identity", 'x'.repeat(129)]) {
      assert.notEqual(run({ MUXD_PRINCIPAL_INSTANCE_ID: identity }).status, 0);
    }
    assert.notEqual(run({ CLR_REMOTE_TOKEN: '' }).status, 0);
    assert.notEqual(run({ CLR_REMOTE_BRIDGE: '0' }).status, 0);
    assert.equal(run({}, { CLR_REMOTE_BRIDGE: '0' }).status, 0);
    assert.notEqual(run({ CLR_REMOTE_PORT: settings.CLR_RELAY_PORT }).status, 0);
    assert.notEqual(run({ CLR_REMOTE_HLAUTH: '1', CLR_REMOTE_HLAUTH_BASE: '' }).status, 0);
    for (const key of ['MUXD_PROFILE', 'MUXD_PYTHON', 'MUXD_RUNTIME_ROOT', 'CLR_REMOTE_BRIDGE_TARGET', 'CLR_REMOTE_TOKEN', 'CLR_REMOTE_SYNC']) {
      const inherited = run({ [key]: undefined }, { [key]: settings[key] });
      assert.notEqual(inherited.status, 0, `${key} must not be inherited`);
    }
    for (const target of ['-oProxyCommand=bad', 'host;bad', 'user@host extra']) {
      assert.notEqual(run({ CLR_REMOTE_TUNNEL_TARGET: target }).status, 0);
    }
    assert.notEqual(run({ CLR_REMOTE_HLAUTH: '0\nCLR_REMOTE_HLAUTH=1' }).status, 0);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});
