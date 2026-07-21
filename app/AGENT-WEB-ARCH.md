# Agent Web — architecture

**Goal:** the Claude/Codex agentic-coding experience, in the browser, served from this PC and reachable
anywhere (harmonizerlabs.cc/remote, behind hl-auth). Works for **all chats — old and new**: browse any
past chat as a real conversation, **resume** it as a live session, or start a new one, with **real-time
streaming** of the agent's reasoning, tool calls, command output, and skills, plus full **steering**
(send messages, interrupt, approve/deny actions, pick model). Both agents.

This replaces the "JSON store" feel with a real agent chat. The current archive server (browse/search +
the read-only co-pilot) becomes one tab; the new live-agent engine is the heart.

---

## Why it's shaped this way (grounded in the CLIs)

- **Claude Code** speaks a documented bidirectional protocol:
  `claude -p --input-format stream-json --output-format stream-json --verbose [--resume <uuid>] [--model M] [--add-dir <ws>] [--permission-mode M]`.
  We write user turns as JSON to stdin; it streams JSON events (system/init, assistant text deltas,
  `thinking`, `tool_use`, `tool_result`, `result`) on stdout. `--replay-user-messages` + `--session-id`
  let us pin/track a session. This is a first-class transport — Claude is the v1 driver.
- **Codex** exposes `app-server` / `remote-control` (the JSON-RPC the desktop app uses) for full
  interactivity, and `codex exec --json` for one-shot streamed runs, plus `codex resume`. Codex is v3
  (exec --json first for streaming, then app-server for full steering).
- Both already persist sessions to disk (`~/.codex/sessions`, `~/.claude/projects`) — which the existing
  `ArchiveService` indexes. So **a live session IS a real on-disk chat**; new sessions appear in history
  automatically on the next index. "All chats, old and new" falls out of this.

---

## The layers

### 1. Agent transport adapters (PC)  →  `Core/Agents/`
The server owns one contained Codex app-server and one contained Claude process per live turn.
Both adapters normalize output into the same `AgentEvent` wire model.

- `ClaudeLiveDriver` — spawns a contained stream-json process and maps Claude events → `AgentEvent`.
- `CodexAgentHub` — owns one contained `codex app-server` JSON-RPC process and multiplexes threads.

### 2. Unified event model  →  `Core/Agents/AgentEvent.cs`  (the crux)
Both live adapters **and** the stored-rollout parser normalize into ONE model, so a single renderer
shows history and live identically:
```
session_started · user_message · assistant_delta / assistant_text · thinking(delta) ·
tool_call(name,input) · tool_output(stream) · permission_request(id,detail) ·
status(running|idle|error) · turn_result(usage) · session_ended
```

### 3. Session manager (server)  →  `Server/`
Owns live routes keyed by thread id, tracks cross-process writer claims, and tears down contained
agent processes when the server or owning socket exits.

### 4. Archive layer (existing, extended)  →  `Core/Services/ArchiveService` + a new parser
- Already indexes **all** codex + claude chats (old + new). Keep it.
- NEW `RolloutToEvents` parser: turn a stored `.jsonl` rollout into the **same `AgentEvent` stream** a
  live session produces → past chats render as real conversations (reasoning, tools, commands), not JSON.
- New live sessions write their own rollout on disk → re-indexed → appear in the sidebar. Write-through;
  no separate store.

### 5. Transport: WebSocket per session  →  `WS /api/agent`
- **Up:** `user_message`, `interrupt`, `approve`, `set_model`, `resume{chatId}` / `new{workspace,agent}`.
- **Down:** the `AgentEvent` stream as JSON.
- Reconnect → re-attach to the server-side session id → replay buffer. hl-auth checked on the WS upgrade.

### 6. Exposure / auth (already built)
harmonizerlabs.cc/remote → Cloudflare → nginx `/remote` → this PC over the LAN → server (hl-auth gated).
nginx `/remote` already forwards `Upgrade` headers, so WebSockets work. WS upgrade re-checks the
`hl_session` cookie via the existing `HlAuthGate`.

### 7. Frontend (web app at /remote)  →  `wwwroot/`
- **Sidebar:** all chats (old + new), grouped by workspace/project, searchable, "New session", live badge.
- **Conversation view (ONE component for stored + live):** user turns; assistant text streaming token by
  token; collapsible **reasoning**; **tool calls** (commands / file edits / skills) with their streamed
  output and diffs; inline **permission prompts** (Approve / Deny).
- **Composer:** message box, Send, **Stop/Interrupt**, agent + model picker, workspace indicator, usage.

