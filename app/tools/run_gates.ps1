[CmdletBinding()]
param(
    # Acceptance callers must provide -GatewayRoot and -GatewaySha.
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$Tag,
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$GatewayRoot,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$GatewaySha
)

$ErrorActionPreference = 'Stop'
$script:hardFail = $false
$results = [ordered]@{}
$artifactDir = $null
$muxRoot = $null
$muxRootInfo = $null
$defaultSourceInfo = [pscustomobject]@{
    Path = 'unresolved'
    Head = 'unavailable'
    Identity = 'unavailable'
}

function ConvertTo-WindowsArgument {
    param([AllowNull()][string]$Value)

    if ($null -eq $Value -or $Value.Length -eq 0) {
        return '""'
    }
    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    $escaped = $Value -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return '"' + $escaped + '"'
}

function Set-ProcessArguments {
    param(
        [System.Diagnostics.ProcessStartInfo]$StartInfo,
        [string[]]$Arguments
    )

    $argumentListProperty = $StartInfo.PSObject.Properties['ArgumentList']
    if ($null -ne $argumentListProperty) {
        foreach ($argument in @($Arguments)) {
            [void]$StartInfo.ArgumentList.Add([string]$argument)
        }
        return
    }

    $StartInfo.Arguments = (@($Arguments) | ForEach-Object {
        ConvertTo-WindowsArgument $_
    }) -join ' '
}

function Set-ChildEnvironment {
    param(
        [System.Diagnostics.ProcessStartInfo]$StartInfo,
        [hashtable]$Environment
    )

    foreach ($name in $Environment.Keys) {
        try {
            $StartInfo.Environment[$name] = [string]$Environment[$name]
        } catch {
            $StartInfo.EnvironmentVariables[$name] = [string]$Environment[$name]
        }
    }
}

function Stop-ProcessTree {
    param([int]$ProcessId)

    # taskkill /T /F is the Windows PowerShell-compatible tree termination path.
    try {
        $killerStartInfo = New-Object System.Diagnostics.ProcessStartInfo
        $killerStartInfo.FileName = 'taskkill.exe'
        $killerStartInfo.Arguments = "/PID $ProcessId /T /F"
        $killerStartInfo.UseShellExecute = $false
        $killerStartInfo.CreateNoWindow = $true
        $killerStartInfo.RedirectStandardOutput = $true
        $killerStartInfo.RedirectStandardError = $true
        $killer = New-Object System.Diagnostics.Process
        $killer.StartInfo = $killerStartInfo
        if ($killer.Start()) {
            $outTask = $killer.StandardOutput.ReadToEndAsync()
            $errTask = $killer.StandardError.ReadToEndAsync()
            [void]$killer.WaitForExit(10000)
            if (-not $killer.HasExited) {
                try { $killer.Kill() } catch {}
            }
            [void]$outTask.Wait(1000)
            [void]$errTask.Wait(1000)
        }
    } catch {}

    try {
        $rootProcess = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($null -ne $rootProcess) {
            $rootProcess.Kill()
        }
    } catch {}
}

function Invoke-BoundedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][int]$TimeoutMs,
        [hashtable]$Environment = @{}
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    Set-ProcessArguments -StartInfo $startInfo -Arguments $Arguments
    Set-ChildEnvironment -StartInfo $startInfo -Environment $Environment

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            return [pscustomobject]@{
                Started = $false
                ExitCode = -1
                TimedOut = $false
                StdOut = ''
                StdErr = ''
                TerminatedTree = $false
            }
        }
    } catch {
        return [pscustomobject]@{
            Started = $false
            ExitCode = -1
            TimedOut = $false
            StdOut = ''
            StdErr = ''
            TerminatedTree = $false
        }
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $finished = $process.WaitForExit($TimeoutMs)
    $timedOut = -not $finished
    $terminatedTree = $false
    if ($timedOut) {
        Stop-ProcessTree -ProcessId $process.Id
        $terminatedTree = $true
        [void]$process.WaitForExit(5000)
    }

    $stdout = ''
    $stderr = ''
    try {
        if ($stdoutTask.Wait(5000)) { $stdout = $stdoutTask.Result }
    } catch {}
    try {
        if ($stderrTask.Wait(5000)) { $stderr = $stderrTask.Result }
    } catch {}

    $exitCode = -1
    try {
        if ($process.HasExited) {
            $exitCode = $process.ExitCode
        }
    } catch {}

    return [pscustomobject]@{
        Started = $true
        ExitCode = $exitCode
        TimedOut = $timedOut
        StdOut = [string]$stdout
        StdErr = [string]$stderr
        TerminatedTree = $terminatedTree
    }
}

