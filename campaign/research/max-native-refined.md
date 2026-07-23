# LANE max-native — refined leaf set (real-local-terminal fidelity)

Refines `campaign/mux-native.md` under the campaign governing calls. §0 of the base plan (the
alt-screen doctrine) STANDS unchanged: no faked alt-screen scrollback, ever — forward scroll
intent natively; invest real-scrollback effort only where it is winnable (the normal buffer and
the muxd-side terminal model).

## Validation

Everything below verified against HEAD 7968259 (tree clean) on 2026-07-22.

### Anchors verified (with drift corrections)

| Base-plan anchor | Current tree | Status |
|---|---|---|
| `wheel_input_sequences` in muxctl.py | `muxd/muxctl.py:196`; alt-screen branch fires first at :217 (`tracker.alt_screen and alt_scroll != "sgr"`); default `alt_scroll="pagekeys"` wired from `MUXCTL_WHEEL` at :660 | ✔ as described |
| `ScreenModeTracker` | `muxd/muxctl.py:126`; tracks 47/1047/1049 + 9/1000/1002/1003 + 1006/1015 + DECCKM(1). **1007 is NOT tracked** — leaf A must add it | ✔ |
| QuickEdit cleared unconditionally | `attach_input_mode` `muxd/muxctl.py:54-57` clears `ENABLE_QUICK_EDIT_MODE` always; `set_mouse_capture` at :353, toggled from `wants_mouse_capture()` in the attach loop at :617-624 | ✔ leaf E premise holds |
| `attach_replay_payload` muxd.py:1248 | `muxd/muxd.py:1248` exactly | ✔ no drift |
| `RING_CAP=800_000`, `SB_SEND=260_000`, `LOCAL_SB_SEND=60_000` | `muxd/muxd.py:894-896` | ✔ |
| `REPLAY_PRIVATE_MODES` incl. 1007 | `muxd/muxd.py:920`: {1, 47, 1047, 1049, 2004, 9, 1000, 1002, 1003, 1005, 1006, 1007, 1015} | ✔ |
| Web build tag `2026-07-15-wheelscroll` | **DRIFT.** `relay/public/index.html:1015` = `'2026-07-12-mobile-ime-viewport-hardening'`. The *functionality* the base plan attributes to the 07-15 build IS present (see next rows) but the tag was never bumped past 07-12 through the c61dbab merge / 4c079d1 restore | ⚠ tag stale |
| wheel router ~1298 | `index.html:1393-1419` (capture-phase, `isTrusted` guard at :1394, alt-buffer forwarding at :1407-1418). Touch path scrolls alt via `scrollAlternate()` at :1282-1287 (page keys, 90 ms limit) | ⚠ line drift only |
| `stripMouseReports` ~1432 | `index.html:1527-1538` — keeps wheel (Cb bit 64) at :1536, strips X10/SGR-non-wheel/urxvt. **~1432 is actually `stripTerminalGeneratedReports`** (:1432-1440), a different live-path filter — this misdirection matters for the hang story (Task 2 below) | ⚠ drifted + conflated |
| xterm scrollback:60000 | `index.html:741` | ✔ |
| input send no-op ~1091 | **DRIFT**: `const send = (t,d) => { if (ws && ws.readyState===1) ws.send(t+d); };` is at `index.html:1180`. :1091 is now IME composition code | ⚠ |
| xterm input routes to send | `term.onData` :1539-1556 → `send('i', d)` :1555; keybar `sendKey/sendChar/sendRaw` :2354-2356 | ✔ |
| compose closes dialog before paste ~2116-2117 | **DRIFT**: `index.html:2198-2199` (`$('#sendcompose')`/`$('#sendcomposeenter')` — `.close()` runs before `term.paste(t)`) | ⚠ |
| selection/freeze ~1459, copy ~2290-2519 | freeze state :1199-1203 (`selectMode`/`autoFreeze`/`flushFrozen`), drag-latch listeners :982-990 and :1133-1140, copy helpers :1125-1128 / :2374-2377, selectMode toggle :2384-2389, copy-all overlay :2574-2596 | ⚠ line drift only |
| relay tests | `relay/tests/relay.test.js`, `wheel-scroll.test.js` (vm `extractFn` harness :14-26), `client-layout.test.js` (section-assertion harness) — all present | ✔ |
| muxd tests | `muxd/tests/`: test_console_input_roundtrip, test_local_scroll_forwarding, test_muxctl_input_mode, test_muxd_local_integration, test_muxd_state, test_vps_smoke | ✔ |
| Leaf F muxd-availability assumption | muxctl auto-starts the `MuxdSessionHost` scheduled task (`muxd/muxctl.py:426-445`), port 7699 (:13). The probe can copy this pattern; exits diagnosably if the task is absent | ✔ holds |

### Base-plan assumptions now false or stale

1. **"Shipped web build `2026-07-15-wheelscroll`"** — that tag never landed; the current file
   carries the wheel-forwarding router AND the wheel-keeping `stripMouseReports` under the older
   `2026-07-12` tag. Consequence: any leaf that gates on the build tag must gate on *behavior*
   (the parity tests), not the tag string. First index.html leaf to merge should also bump the tag.
2. **`muxd/DEPLOY-local-scroll.md` is stale about the web surface** — it states the web build
   "strips ALL mouse reports … no exceptions" and scrolls alt via page keys only. True for the
   pre-merge live build; false for the current tree (wheel reports are kept, wheel events are
   forwarded). Its muxctl-side content is still accurate. Doc refresh folded into leaf A.
