# PRONG: mux-native — make both surfaces behave like a real local terminal

Plan owner: mux-native prong. Product tree: `Z:\328\CMPUT328-A2\codexworks\301\mux-local-retrieval`
(READ-ONLY for this planner; implementation happens via orch leaves in the campaign worktree).

Sources read (build on, don't re-derive): `muxd/muxctl.py` (attach client + wheel translation),
`muxd/muxd.py` (ring/replay: `RING_CAP=800_000`, `SB_SEND=260_000`, `LOCAL_SB_SEND=60_000`,
`REPLAY_PRIVATE_MODES` incl. 1007, `attach_replay_payload` at muxd.py:1248),
`relay/public/index.html` (build `2026-07-15-wheelscroll`: xterm scrollback:60000, capture-phase
wheel router ~line 1298, `stripMouseReports` ~1432 keeps wheel reports, selection/freeze/copy
overlay ~2490), `muxd/DEPLOY-local-scroll.md`, `muxd/PLAN.md`, `relay/state-and-next.md`,
existing harnesses `muxd/tests/*` (pytest) and `relay/tests/*` (node:test, vm-extract of real
functions from index.html + a real-relay+fake-muxd integration rig in `relay.test.js`).

---

## 0. The honest architecture answer: the alt-screen problem

**Fact:** content that scrolls out of an alternate-screen viewport is destroyed *inside the app*.
The TUI repaints in place; the discarded transcript lines never cross the PTY in any form a host
could retain. This is why tmux — the gold standard we're chasing — gives the alternate screen **no
history at all**. There is no faithful passive capture of an alt-screen TUI's transcript. Anything
that pretends otherwise (frame-history scrubbing, screen-diff reconstruction) is an approximation,
and we don't ship approximations as scrollback.

**What a real local terminal actually does** (Windows Terminal, iTerm2, xterm):
1. App tracks the mouse (DECSET 1000/1002 + 1006 — codex does this) → wheel is delivered as **SGR
   wheel reports**; the app scrolls its own transcript. This is native behavior.
2. Bare alt screen, app set DECSET 1007 (alternateScroll) → wheel becomes **arrow keys**.
3. Bare alt screen, no 1007 → wheel does nothing (or terminal-configurable alt-scroll).
4. Normal screen → the terminal scrolls its **own scrollback**; the app never sees the wheel.

**Verdict:** the page-key hack (`MUXCTL_WHEEL=pagekeys`, translator in `muxctl.py`
`wheel_input_sequences`, alt-screen branch wins over tracking) was a band-aid dating from when the
web input filter stripped ALL mouse reports, so wheel reports couldn't work end-to-end. That era is
over: the shipped web build (`2026-07-15-wheelscroll`) forwards trusted wheel events to xterm in
the alternate buffer and `stripMouseReports` explicitly **keeps** wheel reports (Cb bit 6), with a
regression suite (`relay/tests/wheel-scroll.test.js`). The faithful architecture is therefore:

> **Forward scroll intent natively (the real-terminal priority order above) on BOTH surfaces;
> never fake alt-screen scrollback. Invest real-scrollback effort where it's actually winnable:
> the normal buffer (muxd ring → conhost/xterm scrollback), whose remaining gaps are raw-byte
> replay garbling on width change and unstructured history export — fixed long-term by a
> muxd-side terminal model (deferred, kind: plan).**

Concretely: the local attach's remaining infidelity is that `wheel_input_sequences` prefers page
keys **even when the app tracks the mouse** (`tracker.alt_screen and alt_scroll != "sgr"` fires
first). A real terminal gives codex wheel reports. Flip the priority; keep page keys only as the
bare-alt-no-1007 fallback and as an env-forced compatibility mode. `REPLAY_PRIVATE_MODES` already
replays 1000–1015 + 1007, so mid-session attaches classify correctly — no muxd daemon change is
needed for this (important: avoids the restart-discipline cost in `PLAN.md`).

**The flood-bug constraint (why input filtering stays):** the historic "mouse thing" — a leaked
tracking mode after a TUI dies floods the pty with motion/click reports the shell echoes back —
is real and the guard must survive. Wheel reports are discrete and low-volume; motion reports are
the flood. Click forwarding is deferred behind a leak-detector design (leaf G).

---

## 1. Win condition (measurable)

The remoted terminal is behaviorally indistinguishable from a local one for the core loop, on both
surfaces, with every claim either machine-verified or on the explicit human-GUI checklist:

1. **Scroll fidelity matrix green:** for every state in {normal, alt} × {tracking on/off} ×
   {1007 on/off} × {surface: muxctl, web}, exactly one scroll mechanism fires per wheel notch and
   it is the one a real local terminal would use. Encoded in a shared vector file, asserted by
   `python -m pytest muxd/tests -q -k scroll` AND `node --test relay/tests/` — both green.
2. **Selection + copy:** drag-select and copy work on the web viewer (desktop drag + Ctrl/⌘-C or
   copy affordance; phone via existing selectMode/freeze) and on the local attach (conhost
   QuickEdit drag when the app doesn't own the mouse; WT native selection always). Machine checks
   green + human checklist items signed off.
3. **Visible scroll position:** the web viewer shows a visible scrollbar/position indicator in the
   normal buffer and an explicit "app-controlled scroll" state in the alternate buffer (never a
   dead scrollbar). Section-level tests green + human check.
4. **Latency budget exists and passes:** an executable probe publishes input→echo RTT p50/p95 for
   (a) local muxctl path and (b) web path via relay; local p95 ≤ 50 ms, relay-LAN p95 ≤ 120 ms
   budgets enforced by the probe's exit code (budgets tightened later only with data).
5. No regression: existing suites (`python -m pytest muxd -q`, `node --test relay/tests/`) stay
   green; `window.__muxBuild` bumped on any web change.

---

## 2. Leaves (one-level decomposition)

> Campaign constraints inherited from the tree: muxctl.py changes take effect on next attach (no
> daemon restart); muxd.py changes need the `MuxdSessionHostRestart` discipline (avoided in the
> first cut — only deferred leaf H touches muxd.py); web changes must bump `window.__muxBuild`.
> All verifiers run from repo root of the campaign worktree; all are bounded (pytest/node:test
> have no network deps except where env-gated; probe has hard timeouts).

### Leaf A — `muxctl-native-wheel`: real-terminal wheel priority in the local attach
- **goal:** Make `wheel_input_sequences` use the native priority order — mouse-tracking → wheel
  reports (SGR/urxvt/X10, even on the alt screen), bare alt + 1007 → arrows (3/notch, xterm.js
  parity, SS3 under DECCKM), bare alt without 1007 → page keys (rate-limited, unchanged), normal
  screen → nothing — while keeping the exactly-one-mechanism-per-notch invariant and
  `MUXCTL_WHEEL=pagekeys` as a forced compatibility override (`sgr` becomes the default; keep the
  env accepted).
- **area/files:** `muxd/muxctl.py` (`wheel_input_sequences`, `ConsoleInputTranslator`,
  `ScreenModeTracker` — add 1007 to tracked modes), `muxd/tests/test_local_scroll_forwarding.py`,
  `muxd/tests/test_console_input_roundtrip.py`; NEW `docs/scroll-parity.json` (the shared
  state→mechanism vector table, single source of truth for both surfaces).
- **verifier:** `python -m pytest muxd/tests/test_local_scroll_forwarding.py muxd/tests/test_console_input_roundtrip.py muxd/tests/test_muxctl_input_mode.py -q`
  — the forwarding test must be rewritten to load `docs/scroll-parity.json` and assert every cell
  of the full matrix (incl. exactly-one-mechanism and the 90 ms page-key rate limit surviving in
  fallback mode). Bounded, no network, no daemon.
- **human check:** wheel-scroll a local codex attach (transcript scrolls smoothly per-notch, not
  per-page); `less` still pages exactly once per notch; plain shell keeps conhost native
  scrollback; Ctrl-] detach clean. (These mirror `DEPLOY-local-scroll.md` §Human checks.)
- **size:** small (one function's priority order + tests + vector file). Self-contained.

### Leaf B — `web-scroll-parity`: lock the web surface to the same matrix
- **goal:** Extend the web regression suite to consume `docs/scroll-parity.json` so the two
  surfaces can never silently diverge again, and close the web's bare-alt gap (document/assert
  xterm.js's arrow fallback; assert the capture-phase router's invariants: trusted-only, no
  recursion, normal-buffer wheel never reaches the app, alt-buffer wheel always forwarded).
- **area/files:** `relay/tests/wheel-scroll.test.js`, `relay/tests/client-layout.test.js`
  (section assertions), `relay/public/index.html` ONLY if an assertion exposes a real divergence
  (then bump `__muxBuild`); reads `docs/scroll-parity.json`.
- **verifier:** `node --test relay/tests/wheel-scroll.test.js relay/tests/client-layout.test.js`
  — must include ≥1 test that fails if `docs/scroll-parity.json` and the shipped
  `stripMouseReports`/wheel-router behavior disagree on any wheel cell. Bounded, no network.
- **depends on:** A (vector file exists).
- **size:** small.

### Leaf C — `web-scrollbar`: visible scrollbar + honest alt-screen state
- **goal:** Give the web viewer a visible, styled scrollbar bound to xterm's scrollback in the
  normal buffer (style `.xterm-viewport` scrollbar: `scrollbar-width:thin` + webkit rules, theme
  colors), plus a compact position readout (e.g. "1 240 ↑" or N% chip co-located with the existing
  `#jumplive` pill), and in the alternate buffer HIDE the scrollbar and show an "app scroll" state
  chip instead — a dead scrollbar is worse than none.
- **area/files:** `relay/public/index.html` (CSS block near `#term` ~line 120, small JS hooking
  `term.onScroll`/`term.buffer.onBufferChange`, reuse `atBottom()`/`showJump()` plumbing); bump
  `__muxBuild`.
- **verifier:** `node --test relay/tests/client-layout.test.js` extended with section assertions:
  (1) a `.xterm-viewport` scrollbar rule exists and is NOT `display:none`, (2) the alt-buffer
  branch toggles the chip and hides the scrollbar (extract the toggle fn via the existing
  `extractFn` vm harness and drive it with a fake `term.buffer.active.type`), (3) position math
  (extracted pure fn: `(viewportY, baseY, rows) → label`) is correct at top/middle/bottom.
  Bounded.
- **human check (GUI-only):** scrollbar visible + draggable on desktop at Ahmed's real viewport
  (~1906×912 per standing memory), doesn't fight touch pan on phone, alt-screen shows the chip.
- **depends on:** nothing (but SAME FILE as D/G — serialize C→D within the prong).
- **size:** small-medium.

### Leaf D — `web-selection-copy`: selection that feels native on desktop
- **goal:** Make desktop selection first-class: drag-select in the normal buffer must survive
  streaming output (latch the existing freeze/selectMode machinery on `onSelectionChange` from a
  mouse drag, not just the explicit select mode), Ctrl/⌘+Shift+C copies the current xterm
  selection, auto-copy-on-select as a persisted toggle (`LS` setting), Shift+drag documented in
  the help sheet for alt-screen/tracking sessions, and the browser context menu not suppressed
  over a selection. Phone flow (selectMode + freeze + copy overlay) stays as-is.
- **area/files:** `relay/public/index.html` (selection/freeze block ~1459, copy helpers ~1037 &
  ~2290–2519, keybar/help text ~2343); bump `__muxBuild`.
- **verifier:** NEW `relay/tests/selection.test.js` (node:test + the `extractFn` vm harness):
  (1) the drag-latch fn freezes output-flush while a selection exists and flushes on clear,
  (2) the copy keybinding handler routes `term.getSelection()` → clipboard writer (stubbed) and
  falls back to the copy overlay when the clipboard API rejects, (3) auto-copy toggle round-trips
  through the `LS` shim, (4) section assertion: no `contextmenu` preventDefault over `#term`.
  Run: `node --test relay/tests/selection.test.js`. Bounded.
- **human check (GUI-only):** while codex streams output, drag-select 30 lines in a normal-buffer
  session → selection doesn't vanish, copy lands in clipboard; Shift+drag selects in an
  alt-screen codex tab; long-press flow on phone unchanged.
- **depends on:** C (same file; serialize). Independent of A/B.
- **size:** medium.

### Leaf E — `muxctl-local-selection`: restore drag-select in the local attach
- **goal:** In classic conhost, `attach_input_mode` clears `ENABLE_QUICK_EDIT_MODE`
  unconditionally (muxctl.py:56), killing native drag-select for plain shells. Restore QuickEdit
  whenever mouse capture is OFF (normal buffer — conhost owns the wheel anyway) and clear it only
  while capture is ON (alt/tracking, where `set_mouse_capture` needs the events); confirm Windows
  Terminal's own selection is unaffected in both states (WT handles selection above the console
  input mode). Note: QuickEdit pausing output during a drag is *native local behavior* — that's
  the point.
