# Reliability Loops

`CURRENT.md` is authoritative for accepted state. This ledger owns contracts and progress only.

## Grand Goal Contract

- G1: Concurrent app-controlled starts for the same canonical identity produce exactly one writer.
- G2: Relaunch proves the old writer exited before the replacement starts.
- G3: Remote state and requests contain no executable commands or local filesystem paths.
- G4: Producer and consumer use one versioned schema exercised from real serialized output.
- G5: A successful persistence or queue acknowledgement survives restart and reads back identically.
- G6: App-server child writers cannot outlive ownership undetected.
- G7: Unified acceptance, install, relay deployment, and production smoke are green.

## Loop Queue

| Loop | Invariant | Verifier | Status |
| --- | --- | --- | --- |
| L1 muxd launch coordinator | one create/relaunch owner; old PID exited | muxd unit + local integration race tests | done `L2b_20260709`: 44 passed, 3 VPS-only skipped |
| L2 shared launch identity | C# and muxd contend on the same alias claim files | cross-language claim compatibility tests | done `L2b_20260709`: app 306 passed, muxd claim/alias tests green |
| L3 opaque relay protocol | relay never stores or receives commands/paths | real projection contract + relay tests | done `L3_20260709`: projection probe green; app 308 passed; relay 35 passed; muxd 55 passed |
| L4 durable state | success requires flush/replace/read-back | injected-failure persistence tests | done `L4_20260709i`: app 320 passed / 2 skipped; relay 51 passed; muxd 75 passed / 3 VPS-only skipped |
| L5 durable command delivery | lease/idempotency; no age loss | crash/restart/replay queue tests | pending |
| L6 canonical server launcher | archive identity and trusted args only | server route/launcher tests | pending |
| L7 process containment | child writer exits with owner or remains blocked | Windows lifecycle integration test | pending |
| L8 synthesis/deploy | all gates and runtime probes green | `tools/run_gates.ps1` plus install/deploy smoke | pending |

## Baseline

- App: build green; 303 passed, 2 skipped.
- Relay: 25 passed.
- muxd: 32 passed, 3 opt-in VPS tests skipped.
- Baseline commits: app `e0a1583`, relay `33ed69b`, muxd `687b359`.

## Learnings

- muxd integration tests require a unique `INSTANCE_MUTEX_NAME`.
- Passing component-local tests did not detect the real projection/relay contract mismatch.
- A launch reservation must cover termination and replacement, not only the initial live scan.
- A failed process termination must preserve the PTY handle, input writer, and claim; "requested stop" is not "exited."
- Visible owner sidecars need a stable reconnect key and explicit child-exit acknowledgement; websocket closure is not process death.
- Claim identities are restricted to the shared ASCII session-id alphabet so C# and Python filenames cannot diverge.

## Progress

- `L2b_20260709`: unified runner green. App 306 passed / 2 skipped; relay 25 passed; muxd 44 passed / 3 VPS-only skipped.
- muxd checkpoint: `be29d6b` (`chore: preserve muxd local integration work`).
- Independent Claude audit refuted the first L2 candidate on failed-termination and owner-sidecar edges; both were fixed and regression-tested before this acceptance.
- `L3_20260709`: unified runner green. App 308 passed / 2 skipped; serialized projection contract probe passed; relay 35 passed; muxd 55 passed / 3 VPS-only skipped. Evidence: `artifacts/reliability/L3_20260709`.
- L3 removed executable launch commands and structured local paths from relay-facing schema 3, made host replacement atomic only after full-frame validation, added pending-identity reconciliation through the trusted local muxd bind API, and required terminal input acknowledgements to reflect PTY acceptance.
- Independent Codex review found nine L3 edge cases. Fresh CLI recovery, visible-owner restart recovery, conflicting identity handling, persistent self-heal history, actual input acceptance, and stale VPS tests were corrected before acceptance. Durable replay and persistence-failure semantics remain explicitly queued in L4-L5.
- `L4_20260709i`: unified runner green. App 320 passed / 2 environment skips; serialized projection contract passed; relay 51 passed; muxd 75 passed / 3 VPS-only skips. Evidence: `artifacts/reliability/L4_20260709i`.
- L4 now treats post-replace outcomes as match/mismatch/unknown, never rolls back an unknown primary, retries exact committed bytes, flushes directory metadata, validates semantic store shape, and restores the highest valid generation.
- App backups are fenced by authoritative store identity, preventing one store from restoring or pruning another store's snapshots. Concurrent writers are generation-checked under a cross-process lock.
- Relay boot reconciles upload tombstones and durable rename intents without blocking unrelated service startup. muxd persists lifecycle intent, loads the complete manifest before reconciliation, and only reaps a PID when its process-creation token proves custody.
- Independent Claude and fresh Codex reviews found stale-PID reaping, partial-manifest deletion, committed-outcome downgrade, stale-primary recovery, and cross-store backup contamination. Each received a reproducing regression test before L4 acceptance. Lost relaunch acknowledgements remain explicitly queued for L5 idempotency; structural process containment remains L7.
- The final relay review found a committed rename-intent write could leave memory stale and unnecessarily block later writes. Its fault-injected regression was mutation-proven red with the fix removed and green after restoration before final acceptance.