3. **"relay suite can HANG in the stripMouseReports area"** — does not reproduce as stated; see
   Task 2 findings next.
4. `relay/package.json` has **no test script** (`"test": "echo \"Error: no test specified\" && exit 1"`)
   — "`npm test` is unsafe" is moot; there is no `npm test`.
5. `docs/` does not exist at repo root yet — leaf A creates it with `docs/scroll-parity.json`.
6. pyte is importable in the repo's Python; `@xterm/headless` is NOT in `relay/node_modules`
   (only express/ws deps) — leaf H1 must add it as a devDependency.

### Task 2 findings — the "stripMouseReports hang", characterized

Commands run (all bounded):

- `node --test --test-force-exit --test-timeout=20000 tests/wheel-scroll.test.js` → 3/3 pass, 190 ms.
  The three `stripMouseReports` tests are here and are fast. **No hang in this file.**
- `node --test --test-force-exit --test-timeout=20000 tests/relay.test.js` → all subtests that get
  to run PASS, then the **file-level** test reports
  `failureType: 'testTimeoutFailure', error: 'test timed out after 20000ms'` and remaining
  subtests are cancelled. With `--test-timeout=120000` → **79 tests, 78 pass, 1 skip, 0 fail,
  duration 78.2 s**. Without `--test-force-exit` (bounded by `timeout 150`) → exits **0** in 71.7 s.
- Conclusion: **there is no hanging/looping test today.** The observed "hang" is relay.test.js's
  cumulative 72–78 s wall time tripping any per-test timeout < ~80 s at the *file* level (node
  counts the whole file as one test). It pattern-matches to a hang but is slowness in the
  real-relay+fake-muxd integration rig. `stripMouseReports` itself (:1527-1538) is three
  `String.replace` calls with linear regexes — no loop exists to get stuck.
- **However, a real pathological-input freeze exists on the live input path**, in the function the
  directive's ~1432 anchor actually points at: `takeTerminalReport` (:1424-1430), called per
  character by `stripTerminalGeneratedReports` (:1432-1440), which runs on every `term.onData`
  chunk (:1545) — i.e. on every keystroke and every paste. The OSC pattern
  `^\x1b\][0-9;]+;[^\x07\x1b]*(?:\x07|\x1b\\)` backtracks superquadratically when the input is
  `ESC ]` followed by a long `[0-9;]` run containing semicolons with **no BEL/ST terminator**
  (every split of the run between `[0-9;]+;` and `[^\x07\x1b]*` is retried, each with an O(n)
  tail scan). Measured (vm-extracted real functions, this machine):
  `ESC ] + ';'×20000` → 201 ms; ×40000 → 789 ms; ×80000 → 4 861 ms (growth ≈ n^2.5). A few
  hundred KB — e.g. re-pasting captured terminal output whose OSC terminator got clipped — freezes
  the browser main thread for minutes-to-hours: a user-visible hard hang, though technically
  finite. The loop index itself always advances (no true infinite loop); the blowup is inside one
  regex attempt. Plain large pastes are fine (200 KB plain → 76 ms; V8 sliced strings keep the
  per-char `s.slice(i)` cheap). Fix is leaf nat-S1.

### Task 4 decisions (A–F refresh + G)

- **A→B stays** (semantic: `docs/scroll-parity.json` contract must exist before B consumes it).
- **A→E dropped.** E changes `attach_input_mode`/`set_mouse_capture` mode math; A changes
  `wheel_input_sequences`/tracker. No contract flows between them — the QuickEdit decision keys off
  `wants_mouse_capture()`, which A does not alter (1007 is not in `ALT_SCREEN_MODES|MOUSE_TRACK_MODES`).
  Same-file only → branch-per-leaf handles it at merge.
- **C→D dropped** — pure same-file serialization, superseded by governing call 1.
- **Leaf G: ENTER THE TREE NOW.** Under the "full native" bar, click-to-position/click-to-focus in
  alt-screen TUIs is native behavior we currently amputate on the web. The historic flood was
  *motion* reports echoed by a dead TUI's shell (per the guard's own comment at :1522-1526);
  motion stays stripped forever. The leak-detector design is pinned in the leaf goal (echo-detect →
  DECRST + stop-forwarding), it is fully unit-testable in the existing vm harness, and deploys are
  free this campaign. Deferring it again just moves the same risk to a worse time.
- **Leaf F kept**; runner assumption verified above. Budgets stay 50/120 ms p95 until F reports data.
- **H needs NO kind:"plan" node.** The only genuine open design decision is
  *which emulator + snapshot format + reflow strategy* — exactly what the H1 spike produces
  evidence for and the deep-tier judge nat-H1J pins. H2–H4 are spec-tight modulo that pinned
  interface, so they are build leaves gated on H1J. (Named decision: EMULATOR / SNAPSHOT_FORMAT /
  REFLOW / MEMORY_BOUND in `campaign/research/decisions/nat-h-model.md`.)

### H grounding (verified)

- Ring tap point: `Session` output path `muxd/muxd.py:1540-1543` already calls
  `self.replay_state.ingest(b)` then `self._append_ring(b)` (:1555) — the model sidecar ingests at
  the same seam. NOTE: `OwnerSession` (:1735) duplicates the ring code (:1793, :1842) and needs the
  same tap.
- Replay/prefix machinery: `TerminalReplayState` :1379-1414; `attach_replay_payload` :1248-1255;
  `redraw_nudge` :1257-1281 (alt-screen SIGWINCH wiggle — stays, it is the alt-screen answer).
