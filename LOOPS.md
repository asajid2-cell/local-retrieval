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
| L5 durable command delivery | lease/idempotency; no age loss | crash/restart/replay queue tests | done `L5_20260709`: app 329 passed / 2 skipped; relay 64 passed; muxd 80 passed / 3 skipped |
| L6 canonical server launcher | archive identity and trusted args only | server route/launcher tests | done `L6_20260710`: app 345 passed / 2 skipped; relay 64 passed; muxd 80 passed / 3 skipped |
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
- 2026-07-09 L5 contract: browser/API retries carry one opaque intent id; relay deduplicates
  intent id + canonical payload, rejects key reuse with different payload, leases one command to
  one consumer with a durable token/expiry/attempt, and requires that token for acknowledgement.
  Pending and leased work is never age-pruned; only terminal outcomes may be retention-pruned.
- L5 execution contract: both C# consumers use the lease API and propagate the command intent into
  muxd. Create/relaunch is replay-safe through a durable muxd outcome ledger. PTY prompt insertion
  is explicitly at-most-once: muxd durably records `dispatching` before writing and never repeats an
  uncertain dispatch after restart, because PTY input and a file journal cannot commit atomically.
- 2026-07-09 L5 verification gate is trusted. The accepted L4 implementation was observed red for
  relay retry deduplication and missing lease endpoints, C# intent propagation failed to compile,
  replayed muxd input executed twice, and replayed relaunch accepted conflicting payload/restarted.
- L5 review hardening narrowed each serial C# consumer to one leased command, added a shared
  replay-policy contract, retried fenced acknowledgements, and made undeclared future command
  effects fail closed. Current effects are either refused, read-only, idempotent set operations,
  or downstream intent-fenced.
- Browser mutations use one shared durable intent journal. Network and retryable HTTP failures keep
  the same intent across reloads and later user retries; terminal refusals clear it so corrected
  work receives a fresh intent. Missing durable browser storage prevents the mutation from sending.
- Relay terminal failures now return clearable `409` responses while only uncertain outcomes remain
  retryable `5xx`. Leased work is not mistaken for terminal failure. Direct relaunch retries build
  a stable muxd frame, queue health reports pending/leased work, and terminal command retention is
  bounded without pruning live work.
- muxd serializes create intents per session, lazily terminalizes abandoned `accepted` records, and
  preserves explicit at-most-once uncertainty for PTY input. Terminal intent history is bounded;
  `accepted` and `dispatching` records are never compacted.
- Fresh Claude implementation review initially returned REFUTE with eight concrete failures. After
  fixes, its second pass found one internal `startmux` policy propagation regression. Policy
  derivation moved into the single enqueue authority and a cross-lane lease test was added. The
  final independent verdict was ACCEPT.
- Mutation evidence: removing relay lease-token comparison changed the expected stale-token `409`
  to `200`; restoring it returned green. Replacing muxd cached create-outcome replay with an error
  made the restart replay test fail; restoring it returned green. Full pre-gate suites: app
  329 passed / 2 environment skips; relay 63 passed / 1 projection-artifact skip; muxd 80 passed /
  3 VPS-only skips.
- `L5_20260709`: unified runner green. App 329 passed / 2 environment skips; serialized projection
  contract passed; relay 64 passed; muxd 80 passed / 3 VPS-only skips. Evidence:
  `artifacts/reliability/L5_20260709`.
- 2026-07-09 L6 plan: reduce the open request to an allowlisted target only; refresh and resolve a
  unique canonical session from the server-side archive; derive tool, workspace, and aliases from
  that trusted record; pass a typed descriptor into `SessionLauncher`; and launch with structured
  `ProcessStartInfo.ArgumentList` entries only. Unknown, ambiguous, unsupported-tool, invalid-target,
  and missing-workspace cases fail closed. The verifier must be observed red before implementation.
- 2026-07-09 L6 archive-runtime verifier is mutation-proven. With `ArchiveRuntime`'s semaphore
  weakened from `(1, 1)` to `(2, 2)`, `dotnet test
  native\CodexLocalRetrieval.Native.Tests\CodexLocalRetrieval.Native.Tests.csproj --no-restore
  --filter "FullyQualifiedName~ArchiveRuntimeTests"` exited 1: both tests failed because a second
  archive operation entered during an active operation and idle unload completed during an active
  reader. Restoring `(1, 1)` and rerunning the same command exited 0: 2 passed.
- 2026-07-10 L6 transactional-open verification is mutation-proven. Resolver failure and new-thread
  failure tests first failed because the prior open session was cleared, then passed after both
  flows became prepare-then-commit. Codex ordering tests also exposed and then verified the required
  live-check -> cross-process lease -> app-server ensure -> local active-claim sequence.
- The final L6 focused gate passed 28/28:
  `dotnet test native\CodexLocalRetrieval.Native.Tests\CodexLocalRetrieval.Native.Tests.csproj
  --no-restore --filter
  "FullyQualifiedName~ArchiveRuntimeTests|FullyQualifiedName~SessionOpenServiceTests|FullyQualifiedName~ServerSingleOwnerGateTests|FullyQualifiedName~TranscriptEnumeration"`.
- The inherited full-suite gate was observed red as an operational verifier: 16-way method-level
  parallelism drove the owned testhost above 2.3 GB, and the 3-minute hang collector identified
  `SyncFromDisk_ResurfacesRealStoreIncludingMonthsOld` as an unbounded live-archive scan. Tests are
  now single-worker, `RealStore` and `LiveCodex` are mechanically opt-in, and the acceptance runner
  has a per-test hang collector. The corrected full suite passed 345 tests with 2 environment skips
  in 42 seconds; a plain `dotnet test --no-build --no-restore` independently passed the same
  345/347 contract in 26 seconds.
- Final independent Claude review `mux-l6-review-20260709` returned ACCEPT after re-reading the
  frozen production diff, including archive enumeration, transactional route takeover, writer
  leases, process cleanup, structured arguments, and the bounded gate changes. No deterministic
  session-loss, duplicate-writer, route, archive, launch, or process-leak blocker was found.
- `L6_20260710`: unified runner green. App 345 passed / 2 environment skips; serialized projection
  contract passed; relay 64 passed; muxd 80 passed / 3 VPS-only skips. Evidence:
  `artifacts/reliability/L6_20260710`. The runner advanced `CURRENT.md` only after every gate passed.
