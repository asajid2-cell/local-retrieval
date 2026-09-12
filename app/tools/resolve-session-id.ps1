[CmdletBinding()]
param(
    [Alias('session-id')]
    [string]$SessionId,
    [ValidateSet('claude', 'codex')]
    [string]$Tool,
    [Alias('pid')]
    [int]$ProcessId,
    [string]$InputJson,
    [switch]$Hook,
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function New-Result {
    param(
        [bool]$Ok,
        [string]$Detail,
        [string]$ResolvedTool = '',
        [string]$ResolvedId = '',
        [int]$HostPid = 0,
        [string]$Source = '',
        [string]$Confidence = 'none',
        [bool]$OwnerPidVerified = $false,
        [string]$TranscriptPath = '',
        [string]$Cwd = '',
        [string]$HookEventName = ''
    )
    [pscustomobject]@{
        ok = $Ok
        tool = $ResolvedTool
        session_id = $ResolvedId
        host_pid = $HostPid
        source = $Source
        confidence = $Confidence
        owner_pid_verified = $OwnerPidVerified
        transcript_path = $TranscriptPath
        cwd = $Cwd
        hook_event_name = $HookEventName
        detail = $Detail
    }
}

function Test-SessionId {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    return $Value -match '^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$'
}

function Get-PropertyValue {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return '' }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return '' }
    return [string]$property.Value
}

function Get-ResumeId {
    param([string]$CommandLine)
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return '' }
    $match = [regex]::Match(
        $CommandLine,
        '(?i)(?:^|\s)--resume(?:\s+|=)(?:"([^"]+)"|''([^'']+)''|([^\s]+))')
    if (-not $match.Success) { return '' }
    foreach ($index in 1..3) {
        if (-not [string]::IsNullOrWhiteSpace($match.Groups[$index].Value)) {
            return $match.Groups[$index].Value.Trim()
        }
    }
    return ''
}

function Get-ProcessTool {
    param([string]$Name, [string]$CommandLine)
    $lowerName = if ($null -eq $Name) { '' } else { $Name.ToLowerInvariant() }
    if ($lowerName -eq 'claude.exe') { return 'claude' }
    if ($lowerName -eq 'codex.exe') { return 'codex' }
    if ($lowerName -match '^(bun|node)(\.exe)?$' -and
        $CommandLine -match '(?i)gateway[\\/]dist[\\/]cli\.js' -and
        (Get-ResumeId $CommandLine)) {
        return 'claude'
    }
    return ''
}

function Get-ProcessTable {
    try {
        return @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name, CommandLine)
    }
    catch {
        return @()
    }
}

function Get-Ancestors {
    param([object[]]$Processes, [int]$StartPid)
    $byPid = @{}
    foreach ($process in $Processes) {
        $processId = 0
        try { $processId = [int]$process.ProcessId } catch { continue }
        if ($processId -gt 0) { $byPid[$processId] = $process }
    }

    $result = @()
    $current = $StartPid
    $seen = @{}
    for ($i = 0; $i -lt 32 -and $current -gt 0; $i++) {
        if ($seen.ContainsKey($current)) { break }
        $seen[$current] = $true
        if (-not $byPid.ContainsKey($current)) { break }
        $row = $byPid[$current]
        $result += $row
        $parent = 0
        try { $parent = [int]$row.ParentProcessId } catch { $parent = 0 }
        $current = $parent
    }
    return $result
}

