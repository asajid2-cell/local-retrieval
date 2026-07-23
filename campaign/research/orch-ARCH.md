# Campaign architecture - mux-local-retrieval MAX

## Goal
Lift the combined WinUI app, web relay, and muxd host from "good enough" to native-responsive,
hardened, useful, and explicit about its trust boundary.

## Five Prongs
1. `r.1` reliability and adversarial hardening: protocol registry, custody/claim cleanup,
   deploy drain, parser hardening, output fairness, process truth, alerts, and bounded gates.
2. Root ordinals `r.5` through `r.16` form the trust and authorization prong: the relay/VPS is not
   a PC-control authority. P-256 principal proofs are verified by muxd or the desktop bridge; muxd
   signs terminal/control truth; muxd owns write leases; local control and credentials are
   separately authenticated and ACL-hardened. All 12 trust leaves are `build/default`; no deep
   trust node is permitted.
3. `r.2` value workflows: notifications, signed durable send, muxd-owned send-when-idle,
   fleet/archive/transcript surfaces, retention, and a signed three-process smoke. This prong
   starts only after final trust leaf `r.16` folds.
4. `r.3` native terminal behavior: scroll/selection/copy, signed input continuity and clicks,
   terminal model, resize-correct repaint, rendered history, authoritative lease UI,
   viewer backpressure, and divergence correction. This prong starts only after final trust leaf
   `r.16` folds.
5. `r.4` WinUI performance: split the ArchiveService god-file, move I/O and integrity work off
   the UI thread, cache hot paths, parse incrementally, and enforce measured performance gates.

## Load And Dependency Order
- Start from a fresh `orch init`, then copy `campaign/research/orch-ARCH.md` to `.orch/ARCH.md`
  before the first `orch plan` command. The loader rejects a deep root plan without this file.
- Ingest the root array from `campaign/research/orch-plan.json`.
- Expand `r.1` and `r.4` from their direct arrays.
- The 12 inlined trust leaves are root ordinals `r.5` through `r.16`. `r.5` depends on the folded
  reliability prong, and the remaining trust dependencies are ordinal edges inside the root batch.
  There is no childless trust composite and no prose-only attach race.
- `r.2` and `r.3` depend on final trust leaf `r.16`; expand them from their direct arrays when that
  dependency folds.
- Never preload `r.2` or `r.3` descendants before `r.16` folds. `orch plan` does not enforce the
  parent node's own dependencies at ingest time, so staged load order is part of this artifact's
  executable contract.
- `r.17` is the deep final judge and depends on reliability, value, native, performance, and the
  final trust leaf.
- Cross-prong contracts are pinned in goals because orch accepts external dependencies only when
  the referenced node is already folded.

## Trust Decisions
- The relay is untrusted because the VPS is multi-tenant; trusting the relay promotes every
  same-host process into developer-PC authority.
- The trusted client is published from an administratively independent origin. Device keys are
  non-extractable and pairing is bootstrapped by a local QR/fingerprint/one-time secret.
- Plaintext remains visible to the relay in this campaign. Relay-blind encryption would remove
  four value capabilities spanning `r.2` children 6/7, 8, 10, and 16. Integrity is provided by
  muxd signatures, not by relay honesty.
- Protocol stays at 4. New fields and capabilities are additive; enforcement is local policy and
  cannot be negotiated down by the relay.

## Execution Rules
- Planning and judging use `deep`; build and sweep use `default`; no leaf is below `default`.
- Every build/sweep has one bounded executable verifier and span-scoped declared reads.
- Shared-file leaves are serialized by dependencies or by the staged prong order.
- The final judge reruns integrated gates, the trust residual checker, hostile-relay tests, and
  the real three-process smokes. Human checks remain one final batched checklist.

## Win Condition
All five prongs are folded and merged; principal authorization is enforced at PC-side execution
boundaries; terminal/control truth is signed; value and native flows use the trusted signer;
reliability and retention bounds hold; WinUI performance gates hold; all automated verifiers are
green; and the final human checklist is emitted.