function Ensure-NativePathType {
    if (-not ('MuxGate.NativeMethods' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MuxGate {
    public static class NativeMethods {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetFinalPathNameByHandle(
            IntPtr fileHandle,
            StringBuilder filePath,
            uint filePathLength,
            uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
'@
    }
}

function Get-FinalPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    Ensure-NativePathType
    $handle = [MuxGate.NativeMethods]::CreateFile(
        $Path,
        0x80,
        0x7,
        [IntPtr]::Zero,
        3,
        0x02000000,
        [IntPtr]::Zero)
    if ($handle -eq [IntPtr](-1)) {
        throw 'path-finalization-failed'
    }

    try {
        $buffer = New-Object System.Text.StringBuilder 32768
        $length = [MuxGate.NativeMethods]::GetFinalPathNameByHandle(
            $handle,
            $buffer,
            [uint32]$buffer.Capacity,
            0)
        if ($length -eq 0 -or $length -ge $buffer.Capacity) {
            throw 'path-finalization-failed'
        }
        $final = $buffer.ToString()
    } finally {
        [void][MuxGate.NativeMethods]::CloseHandle($handle)
    }

    if ($final.StartsWith('\\?\UNC\', [System.StringComparison]::OrdinalIgnoreCase)) {
        return '\\' + $final.Substring(8)
    }
    if ($final.StartsWith('\\?\', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $final.Substring(4)
    }
    return $final
}

function Get-CanonicalPathInfo {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolved = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    $final = Get-FinalPath -Path $resolved
    $full = [System.IO.Path]::GetFullPath($final)
    if ($full.Length -gt 3) {
        $full = $full.TrimEnd('\', '/')
    }
    $comparable = $full.Replace('\', '/').ToLowerInvariant()
    return [pscustomobject]@{
        Full = $full
        Comparable = $comparable
    }
}

function Test-ForbiddenPath {
    param([Parameter(Mandatory = $true)][string]$ComparablePath)

    $normalized = $ComparablePath.Replace('\', '/').ToLowerInvariant()
    # Deployment targets only. The gateway SOURCE repo (workrepo/claudex) is a legitimate
    # acceptance input; the targets are where runtime copies live, never source authority.
    $forbiddenTokens = @(
        'multiplex-app-patch',
        'C:/Users/Ahmed/muxd'
    )
    foreach ($token in $forbiddenTokens) {
        if ($normalized.Contains($token.ToLowerInvariant())) {
            return $true
        }
    }
    return $false
}

function Test-PathWithin {
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$RootInfo,
        [Parameter(Mandatory = $true)][pscustomobject]$PathInfo
    )

    if ($RootInfo.Comparable -eq $PathInfo.Comparable) {
        return $true
    }
    $prefix = $RootInfo.Comparable.TrimEnd('/') + '/'
    return $PathInfo.Comparable.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-MuxSourcePath {
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$PathInfo,
        [Parameter(Mandatory = $true)][pscustomobject]$RootInfo
    )

    if (Test-ForbiddenPath -ComparablePath $PathInfo.Comparable) {
        throw 'forbidden-source-path'
    }
    if (-not (Test-PathWithin -RootInfo $RootInfo -PathInfo $PathInfo)) {
        throw 'source-path-outside-mux-root'
    }
}

function Assert-GatewaySourcePath {
    param([Parameter(Mandatory = $true)][pscustomobject]$PathInfo)

    if (Test-ForbiddenPath -ComparablePath $PathInfo.Comparable) {
        throw 'forbidden-gateway-path'
    }
}

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [int]$TimeoutMs = 30000
    )

    return Invoke-BoundedProcess `
        -FilePath 'git.exe' `
        -Arguments (@('-C', $Repository) + $Arguments) `
        -WorkingDirectory $Repository `
        -TimeoutMs $TimeoutMs
}

function Get-GitInfo {
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$PathInfo,
        [string[]]$Excludes = @()
    )

    $worktree = Invoke-Git -Repository $PathInfo.Full -Arguments @('rev-parse', '--is-inside-work-tree')
    $top = Invoke-Git -Repository $PathInfo.Full -Arguments @('rev-parse', '--show-toplevel')
    $head = Invoke-Git -Repository $PathInfo.Full -Arguments @('rev-parse', 'HEAD')
    $tree = Invoke-Git -Repository $PathInfo.Full -Arguments @('rev-parse', 'HEAD^{tree}')

    $statusArguments = @('status', '--porcelain=v1', '--untracked-files=all', '--', '.')
    foreach ($exclude in @($Excludes)) {
        $statusArguments += ":(exclude)$exclude"
    }
    $status = Invoke-Git -Repository $PathInfo.Full -Arguments $statusArguments

    $worktreeOk = $worktree.Started -and -not $worktree.TimedOut -and
        $worktree.ExitCode -eq 0 -and $worktree.StdOut.Trim() -eq 'true'
    $topOk = $false
    if ($top.Started -and -not $top.TimedOut -and $top.ExitCode -eq 0) {
        try {
            $topInfo = Get-CanonicalPathInfo -Path $top.StdOut.Trim()
            $topOk = $topInfo.Comparable -eq $PathInfo.Comparable
        } catch {}
    }
    $headValue = if ($head.Started -and -not $head.TimedOut -and $head.ExitCode -eq 0) {
        $head.StdOut.Trim().ToLowerInvariant()
    } else {
        'unavailable'
    }
    $treeValue = if ($tree.Started -and -not $tree.TimedOut -and $tree.ExitCode -eq 0) {
        $tree.StdOut.Trim().ToLowerInvariant()
    } else {
        'unavailable'
    }
    $statusOk = $status.Started -and -not $status.TimedOut -and $status.ExitCode -eq 0
    $clean = $statusOk -and [string]::IsNullOrWhiteSpace($status.StdOut)
    $valid = $worktreeOk -and $topOk -and $headValue -ne 'unavailable' -and
        $treeValue -ne 'unavailable' -and $statusOk
    $identityState = if ($clean) { 'clean' } else { 'dirty-or-unavailable' }

    return [pscustomobject]@{
        Path = $PathInfo.Full
        Head = $headValue
        Tree = $treeValue
        Clean = $clean
        ValidWorktree = $valid
        Identity = "git-head=$headValue;git-tree=$treeValue;status=$identityState"
    }
}

function Test-LiteralMarker {
    param(
        [AllowNull()][string]$Output,
        [Parameter(Mandatory = $true)][string]$Marker
    )

    return ([string]$Output).IndexOf($Marker, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Get-DiscoveredTestCount {
    param(
        [AllowNull()][string]$Output,
        [Parameter(Mandatory = $true)][string]$Pattern
    )

    $matches = [regex]::Matches(
        [string]$Output,
        $Pattern,
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
            [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if ($matches.Count -eq 0) {
        return 0
    }
    return [int]$matches[$matches.Count - 1].Groups[1].Value
}

function Write-GateOutput {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [AllowNull()][string]$Output
    )

    if ($null -eq $artifactDir) {
        return
    }
    try {
        $logPath = Join-Path $artifactDir ($Name + '.log')
        [System.IO.File]::WriteAllText(
            $logPath,
            [string]$Output,
            (New-Object System.Text.UTF8Encoding($false)))
    } catch {}
}

function Add-GateResult {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][pscustomobject]$SourceInfo,
        [Parameter(Mandatory = $true)][int]$ExitCode,
        [Parameter(Mandatory = $true)][bool]$TimedOut,
        [Parameter(Mandatory = $true)][string]$Marker,
        [Parameter(Mandatory = $true)][bool]$MarkerFound,
        [object]$DiscoveredTests = $null,
        [Parameter(Mandatory = $true)][bool]$Skipped,
        [Parameter(Mandatory = $true)][string]$FailureKind
    )

    $testCountOk = $true
    if ($null -ne $DiscoveredTests) {
        $testCountOk = ([int]$DiscoveredTests -gt 0)
    }
    $ok = $ExitCode -eq 0 -and -not $TimedOut -and $MarkerFound -and
        $testCountOk -and -not $Skipped
    $record = [ordered]@{
        commandLabel = $Name
        exitCode = $ExitCode
        timeout = $TimedOut
        timeoutStatus = if ($TimedOut) { 'timed-out' } else { 'completed' }
        sourcePath = $SourceInfo.Path
        sourceSha = $SourceInfo.Head
        sourceIdentity = $SourceInfo.Identity
        assertionMarker = $Marker
        markerFound = $MarkerFound
        discoveredTests = $DiscoveredTests
        skipped = $Skipped
        result = if ($ok) { 'PASS' } else { 'FAIL' }
        failureKind = if ($ok) { 'none' } else { $FailureKind }
    }
    $results[$Name] = $record
    if (-not $ok) {
        $script:hardFail = $true
    }
    $status = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host "[$status] $Name exit=$ExitCode timeout=$TimedOut"
    return $record
}

function Add-ValidationGate {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][pscustomobject]$SourceInfo,
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Marker,
        [Parameter(Mandatory = $true)][string]$FailureKind
    )

    return Add-GateResult `
        -Name $Name `
        -SourceInfo $SourceInfo `
        -ExitCode $(if ($Condition) { 0 } else { 1 }) `
        -TimedOut:$false `
        -Marker $Marker `
        -MarkerFound:$Condition `
        -Skipped:$false `
        -FailureKind $FailureKind
}

function Add-BlockedGate {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][pscustomobject]$SourceInfo,
        [Parameter(Mandatory = $true)][string]$Marker
    )

    return Add-GateResult `
        -Name $Name `
        -SourceInfo $SourceInfo `
        -ExitCode 125 `
        -TimedOut:$false `
        -Marker $Marker `
        -MarkerFound:$false `
        -Skipped:$true `
        -FailureKind 'blocked-by-prerequisite'
}

function Run-ExternalGate {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][int]$TimeoutMs,
        [Parameter(Mandatory = $true)][pscustomobject]$SourceInfo,
        [Parameter(Mandatory = $true)][string]$PositiveMarker,
        [string]$TestCountPattern,
        [hashtable]$Environment = @{},
        [scriptblock]$Postcondition,
        [switch]$EmitMarkerOnSuccess
    )

    $execution = Invoke-BoundedProcess `
        -FilePath $FilePath `
        -Arguments $Arguments `
        -WorkingDirectory $WorkingDirectory `
        -TimeoutMs $TimeoutMs `
        -Environment $Environment
    $combined = ([string]$execution.StdOut) + [Environment]::NewLine + ([string]$execution.StdErr)
    $processOk = $execution.Started -and -not $execution.TimedOut -and $execution.ExitCode -eq 0
    $postconditionOk = $true
    if ($null -ne $Postcondition) {
        try {
            $postconditionOk = [bool](& $Postcondition $execution)
        } catch {
            $postconditionOk = $false
        }
    }

    $markerFound = if ($EmitMarkerOnSuccess) {
        $processOk -and $postconditionOk
    } else {
        Test-LiteralMarker -Output $combined -Marker $PositiveMarker
    }
    $discoveredTests = $null
    if (-not [string]::IsNullOrWhiteSpace($TestCountPattern)) {
        $discoveredTests = Get-DiscoveredTestCount -Output $combined -Pattern $TestCountPattern
    }

    if ($EmitMarkerOnSuccess -and $markerFound) {
        $combined += [Environment]::NewLine + $PositiveMarker + [Environment]::NewLine
    }
    Write-GateOutput -Name $Name -Output $combined

    $failureKind = 'none'
    if ($execution.TimedOut) {
        $failureKind = 'timeout'
    } elseif (-not $execution.Started) {
        $failureKind = 'tool-unavailable'
    } elseif ($execution.ExitCode -ne 0) {
        $failureKind = 'nonzero-exit'
    } elseif (-not $markerFound) {
        $failureKind = 'marker-missing'
    } elseif ($null -ne $discoveredTests -and $discoveredTests -le 0) {
        $failureKind = 'zero-tests'
    } elseif (-not $postconditionOk) {
        $failureKind = 'postcondition-failed'
    }

    return Add-GateResult `
        -Name $Name `
        -SourceInfo $SourceInfo `
        -ExitCode $execution.ExitCode `
        -TimedOut:$execution.TimedOut `
        -Marker $PositiveMarker `
        -MarkerFound:$markerFound `
        -DiscoveredTests $discoveredTests `
        -Skipped:$false `
        -FailureKind $failureKind
}