function Get-RegistryId {
    param([int]$ProcessId)
    if ($ProcessId -le 0) { return '' }
    $path = Join-Path $HOME ('.claude\sessions\' + $ProcessId + '.json')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return '' }
    try {
        $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $value = Get-PropertyValue $json 'sessionId'
        if (Test-SessionId $value) { return $value }
    }
    catch { }
    return ''
}

function Add-Candidate {
    param(
        [hashtable]$Candidates,
        [string]$Id,
        [string]$CandidateTool,
        [int]$HostPid,
        [string]$CandidateSource,
        [string]$TranscriptPath = '',
        [string]$Cwd = ''
    )
    if (-not (Test-SessionId $Id)) { return }
    if (-not [string]::IsNullOrWhiteSpace($CandidateTool) -and
        -not [string]::IsNullOrWhiteSpace($Tool) -and
        $CandidateTool -ne $Tool) { return }
    if (-not $Candidates.ContainsKey($Id)) {
        $Candidates[$Id] = [pscustomobject]@{
            id = $Id
            tool = $CandidateTool
            pid = $HostPid
            source = $CandidateSource
            transcript_path = $TranscriptPath
            cwd = $Cwd
        }
        return
    }

    $existing = $Candidates[$Id]
    if ($existing.pid -le 0 -and $HostPid -gt 0) { $existing.pid = $HostPid }
    if ([string]::IsNullOrWhiteSpace($existing.tool) -and -not [string]::IsNullOrWhiteSpace($CandidateTool)) { $existing.tool = $CandidateTool }
    if (-not [string]::IsNullOrWhiteSpace($CandidateSource) -and
        $existing.source -notlike "*$CandidateSource*") { $existing.source += ", " + $CandidateSource }
    if ([string]::IsNullOrWhiteSpace($existing.transcript_path) -and -not [string]::IsNullOrWhiteSpace($TranscriptPath)) { $existing.transcript_path = $TranscriptPath }
    if ([string]::IsNullOrWhiteSpace($existing.cwd) -and -not [string]::IsNullOrWhiteSpace($Cwd)) { $existing.cwd = $Cwd }
}

$hookObject = $null
if ($Hook -and [string]::IsNullOrWhiteSpace($InputJson)) {
    try {
        $stdin = [Console]::In.ReadToEnd()
        if (-not [string]::IsNullOrWhiteSpace($stdin)) { $InputJson = $stdin }
    }
    catch { }
}
if (-not [string]::IsNullOrWhiteSpace($InputJson)) {
    try { $hookObject = ConvertFrom-Json -InputObject $InputJson }
    catch {
        $result = New-Result $false 'hook input was not valid JSON'
        if ($Json -or $Hook) { $result | ConvertTo-Json -Compress } else { Write-Error $result.detail }
        exit 2
    }
}

$hookId = Get-PropertyValue $hookObject 'session_id'
$hookTool = Get-PropertyValue $hookObject 'tool'
$hookTranscript = Get-PropertyValue $hookObject 'transcript_path'
$hookCwd = Get-PropertyValue $hookObject 'cwd'
$hookEvent = Get-PropertyValue $hookObject 'hook_event_name'
if (-not [string]::IsNullOrWhiteSpace($hookTool)) { $Tool = $hookTool.ToLowerInvariant() }
if ([string]::IsNullOrWhiteSpace($Tool)) {
    if (-not [string]::IsNullOrWhiteSpace($env:CLAUDE_CODE_SESSION_ID) -and
        [string]::IsNullOrWhiteSpace($env:CODEX_THREAD_ID)) { $Tool = 'claude' }
    elseif (-not [string]::IsNullOrWhiteSpace($env:CODEX_THREAD_ID) -and
            [string]::IsNullOrWhiteSpace($env:CLAUDE_CODE_SESSION_ID)) { $Tool = 'codex' }
    elseif (-not [string]::IsNullOrWhiteSpace($hookId)) { $Tool = 'claude' }
}
if ($Tool -notin @('', 'claude', 'codex')) {
    $result = New-Result $false 'tool must be claude or codex'
    if ($Json -or $Hook) { $result | ConvertTo-Json -Compress } else { Write-Error $result.detail }
    exit 2
}

$candidates = @{}
$explicitId = if (-not [string]::IsNullOrWhiteSpace($SessionId)) { $SessionId.Trim() } elseif (-not [string]::IsNullOrWhiteSpace($hookId)) { $hookId.Trim() } else { '' }
if (-not [string]::IsNullOrWhiteSpace($explicitId)) {
    if (-not (Test-SessionId $explicitId)) {
        $result = New-Result $false 'session id is malformed'
        if ($Json -or $Hook) { $result | ConvertTo-Json -Compress } else { Write-Error $result.detail }
        exit 2
    }
    $source = if (-not [string]::IsNullOrWhiteSpace($SessionId)) { 'cli' } else { 'hook-stdin' }
    Add-Candidate $candidates $explicitId $Tool 0 $source $hookTranscript $hookCwd
}

$envClaude = if ($null -eq $env:CLAUDE_CODE_SESSION_ID) { '' } else { $env:CLAUDE_CODE_SESSION_ID.Trim() }
$envCodex = if ($null -eq $env:CODEX_THREAD_ID) { '' } else { $env:CODEX_THREAD_ID.Trim() }
if (-not [string]::IsNullOrWhiteSpace($envClaude) -and -not (Test-SessionId $envClaude)) { $envClaude = '' }
if (-not [string]::IsNullOrWhiteSpace($envCodex) -and -not (Test-SessionId $envCodex)) { $envCodex = '' }
if ([string]::IsNullOrWhiteSpace($explicitId)) {
    if (($Tool -eq 'claude' -or [string]::IsNullOrWhiteSpace($Tool)) -and $envClaude) {
        Add-Candidate $candidates $envClaude 'claude' 0 'environment'
    }
    if (($Tool -eq 'codex' -or [string]::IsNullOrWhiteSpace($Tool)) -and $envCodex) {
        Add-Candidate $candidates $envCodex 'codex' 0 'environment'
    }
    if ([string]::IsNullOrWhiteSpace($Tool) -and $envClaude -and $envCodex -and $envClaude -ne $envCodex) {
        $result = New-Result $false 'CLAUDE_CODE_SESSION_ID and CODEX_THREAD_ID disagree; specify -Tool'
        if ($Json -or $Hook) { $result | ConvertTo-Json -Compress } else { Write-Error $result.detail }
        exit 2
    }
}

$environmentId = if ($Tool -eq 'claude') { $envClaude } elseif ($Tool -eq 'codex') { $envCodex } else { '' }
if ([string]::IsNullOrWhiteSpace($explicitId) -and [string]::IsNullOrWhiteSpace($environmentId)) {
    $processes = Get-ProcessTable
    $lookupPid = if ($ProcessId -gt 0) { $ProcessId } else { $PID }
    $ancestors = @(Get-Ancestors $processes $lookupPid)
    $processCandidates = @()
    foreach ($process in $ancestors) {
        $name = Get-PropertyValue $process 'Name'
        $commandLine = Get-PropertyValue $process 'CommandLine'
        $candidateTool = Get-ProcessTool $name $commandLine
        if ([string]::IsNullOrWhiteSpace($candidateTool)) { continue }
        $candidateId = Get-ResumeId $commandLine
        if (-not [string]::IsNullOrWhiteSpace($candidateId) -and (Test-SessionId $candidateId)) {
            $processId = 0
            try { $processId = [int]$process.ProcessId } catch { }
            $processCandidates += [pscustomobject]@{ id = $candidateId; tool = $candidateTool; pid = $processId; source = 'process-command-line'; transcript = ''; cwd = '' }
        }
        $registryId = Get-RegistryId ([int]$process.ProcessId)
        if ($registryId) {
            $processId = 0
            try { $processId = [int]$process.ProcessId } catch { }
            $processCandidates += [pscustomobject]@{ id = $registryId; tool = 'claude'; pid = $processId; source = 'claude-registry'; transcript = ''; cwd = '' }
        }
    }

    foreach ($candidate in $processCandidates) {
        Add-Candidate $candidates $candidate.id $candidate.tool $candidate.pid $candidate.source $candidate.transcript $candidate.cwd
    }
}

if ($candidates.Count -eq 0) {
    $result = New-Result $false 'no reliable session id was found; refusing cwd/mtime guessing' $Tool
}
elseif ($candidates.Count -gt 1) {
    $ids = @($candidates.Keys | Sort-Object)
    $result = New-Result $false ('ambiguous session identity: ' + ($ids -join ', ')) $Tool
}
else {
    $candidate = @($candidates.Values)[0]
    $ownerVerified = $candidate.pid -gt 0
    $result = New-Result $true 'resolved' $candidate.tool $candidate.id $candidate.pid $candidate.source 'strong' $ownerVerified $candidate.transcript_path $candidate.cwd $hookEvent
}

if ($Json -or $Hook) {
    $result | ConvertTo-Json -Compress
}
else {
    if ($result.ok) {
        Write-Output ("tool={0} session_id={1} host_pid={2} source={3} confidence={4} owner_pid_verified={5}" -f $result.tool, $result.session_id, $result.host_pid, $result.source, $result.confidence, $result.owner_pid_verified)
    }
    else {
        Write-Error $result.detail
        exit 1
    }
}