- **area/files:** `muxd/muxctl.py` (`attach_input_mode`, `set_mouse_capture`,
  `terminal_attach_mode`), `muxd/tests/test_muxctl_input_mode.py`.
- **verifier:** `python -m pytest muxd/tests/test_muxctl_input_mode.py -q` — matrix over
  (capture on/off × VT-input on/off): QUICK_EDIT set exactly when capture is off and
  MUXCTL_VT_INPUT unset; MOUSE_INPUT set exactly when capture is on; original mode restored on
  exit. Pure mode-math, no console needed. Bounded.
- **human check (GUI-only):** in a classic conhost window, drag-select + Enter copies from a plain
  shell attach; wheel still forwards inside codex; detach restores the original mode.
- **depends on:** A (touches the same file/nearby code — serialize A→E). 
- **size:** small.

### Leaf F — `latency-probe`: measurable "feels local" budget
- **goal:** Build `scripts/latency_probe.py`: opens a throwaway muxd session running a pure echo
  child (`cmd /q /k` or python echo loop), sends single-byte inputs over (a) the local muxd ws
  (`ws://127.0.0.1:7699`, same path muxctl uses) and (b) optionally the relay ws path (env-gated
  `MUXD_VPS_TESTS=1`, reusing the smoke-test plumbing in `muxd/tests/test_vps_smoke.py`), measures
  input→matching-output RTT over ≥200 samples, prints p50/p95/p99, and exits non-zero if over
  budget (`--budget-p95-ms`, default 50 local / 120 relay). Hard `--timeout 60` wall clamp; always
  kills its session (reuse the leak-hygiene pattern the repo already learned). This leaf is
  measure-only: no tuning until numbers exist.
