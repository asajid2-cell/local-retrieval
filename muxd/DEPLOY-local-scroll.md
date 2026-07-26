# Deploying the local-attach scroll fix (branch `fix/local-attach-wheel-scroll`)

## What ships together

- `muxctl.py` — wheel forwarding (mode tracker + `ReadConsoleInputW` reader). Takes effect on the
  **next** `mux <name>` attach; running attaches keep the old code until reattached.
- `muxd.py` — `REPLAY_PRIVATE_MODES` now replays the mouse-tracking family + DECCKM, and
  `attach_replay_payload()` sends the mode prefix even at `sb=0`. Takes effect only after a
  **muxd restart**.

## How the local wheel scrolls an alt-screen TUI (and why)

muxctl now defaults to what a real local terminal does: `DEFAULT_ALT_SCROLL = "sgr"`. The
priority ladder in `wheel_input_sequences()` is, in order:

1. **App tracks the mouse** → one wheel report per notch (SGR when `?1006`, urxvt when `?1015`,
   else X10), **including on the alternate screen**. This is the common codex/claude cell.
2. **Bare alternate screen WITH DECSET 1007** (alternateScroll) → 3 arrow keys per notch, in SS3
   form under DECCKM (`?1`), matching xterm/xterm.js.
3. **Bare alternate screen WITHOUT 1007** → a single PageUp/PageDown per wheel *event*,
   rate-limited to one per 90ms (`ConsoleInputTranslator.PAGE_SCROLL_INTERVAL`). A real terminal
   would drop the notch here; the page key is kept purely as a compatibility fallback so
   bare-alt pagers (`less`, `man`) still scroll.
4. **Normal screen, no tracking** → nothing.

`docs/scroll-parity.json` is the authoritative contract for that table — every state/mechanism
cell is asserted cell-by-cell by `muxd/tests/test_local_scroll_forwarding.py`. Change behavior
there first.

`MUXCTL_WHEEL=pagekeys` is now the **forced compatibility override**, not the default: it
restores the old alt-screen-first order (the alternate screen always takes a page key, even when
the app tracks the mouse), for a TUI that scrolls from page keys but not from wheel reports. An
unrecognized `MUXCTL_WHEEL` value normalizes back to `sgr`. Mode `1007` is in muxctl's
`TRACKED_MODES` and was already in `muxd.REPLAY_PRIVATE_MODES`, so this sgr-priority flip is muxctl-only and
takes effect on the next attach — no daemon restart needed for the flip itself.
(The muxd restart in “Required order” below covers the separate `muxd.py`
replay-prefix change and full mode fidelity for long-lived sessions.) **Exactly one mechanism fires per notch in every state/mode
combination** (unit-tested exhaustively). Plain shells keep conhost's native wheel scrollback
untouched (the wheel is not even captured there).

## Required order

Merge the branch, then **restart muxd immediately** (safe path: the `MuxdSessionHostRestart`
scheduled task / `ops/restart_muxd.ps1`, which refuses while a hosted session is mid-activity).

Why the order matters — and why it matters MORE than it used to. Under the old page-key default
the alt-screen mode (`?1049h`) alone was enough to scroll, and the OLD muxd already replays
that, so the headline fix worked even before the restart. Under the `sgr` default it is the
mouse-tracking modes (`?1000h..?1006h`) that classify the common codex/claude cell into rung 1.
Tracking state that scrolled out of the replay ring is only restored by the NEW muxd's mode
prefix; without it a long-lived session misclassifies as a bare alt screen and falls to the
page-key fallback. Full mode fidelity is now a dependency of the headline fix, not a refinement
of it. Don't leave the gap open.

This branch also carries the previously uncommitted live hotfix (`CREATE_NO_WINDOW` on the CIM
agent-cmdline probe), so merging does not revert the running behavior.

## Intentional behavior notes

- **Web text selection (decided, no code change):** replaying `?1000h..?1006h` to the web xterm
  means selection needs Shift-drag while a TUI has the mouse — xterm/tmux convention, and the web
  UI already documents it ("If the app has grabbed the mouse, hold Shift to select"). Its input
  filter (`stripMouseReports`, `relay/public/index.html:1527-1538`) strips X10 reports
  (`\x1b[M` + 3 bytes) and urxvt/1015 reports (`\x1b[b;x;yM`-form) wholesale — wheel forms
  included. Only the SGR (?1006) wheel exemption survives: the `((+cb) & 64) ? m : ''` guard
  keeps wheel reports while still stripping the motion/drag/click flood, so from the web
  surface only an app negotiating SGR scrolls from wheel reports; X10 or urxvt wheel never
  reaches the pty. This is state *fidelity*: the same session freshly attached before the
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

1. Wheel-scroll a local claude/codex attach — it should scroll from wheel REPORTS (rung 1): the
   transcript moves once per notch, typing intact. This is the same mechanism the web surface
   uses, since its input filter keeps wheel reports. Then check `less` on a bare alt screen — it
   still pages exactly once per notch via the rung-3 fallback. If a specific TUI scrolls from
   page keys but not from reports, `MUXCTL_WHEEL=pagekeys` is the escape hatch for that attach.
2. Plain shell attach — wheel still scrolls conhost's native scrollback; after a TUI exits,
   native scrollback resumes.
3. `less` (bare alt screen) — wheel pages exactly once per notch, not doubled. If any terminal
   doubles it, that terminal synthesizes alt-scroll in win32 input mode too — set
   `MUXCTL_VT_INPUT=1` there (which hands the wheel entirely to the terminal) and report it.
4. Ctrl-] detach still restores normal console behavior.
