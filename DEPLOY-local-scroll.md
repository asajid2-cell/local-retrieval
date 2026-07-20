# Deploying the local-attach scroll fix (branch `fix/local-attach-wheel-scroll`)

## What ships together

- `muxctl.py` — wheel forwarding (mode tracker + `ReadConsoleInputW` reader). Takes effect on the
  **next** `mux <name>` attach; running attaches keep the old code until reattached.
- `muxd.py` — `REPLAY_PRIVATE_MODES` now replays the mouse-tracking family + DECCKM, and
  `attach_replay_payload()` sends the mode prefix even at `sb=0`. Takes effect only after a
  **muxd restart**.

## Required order

Merge the branch, then **restart muxd immediately** (safe path: the `MuxdSessionHostRestart`
scheduled task / `ops/restart_muxd.ps1`, which refuses while a hosted session is mid-activity).

Why the order matters: new muxctl against the OLD running muxd works, but for a long-lived TUI
whose `?1000h/?1006h` enables have scrolled out of the replay window the client cannot know mouse
tracking is on, so the wheel falls back to arrow keys — in claude that navigates prompt history
instead of scrolling the transcript. The restart closes that window; until then the local attach
is no worse than the web terminal in the same state-loss situation, but don't leave the gap open.

This branch also carries the previously uncommitted live hotfix (`CREATE_NO_WINDOW` on the CIM
agent-cmdline probe), so merging does not revert the running behavior.

## Intentional behavior notes

- **Web text selection (decided, no code change):** replaying `?1000h..?1006h` to the web xterm
  means selection needs Shift-drag while a TUI has the mouse — xterm/tmux convention, and the web
  UI already documents it ("If the app has grabbed the mouse, hold Shift to select"). Its input
  filter strips the click/motion reports this re-enables (wheel reports are kept — they are the
  scroll mechanism). This is state *fidelity*: the same session freshly attached before the ring
  rotated always behaved this way.
- **DECCKM (`?1`) replayed:** viewers now pick the correct arrow encoding (SS3 vs CSI). If a TUI
  dies without resetting it, readline accepts both arrow forms, so a shell can't get wedged.
- **`MUXCTL_VT_INPUT=1` opt-in:** muxctl suppresses its own bare-alt-screen arrow fallback under
  this env var, because a VT-input-mode terminal may synthesize alternate-scroll arrows itself —
  one wheel notch must never scroll twice. Default mode (VT input off) delivers a wheel notch as
  exactly one win32 `MOUSE_EVENT` and no synthesized arrows (synthesis lives in the VT-input
  translation layer: conhost `TerminalInput` / Windows Terminal alternateScroll→ConPTY).

## Human GUI checks after deploy (cannot be verified headlessly)

1. Wheel-scroll a local claude/codex attach — transcript scrolls both directions, typing intact.
2. Plain shell attach — wheel still scrolls conhost's native scrollback; after a TUI exits,
   native scrollback resumes.
3. `less` (bare alt screen, no mouse tracking) — wheel scrolls exactly ONE step per notch
   (3 lines), not doubled. If doubled on some terminal, that terminal synthesizes alt-scroll in
   win32 input mode too — set `MUXCTL_VT_INPUT=1` there or report it so the fallback can key off
   a runtime probe instead of the env var.
4. Ctrl-] detach still restores normal console behavior.