- **area/files:** NEW `scripts/latency_probe.py`, NEW `muxd/tests/test_latency_probe.py` (probe
  logic unit-tested with a loopback fake: sample matching, percentile math, timeout kill).
- **verifier:** `python -m pytest muxd/tests/test_latency_probe.py -q` AND
  `python scripts/latency_probe.py --local --samples 100 --budget-p95-ms 50 --timeout 60`
  (requires a running muxd on the PC runner — the campaign's runner has one; probe auto-starts
  via the scheduled task exactly like muxctl does, and exits 3 with a clear message if unavailable
  so the failure mode is diagnosable, not a hang). Bounded by the wall clamp.
- **depends on:** nothing. Fully parallel.
- **size:** medium.

### Leaf G — `web-click-forwarding` (DEFERRED from first cut): scoped mouse-report guard v2
- **goal:** Forward discrete click/release SGR reports (not motion) to alt-screen tracking apps so
  click-to-position/click-to-focus works on the web, WITHOUT reopening the leaked-mode flood: add
  a leak detector — if the session *output* echoes back our own report bytes (the signature of a
  dead TUI + live shell), auto-send DECRST 1000/1002/1003/1006 and stop forwarding until the next
  DECSET. Requires a small mode-tracker in the web client (mirror of muxctl's
  `ScreenModeTracker`).
