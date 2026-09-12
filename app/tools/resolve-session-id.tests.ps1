$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'resolve-session-id.ps1'

function Invoke-Resolver {
    param([string[]]$Arguments, [string]$Input = '')
    if ([string]::IsNullOrWhiteSpace($Input)) {
        $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $scriptPath @Arguments
    }
    else {
        $output = $Input | & powershell -NoProfile -ExecutionPolicy Bypass -File $scriptPath @Arguments
    }
    if ($LASTEXITCODE -ne 0) { throw "resolver exited ${LASTEXITCODE}: $output" }
    return ($output | ConvertFrom-Json)
}

$explicit = Invoke-Resolver @('-SessionId', 'explicit-session', '-Tool', 'claude', '-Json')
if (-not $explicit.ok -or $explicit.session_id -ne 'explicit-session' -or $explicit.source -ne 'cli') { throw 'explicit identity failed' }

$hook = Invoke-Resolver @('-Hook', '-Json') '{"session_id":"hook-session","tool":"claude","cwd":"C:\\repo","transcript_path":"C:\\t.jsonl","hook_event_name":"SessionStart"}'
if (-not $hook.ok -or $hook.session_id -ne 'hook-session' -or $hook.source -ne 'hook-stdin') { throw 'hook identity failed' }

$oldClaude = $env:CLAUDE_CODE_SESSION_ID
$oldCodex = $env:CODEX_THREAD_ID
try {
    $env:CLAUDE_CODE_SESSION_ID = 'environment-session'
    $env:CODEX_THREAD_ID = $null
    $environment = Invoke-Resolver @('-Tool', 'claude', '-Json')
    if (-not $environment.ok -or $environment.session_id -ne 'environment-session' -or $environment.source -ne 'environment') { throw 'environment identity failed' }
}
finally {
    $env:CLAUDE_CODE_SESSION_ID = $oldClaude
    $env:CODEX_THREAD_ID = $oldCodex
}

$malformed = (& powershell -NoProfile -ExecutionPolicy Bypass -File $scriptPath -SessionId 'not valid' -Json | ConvertFrom-Json)
if ($malformed.ok -or $malformed.detail -notlike '*malformed*') { throw 'malformed identity was accepted' }

$missing = (& powershell -NoProfile -ExecutionPolicy Bypass -File $scriptPath -Tool claude -ProcessId 999999 -Json | ConvertFrom-Json)
if ($missing.ok -or $missing.detail -notlike '*no reliable session id*') { throw 'missing identity was accepted' }

'resolve-session-id tests passed'
