# Final Fold Report

## Verdict

**READY.**

The final plan is structurally valid, within the loader's context budgets, and accepted by the
real `orch` loader in an isolated staged rehearsal. The loaded tree contains 91 nodes including
the implicit root. No readiness blocker remains.

Loader evidence is retained at:

`C:\Users\Ahmed\AppData\Local\Temp\orch-final-7895b0be5516477cbec304f96ca1a3d1`

## Artifact Shape

The earlier campaign shape was converted to the loader's direct-array format:

- `orch-plan.json`: root array with 17 children.
- `orch-plan.r.1.json`: reliability array with 18 children.
- `orch-plan.r.2.json`: value array with 20 children.
- `orch-plan.r.3.json`: native array with 18 children.
- `orch-plan.r.4.json`: performance array with 17 children.

Every child is an object carrying `title`, `kind`, `goal`, `tier`, `deps`, and declared `reads`;
every build child also carries one executable `verifier` field. Dependencies are 1-based ordinal
strings local to the array being ingested.

The 12 trust leaves are inlined at root ordinals `r.5` through `r.16`. This is deliberate. A
childless `build/default` trust composite was dispatchable before its children were attached.
Making it `plan/deep` would put a deep node inside the trust prong, while `plan/default` would
violate the tier policy. Inlining makes the reliability-to-trust and trust-to-value/native gates
mechanical without either violation. All 12 Codex-base trust leaves are present and intact; no
trust mechanism leaf was removed during the conversion.

## Measured Plan

Counts below exclude the implicit loader root unless stated otherwise.

| Measure | Result |
|---|---:|
| Total planned nodes | 90 |
| `orch-plan.json` | 17 |
| `orch-plan.r.1.json` | 18 |
| `orch-plan.r.2.json` | 20 |
| `orch-plan.r.3.json` | 18 |
| `orch-plan.r.4.json` | 17 |
| `build/default` | 84 |
| `plan/deep` | 4 |
| `judge/deep` | 2 |
| Default-tier nodes | 84 |
| Deep-tier nodes | 6 |
| Build leaves missing verifier | 0 |
| Build leaves missing declared reads | 0 |
| Codex/GPT tier values | 0 |
| Trust-prong nodes | 12 |
| Deep nodes inside trust prong | 0 |
| Deep nodes whose mission is trust/authorization design or implementation | 0 |
| Non-build nodes inside trust prong | 0 |
| Non-ordinal dependencies | 0 |
| Missing or out-of-range dependencies | 0 |
| Dependency cycles | 0 |
| Maximum fan-in | 8 |
| Inner `timeout` values at or above the 300 s orch bound | 0 |
| Reads above the 15,000-token per-read cap | 0 |
| Nodes above their ingest-context limit | 0 |

The real loaded tree adds the implicit `r` plan root:

| Loaded kind/tier | Count |
|---|---:|
| `plan/deep` | 5 |
| `build/default` | 84 |
| `judge/deep` | 2 |
| Total | 91 |

No serialized campaign node changed between the 90-node file count and the 91-node loaded count.
The extra node is `r`, the implicit `plan/deep` root created by `orch init`; it is not stored in
any plan array. The six serialized deep nodes remain four prong planners and two judges. The final
campaign judge checks fixed trust evidence but is explicitly barred from reopening or redesigning
the trust prong.

The resolved loaded graph has no missing dependency IDs and no cycles. The final judge resolves
to `r.1`, `r.2`, `r.3`, `r.4`, and `r.16`; the transitive closure of `r.16` covers the preceding
11 trust leaves.

## Loader Rehearsal

The final files were loaded with the production `orchestrate/bin/orch.mjs`, not a surrogate
schema checker. The rehearsal used a fresh state directory with no local `.orch/config.json`, so
the arrays passed the stricter default ingest budgets. It loaded a frozen snapshot, then confirmed
that every source hash still matched that snapshot after the run.

Observed frontier:

1. Root loaded: only `r.1` and `r.4` appeared under `EXPAND`; nothing was dispatchable.
2. `r.1` and `r.4` arrays loaded: their dependency-ready build leaves appeared.
3. Reliability folded: `r.5` became the only trust leaf dispatchable; performance work remained
   parallel.
4. Root trust leaves `r.5` through `r.16` folded: `r.2` and `r.3` appeared under `EXPAND`.
5. Value and native arrays loaded: the tree reached 91 nodes with batch counts
   `18/20/18/17`.

Required production load order:

