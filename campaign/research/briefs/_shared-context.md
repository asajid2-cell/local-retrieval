# SHARED LANE CONTEXT — MAX campaign research lanes (read first)

You are a deep-reasoning RESEARCH LANE for one prong of the MAX campaign on
`Z:\328\CMPUT328-A2\codexworks\301\mux-local-retrieval` (repo root; also your cwd unless stated).
Product components: `app/` (WinUI 3 desktop app, C#), `relay/` (Node web relay + browser terminal
`relay/public/index.html`), `muxd/` (Python PC session host + `muxctl.py` local attach).

Your job: validate and REFINE your prong's base plan into an orch-ingestable leaf set. You are
READ-ONLY on production code. Your deliverable is ONE file (path in your prong brief).

## Campaign governing calls (override the base plans where they conflict)
1. **Branch-per-leaf.** Every leaf runs in its own git worktree/branch and merges up; conflicts
   resolved at merge. The base plans' "serialize leaves that share a big file" is SUPERSEDED —
   drop file-ownership serialization from deps; keep ONLY true semantic deps (contract X must
   exist before consumer Y). Maximize parallelism.
2. **Full native responsiveness is the bar.** The muxd terminal-model sidecar is PROMOTED to a
   core deliverable (native lane owns its decomposition). "Good enough is the curse we are lifting."
3. **muxd deploys freely** (no live sessions; `scripts/deploy-muxd.ps1` + MuxdSessionHostRestart).
4. **Human verification batched to the very end** — leaves NEVER gate on human GUI checks; each
   leaf may emit human-checklist items collected at campaign end.
5. **No wave-gating** — the orch campaign runs fully autonomously.

## Ground truth as of 2026-07-22 (verified by the orchestrator)
- HEAD = 7968259 (campaign directive commit), tree CLEAN. The integration lane has SETTLED:
  c61dbab merged repo-side client hardening into relay, 413c13b landed app-side reclaim work.
  All base-plan "wait for integration-lane settle" gates are LIFTED — but this means base-plan
  file:line anchors may have DRIFTED. Verify every anchor you rely on against the current tree;
  report drift.
- `relay/durable-state.js`, `relay/public/intent-journal.js` exist. Gates runner is at
  `app/tools/run_gates.ps1` (NOT repo-root tools/). Deploy scripts: `scripts/deploy-muxd.ps1`,
  `scripts/deploy-relay.sh`. muxd tests in `muxd/tests/` (pytest-style), relay tests in
  `relay/tests/` (node:test): `relay.test.js`, `wheel-scroll.test.js`, `client-layout.test.js`.
- KNOWN HAZARD: the relay suite can HANG (a hanging test exists in the stripMouseReports area;
  `npm test` is unsafe without `--test-force-exit`). NEVER run any test suite unbounded. If you
  run tests at all, wrap them: `node --test --test-force-exit --test-timeout=20000 …` or
  `timeout 120 python -m pytest …`. Prefer reading code over running things.

## Tier model for this campaign (iron rule)
Deep/planning/judge nodes → tier `"deep"` (fable). Build/implementation leaves → tier
`"default"` (opus). Use ONLY these two tier values. No leaf may be tiered below default.

## Deliverable format (mandatory)
Your deliverable file must contain, in order:
1. `## Validation` — what you verified in the current tree, every anchor drift found, and any
   base-plan assumption that is now false (with evidence: file, function, or command + output).
2. `## Leaves` — a single fenced json block: an array of leaf objects in EXACTLY this schema:
   {"id":"<prong-prefix>-<short>", "title":"…", "kind":"plan|build|judge",
    "goal":"1-3 sentences, self-contained — a fresh builder with no campaign context must be
            able to act on it",
    "tier":"deep|default",
    "deps":["ids"],            // semantic deps ONLY (contracts/data), not file-sharing
    "reads":["exact/repo/relative/paths"],  // the SMALL set of files the builder must read;
                                            // god-files get a line-range or region hint in goal
    "verifier":"<one bounded executable command, exit 0 = proven; include timeouts>",
    "size":"S|M|L",            // one-sitting estimate; L means consider splitting
    "notes":"anchors, design pins, risks (optional)"}
   Every `build` leaf MUST have a real executable verifier. A leaf you cannot give one → decompose
   or mark kind:"plan". Keep leaves SMALL (≤ one sitting, ~≤100k tokens of work).
3. `## Ordering` — recommended first cut + dependency graph (text), assuming branch-per-leaf.
4. `## Seams` — cross-prong contracts/files needing one owner; name the owner you propose.
5. `## Open questions` — decisions only the orchestrator/head can make.

## Conduct
- Reach your own conclusions; where you disagree with the base plan, say so and why — your
  disagreement is wanted, not smoothed over.
- Blocker claims need proof (exact command + output). Do not improvise around a contradiction:
  record it in Open questions.
- End your turn reply with: `LANE <label> → DONE|BLOCKED` + ≤6-line summary + deliverable path.
