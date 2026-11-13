# MUX

**One searchable archive for the Codex and Claude Code sessions on a PC, with a browser terminal for the live ones.**

MUX puts both agent-session stores into one searchable archive. It reopens any chat in the terminal where it started, and carries that terminal to a browser without opening a port on the PC. The repo holds the desktop app, the PC-side session host, and the VPS relay.

[![writing gate](https://github.com/asajid2-cell/local-retrieval/actions/workflows/writing-gate.yml/badge.svg?branch=release)](https://github.com/asajid2-cell/local-retrieval/actions/workflows/writing-gate.yml)

![The MUX desktop app](app/docs/media/hero.gif)

## The result

| Result | Evidence |
| --- | --- |
| Two session formats in one archive | Codex rollouts and Claude transcripts, parsed by `app/native/CodexLocalRetrieval.Core/Services/ArchiveService.cs` |
| Three processes in one repo | `app/`, `muxd/`, `relay/` |
| 1,700+ automated tests | `app/native/CodexLocalRetrieval.Native.Tests`, `relay/tests` |
| Zero inbound ports on the PC | `muxd/muxd.py` dials out to the relay |
| One action to resume | `codex resume` or `claude --resume`, in the chat's original working directory |

## What I built

I built the archive engine, the WinUI 3 client, the co-pilot tool loop, the ConPTY session host, and the relay. The hard part is the live file: the session you most want to resume is often still being written, in one of two formats, with subagent transcripts mixed in. The parser streams it, keys each chat on its resumable id, and an incremental stamp cache keeps a re-scan off files that did not change.

## How it works

The app scans `~/.codex/sessions` and `~/.claude/projects` on launch and routes each file by format. Claude chats key on the transcript filename, which is also the id `claude --resume` needs, and sidechain transcripts are skipped. Pins, projects, and renames live in an app-owned store, so a re-sync never rewrites a source `.jsonl` file.

`muxd` owns one ConPTY per session on the PC and dials out to the relay over a single WebSocket. The PC needs no inbound port, so a Wi-Fi drop or relay deploy only makes viewers reattach. The relay mirrors sessions and keeps durable state for projects and pins; write leases live in `muxd`, so the relay never decides who may type.

The obvious alternative is SSH plus tmux. That needs an inbound path or a permanent tunnel to the PC, and a Unix host. `muxd` dials out instead, and owns Windows ConPTY directly.

## Try it

```powershell
git clone https://github.com/asajid2-cell/local-retrieval.git
cd local-retrieval/app/native/CodexLocalRetrieval.Native
dotnet run -c Debug
```

Requires Windows 10 or 11 and the .NET 8 SDK. On first launch the app indexes both default session stores; add another folder in Settings → Sources.

## What it does

- **Resurface old chats.** Launch re-scans both stores, so a conversation from months ago is back in the list.
- **Read it clean.** The reader strips IDE, environment, and system-reminder boilerplate.
- **Organize.** Chats group by project path; file one into a named project, or pin it. Organization survives every re-sync.
- **Resume.** One action runs `codex resume` or `claude --resume` in the chat's original working directory.
- **Ask a co-pilot.** A DeepSeek-backed tool loop searches and organizes the archive, with a confirmation step before any change.
- **Reach it from a phone.** The headless `CodexLocalRetrieval.Server` serves a bearer-authenticated web UI through your own reverse proxy.

## Where the substance is

| Area | Path |
| --- | --- |
| Indexing, parsing, sync, resume | `app/native/CodexLocalRetrieval.Core/Services/ArchiveService.cs` |
| App shell and navigation | `app/native/CodexLocalRetrieval.Native/MainPage.xaml.cs` |
| Co-pilot tool loop | `app/native/CodexLocalRetrieval.Core/Chat/` |
| Remote API and auth | `app/native/CodexLocalRetrieval.Core/Remote/` |
| Session host | `muxd/muxd.py` |
| Relay and browser terminal | `relay/server.js`, `relay/public/` |
| Element-map contract | `docs/terminal-element-map.md` |

Run the app suite with `dotnet test app/native/CodexLocalRetrieval.Native.Tests`, and the relay suite with `npm test` in `relay/`.

## Limitations

- The desktop app and `muxd` run on Windows only, because both depend on WinUI 3 and ConPTY.
- The portable build is unsigned; Windows SmartScreen warns on first launch.
- Remote access needs your own VPS, TLS, and a bearer token of at least 24 characters. The server binds `127.0.0.1` by default.
- The co-pilot needs `DEEPSEEK_API_KEY`; without it, chat is disabled.
- Source `.jsonl` files are never modified, so organization lives only in the app store.
- MUX is unofficial and not affiliated with OpenAI or Anthropic.

## Status and license

Development continues on `main`; this branch is the release cut. The desktop release notes track v0.2.0 in `app/RELEASE_NOTES.md`. The desktop app is MIT (`app/LICENSE`); the relay package declares ISC.

Read `app/CONTRIBUTING.md` and run both suites before a pull request.
