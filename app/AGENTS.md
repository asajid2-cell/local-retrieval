# MUX - Agent Protocol

You are an AI coding agent (Claude or Codex) running in a terminal. The human uses
MUX to index local Claude/Codex chats so they can be organized, searched, and resumed.

The app watches an inbox file and applies one JSON command per complete line.

- Inbox: `%LOCALAPPDATA%\CodexLocalRetrieval\agent-inbox.jsonl`
- Acks: `%LOCALAPPDATA%\CodexLocalRetrieval\agent-outbox.jsonl`

## Identify Yourself

For per-chat operations, pass your runtime session id:

- Codex: `$env:CODEX_THREAD_ID`
- Claude: `$env:CLAUDE_CODE_SESSION_ID`

Send it as `id` with `tool:"codex"` or `tool:"claude"`. The app resolves that
runtime id to the stored chat key using exact aliases from the transcript
header/path, including resumed/forked Codex ids. Unknown ids fail closed; the app
does not fall back to a different chat.

If your runtime truly has no session id variable, use `target:"self"` with your
real `cwd` and `tool`. That fallback only matches an already-indexed chat in that
exact workspace and tool.

Always include a `requestId` so you can find the matching ack without guessing
from the outbox tail.

## PowerShell

Codex:

```powershell
$rid = [guid]::NewGuid().ToString()
$cmd = @{op="addSelfToProject";project="X";id=$env:CODEX_THREAD_ID;tool="codex";requestId=$rid} | ConvertTo-Json -Compress
Add-Content -Path "$env:LOCALAPPDATA\CodexLocalRetrieval\agent-inbox.jsonl" -Value $cmd -Encoding utf8
Get-Content "$env:LOCALAPPDATA\CodexLocalRetrieval\agent-outbox.jsonl" | Select-String $rid
```

Claude:

```powershell
$rid = [guid]::NewGuid().ToString()
$cmd = @{op="addSelfToProject";project="X";id=$env:CLAUDE_CODE_SESSION_ID;tool="claude";requestId=$rid} | ConvertTo-Json -Compress
Add-Content -Path "$env:LOCALAPPDATA\CodexLocalRetrieval\agent-inbox.jsonl" -Value $cmd -Encoding utf8
Get-Content "$env:LOCALAPPDATA\CodexLocalRetrieval\agent-outbox.jsonl" | Select-String $rid
```

## Commands

```jsonl
{"op":"init","requestId":"..."}
{"op":"addSource","tool":"claude","root":"<path>","requestId":"..."}
{"op":"favorite","id":"<runtime-id>","tool":"codex","requestId":"..."}
{"op":"addSelfToProject","project":"X","id":"<runtime-id>","tool":"codex","requestId":"..."}
{"op":"rename","id":"<runtime-id>","tool":"codex","localName":"...","canonicalName":"...","requestId":"..."}
```

`addToProject` and `addToCollection` are accepted as legacy aliases for
`addSelfToProject`.

Each ack echoes `requestId`, `line`, `op`, `inputId`, `resolvedSessionId`,
`project`, and `persisted`. For project filing, treat `ok:true` plus
`persisted:true` as success.
