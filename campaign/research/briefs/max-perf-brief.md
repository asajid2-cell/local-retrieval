READ FIRST: campaign/research/briefs/_shared-context.md (shared campaign context, deliverable
schema, conduct rules). Then this brief. Repo root = Z:\328\CMPUT328-A2\codexworks\301\mux-local-retrieval

# LANE max-perf — refine the win32-perf prong (WinUI app native-fast)

Base plan: `campaign/win32-perf.md` (P0 + A/B/C/D/E leaf groups; first cut P0,B1,B4,A1,A3,A4,
C1,C2,D1). Read it fully — the hot-spot map (§1) is evidence-dense and mostly still valid.
Your job: run the plan's own "FIRST ACTION: check if landed" items, decide the god-file slicing
leaves, re-cut deps for branch-per-leaf, and emit the refined orch-ingestable leaf set.

Deliverable: `campaign/research/max-perf-refined.md` (schema in shared context).

## Task 1 — landed-state checks (the plan mandates these before scheduling)
The integration lane merged since the plan was written (413c13b touched the app reclaim path,
incl. MainPage.Integrity-adjacent code). Check with evidence (read the current code):
- B1: does Core `RunningSessions` already have a short-TTL scan cache (premax Phase-0 item 6)?
  Are the [F#3] post-claim bypass / [F#4] InvalidateScanCache sites / [F#8] no-fail-caching
  semantics present? If landed → B1 becomes verify+test-backfill; if partial → scope the delta.
- B4: is `RenderIntegrity` still a synchronous UI-thread Build (MainPage.Integrity.cs ~:36) or
  did the reclaim work land the async half? Scope B4 accordingly and note the rebase surface
  413c13b created.
- Confirm the polling-mesh facts (§1) still hold post-merge (MainPage.Remote.cs regions moved?).

## Task 2 — god-file slicing (campaign requirement: god-files sliced)
The campaign's context bands demand small leaves with small reads[]. Two god-files dominate this
prong: `Core/Services/ArchiveService.cs` (~288 KB) and `Native/MainPage.xaml.cs`.
- Spec an EARLY mechanical partial-class split leaf for ArchiveService.cs: pure code motion into
  region-named partial files (Search/Save/Parse/Projection/…), zero behavior change; verifier =
  Release build + full existing suite green (bounded via run_gates.ps1's dotnet-tests filter) +
  a "no semantic diff" argument (e.g. same public API surface via a reflection dump compare, or
  git diff stat showing only moves). Decide the split boundaries so each downstream leaf's
  reads[] is 1-2 partial files, and re-point A1/A2/A4/C1/D1/B5 reads[] at the partials.
- Judge whether MainPage.xaml.cs (already partial-class across MainPage.*.cs files — check)
  needs further slicing or just region hints in goals.
- Sequence: the split leaf runs FIRST in this prong (after P0? or before — argue it) since every
  ArchiveService leaf's branch merges against the post-split shape.

## Task 3 — re-cut deps for branch-per-leaf
The base plan's §3 graph serializes the ArchiveService chain (A1→A2→A4→C1→C2, D1 after C1) on
same-file grounds — superseded. After the split leaf, which of those deps remain SEMANTIC?
(e.g. C2 genuinely needs C1's delta output; A2 needs A1's cached fields? — decide each.) Also:
B5 needs B2's ProcessSnapshot (semantic — keep); D2 needs B4+D1 (semantic — keep); E1 fences
others (keep last). Emit the honest graph.

## Task 4 — finalize leaf specs
- Keep the plan's counter/ratio verifier discipline ([Timeout] mandatory, corpus in %TEMP%,
  ≥5× wall headroom) — carry the exact verifier commands into the schema, adjusted to the real
  test-project paths (confirm CodexLocalRetrieval.Native.Tests exists and its invocation form
  from app/tools/run_gates.ps1).
- Second-cut leaves (B3, A2, B2, C4, B5, D2, E1) get full specs too — the orch campaign runs
  the whole prong, no wave gating.
- The plan's §4 deferred items (transcript virtualization, store/SQLite split, FTS index, SSH
  transport) stay OUT unless you find evidence one became load-bearing; if kept out, list them
  in Open questions as named future plan-nodes.

Reads to start from (verify, then expand as needed): campaign/win32-perf.md,
app/native/CodexLocalRetrieval.Core/Services/ArchiveService.cs (skim structure/regions, not
every line), app/native/CodexLocalRetrieval.Core/Remote/RunningSessions.cs,
app/native/CodexLocalRetrieval.Core/Remote/SessionLaunchClaims.cs,
app/native/CodexLocalRetrieval.Native/MainPage.Integrity.cs (find actual path under app/native),
app/native/CodexLocalRetrieval.Native/MainPage.Remote.cs, app/tools/run_gates.ps1,
app/native/CodexLocalRetrieval.Native.Tests/ (project layout), 301/premax-plan-win32-v3.md if
present (Z:\328\CMPUT328-A2\codexworks\301\premax-plan-win32-v3.md — cited by the base plan;
skip if absent).
