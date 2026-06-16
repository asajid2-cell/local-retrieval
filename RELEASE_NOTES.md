# v0.2.0

Multi-agent archive: index Codex and Claude, resume in a terminal, and let agents file themselves in.

## Highlights

- Indexes both Codex (`~/.codex/sessions`) and Claude (`~/.claude/projects`) sessions in one archive, each tagged with a `CX`/`CL` source badge. Claude sidechain/subagent transcripts are skipped; every chat keys on its resumable id.
- Resume any chat in a real terminal — `codex resume` for a Codex chat, `claude --resume` for a Claude chat — in its original working directory.
- Months-old chats resurface on launch via an off-thread, incremental scan that re-reads only changed files; a parser-version guard forces a full, orphan-pruning re-parse only when the parser changes.
- The reader strips the IDE/environment/system-reminder boilerplate that buries the actual conversation.
- Organize: auto-group by project under Workspaces, file chats into Projects, and pin favorites. Pins, projects, and renames survive every re-sync; source `.jsonl` files are never modified.
- Rename in-app, and optionally write the canonical name back to Codex's own thread title.
- Source inspector shows the real rollout event timeline (messages, tool calls, commands, reasoning).
- Agent self-service: point any Claude/Codex chat at `AGENTS.md` and it can favorite itself, file itself into a project, rename itself, or register a non-default chat folder over a local JSON inbox (`agent-inbox.jsonl`).
- Configurable session sources in Settings.

## Notes

- Resume launches the installed `codex` / `claude` CLI; unsafe session ids are refused before any terminal launch.
- The portable build is unsigned, so Windows SmartScreen may warn on first launch. Unofficial; not affiliated with OpenAI or Anthropic.

# v0.1.3

Patch release for small icon proportions.

## Fixes

- Redrew the transparent folder mark with taller proportions so the titlebar and taskbar icon no longer read as a squished strip.

# v0.1.2

Patch release for the app icon.

## Fixes

- Regenerated the app icon, titlebar icon, tile logos, and launcher icon with transparent backgrounds.

# v0.1.1

Patch release for local session indexing.

## Fixes

- Local session indexing streams Codex JSONL files, so large or actively written chat logs can load without leaving the app stuck on sample sessions.
- Real Codex session titles are refreshed after indexing so freshly indexed local chats sort and label correctly.

# v0.1.0

First public release of Codex Local Retrieval.

## Highlights

- Native WinUI 3 desktop app for browsing sanitized local session archives.
- Deep fuzzy search across titles, paths, messages, and code blocks.
- Restore packet builder for continuing archived work in a new chat.
- Workspace and collection group views.
- Right-click chat actions for pin, rename, archive, copy path, and collections.
- Theme, accent, shape, and density settings.
- Optional OpenAI-compatible AI provider support with model detection and Windows credential storage for API keys.
- Startup auto-detection for local Codex sessions, plus a Settings chat source path for manual indexing.

## Release Artifact

- `codex-local-retrieval-win-x64.zip`
- Portable Windows x64 build.
- Contains a top-level `Codex Local Retrieval.exe` launcher and an `app` folder for runtime files.
- Unsigned; Windows SmartScreen may warn on first launch.

## Validation

- `dotnet build .\native\CodexLocalRetrieval.Native\CodexLocalRetrieval.Native.csproj -p:Platform=x64`
- `dotnet test .\native\CodexLocalRetrieval.Native.Tests\CodexLocalRetrieval.Native.Tests.csproj -p:Platform=x64`
- `.\tools\release\package-win-x64.ps1`
- Extracted ZIP launcher smoke test.

## Known Issues

- MSIX packaging and code signing are not implemented.
- Auto-detection targets standard Codex folders. Users with custom archive locations should set the chat source path in Settings.
- A sanitized README screenshot is included. Additional workflow video capture is not included.
