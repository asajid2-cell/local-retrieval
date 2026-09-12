[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolver = Join-Path $PSScriptRoot 'resolve-session-id.ps1'
$inputJson = [Console]::In.ReadToEnd()
$resultText = $inputJson | & powershell -NoProfile -ExecutionPolicy Bypass -File $resolver -Hook -Json
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($resultText)) {
    if (-not [string]::IsNullOrWhiteSpace($resultText)) { Write-Output $resultText }
    exit 0
}

$result = $resultText | ConvertFrom-Json
if ($result.ok) {
    $stateDir = Join-Path $env:LOCALAPPDATA 'CodexLocalRetrieval'
    [System.IO.Directory]::CreateDirectory($stateDir) | Out-Null
    $statePath = Join-Path $stateDir 'claude-session.json'
    $result | ConvertTo-Json -Compress | Set-Content -LiteralPath $statePath -Encoding utf8

    if (-not [string]::IsNullOrWhiteSpace($env:CLAUDE_ENV_FILE)) {
        Add-Content -LiteralPath $env:CLAUDE_ENV_FILE -Value ('CLAUDE_LOCAL_SESSION_ID=' + $result.session_id) -Encoding utf8
        Add-Content -LiteralPath $env:CLAUDE_ENV_FILE -Value ('CLAUDE_LOCAL_SESSION_TOOL=' + $result.tool) -Encoding utf8
        Add-Content -LiteralPath $env:CLAUDE_ENV_FILE -Value ('CLAUDE_LOCAL_SESSION_STATE=' + $statePath) -Encoding utf8
    }
}

# Hook output is intentionally compact and machine-readable. The resolver's result contains no secrets.
Write-Output ($result | ConvertTo-Json -Compress)
exit 0
