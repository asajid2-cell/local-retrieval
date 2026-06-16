# Codex Local Retrieval — Agent Protocol

You are an AI coding agent (Claude or Codex) running in a terminal. The human you're working with
uses **Codex Local Retrieval**, a desktop app that indexes every Claude/Codex chat on this machine
so old conversations are never lost and can be reopened, organized, and resumed.

You can drive that app from here. The human will say things like *"set yourself up in my app"*,
*"favorite yourself"*, or *"add yourself to my VENPOD project"*. This file tells you how.

## How it works

The app watches an **inbox file** and applies one command per line (JSONL). Append a line, and the
app picks it up within a couple of seconds while it's running (or on its next launch).

- Inbox:  `%LOCALAPPDATA%\CodexLocalRetrieval\agent-inbox.jsonl`  (this file's folder; create it if missing)
- Acks:   `%LOCALAPPDATA%\CodexLocalRetrieval\agent-outbox.jsonl`  (the app writes one line back per command)

To append on Windows PowerShell:

```powershell
$cmd = '{"op":"favorite","target":"self","cwd":"<YOUR-CWD>"}'
Add-Content -Path "$env:LOCALAPPDATA\CodexLocalRetrieval\agent-inbox.jsonl" -Value $cmd -Encoding utf8
```

## Identifying yourself ("self")

For per-chat ops, the app resolves `"target":"self"` to **the newest session whose workspace is
the cwd you provide**. So always include your current working directory:

- Find it: run `pwd` (or `echo %cd%`), e.g. `z:\328\CMPUT328-A2\codexworks\301`.
- Put that exact path in the `cwd` field. Forward or back slashes are both fine.
- Optionally add `"tool":"claude"` or `"tool":"codex"` to disambiguate if both ran in that folder.

Because you are the chat actively running in this cwd, "newest session here" is you.

## Commands

| op | fields | does |
|----|--------|------|
| `init` | — | Registers the default Codex + Claude session folders so all chats show up. |
| `addSource` | `tool`, `root` | Registers a **non-default** session folder (e.g. chats stored somewhere unusual). |
| `favorite` | `target`/`cwd` | Pins the chat to the top of the app. |
| `addToProject` | `project`, `target`/`cwd` | Files the chat into a project (created if new). |
| `rename` | `target`/`cwd`, `localName`, `canonicalName?` | Renames the chat in the app (`localName`); `canonicalName` also writes back to Codex's own title so it shows in `codex resume`. |

Instead of `cwd`+`self` you may target an exact chat with `"id":"<session-id>"`.

## Examples (the usual asks)

```jsonl
{"op":"init"}
{"op":"favorite","target":"self","cwd":"z:\\328\\CMPUT328-A2\\codexworks\\301"}
{"op":"addToProject","project":"VENPOD","target":"self","cwd":"z:\\328\\CMPUT328-A2\\codexworks\\301","tool":"claude"}
{"op":"rename","target":"self","cwd":"z:\\328\\CMPUT328-A2\\codexworks\\301","localName":"Voxel renderer perf hunt","canonicalName":"Voxel renderer perf hunt"}
{"op":"addSource","tool":"claude","root":"d:\\work\\.claude\\projects"}
```

Set yourself up end to end in one go: append `init`, then `favorite self`, then `addToProject`.
The app re-scans before applying per-chat ops, so your own session is indexed first. Check
`agent-outbox.jsonl` to confirm each command's result.

## Trust model

The inbox is a **trusted local control channel**: any process running as you can append commands,
and they run with your permissions (organize chats, register source folders, write Codex's own
thread title). That is the same trust any local app you run already has. The app never executes
arbitrary commands from the inbox — only the fixed ops above — and refuses to resume a chat whose
id isn't a safe token. If you don't want agents driving it, don't share this file.