- Protocol seams for new caps: local attach ws handler :3626-3668 (attach `sb` at :3636, per-msg
  `i`/`resize` at :3653-3662); relay-side ops `i`/`resize`/`sb`/`tail` at :3819-3859 — `snapshot`
  and `sbtext` slot beside `sb` in both handlers.
- Bounded-resources contract: `muxd/PLAN.md` §"Responsiveness and resource bounds" (:38-46) —
  ring access synchronized, queues bounded, output bounded and drained. The model must state and
  enforce its own per-session cap to stay inside this contract.
- Width-garble mechanism confirmed: ring stores raw bytes wrapped at write-time width; attach at a
  different width replays those bytes verbatim (`scrollback()` :1694-1701) → hard-wrapped garble.
  Only a structured model can reflow; xterm.js headless has native reflow + a serialize addon,
  pyte does not reflow (its resize keeps hard-wrapped lines) — this asymmetry is a first-class H1
  criterion, not an afterthought.

## Leaves

```json
[
  {
    "id": "nat-A",
    "title": "muxctl native wheel priority + scroll-parity vector file",
    "kind": "build",
    "goal": "In muxd/muxctl.py, make wheel_input_sequences use real-terminal priority: mouse-tracking active -> wheel reports (SGR/urxvt/X10, even on the alt screen); bare alt screen WITH DECSET 1007 -> 3 arrows per notch (SS3 under DECCKM); bare alt WITHOUT 1007 -> page keys (existing 90ms rate limit) as the compatibility fallback; normal screen -> nothing. Add 1007 to ScreenModeTracker's tracked modes. Default alt_scroll becomes 'sgr'; MUXCTL_WHEEL=pagekeys stays as a forced override and 'pagekeys' keeps today's alt-screen-first behavior. Create docs/scroll-parity.json: a vector table mapping every state {normal|alt} x {tracking on|off} x {1007 on|off} x {surface: muxctl|web} to exactly one mechanism, and rewrite the forwarding tests to assert every cell from that file (exactly-one-mechanism invariant included). Also refresh the stale web-surface paragraphs of muxd/DEPLOY-local-scroll.md (it still claims the web strips ALL mouse reports; index.html:1527-1538 now keeps wheel).",
    "tier": "default",
    "deps": [],
    "reads": ["muxd/muxctl.py", "muxd/tests/test_local_scroll_forwarding.py", "muxd/tests/test_console_input_roundtrip.py", "muxd/tests/test_muxctl_input_mode.py", "muxd/DEPLOY-local-scroll.md"],
    "verifier": "timeout 180 python -m pytest muxd/tests/test_local_scroll_forwarding.py muxd/tests/test_console_input_roundtrip.py muxd/tests/test_muxctl_input_mode.py -q",
    "size": "M",
    "notes": "Anchors: wheel_input_sequences muxctl.py:196 (alt-first branch :217), ScreenModeTracker :126, MUXCTL_WHEEL wiring :660. muxctl-only: takes effect on next attach, no daemon restart. Existing tests assert the pagekeys-first order ('proven default' test ~:413) and MUST be rewritten against the vector file, not patched around. Human-checklist items: wheel-scroll a local codex attach (per-notch transcript scroll), less pages once per notch, plain shell keeps conhost scrollback, Ctrl-] detach clean."
  },
  {
    "id": "nat-B",
    "title": "web scroll parity locked to the shared vector file",
    "kind": "build",
    "goal": "Extend relay/tests/wheel-scroll.test.js (+ client-layout.test.js section assertions) to load docs/scroll-parity.json and fail if the shipped web behavior disagrees with any web-surface wheel cell: normal buffer -> capture router scrolls xterm scrollback and stops the event; alt buffer -> trusted wheel forwarded to xterm (app receives SGR report when tracking, xterm alternateScroll arrows when 1007, nothing when bare); synthetic events rejected (isTrusted); no recursion. Document/assert the touch path separately (scrollAlternate page keys, 90ms limit, index.html:1282-1287) as the touch-surface cell. Only touch relay/public/index.html if an assertion exposes a real divergence; then bump window.__muxBuild (currently stale at '2026-07-12-mobile-ime-viewport-hardening', index.html:1015).",
    "tier": "default",
    "deps": ["nat-A"],
    "reads": ["relay/tests/wheel-scroll.test.js", "relay/tests/client-layout.test.js", "docs/scroll-parity.json", "relay/public/index.html"],
    "verifier": "node --test --test-force-exit --test-timeout=20000 relay/tests/wheel-scroll.test.js relay/tests/client-layout.test.js",
    "size": "S",
    "notes": "index.html regions: wheel router :1393-1419, stripMouseReports :1527-1538, xterm scrollback:60000 :741. Reuse the extractFn vm harness (wheel-scroll.test.js:14-26) and the section() harness (client-layout.test.js:8-14). If xterm.js's bare-alt+1007 arrow behavior can't be asserted headlessly from source sections alone, assert the router's forwarding contract and record the 1007 cell as covered by nat-H1's xterm-headless rig."
  },
  {
    "id": "nat-C",
    "title": "web visible scrollbar + honest alt-screen scroll state",
    "kind": "build",
    "goal": "Give the web viewer a visible themed scrollbar on .xterm-viewport in the normal buffer (scrollbar-width:thin + webkit rules, theme colors) plus a compact scroll-position readout co-located with the #jumplive pill, and in the alternate buffer hide the scrollbar and show an 'app scroll' state chip instead (a dead scrollbar is worse than none). Hook term.onScroll and buffer-type changes; reuse atBottom()/showJump() plumbing (index.html:1562-1591). Bump window.__muxBuild.",
    "tier": "default",
    "deps": [],
    "reads": ["relay/public/index.html", "relay/tests/client-layout.test.js"],
    "verifier": "node --test --test-force-exit --test-timeout=20000 relay/tests/client-layout.test.js",
    "size": "M",
    "notes": "Extend client-layout.test.js: (1) a .xterm-viewport scrollbar rule exists and is not display:none, (2) the alt-buffer toggle fn (vm-extracted, driven with fake term.buffer.active.type) shows chip + hides scrollbar, (3) position label math is a pure fn tested at top/middle/bottom. Human-checklist: scrollbar visible+draggable at ~1906x912, no touch-pan fight on phone, alt screen shows chip."
  },
  {
    "id": "nat-D",
    "title": "web selection/copy that feels native on desktop",
    "kind": "build",
    "goal": "Desktop selection first-class in relay/public/index.html: drag-select survives streaming output (the autoFreeze latch exists at :982-990/:1133-1140 — harden it on onSelectionChange from mouse drags, not just explicit select mode), Ctrl/Cmd+Shift+C copies term.getSelection() with fallback to the copy overlay when the clipboard API rejects, auto-copy-on-select as a persisted LS toggle, Shift+drag documented in the help sheet for tracking sessions, and no contextmenu preventDefault over #term while a selection exists. Phone flow (selectMode + freeze + copy overlay, :2384-2389/:2574-2596) unchanged. Bump window.__muxBuild.",
    "tier": "default",
    "deps": [],
    "reads": ["relay/public/index.html", "relay/tests/client-layout.test.js"],
    "verifier": "node --test --test-force-exit --test-timeout=20000 relay/tests/selection.test.js",
    "size": "M",
    "notes": "NEW relay/tests/selection.test.js using the extractFn vm harness: (1) drag-latch freezes flush while a selection exists and flushes on clear, (2) copy handler routes selection -> stubbed clipboard writer, falls back to overlay on reject, (3) auto-copy toggle round-trips the LS shim, (4) section assertion: no contextmenu preventDefault over #term. Copy helpers today: :1125-1128, :2374-2377. Human-checklist: drag-select 30 lines while codex streams (selection survives, copy lands), Shift+drag in alt-screen codex, phone long-press unchanged."
  },
  {
    "id": "nat-E",
    "title": "restore conhost QuickEdit drag-select in the local attach",
    "kind": "build",
    "goal": "muxd/muxctl.py attach_input_mode (:54-62) clears ENABLE_QUICK_EDIT_MODE unconditionally, killing native drag-select for plain-shell local attaches. Restore QuickEdit whenever mouse capture is OFF (normal buffer, conhost owns wheel+selection) and clear it only while set_mouse_capture(True) is active (alt/tracking); restore the original mode exactly on exit. QuickEdit pausing output during a drag is native local behavior and is desired. MUXCTL_VT_INPUT set -> VT input path unchanged (no QuickEdit).",
    "tier": "default",
    "deps": [],
    "reads": ["muxd/muxctl.py", "muxd/tests/test_muxctl_input_mode.py"],
    "verifier": "timeout 60 python -m pytest muxd/tests/test_muxctl_input_mode.py -q",
    "size": "S",
    "notes": "Test matrix over (capture on|off) x (vt_input on|off): QUICK_EDIT set exactly when capture off and vt_input off; MOUSE_INPUT set exactly when capture on; ENABLE_EXTENDED_FLAGS always; original mode restored on detach. Pure mode math, no console handle needed. Anchors: set_mouse_capture :353, wants_mouse_capture wiring :617-624. Human-checklist: conhost drag-select+Enter copies from a plain shell attach; wheel still forwards inside codex; detach restores mode."
  },
  {
    "id": "nat-F",
    "title": "input->echo latency probe with enforced budgets",
    "kind": "build",
    "goal": "Build scripts/latency_probe.py: open a throwaway muxd session running a pure echo child, send single-byte inputs over (a) the local muxd ws (ws://127.0.0.1:7699, MUXCTL_PORT env, same path muxctl uses) and (b) optionally the relay ws path gated behind MUXD_VPS_TESTS=1 (reuse muxd/tests/test_vps_smoke.py plumbing), measure input->matching-output RTT over >=200 samples, print p50/p95/p99, exit non-zero over budget (--budget-p95-ms, default 50 local / 120 relay). Hard --timeout wall clamp; always kill the probe session on every exit path. If muxd is unreachable, auto-start the MuxdSessionHost scheduled task exactly like muxctl does (muxctl.py:426-445) and exit 3 with a clear message if that fails. Measure-only: no tuning in this leaf.",
    "tier": "default",
    "deps": [],
    "reads": ["muxd/muxctl.py", "muxd/tests/test_vps_smoke.py", "muxd/muxd.py"],
    "verifier": "timeout 180 python -m pytest muxd/tests/test_latency_probe.py -q && python scripts/latency_probe.py --local --samples 100 --budget-p95-ms 50 --timeout 60",
    "size": "M",
    "notes": "NEW muxd/tests/test_latency_probe.py unit-tests probe logic against a loopback fake (sample matching, percentile math, timeout kill). Runner assumption verified: scheduled-task autostart exists. Budgets tightened later only with data; leaf I (pump tuning) stays deferred and gated on this probe's numbers."
  },
  {
    "id": "nat-G",
    "title": "web click forwarding v2 with leak detector (mouse-report guard v2)",
    "kind": "build",
    "goal": "Let alt-screen tracking apps receive discrete click/release SGR reports from the web surface (click-to-position/focus) without reopening the leaked-mode flood. In relay/public/index.html: add a small output-side mode tracker (mirror ScreenModeTracker semantics, muxctl.py:126-188: scan session output for DECSET/DECRST of 9/1000/1002/1003/1006 with a carried tail); stripMouseReports v2 passes SGR press/release reports (final M/m, Cb with neither bit 32 motion nor bit 64 wheel) ONLY while the tracker says tracking is active, motion reports are stripped unconditionally forever. Leak detector: if session OUTPUT contains an echo of report bytes we forwarded within a short window (the dead-TUI+live-shell signature), send DECRST 1000;1002;1003;1006 as input, stop forwarding, and re-arm only on the next DECSET seen in output. Bump window.__muxBuild.",
    "tier": "default",
    "deps": [],
    "reads": ["relay/public/index.html", "relay/tests/wheel-scroll.test.js", "muxd/muxctl.py"],
    "verifier": "node --test --test-force-exit --test-timeout=20000 relay/tests/wheel-scroll.test.js",
    "size": "M",
    "notes": "RECOMMENDED IN (was deferred): the historic flood was motion reports (guard comment index.html:1522-1526); motion stays dead, clicks are discrete and low-volume, and the detector is unit-testable in the vm harness (clicks pass when tracking-active, motion always stripped, echoed-report -> DECRST + drop + re-arm). Higher risk than the other web leaves; if the orchestrator wants extra safety, land after nat-B so parity tests are already locked. Wheel cells of docs/scroll-parity.json must stay green untouched."
  },
  {
    "id": "nat-S1",
    "title": "fix superlinear input-filter freeze (takeTerminalReport OSC backtracking)",
    "kind": "build",
    "goal": "relay/public/index.html takeTerminalReport (:1424-1430) uses the OSC pattern \\x1b\\][0-9;]+;[^\\x07\\x1b]*(?:\\x07|\\x1b\\\\) which backtracks superquadratically on 'ESC ]' followed by a long unterminated [0-9;] run containing semicolons (measured on the real function: 20k->201ms, 40k->789ms, 80k->4.9s; paste-sized inputs freeze the UI thread for minutes). It runs per input chunk via stripTerminalGeneratedReports (:1432-1440) on term.onData (:1545) — every keystroke and paste. Replace with a linear-time scan (hand-rolled OSC scanner or an equivalent non-backtracking pattern; also skip non-ESC spans with indexOf instead of per-char slicing) with byte-identical filtering behavior on all well-formed reports. Bump window.__muxBuild.",
    "tier": "default",
    "deps": [],
    "reads": ["relay/public/index.html", "relay/tests/wheel-scroll.test.js"],
    "verifier": "node --test --test-force-exit --test-timeout=20000 relay/tests/input-filter.test.js",
    "size": "S",
    "notes": "NEW relay/tests/input-filter.test.js (vm extractFn harness): (1) regression: strip('\\x1b]' + ';'.repeat(200000)) completes and the whole test file finishes under --test-timeout=20000 (the pathological input under the timeout IS the regression gate), (2) all current behavior preserved: well-formed DA/CPR/DSR/OSC reports stripped, plain text and non-report CSIs untouched, dropped flag correct, (3) unterminated OSC at end of chunk: pick and TEST a policy (recommend pass-through unchanged — a report split across onData chunks is rare and the pty tolerates it; document the choice in the test). NOT an infinite loop — the char index always advances; the blowup is inside one regex attempt. This closes the campaign directive's 'hanging test' item: the directive's ~1432 anchor points at THIS function, not stripMouseReports; no test hangs today (see Validation for the relay.test.js slowness that mimicked a hang)."
  },
  {
    "id": "nat-S2",
    "title": "input continuity across ws reconnects (cold finding 5)",
    "kind": "build",
    "goal": "relay/public/index.html send() (:1180) silently no-ops unless ws.readyState===1; all xterm input routes there (term.onData :1539-1556, keybar :2354-2356), and the compose dialog closes itself BEFORE term.paste/send (:2198-2199) — so typing or submitting during a transient reconnect silently loses data. Implement a bounded pending-input buffer with replay: send('i',...) during a non-open socket enqueues into a buffer capped at 4096 bytes and 15s TTL; on socket open (after the attach replay is requested) flush in order; on cap/TTL overflow drop the OLDEST data and show a visible notice; while anything is queued show a status affordance ('N keys queued - reconnecting'). Non-'i' frames (heartbeats, viewport reports) are NOT buffered. Compose dialog: only close after the text was sent or queued; if queued, keep the 'queued' notice visible. Bump window.__muxBuild.",
    "tier": "default",
    "deps": [],
    "reads": ["relay/public/index.html"],
    "verifier": "node --test --test-force-exit --test-timeout=20000 relay/tests/input-continuity.test.js",
    "size": "M",
    "notes": "Design choice argued: buffer+replay over disable-input because this client's reconnects are frequent and short (auto-reconnect on visibilitychange/online, :1688-1693) and a real local terminal never drops keys — typing through a 1-3s blip must land; the stale-intent hazard of replaying old keys into a changed screen is bounded by the small cap + 15s TTL + visible queue state, and disable-input would punish the common case to guard the rare one. NEW relay/tests/input-continuity.test.js (vm-extract the buffer module): enqueue-while-closed, in-order flush on open, cap eviction, TTL expiry, notice-state transitions, non-'i' frames never buffered, compose queue-then-close contract. Reconnect attach ordering: flush AFTER the attach frame so replayed keys follow the session's current screen. Seam: adversarial lane owns ws control-frame validation (cold finding 6) in the same file region — keep this leaf strictly client-side input-path."
  },
  {
    "id": "nat-H1",
    "title": "terminal-model fidelity spike: pyte vs xterm-headless on real ConPTY streams",
    "kind": "build",
    "goal": "Build muxd/spike/vt-fidelity/: (1) capture.py — records raw ring bytes from live muxd sessions over the local ws (t:'sb', max=800000, muxd.py:3823-3841) into fixture files; capture at least one alt-screen codex/claude session, one claude normal-buffer transcript, one plain shell; if no live TUI session exists on the runner, generate fallback fixtures by driving scripted TUIs (less on a large file, a python-curses demo) under ConPTY. (2) synthetic fixtures exercising every REPLAY_PRIVATE_MODES mode (1,47,1047,1049,2004,9,1000,1002,1003,1005,1006,1007,1015) plus DECAWM wrap, SGR 256/truecolor, ED/EL, DECSTBM regions, CUP/CUU/CUD, DECCKM, OSC 0/2, UTF-8 wide chars/emoji — the ConPTY dialect muxd actually replays. (3) harness.mjs + a python shim — feed each fixture to BOTH candidates: pyte (HistoryScreen) and @xterm/headless (add as relay devDependency); diff final visible screen text + cursor against @xterm/headless as oracle; run a REFLOW probe per fixture: candidate ingests at 140 cols, resizes to 80, serializes; compare against xterm-headless fed the same stream then resized to 80. (4) emit report.json: per-fixture per-candidate cell-fidelity %, reflow-fidelity %, ingest throughput (bytes/s), and a computed go/no-go per candidate. GO means: >=99.5% cell fidelity on synthetic corpus AND >=99% on recorded corpus AND >=98% reflow fidelity AND throughput >= 5MB/s.",
    "tier": "default",
    "deps": [],
    "reads": ["muxd/muxd.py", "relay/package.json", "muxd/tests/test_local_scroll_forwarding.py"],
    "verifier": "timeout 300 node muxd/spike/vt-fidelity/harness.mjs --corpus muxd/spike/vt-fidelity/fixtures --out muxd/spike/vt-fidelity/report.json && python -c \"import json;r=json.load(open('muxd/spike/vt-fidelity/report.json'));assert r.get('fixtures') and all('go' in c for c in r['candidates'].values())\"",
    "size": "L",
    "notes": "The verifier proves the harness ran and produced a complete report — the fidelity OUTCOME is data for nat-H1J, so a no-go candidate does not fail this leaf. Known asymmetry to surface honestly: pyte does not reflow wrapped lines on resize (expect reflow no-go) while xterm-headless reflows natively and its SerializeAddon emits replayable VT — if confirmed, the report should say so with numbers, not smooth it over. muxd runs on the runner (scheduled-task autostart, muxctl.py:426-445). Fixtures containing recorded session content: flag for the orchestrator before committing (Open questions)."
  },
  {
    "id": "nat-H1J",
    "title": "judge: pin the terminal-model architecture from H1 evidence",
    "kind": "judge",
    "goal": "Read muxd/spike/vt-fidelity/report.json and the H1 fixtures/harness, then write campaign/research/decisions/nat-h-model.md pinning: EMULATOR: (pyte in-proc | xterm-headless node sidecar | other), SNAPSHOT_FORMAT: (serialized VT bytes | JSON grid) for the snapshot/sbtext caps, REFLOW: how attach-repaint reflow is produced (native emulator reflow vs model-side logical-line tracking), MEMORY_BOUND: the per-session cap and trim policy consistent with muxd/PLAN.md's bounded-resources contract, and PROCESS_MODEL: in-process vs one shared sidecar process (weigh ops cost on the muxd restart discipline). Justify each against the report's numbers; a candidate that failed reflow cannot be chosen for H3 duty unless the decision adds a compensating design.",
    "tier": "deep",
    "deps": ["nat-H1"],
    "reads": ["muxd/spike/vt-fidelity/report.json", "campaign/mux-native.md", "muxd/PLAN.md", "muxd/muxd.py"],
    "verifier": "python -c \"import re;s=open('campaign/research/decisions/nat-h-model.md',encoding='utf-8').read();assert all(re.search(k+r':\\\\s*\\\\S+',s) for k in ('EMULATOR','SNAPSHOT_FORMAT','REFLOW','MEMORY_BOUND','PROCESS_MODEL'))\"",
    "size": "S",
    "notes": "This judge replaces the base plan's deferred kind:'plan' node — the emulator/reflow/process choice is the ONE genuine open decision in H. Everything downstream (H2-H4) consumes this contract."
  },
  {
    "id": "nat-H2",
    "title": "muxd terminal-model sidecar + snapshot capability (feature-flagged)",
    "kind": "build",
    "goal": "Per the pinned contract in campaign/research/decisions/nat-h-model.md, add a per-session terminal model to muxd/muxd.py fed from the session output tap (Session output path :1540-1543 beside replay_state.ingest; OwnerSession's duplicate path :1793 too), behind env flag MUXD_TERM_MODEL (default OFF -> byte-identical behavior everywhere). Enforce the pinned per-session memory bound (trim history on breach, never crash, expose a gauge in session info). Add a 'snapshot' ws capability beside 'sb' in BOTH handlers (local :3626-3668, relay :3819-3859): request {t:'snapshot',s,rid} -> response {t:'snapshot',rid,cols,rows,modes,d:<pinned format>}. NO attach-path change in this leaf — attach replay stays raw-ring. Advertise the capability in the protocol info so clients can feature-detect.",
    "tier": "default",
    "deps": ["nat-H1J"],
    "reads": ["campaign/research/decisions/nat-h-model.md", "muxd/muxd.py", "muxd/PLAN.md", "muxd/tests/test_muxd_state.py"],
    "verifier": "timeout 300 python -m pytest muxd/tests/test_terminal_model.py muxd/tests/test_muxd_state.py -q",
    "size": "L",
    "notes": "NEW muxd/tests/test_terminal_model.py: (1) flag OFF -> attach_replay_payload and scrollback byte-identical to today (golden), (2) flag ON -> snapshot of a fed fixture matches the emulator oracle, (3) memory gauge respects the pinned cap under a RING_CAP-sized flood, (4) model death/lag never blocks the output pump (tap must be non-blocking; drop-model-not-output). muxd.py restart discipline applies but deploys are free (governing call 3: scripts/deploy-muxd.ps1 + MuxdSessionHostRestart). If H1J pins a node sidecar process, the tests stub the transport and one integration test drives the real child."
  },
  {
    "id": "nat-H3",
    "title": "resize-correct attach repaint from the model snapshot",
    "kind": "build",
    "goal": "When MUXD_TERM_MODEL is on and a viewer attaches with cols/rows differing from the width the ring bytes were written at, serve the attach replay from the model (reflowed to the session's current size per the pinned REFLOW strategy) instead of raw ring bytes — killing the wrapped-line garble that raw byte replay produces on width change. Extend attach_replay_payload (muxd.py:1248) and its call sites (local attach :3636-3641, relay 'sb' op :3823-3841) with the viewer-dims decision; the raw ring path remains the fallback whenever the flag is off, the model is dead/lagging, or sizes match. redraw_nudge (:1257) stays for alt-screen truth.",
    "tier": "default",
    "deps": ["nat-H2"],
    "reads": ["campaign/research/decisions/nat-h-model.md", "muxd/muxd.py", "muxd/tests/test_terminal_model.py"],
    "verifier": "timeout 300 python -m pytest muxd/tests/test_terminal_model.py muxd/tests/test_attach_repaint.py -q",
    "size": "M",
    "notes": "NEW muxd/tests/test_attach_repaint.py: write wrapped-line fixture at 140 cols, attach at 80 -> replay rendered through the emulator at 80 equals the oracle text (and a control test proving the raw path DOES garble, so the win is demonstrated not assumed); same-width attach -> byte-identical raw replay (no regression); model-fallback path exercised. Alt-screen sessions: replay mode-prefix + nudge as today — the model repaint targets the NORMAL buffer per the alt-screen doctrine."
  },
  {
    "id": "nat-H4a",
    "title": "sbtext capability: rendered history text export",
    "kind": "build",
    "goal": "Add an 'sbtext' ws capability beside 'sb'/'snapshot' in both muxd handlers: request {t:'sbtext',s,lines:N,rid} -> {t:'sbtext',rid,d:<rendered plain-text history lines, newest last>} served from the terminal model's history+screen (bounded: N clamped to the model's retained history, response size capped consistent with PLAN.md bounds). This exports RENDERED lines (what a scrollback pager would show), not raw VT bytes — the contract the web history/search overlay and future muxctl copy/search consume. Normal-buffer history only; alt-screen yields the current screen text plus any normal-buffer history (doctrine: no fake alt transcript).",
    "tier": "default",
    "deps": ["nat-H2"],
    "reads": ["campaign/research/decisions/nat-h-model.md", "muxd/muxd.py", "muxd/tests/test_terminal_model.py"],
    "verifier": "timeout 180 python -m pytest muxd/tests/test_sbtext.py -q",
    "size": "S",
    "notes": "NEW muxd/tests/test_sbtext.py: rendered output matches emulator oracle for a wrapped+SGR fixture (attributes stripped, wide chars intact), clamping honored, flag-off -> capability absent from protocol info. Value beyond xterm's own 60000-line scrollback: exports history past the attach-replay window (full RING_CAP-worth, rendered), and serves surfaces that never replayed it (muxctl, future exports)."
  },
  {
    "id": "nat-H4b",
    "title": "web history/search overlay over sbtext",
    "kind": "build",
    "goal": "Add a history/search overlay to relay/public/index.html: a keybar/header affordance opens a frozen overlay pane (pattern: the existing copy-overlay <pre>, :2574-2596) that requests sbtext from muxd (feature-detected via protocol info; hidden when absent), renders the tail, supports incremental text search with match highlighting and prev/next jumps within the overlay, and native selection/copy inside it. The overlay is read-only and separate from the live xterm viewport (no fake scrollback injection into xterm — alt-screen doctrine). Bump window.__muxBuild.",
    "tier": "default",
    "deps": ["nat-H4a"],
    "reads": ["relay/public/index.html", "relay/tests/client-layout.test.js"],
    "verifier": "node --test --test-force-exit --test-timeout=20000 relay/tests/history-overlay.test.js",
    "size": "M",
    "notes": "NEW relay/tests/history-overlay.test.js (vm extractFn + section harness): search fn is pure and tested (case-insensitive, match indexing, prev/next wrap), overlay open/close never touches term input state, feature-detect hides the affordance when sbtext is absent, request uses rid correlation. SEAM: same file as nat-C/D and the value prong's UI work — coordinate the keybar/help-sheet region at merge; adversarial lane owns ws frame validation."
  }
]
```