### 8. Security
- hl-auth (owner) gates everything; the agent has full PC access *because it's coding* — only the
  authenticated owner reaches it. Permission requests surface in the UI (approve/deny), with an opt-in
  "auto-approve in this workspace" + a `--permission-mode` selector (default / acceptEdits / plan).
- Caps: max concurrent sessions, idle timeout, per-turn output cap, sanitized workspace paths.

---

## Build order (vertical slices — each independently usable)

- **P0 — Live Claude session, end to end (the core proof).** `AgentEvent` +
  `ClaudeLiveDriver` (stream-json) + `WS /api/agent` + a minimal conversation UI: type a message →
  watch Claude reason, call tools, run commands, answer — live, in a new workspace. *Verifier:* an
  integration test driving `ClaudeAgentSession` against the real CLI on a throwaway dir asserts we get
  assistant + tool events; a live browser turn streams.
- **P1 — Resume any Claude chat.** Archive chat → `--resume <id>` in its workspace; prior turns shown,
  continue live.
- **P2 — History as real conversations.** `RolloutToEvents` for Claude (then Codex) → every old chat
  renders in the same view. *Verifier:* parser tests on fixture rollouts.
- **P3 — Codex.** `CodexAgentHub` via one shared `app-server` for full steering.
- **P4 — Steering & robustness.** interrupt, approve/deny, model pick, reconnect/replay, caps.
- **P5 — Ship through /remote.** hl-auth-gate the WS, re-publish, e2e from the phone.

## Risks to retire first (in P0)
1. Claude stream-json **exact** stdin message shape + event schema — pin by running it live.
2. Permission/approval flow over stream-json (`--permission-mode` vs `permission_request` events).
3. `--resume` semantics: does it replay prior turns or just continue? (affects P1/P2).
4. Process lifecycle / partial-line JSON framing / reconnect.

## THE REAL DRIVER (supersedes P0's exec-per-turn): codex app-server client

P0 drove `codex exec --json` per turn — too low-fidelity. The real thing is a client of **`codex
app-server`** (the JSON-RPC protocol the Codex desktop app uses), proven against the real CLI:
- Transport: spawn `codex app-server -c service_tier=fast`, newline-delimited JSON-RPC over stdio.
  Responses `{id,result}`, notifications `{method,params}`, server-requests (approvals) `{id,method,params}`.
- `thread/list` → ALL sessions (id, name, preview, cwd, rollout path, timestamps) — verified, returned
  the user's real sessions. `thread/read` → full history. `thread/resume` + `turn/start` → go live.
  `turn/steer` (mid-turn steering), `turn/interrupt`. ServerNotifications stream `item/*` deltas
  (agentMessage, reasoning, commandExecution output, fileChange patches) + `turn/*`. ServerRequests =
  approvals (execCommandApproval / applyPatchApproval / item/*/requestApproval).
- This makes OUR server a real codex client → /remote shows all sessions as real conversations you
  open and drive, generally (no per-session manual remote). This is what the user actually wants.

Milestones: **M0 (DONE)** `CodexAppServer` JSON-RPC client (Core) + live test (initialize + thread/list
returns real sessions). M1 sidebar of all sessions (thread/list) + open any as a conversation
(thread/read). M2 go live (resume + turn/start + streaming item/* deltas). M3 approvals + steer. M4
Claude adapter + ship through /remote.

## Status (P0 — historical low-fidelity slice, removed)

**P0 — DONE + verified (codex-first).** Live codex session over a WebSocket, driven from the web:
- The obsolete exec-per-turn `CodexAgentSession` fallback was removed. Codex traffic has one
  production owner: `CodexAgentHub` backed by the contained app-server.
- `Server/AgentWebSocket.cs` + `WS /api/agent` (op: start|send|interrupt; streams AgentEvents).
- `wwwroot/`: an **Agent** tab (default) — start a session in a workspace, send tasks, watch the
  agent's commands/output/reasoning/answer stream live; Stop to interrupt.
- Verified end-to-end: a Node WS client AND a real browser drove a full turn — the agent ran a real
  shell command, streamed `ToolCall`/`ToolOutput`/`AssistantText`, rendered as a chat.
- **Codex gotcha:** this codex build's config parser rejects `service_tier="default"` (some configs
  carry it); the driver passes `-c service_tier=fast` (parser- and API-valid). Make this configurable
  or fix the user's `~/.codex/config.toml`.

Next: P1 resume any codex chat from the archive; P2 render stored rollouts as conversations; P3 the
codex `app-server` transport (mid-turn steering) + the Claude adapter; P4 approvals/permissions UI.

## What carries over (already done, not rebuilt)
Indexing of all chats; the deployment chain (Cloudflare→nginx /remote→PC LAN→server, hl-auth gated);
`HlAuthGate`; the resilient model plumbing; secret redaction; the ui-craft'd shell.