- **area/files:** `relay/public/index.html` (`stripMouseReports` → v2 + tracker),
  `relay/tests/wheel-scroll.test.js`.
- **verifier:** `node --test relay/tests/wheel-scroll.test.js` extended: clicks pass when tracker
  says tracking-active, motion always stripped, echoed-report detection triggers DECRST + drop.
- **depends on:** B, D (same file + same test file). **Deferred: the flood bug killed sessions
  before; ship the first cut, then take this with the detector designed in.**
- **size:** medium, higher risk.

### Leaf H — `muxd-terminal-model` (kind: plan — needs its own decomposition)
- **goal (north star, phase 2):** a muxd-side structured terminal model (pyte or equivalent VT
  emulator fed from the session ring tap) giving: (1) resize-correct attach repaint (today
  `attach_replay_payload` replays raw bytes — correct at same width, garbles wrapped lines on a
  width change), (2) full-history *text* export (`sbtext` cap: rendered lines, not raw VT bytes —
  upgrades the copy overlay and enables search), (3) accurate screen snapshots for status/process
  truth. Explicit non-goals: alt-screen transcript recovery (impossible — see §0), replacing the
  raw ring (it stays; the model is a sidecar).
- **why not a leaf:** touches `muxd/muxd.py` (restart discipline!), needs an emulator-fidelity
  spike (ConPTY VT dialect vs pyte), memory bounds per PLAN.md's bounded-resources contract, and
  its own test strategy. Too big for one sitting → the campaign should schedule a dedicated
  `orch plan` node seeded with §0 + this section.