## Ordering

Branch-per-leaf; only semantic deps gate. Recommended first cut launches **9 leaves immediately**:

```
wave-parallel (no deps):  nat-A  nat-C  nat-D  nat-E  nat-F  nat-G  nat-S1  nat-S2  nat-H1
after nat-A:              nat-B
after nat-H1:             nat-H1J (deep judge)
after nat-H1J:            nat-H2
after nat-H2:             nat-H3   nat-H4a   (parallel)
after nat-H4a:            nat-H4b
```

Dependency graph (semantic only):

```
nat-A ──► nat-B                       (docs/scroll-parity.json contract)
nat-H1 ──► nat-H1J ──► nat-H2 ──► nat-H3
                              └───► nat-H4a ──► nat-H4b
nat-C, nat-D, nat-E, nat-F, nat-G, nat-S1, nat-S2 : independent
```

Dropped from the base plan's graph: A→E and C→D (file-ownership serialization, superseded by
governing call 1). Priority within the wave if the orchestrator staggers: nat-S1 and nat-S2 first
(live-path correctness bugs), then nat-A (core fidelity flip), nat-C/nat-D (visible native feel),
nat-H1 early (longest chain), nat-F, nat-E, nat-G. Deferred and unchanged: leaf I (pump tuning,
gated on nat-F data); alt-screen scrollback capture stays REJECTED (doctrine, not deferral).

