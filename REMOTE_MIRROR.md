# Remote terminal mirror — design (explore/remote-mirror)

Goal: launch a CLI session on this PC "remote-enabled", then attach to that exact live session from
anywhere (phone/browser) and see + drive it — same terminal, both ends. Opt-in **at launch** (not a
retroactive attach, which native Windows can't do). Built on the existing remote stack.

## Model
A **remote-enabled session** = a process (`claude`/`codex`/`pwsh`/…) running under a **server-owned
ConPTY**, with N attachable **viewers**. Local console = one viewer; phone = another. All viewers see
the same output and can type. Reconnect replays the scrollback ring buffer.

Launch: `clrterm <cmd>` → asks the local server to spawn `<cmd>` under a ConPTY in the caller's cwd,
and attaches the caller's console as a viewer. The web client attaches the same session by id.

## Layers (reuse the existing server)
1. **PtySession** (`Core/Terminal/PtySession.cs`) — ConPTY wrapper: spawn a process under a pseudo
   console, expose Output (read) + Input (write) streams + Resize + exit. Windows-only (guarded).
2. **TerminalBroker** (`Server`) — registry of live sessions by id; per-session ring buffer (scrollback
   replay on reconnect); fan-out output to all viewers; merge input from all viewers → PtySession.
3. **WS `/api/term/{id}`** (viewer) + **WS `/api/term/{id}/host`** or a spawn control — same auth gate
   as `/api/agent` (bearer or hl-auth). xterm.js on the web; the `clrterm` console client locally.
4. **`clrterm`** launcher — spawns the session remote-enabled and renders it locally (a viewer too).
5. **Pairing/security** — time-limited device↔account pairing (TTL, e.g. 7 days) gating the WS, built
   on `CommandSigner` (HMAC, freshness, nonce-replay) + `HlAuthGate`. Optionally SSH-key (asymmetric)
   signing of the pairing challenge.

## Build slices (risk-first)
- **S1 (now): PtySession ConPTY core + headless test** — spawn a process under ConPTY, read its
  output, assert we get it. Retires the #1 technical risk with no browser/UI.
- **S2: TerminalBroker + `/api/term/{id}` WS + minimal xterm.js page** — one web viewer drives a
  server-spawned `pwsh`. Proves the shared-terminal core end to end on localhost.
- **S3: `clrterm` launcher** — local console is also a viewer; type from either side; scrollback replay.
- **S4: multi-viewer + reconnect ring buffer + caps (idle timeout, max sessions, output cap).**
- **S5: pairing/TTL + signed gate; ship through `/remote` (hl-auth) + phone e2e.**

## Honest constraints
- Can't attach to a console you started bare (no `reptyr` on Windows) → always launch via `clrterm`.
- The session is a child of the server (broker-owned PTY); cwd/env passed at spawn.
- ConPTY needs Win10 1809+. The Server target is net8.0; PtySession guards `OperatingSystem.IsWindows()`.