- **decomposition sketch for that plan node:** H1 fidelity spike (feed recorded ring bytes from a
  real codex/claude session through pyte, diff final screen vs xterm.js headless — go/no-go),
  H2 sidecar model + `snapshot` cap behind a feature flag (no attach-path change), H3 attach
  repaint from snapshot when viewer size ≠ ring size, H4 `sbtext`/search + web history overlay.

### Leaf I — `output-pump-tuning` (DEFERRED, data-gated)
Tuning (relay write coalescing, xterm write-queue slicing `WRITE_SLICE=65536`, muxctl 4 ms input
batching) only if leaf F's numbers show a budget miss, and only against F's probe as the
before/after gate. Not planned further until F reports.

---

## 3. Dependencies & parallelism

```
A ──► B          (shared docs/scroll-parity.json)
A ──► E          (same file muxd/muxctl.py — serialize)
C ──► D ──► (G)  (same file relay/public/index.html — serialize within prong)
F                (independent, fully parallel)
(H) plan node    (independent of all; schedule after first cut)
```
Parallel tracks: {A→B→E}, {C→D}, {F} can run as three concurrent lanes. G/H/I deferred.

## 4. Ranking + bounded first cut

Value/effort order: **A** (the core fidelity fix, tiny and fully testable) → **C** (biggest
visible "feels local" win) → **D** (selection/copy is the #2 daily pain) → **F** (turns "feels
fast" into a number before anyone tunes blind) → **B** (locks parity so it can't rot) → **E**
(local selection, small).

**First cut = A, B, C, D, E, F** (6 leaves, 3 parallel lanes, no muxd.py edits, no daemon
restart, no VPS deploy dependency except the normal web-file deploy the integration lane owns).

**Deferred explicitly:** G (click forwarding — needs the leak detector, flood-bug risk),
H (terminal model — kind: plan, the only true fix for resize-garble + rich history, and the only
muxd.py toucher), I (latency tuning — gated on F's data), and any form of alt-screen scrollback
capture (rejected as unfaithful, see §0 — this is a decision, not a deferral).

## 5. Cross-prong seams (for reconcile)

- **`relay/public/index.html`** — leaves C, D, (G). One giant file every prong is tempted to
  touch (value prong: UX features; adversarial prong: input-filter hardening). Reconcile should
  assign ONE owner per time-slice for this file; this prong's edits are the wheel router,
  selection/copy block, `#term` CSS, help text, `__muxBuild`.
- **`relay/tests/*.js`** — B, D, (G) extend `wheel-scroll.test.js` / `client-layout.test.js` /
  new `selection.test.js`; adversarial prong likely adds here too. Merge-friendly (new test fns),
  but same-file appends should be sequenced.
- **`muxd/muxctl.py`** — A, E. Possible overlap with a win32-perf prong (input/output threads,
  batching). Seam: this prong owns the *translation semantics*; perf prong should own throughput.
- **`docs/scroll-parity.json`** — NEW, owned by this prong; other prongs read-only.
- **`muxd/muxd.py` + restart discipline** — untouched in the first cut (deliberate); leaf H's
  plan node will need coordination with the integration lane and the `MuxdSessionHostRestart`
  procedure.
- **`relay/server.js` / durable-state / app-commands** — NOT touched by this prong at all; owned
  by the integration lane's reliability work. Latency probe (F) only *connects* to existing
  endpoints.
- **Human-GUI checklist** — leaves A, C, D, E each emit 1–3 GUI-only checks (marked above);
  campaign should batch them into one human session rather than gating each leaf on Ahmed.