1. Start from a fresh `orch init`.
2. Copy `campaign/research/orch-ARCH.md` to `.orch/ARCH.md`.
3. Load `campaign/research/orch-plan.json` into `r`.
4. Load `orch-plan.r.1.json` into `r.1` and `orch-plan.r.4.json` into `r.4`.
5. Run and fold reliability; the inlined trust sequence then runs as `r.5` through `r.16`.
6. Only after `r.16` folds, load `orch-plan.r.2.json` and `orch-plan.r.3.json`.
7. Run `r.17` after all four non-trust prongs and `r.16` fold.

Do not preload `r.2` or `r.3`. The loader accepts `orch plan <parent>` even when the parent's own
dependencies are still open, so this staged load order is part of the executable contract.

## Design Revision

All six requested build leaves are present:

- `r.2#18`: relay transcript/command state retention.
- `r.2#19`: muxd durable input-intent retention.
- `r.2#20`: real relay + muxd + ConPTY three-process smoke.
- `r.3#16`: signed UI mirror over the muxd-authoritative input lease.
- `r.3#17`: bounded per-viewer backpressure with forced signed resync.
- `r.3#18`: client/model divergence detection and signed corrective repaint.

Both requested amendments are present:

- `r.3#11` requires and verifies `BACKPRESSURE_RESYNC`.
- `r.3#12` freezes the model flag at session creation and orders snapshot/digest emission through
  the session output path.

`r.2#3` is sequenced after `r.16` and states the corrected rule: `replayPolicy` proves operation
semantics, not who authorized the operation. It requires a signed `input.durable` envelope and
endpoint rejection of proofless input.

No design-revision leaf was dropped.

## Trust Fold

The Codex trust specification supplies all 12 build mechanisms. The independent Opus design
supplies the recording discipline and rationale where it does not override those mechanisms.

Mechanism conflicts resolved in favor of the Codex specification:

- P-256/WebCrypto, not Ed25519.
- A fixed positional JSON-array signing transcript, not binary length-prefix encoding.
- A 256-bit QR pairing secret, HMAC registration proof, and muxd identity-signed receipt.
- A trusted client origin administratively independent of the relay VPS.
- Exact-next sequence enforcement, cached exact duplicates, and channel close on conflicting
  sequence reuse.
- Normal 5-second and hard 15-second interactive expiry, not a 120-second window.
- muxd-authoritative write leases; relay state is only a signed UI mirror.
- Mandatory signed plaintext terminal/control output.
- No X25519/AES-GCM blind-output mode in this campaign.
- A distinct explicit local-control token and Ahmed/SYSTEM ACL hardening.

Recording decisions adopted from the Opus design:

- `RESIDUAL_R1` through `RESIDUAL_R8` are required single-line records.
- Every residual line must contain both `detect:` and `accepted:`.
- The checker is executable and repeated at `r.5`, `r.16`, and `r.17`.
- VPS multi-tenancy and same-host co-tenants are the explicit reason the relay is untrusted.
- The confidentiality tradeoff names the four affected value capabilities:
  `r.2#6/#7`, `r.2#8`, `r.2#10`, and `r.2#16`.
- Enrollment records why the local QR/fingerprint/secret is the trust bootstrap and why
  relay-mediated TOFU would allow relay self-enrollment.

## Existing Leaves Corrected

Replaced:

- The obsolete deep security-fixorder planner formerly in `r.1`.
- Relay-authoritative input arbitration; `r.3#16` is now a mirror over trust leaf `r.9`.
- Relay-owned send-when-idle execution; `r.2#13` is now a mirror over trust leaf `r.11`.

Materially amended:

- Reliability: `r.1#1`, `r.1#10`, `r.1#11`, and the reliability gate.
- Value: `r.2#3`, `#9`, `#12`, `#13`, `#14`; archive/transcript/fleet/bridge routes; retention;
  the three-process smoke; and the widened gate.
- Native: `r.3#5`, `#6`, `#8`, and `#12` through `#18`.
- Root final judge: trust is verified from fixed tests and records; the judge cannot reopen trust
  design or create a deep trust child.

The final independent audit also corrected two stale prose pointers without changing scope or
dependencies: reliability parser hardening now points to endpoint enforcement at `r.8`, and the
value three-process smoke points to the trusted-client implementation at `r.7`.

## Dropped Items

- The old deep reliability security planner.
- The independent design's deep trust planner and deep trust judge.
- The independent blind-output encryption mechanism.
- The childless trust composite and obsolete `orch-plan.r.5.json` support array.

The independent design's residual ledger, multi-tenant VPS rationale, confidentiality accounting,
and enrollment-bootstrap argument were retained. No requested design-revision leaf was dropped.

## Final Ruling

**READY.** The final artifact is loader-ingestible, tier-correct, verifier-complete, acyclic,
context-budget compliant, and staged so value/native work cannot begin before the complete trust
sequence has folded.