function New-SourceInfo {
    param(
        [string]$Path = 'unresolved',
        [string]$Head = 'unavailable',
        [string]$Identity = 'unavailable'
    )

    return [pscustomobject]@{
        Path = $Path
        Head = $Head
        Identity = $Identity
    }
}

try {
    if ($Tag -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') {
        throw 'invalid-tag'
    }

    # The repository root is derived from this file, never from the caller's location.
    $runnerDirectory = (Resolve-Path -LiteralPath $PSScriptRoot -ErrorAction Stop).Path
    $muxRootCandidate = [System.IO.Path]::GetFullPath((Join-Path $runnerDirectory '..\..'))
    $muxRootInfo = Get-CanonicalPathInfo -Path $muxRootCandidate
    $muxRoot = $muxRootInfo.Full
    $defaultSourceInfo = New-SourceInfo -Path $muxRoot

    $artifactRoot = Join-Path $muxRoot 'app\artifacts\reliability'
    $artifactDir = Join-Path $artifactRoot $Tag
    New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null

    $expectedPaths = @(
        'app\CodexLocalRetrieval.sln',
        'app\native\CodexLocalRetrieval.Native\CodexLocalRetrieval.Native.csproj',
        'app\native\CodexLocalRetrieval.Native.Tests\CodexLocalRetrieval.Native.Tests.csproj',
        'app\tools\ProjectionContractProbe\ProjectionContractProbe.csproj',
        'relay\server.js',
        'relay\tests\relay.test.js',
        'muxd\muxd.py',
        'muxd\transcript_guardian.py'
    )
    foreach ($relativePath in $expectedPaths) {
        $candidateInfo = Get-CanonicalPathInfo -Path (Join-Path $muxRoot $relativePath)
        Assert-MuxSourcePath -PathInfo $candidateInfo -RootInfo $muxRootInfo
    }

    $muxSourceExcludes = @(
        # Narrow allowlist: the active harness and these exact muxd state files are not product source.
        'app/tools/run_gates.ps1',
        'muxd/live-tabs.json',
        'muxd/live-tabs.json.bak',
        'muxd/sessions.json',
        'muxd/sessions.json.bak'
    )
    $muxGit = Get-GitInfo -PathInfo $muxRootInfo -Excludes $muxSourceExcludes
    $muxRootValid = $muxGit.ValidWorktree -and $muxRootInfo.Comparable -eq
        (Get-CanonicalPathInfo -Path $muxRoot).Comparable
    $muxSourceInfo = New-SourceInfo `
        -Path $muxRoot `
        -Head $muxGit.Head `
        -Identity $muxGit.Identity
    $muxProvenanceOk = $muxRootValid -and $muxGit.Clean
    Add-ValidationGate `
        -Name 'mux-source-provenance' `
        -SourceInfo $muxSourceInfo `
        -Condition $muxProvenanceOk `
        -Marker 'MUX_SOURCE_AUTHORITY_OK' `
        -FailureKind $(if (-not $muxRootValid) { 'unexpected-repository-root' } elseif (-not $muxGit.Clean) { 'dirty-source' } else { 'none' }) | Out-Null

    $gatewayPathInfo = $null
    $gatewayGit = $null
    $gatewaySourceInfo = New-SourceInfo -Path $GatewayRoot
    $gatewayProvenanceOk = $false
    $gatewayFailureKind = 'gateway-path-rejected'
    try {
        $gatewayPathCandidate = if ([System.IO.Path]::IsPathRooted($GatewayRoot)) {
            $GatewayRoot
        } else {
            Join-Path $muxRoot $GatewayRoot
        }
        $gatewayPathInfo = Get-CanonicalPathInfo -Path $gatewayPathCandidate
        Assert-GatewaySourcePath -PathInfo $gatewayPathInfo
        $gatewayGit = Get-GitInfo -PathInfo $gatewayPathInfo
        $gatewayShaMatches = $gatewayGit.Head -eq $GatewaySha.ToLowerInvariant()
        $gatewayProvenanceOk = $gatewayGit.ValidWorktree -and $gatewayShaMatches -and $gatewayGit.Clean
        $gatewaySourceInfo = New-SourceInfo `
            -Path $gatewayPathInfo.Full `
            -Head $gatewayGit.Head `
            -Identity $gatewayGit.Identity
        $gatewayFailureKind = if (-not $gatewayGit.ValidWorktree) {
            'gateway-not-git-worktree'
        } elseif (-not $gatewayShaMatches) {
            'gateway-head-mismatch'
        } elseif (-not $gatewayGit.Clean) {
            'dirty-gateway-source'
        } else {
            'none'
        }
    } catch {
        $gatewayPathInfo = $null
        $gatewayGit = $null
        $gatewayProvenanceOk = $false
        $gatewayFailureKind = 'gateway-path-rejected'
    }
    Add-ValidationGate `
        -Name 'gateway-source-provenance' `
        -SourceInfo $gatewaySourceInfo `
        -Condition $gatewayProvenanceOk `
        -Marker 'GATEWAY_SOURCE_AUTHORITY_OK' `
        -FailureKind $gatewayFailureKind | Out-Null

    $appRootInfo = Get-CanonicalPathInfo -Path (Join-Path $muxRoot 'app')
    $relayRootInfo = Get-CanonicalPathInfo -Path (Join-Path $muxRoot 'relay')
    $muxdRootInfo = Get-CanonicalPathInfo -Path (Join-Path $muxRoot 'muxd')
    Assert-MuxSourcePath -PathInfo $appRootInfo -RootInfo $muxRootInfo
    Assert-MuxSourcePath -PathInfo $relayRootInfo -RootInfo $muxRootInfo
    Assert-MuxSourcePath -PathInfo $muxdRootInfo -RootInfo $muxRootInfo

    $nativeTestsProject = Join-Path $appRootInfo.Full 'native\CodexLocalRetrieval.Native.Tests\CodexLocalRetrieval.Native.Tests.csproj'
    $nativeAppProject = Join-Path $appRootInfo.Full 'native\CodexLocalRetrieval.Native\CodexLocalRetrieval.Native.csproj'
    $projectionProject = Join-Path $appRootInfo.Full 'tools\ProjectionContractProbe\ProjectionContractProbe.csproj'
    $projectionArtifact = Join-Path $artifactDir 'projects-projection.json'

    if ($muxProvenanceOk -and $gatewayProvenanceOk) {
        $dotnetBuild = Run-ExternalGate `
            -Name 'dotnet-build' `
            -FilePath 'dotnet' `
            -Arguments @('build', $nativeTestsProject, '-c', 'Release', '-m:1', '-p:UseSharedCompilation=false') `
            -WorkingDirectory $appRootInfo.Full `
            -TimeoutMs 300000 `
            -SourceInfo $muxSourceInfo `
            -PositiveMarker 'Build succeeded.'

        if ($dotnetBuild.result -eq 'PASS') {
            $nativeBuild = Run-ExternalGate `
                -Name 'native-app-build' `
                -FilePath 'dotnet' `
                -Arguments @('build', $nativeAppProject, '-c', 'Release', '-m:1', '-p:UseSharedCompilation=false') `
                -WorkingDirectory $appRootInfo.Full `
                -TimeoutMs 300000 `
                -SourceInfo $muxSourceInfo `
                -PositiveMarker 'Build succeeded.'
        } else {
            $nativeBuild = Add-BlockedGate -Name 'native-app-build' -SourceInfo $muxSourceInfo -Marker 'Build succeeded.'
        }

        if ($dotnetBuild.result -eq 'PASS' -and $nativeBuild.result -eq 'PASS') {
            $dotnetTests = Run-ExternalGate `
                -Name 'dotnet-tests' `
                -FilePath 'dotnet' `
                -Arguments @(
                    'test', $nativeTestsProject, '-c', 'Release', '--no-build',
                    '--logger', 'console;verbosity=minimal',
                    '--filter', 'TestCategory!=RealStore&TestCategory!=LiveCodex',
                    '--blame-hang', '--blame-hang-timeout', '3m',
                    '--blame-hang-dump-type', 'mini'
                ) `
                -WorkingDirectory $appRootInfo.Full `
                -TimeoutMs 600000 `
                -SourceInfo $muxSourceInfo `
                -PositiveMarker 'Passed!' `
                -TestCountPattern '(?:Total tests:|Total:)\s*([0-9]+)'
        } else {
            $dotnetTests = Add-BlockedGate -Name 'dotnet-tests' -SourceInfo $muxSourceInfo -Marker 'Passed!'
        }

        if ($dotnetBuild.result -eq 'PASS') {
            $projectionPostcondition = {
                param($Execution)
                if (-not (Test-Path -LiteralPath $projectionArtifact -PathType Leaf)) {
                    return $false
                }
                return (Get-Item -LiteralPath $projectionArtifact).Length -gt 0
            }
            $projection = Run-ExternalGate `
                -Name 'projection-contract' `
                -FilePath 'dotnet' `
                -Arguments @('run', '--project', $projectionProject, '-c', 'Release', '--', $projectionArtifact) `
                -WorkingDirectory $appRootInfo.Full `
                -TimeoutMs 180000 `
                -SourceInfo $muxSourceInfo `
                -PositiveMarker 'projects-projection.json' `
                -Postcondition $projectionPostcondition
        } else {
            $projection = Add-BlockedGate -Name 'projection-contract' -SourceInfo $muxSourceInfo -Marker 'projects-projection.json'
        }

        $relaySyntax = Run-ExternalGate `
            -Name 'relay-syntax' `
            -FilePath 'node' `
            -Arguments @('--check', 'server.js') `
            -WorkingDirectory $relayRootInfo.Full `
            -TimeoutMs 60000 `
            -SourceInfo $muxSourceInfo `
            -PositiveMarker 'RELAY_SYNTAX_OK' `
            -EmitMarkerOnSuccess

        if ($projection.result -eq 'PASS') {
            $relayEnvironment = @{
                NODE_PATH = Join-Path ([System.IO.Path]::GetTempPath()) 'mux-relay-node-deps\node_modules'
                MUX_PROJECTION_ARTIFACT = $projectionArtifact
            }
            $relayTests = Run-ExternalGate `
                -Name 'relay-tests' `
                -FilePath 'node' `
                -Arguments @('--test', '--test-reporter=tap', 'tests\relay.test.js') `
                -WorkingDirectory $relayRootInfo.Full `
                -TimeoutMs 600000 `
                -SourceInfo $muxSourceInfo `
                -PositiveMarker '# pass ' `
                -TestCountPattern '^\s*#\s*tests\s+([0-9]+)\s*$' `
                -Environment $relayEnvironment
        } else {
            $relayTests = Add-BlockedGate -Name 'relay-tests' -SourceInfo $muxSourceInfo -Marker '# pass '
        }

        $muxdCompile = Run-ExternalGate `
            -Name 'muxd-compile' `
            -FilePath 'python' `
            -Arguments @('-m', 'py_compile', 'muxd.py', 'transcript_guardian.py', 'profile.py') `
            -WorkingDirectory $muxdRootInfo.Full `
            -TimeoutMs 60000 `
            -SourceInfo $muxSourceInfo `
            -PositiveMarker 'MUXD_COMPILE_OK' `
            -EmitMarkerOnSuccess

        if ($muxdCompile.result -eq 'PASS') {
            $muxdTests = Run-ExternalGate `
                -Name 'muxd-tests' `
                -FilePath 'python' `
                -Arguments @('-m', 'unittest', 'discover', '-s', 'tests', '-p', 'test_*.py') `
                -WorkingDirectory $muxdRootInfo.Full `
                -TimeoutMs 600000 `
                -SourceInfo $muxSourceInfo `
                -PositiveMarker 'OK' `
                -TestCountPattern 'Ran\s+([0-9]+)\s+tests?'
        } else {
            $muxdTests = Add-BlockedGate -Name 'muxd-tests' -SourceInfo $muxSourceInfo -Marker 'OK'
        }
    } else {
        foreach ($blockedGate in @(
            @{ Name = 'dotnet-build'; Marker = 'Build succeeded.' },
            @{ Name = 'native-app-build'; Marker = 'Build succeeded.' },
            @{ Name = 'dotnet-tests'; Marker = 'Passed!' },
            @{ Name = 'projection-contract'; Marker = 'projects-projection.json' },
            @{ Name = 'relay-syntax'; Marker = 'RELAY_SYNTAX_OK' },
            @{ Name = 'relay-tests'; Marker = '# pass ' },
            @{ Name = 'muxd-compile'; Marker = 'MUXD_COMPILE_OK' },
            @{ Name = 'muxd-tests'; Marker = 'OK' }
        )) {
            Add-BlockedGate -Name $blockedGate.Name -SourceInfo $muxSourceInfo -Marker $blockedGate.Marker | Out-Null
        }
    }

    $finalMuxGit = Get-GitInfo -PathInfo $muxRootInfo -Excludes $muxSourceExcludes
    $finalMuxSourceInfo = New-SourceInfo `
        -Path $muxRoot `
        -Head $finalMuxGit.Head `
        -Identity $finalMuxGit.Identity
    $finalMuxOk = $muxRootValid -and $finalMuxGit.ValidWorktree -and
        $finalMuxGit.Clean -and $finalMuxGit.Head -eq $muxGit.Head
    $finalMuxFailureKind = if (-not $finalMuxGit.ValidWorktree) {
        'unexpected-repository-root'
    } elseif ($finalMuxGit.Head -ne $muxGit.Head) {
        'source-head-changed'
    } elseif (-not $finalMuxGit.Clean) {
        'dirty-source'
    } else {
        'none'
    }
    Add-ValidationGate `
        -Name 'mux-source-provenance-final' `
        -SourceInfo $finalMuxSourceInfo `
        -Condition $finalMuxOk `
        -Marker 'MUX_SOURCE_FINAL_CLEAN_OK' `
        -FailureKind $finalMuxFailureKind | Out-Null

    if ($null -ne $gatewayPathInfo -and $null -ne $gatewayGit) {
        $finalGatewayGit = Get-GitInfo -PathInfo $gatewayPathInfo
        $finalGatewaySourceInfo = New-SourceInfo `
            -Path $gatewayPathInfo.Full `
            -Head $finalGatewayGit.Head `
            -Identity $finalGatewayGit.Identity
        $finalGatewayOk = $finalGatewayGit.ValidWorktree -and
            $finalGatewayGit.Head -eq $GatewaySha.ToLowerInvariant() -and
            $finalGatewayGit.Clean
        $finalGatewayFailureKind = if (-not $finalGatewayGit.ValidWorktree) {
            'gateway-not-git-worktree'
        } elseif ($finalGatewayGit.Head -ne $GatewaySha.ToLowerInvariant()) {
            'gateway-head-mismatch'
        } elseif (-not $finalGatewayGit.Clean) {
            'dirty-gateway-source'
        } else {
            'none'
        }
    } else {
        $finalGatewaySourceInfo = $gatewaySourceInfo
        $finalGatewayOk = $false
        $finalGatewayFailureKind = 'gateway-path-rejected'
    }
    Add-ValidationGate `
        -Name 'gateway-source-provenance-final' `
        -SourceInfo $finalGatewaySourceInfo `
        -Condition $finalGatewayOk `
        -Marker 'GATEWAY_SOURCE_FINAL_CLEAN_OK' `
        -FailureKind $finalGatewayFailureKind | Out-Null
} catch {
    $script:hardFail = $true
    if (-not $results.Contains('runner-fatal')) {
        Add-GateResult `
            -Name 'runner-fatal' `
            -SourceInfo $defaultSourceInfo `
            -ExitCode 1 `
            -TimedOut:$false `
            -Marker 'RUNNER_CONTRACT_OK' `
            -MarkerFound:$false `
            -Skipped:$false `
            -FailureKind 'runner-fatal' | Out-Null
    }
}

if ($null -ne $artifactDir) {
    $overallResult = if ($script:hardFail) { 'FAIL' } else { 'PASS' }
    $artifact = [ordered]@{
        schema = 'mux-acceptance.v1'
        sourceIdentityAlgorithm = 'SHA-256-or-git-tree'
        tag = $Tag
        overallResult = $overallResult
        declaredGatewaySha = $GatewaySha.ToLowerInvariant()
        muxMonorepoRoot = if ($null -ne $muxRoot) { $muxRoot } else { 'unresolved' }
        acceptedStatePreserved = $true
        historicalRecordsUntouched = @('app/CURRENT.md', 'app/LOOPS.md')
        gates = @($results.Values)
    }
    try {
        $jsonPath = Join-Path $artifactDir 'acceptance.json'
        $artifact | ConvertTo-Json -Depth 8 | Out-File -LiteralPath $jsonPath -Encoding utf8
        if ($script:hardFail) {
            $failedPath = Join-Path $artifactDir 'CURRENT_FAILED.md'
            @(
                '# Failed acceptance run'
                ''
                "Tag: $Tag"
                'Overall: FAIL'
                'The accepted-state record was preserved.'
                "Acceptance artifact: $jsonPath"
            ) | Out-File -LiteralPath $failedPath -Encoding utf8
        }
    } catch {
        $script:hardFail = $true
    }
}

if ($script:hardFail) {
    exit 1
}
exit 0