## Seams

- **`relay/public/index.html`** — touched by nat-C, nat-D, nat-G, nat-S1, nat-S2, nat-H4b, plus
  the value prong (UX) and adversarial prong (input/ws hardening). Proposed owner: **this native
  lane owns the terminal-behavior blocks** (wheel router :1388-1419, input filters :1424-1556,
  send/reconnect input path :1180/:1593+, selection/freeze/copy, scrollbar CSS, history overlay);
  adversarial lane owns ws control-frame validation (cold finding 6); value prong owns
  non-terminal UI regions. Merge order within this lane is free (branch-per-leaf), but
  `window.__muxBuild` (:1015) is a guaranteed one-line conflict on every merge — recommend the
  orchestrator's merge step regenerate the tag (date-based) instead of resolving by hand, and note
  the tag is currently stale (2026-07-12) so the FIRST merged web leaf must bump it.
- **`docs/scroll-parity.json`** — NEW, created by nat-A, owned by this lane; nat-B and any other
  prong read-only.
- **`relay/tests/*.js`** — appends from nat-B/D/G/S1/S2/H4b and likely the adversarial lane; new
  files where possible (selection, input-filter, input-continuity, history-overlay) to keep merges
  trivial; wheel-scroll.test.js appends are the only shared-file writes (nat-B, nat-G).
