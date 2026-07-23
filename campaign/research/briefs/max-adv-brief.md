READ FIRST: campaign/research/briefs/_shared-context.md (shared campaign context, deliverable
schema, conduct rules). Then this brief. Repo root = Z:\328\CMPUT328-A2\codexworks\301\mux-local-retrieval

# LANE max-adv — refine the mux-adversarial prong (remoting hardening)

Base plan: `campaign/mux-adversarial.md` (defects A–I, leaves L1–L10c). Read it fully. Your job:
validate it against the CURRENT tree (integration lane has since merged — anchors drifted), fold
in the new cold-correctness findings below, and emit the refined orch-ingestable leaf set.

Deliverable: `campaign/research/max-adv-refined.md` (schema in shared context).

## Task 1 — validate the base plan
- Every leaf's anchors (server.js functions/strings, muxd.py line regions, index.html sites) —
  confirm they still exist post-merge (c61dbab rewrote parts of relay). Report drift.
- The base plan's integration-lane gates are lifted; its muxd.py same-file serialization chain
  (L3→L4→L7→L5a) is superseded by branch-per-leaf — re-cut deps to semantic-only (e.g. L5b
  still needs L5a's frame contract; L10a still comes last as the gate).
- Check whether any base-plan defect was already fixed by the merge (e.g. parts of client
  hardening). A fixed defect's leaf becomes a regression-drill (tests-only) leaf or is dropped.

## Task 2 — fold in the cold-correctness review (2026-07-22, 6 findings)
Full text at `C:\Users\Ahmed\AppData\Local\Temp\claude\Z--328-CMPUT328-A2-codexworks-301\db6cbc9f-36d4-4ad8-9aab-f2b3cbae94e4\scratchpad\mux-cold-correctness-review.md`
(read it). Summary — verify each against the current tree first (some may be partially fixed by
c61dbab/413c13b), then spec each still-real one as a build leaf with an executable verifier:
1. HIGH `fetchfile` contract mismatch: browser stores/uses `c.detail` as a PC path
   (index.html ~2216-2241) but `RemoteUploadTransfer.cs:88-90` returns the status string
   "downloaded to PC" when no insertion mode requested; both bridges ack `transfer.Detail`
   (MainPage.Remote.cs ~393-435, RemoteBridge.cs ~208-237). Add-to-prompt/copy-as-path insert
   literal status text.
2. HIGH `uploadId` path escape: RemoteUploadTransfer.cs:24 only checks nonblank, then
   Path.Combine(destRoot, uploadId) + raw use in SCP remote path — `..\outside` escapes the
   upload root. Needs token grammar + resolved-path containment.
3. HIGH intent-fenced commands accepted without intent/lease: RemoteCommandProtocol.cs:19-35
   verifies only type/policy; pollers dispatch after only IsReplaySafe; GUI start path mints a
   NEW local intent for a missing command intent (MainPage.Remote.cs ~895-908) breaking dedup.
   Require valid leaseToken + stable nonblank intentId before side effects.
4. HIGH HTML injection sink: index.html:1466 innerHTML status writes; `flash` forwards arbitrary
   strings (~1919); collection-name input (~1842-1858) and bridge/API error details reflected.
   Escape or separate trusted markup from text.
5. (owned by native lane — ignore except as seam) terminal input dropped while ws reconnecting.
6. MEDIUM ws control frames unvalidated: index.html ~1165-1170 accepts any JSON after `d` prefix,
   passes cols/rows to term.resize, assumes clients is an array. Require bounded finite ints +
   shape check.
Verifier guidance: app-side findings (1,2,3) → dotnet test with --filter on new test classes in
the existing app test project (find it; run_gates.ps1 shows the invocation); browser-side (1,4,6)
→ node:test static/vm-extract assertions in the client-layout.test.js style, bounded.

## Task 3 — the security-fixorder plan node
The base plan defers MUX_SYSTEM_AUDIT findings 1–8B (muxd loopback auth, muxd.env ACL, relay
0.0.0.0 bind, ambient loopback trust, LAN ws:// token-in-URL, command provenance) to a plan node.
Keep it kind:"plan", tier:"deep". Write its goal text so a fresh fable planner can decompose it:
name the audit file, the findings span, and the deploy-coordination constraint.

## Task 4 — protocol-field registry seam
Base-plan seam S5: L3 (custody field), L5a/L5b (agentTruth), L7 (resync) all add host-link frame
fields. Under branch-per-leaf these can land in any order — propose the registry mechanism (e.g.
one `docs/protocol-fields.md` registry file owned by this prong, additive entries, or a tiny
version-negotiation leaf) and reflect it in leaf specs/deps.

Reads to start from (verify, then expand as needed): campaign/mux-adversarial.md,
relay/server.js, relay/public/index.html, relay/public/intent-journal.js, muxd/muxd.py,
app/native/CodexLocalRetrieval.Core/Remote/RemoteUploadTransfer.cs,
app/native/CodexLocalRetrieval.Core/Remote/RemoteCommandProtocol.cs,
app/native/CodexLocalRetrieval.Core/Remote/RemoteBridge.cs, relay/MUX_SYSTEM_AUDIT.md,
relay/tests/*.js, muxd/tests/test_muxd_state.py.
