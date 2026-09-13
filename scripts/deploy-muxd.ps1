# Deploy muxd/ from the monorepo to the live runtime and restart it.
# MuxdSessionHostRestart refuses while hosted sessions are alive.
param(
  [ValidateSet('audit','enforce')]
  [string]$AuthzMode,
  [string]$RuntimeDir = 'C:\Users\Ahmed\muxd'
)

$ErrorActionPreference = 'Stop'

$src = Join-Path $PSScriptRoot '..\muxd'
$relay = Join-Path $PSScriptRoot '..\relay'
$dst = $RuntimeDir
$files = @('muxd.py','muxctl.py','muxrun.py','host_input_intent.py','ops\restart_muxd.ps1')
$restartTask = 'MuxdSessionHostRestart'
$runtimeEnv = Join-Path $dst 'muxd.env'
$deployFence = Join-Path $dst 'deploying.flag'

function Get-MuxdEnvValue([string]$Path, [string]$Name) {
  if (-not (Test-Path -LiteralPath $Path)) {
    return ''
  }
  foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
    if ($line -match ('^\s*' + [regex]::Escape($Name) + '\s*=\s*(.*)$')) {
      return $Matches[1].Trim()
    }
  }
  return ''
}

function Set-MuxdEnvValue([string]$Path, [string]$Name, [string]$Value) {
  $lines = if (Test-Path -LiteralPath $Path) {
    @([System.IO.File]::ReadAllLines($Path))
  } else {
    @()
  }
  $updated = New-Object System.Collections.Generic.List[string]
  $replaced = $false
  foreach ($line in $lines) {
    if ($line -match ('^\s*' + [regex]::Escape($Name) + '\s*=')) {
      if (-not $replaced) {
        $updated.Add("$Name=$Value")
        $replaced = $true
      }
    } else {
      $updated.Add($line)
    }
  }
  if (-not $replaced) {
    $updated.Add("$Name=$Value")
  }

  $temp = "$Path.deploy-$([guid]::NewGuid().ToString('N')).tmp"
  try {
    [System.IO.File]::WriteAllLines(
      $temp,
      $updated,
      (New-Object System.Text.UTF8Encoding($false))
    )
    if (Test-Path -LiteralPath $Path) {
      $backupPath = "$Path.deploy-backup-$([guid]::NewGuid().ToString('N')).tmp"
      try {
        [System.IO.File]::Replace([string]$temp, [string]$Path, [string]$backupPath, $true)
      }
      finally {
        if (Test-Path -LiteralPath $backupPath) {
          Remove-Item -LiteralPath $backupPath -Force
        }
      }
    } else {
      Move-Item -LiteralPath $temp -Destination $Path
    }
  }
  finally {
    if (Test-Path -LiteralPath $temp) {
      Remove-Item -LiteralPath $temp -Force
    }
  }
}

if (-not $PSBoundParameters.ContainsKey('AuthzMode')) {
  $requestedMode = if ($env:MUX_AUTHZ_MODE) {
    $env:MUX_AUTHZ_MODE.Trim().ToLowerInvariant()
  } else {
    (Get-MuxdEnvValue -Path $runtimeEnv -Name 'MUX_AUTHZ_MODE').ToLowerInvariant()
  }
  $AuthzMode = if ($requestedMode -in @('audit','enforce')) { $requestedMode } else { 'audit' }
}

