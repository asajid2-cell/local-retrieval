READ FIRST: campaign/research/briefs/_shared-context.md (shared campaign context, deliverable
schema, conduct rules). Then this brief. Repo root = Z:\328\CMPUT328-A2\codexworks\301\mux-local-retrieval

# LANE max-value — refine the mux-value prong (features/workflows)

Base plan: `campaign/mux-value.md` (leaves V0–V8b, workflows W1–W6). Read it fully. Your job:
verify the substrate the plan assumes is now REAL (it planned against intended post-integration
behavior — the integration has since merged), finalize leaf specs incl. wave 2, resolve the
notification-ownership seam, and emit the refined orch-ingestable leaf set.

Deliverable: `campaign/research/max-value-refined.md` (schema in shared context).

## Task 1 — substrate verification (the plan's load-bearing assumptions)
Verify in the CURRENT tree, with evidence:
- `relay/durable-state.js` wired into server.js (require + writeJsonState/persistenceBlocked)?
- `relay/public/intent-journal.js` — what does postIntent actually support today (methods,
  retryableStatus, dedupe semantics)? V3a/V3b/V5/V7b build on it.
- The lease/ack `/api/app-commands` API in server.js — real routes, semantics, intentId dedup?
- muxd input intents (muxd.py ~2651-2720): fingerprinted, replay-safe, durable — confirm the
  protocol shape V3a forwards to (field names, caps, `input-ok` ack).
- The fake-host harness inside relay/tests/relay.test.js that V0 extracts — confirm shape/size.
- Gates: `app/tools/run_gates.ps1` — what suites does it run? Every leaf's verifier must be
  registrable there; adjust the plan's `tools/run_gates.ps1` references.
Report any assumption that is false or shifted; adjust affected leaves.

## Task 2 — wave 2 is now plannable
The integration lane settled (c61dbab, 413c13b) — the "after app-tree freeze" gate on
V7a/V7b/V8a/V8b is lifted. Finalize them as leaves NOW:
- V7a (archive-index push): decide build vs kind:"plan" — the base plan flags it plan if the
  app push plumbing is gnarly. Look at the actual projection-push code (MainPage.Remote.cs
  projection push region, ArchiveService.BuildProjectsProjectionJson) and decide with evidence.
- V7b (resume-from-archive picker), V8a (transcriptfetch projection), V8b (reader view — the
  base plan prefers a separate reader.html; confirm and pin that): full leaf specs with two-sided
  verifiers (dotnet test --filter + node --test, both bounded).

## Task 3 — notification ownership seam (resolve, don't duplicate)
Both this prong (V1 attention push, V2 mute/chip) and the adversarial prong (its L10b: ops-health
alerts on degraded transitions — host link down, persistenceBlocked, heal gave-up) build ntfy
push modules on server.js. Propose ONE shared `notify` transport module + two event-source
owners (value: session-attention episodes; adversarial: ops-health edges), with the module as an
explicit leaf one side owns and the other depends on. Reflect it in your leaf deps and Seams.

## Task 4 — re-cut for branch-per-leaf
Drop the "index.html single-owner serialization" ordering (V3b→V2→V6-UI→V5→V4-UI) — leaves run
on branches and merge. Keep only semantic deps (V0 harness first; V3b needs V3a; V4 needs V1+V3a;
V2 needs V1; V5 needs V3b; V7b needs V7a; V8b needs V8a). Sanity-check each remaining dep is
truly semantic. Confirm V0 (harness extraction) is still the right universal first leaf and its
verifier still holds against the current relay.test.js.

Reads to start from (verify, then expand as needed): campaign/mux-value.md, relay/server.js,
relay/durable-state.js, relay/public/intent-journal.js, relay/public/index.html (tab render,
keybar, mobile nav regions), relay/tests/relay.test.js, muxd/muxd.py (input-intent region
~2651-2720), app/tools/run_gates.ps1, app/native/CodexLocalRetrieval.Core/Services/ (projection
push + archive service regions), app/REMOTE.md, app/PLAN.md.
