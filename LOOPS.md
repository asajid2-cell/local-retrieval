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
| L3 opaque relay protocol | relay never stores or receives commands/paths | real projection contract + relay tests | pending |
| L4 durable state | success requires flush/replace/read-back | injected-failure persistence tests | pending |
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
