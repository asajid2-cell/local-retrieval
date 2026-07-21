# MAX CAMPAIGN — execution directive (for the orch deep planner + conductor)

## Goal
Lift the mux-local-retrieval product from "good enough" to **fully native-responsive, hardened, and genuinely more useful** — across `app/` (WinUI desktop app), `relay/` (web relay + browser terminal), and `muxd/` (PC session host). "Good enough is the curse we are lifting." Full local responsiveness is the bar, not a band-aid.

## The decomposition is ALREADY DONE — load it, do not re-derive it
Four prong plans live in `Z:\328\CMPUT328-A2\codexworks\301\plans\max-2026-07\`:
- `mux-adversarial.md` — harden remoting (custody janitor, deploy-drain, launch-claim sweeper, footer-regex state, ops floor)
- `mux-value.md` — real value/workflows (ntfy notifications, durable /send, send-when-idle, fleet, archive-index)
- `mux-native.md` — close the local↔web↔native gap
- `win32-perf.md` — app perf (oracle off UI thread, search hot path, incremental parse)

Your job as planner: build the tree FROM these plans — each prong is a subtree, each plan's leaves are its children WITH THEIR SPECIFIED BOUNDED VERIFIERS. Reconcile/validate; do not throw the plans away and re-invent. Read each plan file fully.

## The five governing calls (override the plans where they conflict)
1. **Branches, not file-ownership serialization.** Every leaf runs in its OWN git worktree/branch and merges up; resolve conflicts at merge (conflict-resolver on collision). Do NOT serialize leaves just because they share a big file (index.html, ArchiveService.cs) — parallelize them on branches and merge. The plans' "one owner per seam" serialization is SUPERSEDED by branch-per-leaf + merge-later. Maximize parallelism.
2. **Full native responsiveness — PROMOTE the native terminal model to CORE.** The `mux-native` plan defers the real terminal-model sidecar (pyte in muxd, option B — true resize-correct scrollback + text history independent of the alt-screen) as a plan-node. PROMOTE it: it is a first-class deliverable, decompose and build it. The page-key/native-xterm work (option A) is the fast path but NOT the finish line — the finish line is a remote terminal that behaves like a real local terminal (native scroll, selection, copy, visible scrollbar, true scrollback, correct reflow, low latency). Also fold in this known bug as a priority native/adversarial leaf: `relay/tests/relay.test.js` has a HANGING test in the `stripMouseReports` area — likely a real infinite-loop on some input in that live-path function; find + fix it with a bounded verifier.
3. **muxd deploys freely.** No live sessions — muxd leaves may land, deploy (`scripts/deploy-muxd.ps1` → C: + `MuxdSessionHostRestart`), and restart as needed. No human-timed deploy gate.
4. **Human verification is batched to the very END.** Run fully autonomously through the whole campaign. Do NOT gate leaves or waves on human GUI checks — collect all "needs a human eye" items (scroll feel, selection, native look at Ahmed's viewport) into ONE final acceptance checklist emitted at completion.
5. **No wave-gating — run the full thing.** Do not stop for review at wave boundaries. Decompose, dispatch bounded leaves, recurse folds up, integrate. The head (Ahmed's opus overseer) watches continuously and is the emergency backstop only.

## Verifier discipline (hardened orch enforces this)
Every buildable leaf MUST have a real, executable, BOUNDED verifier (a test/command that proves it; hang = fail). Leaves without a real verifier are rejected — decompose until each has one. Run suites under `--test-timeout` / `[Timeout]`. Preserve all existing green suites (app 502, relay 85, muxd compile).

## Win condition
All four prongs' leaves landed + merged to one integration branch: the remote terminal feels like a real local terminal (native scroll/select/copy/scrollbar + true scrollback via the terminal model); custody/reliability hardened (janitor, drain, durable command delivery, no hanging stripMouseReports); the WinUI app feels native-fast (oracle off UI thread, search/parse hot paths); high-value workflows added — every leaf proven by its bounded verifier, all component suites green, one final human-acceptance checklist emitted.