- **`muxd/muxd.py` + restart discipline** — nat-H2/H3/H4a only. Deploys are free (governing
  call 3: `scripts/deploy-muxd.ps1` + MuxdSessionHostRestart); coordinate restart timing with the
  integration lane anyway so a restart never races a relay deploy.
- **`muxd/muxctl.py`** — nat-A, nat-E (this lane owns translation semantics); a perf prong, if
  any, owns throughput/batching.
- **Human-GUI checklist** — nat-A/C/D/E emit checklist items (listed in leaf notes); batch into
  one end-of-campaign human session per governing call 4.

## Open questions

1. **relay.test.js wall time (72–78 s) masquerades as the "hanging test".** It trips any
   file-level `--test-timeout` below ~80 s and is the entire basis of the campaign's hang hazard;
   no test actually hangs (evidence in Validation). Splitting/speeding that integration rig is
   integration-lane territory — who owns it, and should the campaign's standard verifier guidance
   change from `--test-timeout=20000` to a per-file budget that relay.test.js can actually meet?
2. **`npm test` is unwired** in relay/package.json (`"Error: no test specified"`). Should the
   campaign wire it (with `--test-force-exit` and sane timeouts) so "run the suite" has one
   blessed bounded entrypoint, and who owns that edit?
3. **H1 fixture provenance:** recording real codex/claude ring bytes commits Ahmed's actual
   session content into the repo as fixtures. Acceptable, or must nat-H1 restrict itself to
   scripted-TUI/synthetic fixtures? (Leaf is written to support both; the go/no-go thresholds
   assume at least one real recorded fixture.)
4. **Parity-vector supremacy:** if nat-B exposes a genuine divergence between real-terminal
   semantics (docs/scroll-parity.json) and xterm.js's built-in behavior on some cell (most likely
   bare-alt+1007), does the vector win (patch the web client) or does xterm win (annotate the
   vector)? Proposal: real-terminal semantics win; the web patch is in-scope for nat-B.
5. **nat-G risk appetite:** I recommend it enters now (motion reports stay dead; detector designed
   in and unit-tested). Confirm, given the flood bug's history of killing live sessions — the
   deciding fact is that deploys and restarts are free this campaign, so a bad detector is cheap
   to roll back.
6. **Build-tag hygiene:** `window.__muxBuild` is stale relative to shipped behavior and
   DEPLOY-local-scroll.md documents the pre-merge web surface. nat-A refreshes the doc's web
   paragraphs; confirm the merge-step tag-regeneration proposal (Seams) so the tag stops lying.
