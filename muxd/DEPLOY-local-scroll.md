# Deploying the local-attach scroll fix (branch `fix/local-attach-wheel-scroll`)

## What ships together

- `muxctl.py` — wheel forwarding (mode tracker + `ReadConsoleInputW` reader). Takes effect on the
  **next** `mux <name>` attach; running attaches keep the old code until reattached.
- `muxd.py` — `REPLAY_PRIVATE_MODES` now replays the mouse-tracking family + DECCKM, and
  `attach_replay_payload()` sends the mode prefix even at `sb=0`. Takes effect only after a
  **muxd restart**.

## How the local wheel scrolls an alt-screen TUI (and why)

**The proven mechanism in this stack is PAGE KEYS, not mouse reports.** The web frontend
(`multiplex-app-patch/public/index.html`, build `2026-07-12-mobile-ime-viewport-hardening`)
strips ALL mouse reports from input (`stripMouseReports`, ~line 1466 — X10, SGR, urxvt, no
exceptions) and scrolls alternate-screen TUIs via `scrollAlternate()` (~line 1282): one
`\x1b[5~` (PageUp) / `\x1b[6~` (PageDown) per wheel event, rate-limited to one per 90ms. That is
what demonstrably scrolls claude/codex on the web. (A newer working-copy build,
`2026-07-15-wheelscroll` on the Z: drive, forwards SGR wheel reports instead — but that
mechanism is NOT the proven one and earlier notes on this branch wrongly claimed it was.)

The local attach therefore defaults to the same page-key translation, with the same 90ms rate
limit (`MUXCTL_WHEEL=pagekeys`). SGR/urxvt/X10 wheel reports — the standard mouse-tracking
contract — remain fully implemented: they are always used when an app tracks the mouse WITHOUT
the alternate screen, and `MUXCTL_WHEEL=sgr` selects them on the alternate screen too, for a TUI
known to scroll from wheel reports. In `sgr` mode a bare alt screen (no tracking) falls back to
3 arrow keys per notch (xterm.js's own fallback). **Exactly one mechanism fires per notch in
every state/mode combination** (unit-tested exhaustively). Plain shells keep conhost's native
wheel scrollback untouched (the wheel is not even captured there).

## Required order

Merge the branch, then **restart muxd immediately** (safe path: the `MuxdSessionHostRestart`
scheduled task / `ops/restart_muxd.ps1`, which refuses while a hosted session is mid-activity).

Why the order matters: with the page-key default the alt-screen state (`?1049h`) is enough to
scroll, and the OLD muxd already replays that — so the headline fix works even before the
restart. The restart matters for full mode fidelity on long-lived sessions: mouse-tracking state
(`?1000h..?1006h`) that scrolled out of the replay ring is only restored by the NEW muxd's
prefix, and without it a long-lived tracking-without-alt app or `MUXCTL_WHEEL=sgr` use would
misclassify. Don't leave the gap open.

This branch also carries the previously uncommitted live hotfix (`CREATE_NO_WINDOW` on the CIM
agent-cmdline probe), so merging does not revert the running behavior.

## Intentional behavior notes

- **Web text selection (decided, no code change):** replaying `?1000h..?1006h` to the web xterm
  means selection needs Shift-drag while a TUI has the mouse — xterm/tmux convention, and the web
  UI already documents it ("If the app has grabbed the mouse, hold Shift to select"). Its input
  filter strips all mouse reports regardless, so replayed modes cannot inject report bytes into
  the pty from the web. This is state *fidelity*: the same session freshly attached before the
  ring rotated always behaved this way.
- **DECCKM (`?1`) replayed:** viewers now pick the correct arrow encoding (SS3 vs CSI). If a TUI
  dies without resetting it, readline accepts both arrow forms, so a shell can't get wedged.
- **`MUXCTL_VT_INPUT=1` opt-in:** muxctl disables ALL of its wheel synthesis under this env var
  (page keys, reports, arrows), because a VT-input-mode terminal owns the wheel and may
  synthesize its own sequences — one wheel notch must never scroll twice. Default mode (VT input
  off) delivers a wheel notch as exactly one win32 `MOUSE_EVENT` and no synthesized sequences
  (synthesis lives in the VT-input translation layer: conhost `TerminalInput` / Windows Terminal
  alternateScroll→ConPTY).

## Human GUI checks after deploy (cannot be verified headlessly)

1. Wheel-scroll a local claude/codex attach — transcript pages up/down (PageUp/PageDown per
   notch, max ~11/sec), typing intact. This is the same mechanism the web provably uses; the
   check is confirmation, not a coin flip. If a specific TUI scrolls better from wheel reports,
   set `MUXCTL_WHEEL=sgr` for that attach.
2. Plain shell attach — wheel still scrolls conhost's native scrollback; after a TUI exits,
   native scrollback resumes.
3. `less` (bare alt screen) — wheel pages exactly once per notch, not doubled. If any terminal
   doubles it, that terminal synthesizes alt-scroll in win32 input mode too — set
   `MUXCTL_VT_INPUT=1` there (which hands the wheel entirely to the terminal) and report it.
4. Ctrl-] detach still restores normal console behavior.