function Invoke-RestartAndVerify([switch]$Recovery, [string]$RecoveryToken = '') {
  if ($Recovery) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $restartScript `
      -Recovery -RecoveryToken $RecoveryToken
    if ($LASTEXITCODE -ne 0) {
      throw "rollback recovery restart failed with exit $LASTEXITCODE"
    }
  } else {
    Start-ScheduledTask -TaskName $restartTask
    $deadline = (Get-Date).AddSeconds(90)
    $healthy = $false
    do {
      Start-Sleep -Milliseconds 250
      $previousAutostart = $env:MUXCTL_AUTOSTART
      try {
        $env:MUXCTL_AUTOSTART = '0'
        & python (Join-Path $dst 'muxctl.py') status *> $null
        $healthy = $LASTEXITCODE -eq 0
      }
      finally {
        $env:MUXCTL_AUTOSTART = $previousAutostart
      }
    } while (-not $healthy -and (Get-Date) -lt $deadline)

    if (-not $healthy) {
      $info = Get-ScheduledTaskInfo -TaskName $restartTask
      throw "$restartTask did not produce a healthy muxd replacement within 90 seconds (task result $($info.LastTaskResult))"
    }
  }

  $previousAutostart = $env:MUXCTL_AUTOSTART
  try {
    $env:MUXCTL_AUTOSTART = '0'
    & python (Join-Path $dst 'muxctl.py') status *> $null
    if ($LASTEXITCODE -ne 0) {
      throw 'replacement muxd failed its local status check'
    }
  }
  finally {
    $env:MUXCTL_AUTOSTART = $previousAutostart
  }
}

$restartScript = Join-Path $dst 'ops\restart_muxd.ps1'
if (-not (Test-Path -LiteralPath $restartScript)) {
  Write-Error "muxd restart preflight is missing: $restartScript"
  exit 1
}
& powershell -NoProfile -ExecutionPolicy Bypass -File $restartScript -CheckOnly
if ($LASTEXITCODE -ne 0) {
  Write-Error 'active muxd sessions or an unhealthy live runtime blocked deployment before any files were copied'
  exit 1
}

node -e "const c=require(process.argv[1]).createLeaseConduit();const f={t:'i',auth:{principalId:'p',keyId:'k',sig:'x',bodyB64:'eA=='}};const o=c.forwardSignedInput('s',JSON.stringify(f));if(!o||o.t!=='i'||!o.auth||o.auth.sig!=='x')process.exit(1)" (Join-Path $relay 'lease-conduit.js')
if ($LASTEXITCODE -ne 0) {
  Write-Error 'local relay cannot forward the inputDurable t:i contract - NOT deploying'
  exit 1
}

if ($AuthzMode -eq 'enforce') {
  $principalStatus = @'
import sys
sys.path.insert(0, sys.argv[1])
import host_input_intent
try:
    endpoint = host_input_intent.load_principal_endpoint()
except Exception as error:
    print(error)
    raise SystemExit(1)
print(endpoint.summary())
raise SystemExit(0 if endpoint.provisioned() else 1)
'@
  $principalStatus | python - $src
  if ($LASTEXITCODE -ne 0) {
    Write-Error 'enforce deployment requires a DPAPI-protected provisioned principal - NOT deploying'
    exit 1
  }
  $principalAuth = Join-Path $relay 'public\principal-auth.js'
  $viewerHtml = Join-Path $relay 'public\index.html'
  $hasPrincipalAuth = Test-Path -LiteralPath $principalAuth
  $viewerLoadsPrincipalAuth = Select-String -LiteralPath $viewerHtml `
    -Pattern '<script\b[^>]*\bsrc\s*=\s*["''][^"'']*principal-auth\.js(?:[?#][^"'']*)?["'']' `
    -Quiet
  if (-not $hasPrincipalAuth -or -not $viewerLoadsPrincipalAuth) {
    Write-Error 'enforce deployment requires the trusted browser principal-auth.js signer - NOT deploying'
    exit 1
  }
}

# PREFLIGHT-LOCAL-MODULE-MANIFEST: py_compile never resolves imports, so parse staged
# entrypoints and ensure every imported local sibling is present before changing live files.
$preflight = @'
import ast, os, sys
src, dst = sys.argv[1], sys.argv[2]
missing = set()
for entry in ("muxd.py", "muxctl.py"):
    path = os.path.join(dst, entry)
    if not os.path.exists(path):
        missing.add(entry)
        continue
    with open(path, "r", encoding="utf-8") as fh:
        tree = ast.parse(fh.read(), filename=path)
    names = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            names.update(a.name.split(".")[0] for a in node.names)
        elif isinstance(node, ast.ImportFrom):
            if node.level == 0 and node.module:
                names.add(node.module.split(".")[0])
    for name in names:
        if os.path.exists(os.path.join(src, name + ".py")) and not os.path.exists(os.path.join(dst, name + ".py")):
            missing.add(name + ".py")
if missing:
    print("missing local modules in %s: %s" % (dst, ", ".join(sorted(missing))))
    sys.exit(1)
'@

$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("muxd-deploy-" + [guid]::NewGuid().ToString('N'))
try {
  New-Item -ItemType Directory -Path $stage | Out-Null
  foreach ($f in $files) {
    $stagedFile = Join-Path $stage $f
    New-Item -ItemType Directory -Path (Split-Path -Parent $stagedFile) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $src $f) -Destination $stagedFile
    if ($f.EndsWith('.py')) {
      & python -m py_compile $stagedFile
      if ($LASTEXITCODE -ne 0) {
        throw "$f does not compile - live runtime was not changed"
      }
    }
  }

  $preflight | python - $src $stage
  if ($LASTEXITCODE -ne 0) {
    throw 'staged runtime is missing local modules - live runtime was not changed'
  }

  $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
  $rollback = Join-Path $dst ("rollback\deploy-" + $stamp)
  $tracked = @($files + 'muxd.env')
  $hadLive = @{}
  $liveChanged = $false
  $restartRequested = $false
  $fenceCreated = $false
  $recoveryToken = [guid]::NewGuid().ToString('N')

  function Restore-Runtime {
    foreach ($name in $tracked) {
      $live = Join-Path $dst $name
      if ($hadLive[$name]) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $live) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $rollback $name) -Destination $live -Force
      } elseif (Test-Path -LiteralPath $live) {
        Remove-Item -LiteralPath $live -Force
      }
    }
  }

  try {
    New-Item -ItemType Directory -Path $rollback -Force | Out-Null
    foreach ($name in $tracked) {
      $live = Join-Path $dst $name
      $hadLive[$name] = Test-Path -LiteralPath $live
      if ($hadLive[$name]) {
        $rollbackFile = Join-Path $rollback $name
        New-Item -ItemType Directory -Path (Split-Path -Parent $rollbackFile) -Force | Out-Null
        Copy-Item -LiteralPath $live -Destination $rollbackFile -Force
      }
    }

    $liveChanged = $true
    foreach ($f in $files) {
      $liveFile = Join-Path $dst $f
      New-Item -ItemType Directory -Path (Split-Path -Parent $liveFile) -Force | Out-Null
      Copy-Item -LiteralPath (Join-Path $stage $f) -Destination $liveFile -Force
      Write-Host "deployed $f"
    }
    Set-MuxdEnvValue -Path $runtimeEnv -Name 'MUX_AUTHZ_MODE' -Value $AuthzMode

    [System.IO.File]::WriteAllText(
      $deployFence,
      "recovery:$recoveryToken`n",
      (New-Object System.Text.UTF8Encoding($false))
    )
    $fenceCreated = $true
    $restartRequested = $true
    Invoke-RestartAndVerify
    Remove-Item -LiteralPath $deployFence -Force
    $fenceCreated = $false
    Write-Host "muxd deployment healthy; authz=$AuthzMode rollback=$rollback"
  }
  catch {
    $deploymentError = $_
    if ($liveChanged) {
      Restore-Runtime
      if ($restartRequested) {
        try {
          Invoke-RestartAndVerify -Recovery -RecoveryToken $recoveryToken
          Remove-Item -LiteralPath $deployFence -Force
          $fenceCreated = $false
        }
        catch {
          throw "muxd deployment failed: $($deploymentError.Exception.Message); rollback restart also failed: $($_.Exception.Message)"
        }
      }
    }
    throw $deploymentError
  }
  finally {
    if ($fenceCreated) {
      Write-Warning "muxd deployment fence remains at $deployFence because recovery did not complete"
    }
  }
}
finally {
  if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
  }
}
