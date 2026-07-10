# Campaign: Durable Single-Writer Sessions

## Win Condition

- Every app-controlled Claude/Codex start resolves a canonical archived session identity locally.
- Exactly one cross-process reservation covers the complete check, stop, spawn, and visibility gap.
- Relaunch does not start until the previous process tree is confirmed exited.
- Remote DTOs contain opaque identity and result data, never executable commands, local paths, or secrets.
- Persistence success means write, flush, replace, reopen, and semantic read-back all succeeded.
- Queued work is leased, idempotent, durable across outages, and never discarded because of age alone.
- Crash-orphaned child writers are contained or remain detectable until they exit.
- The real app projection, relay, muxd, and installed runtime pass one end-to-end acceptance chain.

## Constraints

- Fail closed whenever ownership, identity, process exit, or persistence cannot be verified.
- Remote automation never kills a local writer without an explicit user-confirmed operation.
- Existing unrelated user features and archived session data must be preserved.
- External manual CLI launches cannot be prevented, but app-controlled launches must detect them.

## Terrain

- The inherited app baseline passed 303 tests with 2 skipped on 2026-07-09.
- The inherited relay baseline passed 25 tests on 2026-07-09.
- The muxd suite passed 32 tests with 3 opt-in VPS tests skipped after its test mutex was isolated.
- Known defects are tracked in `LOOPS.md`; mutable acceptance state lives only in `CURRENT.md`.

## Solved Ground

| Invariant | Evidence | Date |
| --- | --- | --- |
| App baseline preserved before consolidation | commit `e0a1583`; full .NET suite green | 2026-07-09 |
| Relay baseline has deploy provenance | relay commit `33ed69b`; relay suite green | 2026-07-09 |
| muxd integration suite can run beside production | muxd commit `687b359`; 32 tests green | 2026-07-09 |

## Approach Tree

| Approach | Prediction | Kill Criterion | Status |
| --- | --- | --- | --- |
| Caller-local checks | Misses cross-component startup races | Any simultaneous path starts twice | dead |
| Shared identity lease plus consolidated launch service | One winner across app and muxd | Any app-controlled path bypasses lease | active |
| Relay-owned executable commands | Protocol remains coupled and leaks custody | Command/path appears in remote DTO | dead |
| Local command construction from opaque intent | Relay survives schema evolution safely | Real producer/consumer contract fails | active |

## Evidence Log

- 2026-07-09: Independent Codex review confirmed muxd concurrent-create and asynchronous-relaunch overlap.
- 2026-07-09: Real producer projection omits `muxCommand`, while relay/UI fixtures still require it.
- 2026-07-09: Relay persistence swallows write failures and command delivery expires after ten minutes.
- 2026-07-09: Independent tandem audit found that failed PTY termination orphaned the handle and visible owner websocket closure was incorrectly treated as child exit.
- 2026-07-09: L1/L2 accepted at `L2b_20260709`: failed termination now retains PTY/input/claim, owner registration is claim-protected with reconnect identity and explicit exit confirmation, boot/heal preserve aliases, node CLI shims are scanned, and relay backlog is bounded.
- 2026-07-09: Unified acceptance evidence: app 306 passed / 2 skipped, relay 25 passed, muxd 44 passed / 3 VPS-only skipped. muxd checkpoint `be29d6b`.
- 2026-07-10: L7 contained app-server and Claude child processes with Windows Job Objects, bounded
  all process/line capture, supervised server pumps, and retained cross-process ownership whenever
  child termination could not be proven. Archive loading became lazy, per-session serialized, and
  context-independent so a blocked UI dispatcher cannot deadlock an in-flight load.
- 2026-07-10: The dispatcher deadlock repro now prints `no deadlock observed`; the prior flaky
  confirmed-termination claim-release test passed 50/50 isolated runs; the broader reliability
  slice passed 188/188 and the full .NET suite passed 388 with 2 environment skips.
- 2026-07-10: The first L7 runner attempt exposed a muxd resource leak under cumulative session
  churn. Dormant and failed-before-spawn sessions eagerly created immortal input-writer threads,
  and already-stopped sessions never sent the writer sentinel. Writers are now lazy and
  generation-bound, successful terminal paths stop them, and a mutation-proven 100-session test
  verifies zero thread growth instead of the old +100.
- 2026-07-10: Unified `L7_20260710` acceptance passed app build/tests, real projection contract,
  relay tests, and muxd tests. Evidence is under `artifacts/reliability/L7_20260710`; machine-owned
  `CURRENT.md` advanced only on the all-green rerun.
- 2026-07-10: The post-L7 muxd audit moved durable manifest I/O off the event loop under a
  cancellation-safe global state transaction, closed natural-EOF writer leaks, made enqueue and
  shutdown sentinel ordering atomic, pinned writers to PTY generations, and kept non-durable input
  responsive during unrelated durable writes.
- 2026-07-10: Independent tandem `mux-l7b-async-persistence-20260710` accepted pinned final blobs
  `212ddf8` / `8129a19` / `bb4575c`. The unified `L7b_20260710` gate passed app 388 / 2 skipped,
  relay 66, and muxd 92 / 3 VPS-only skipped. Evidence is under
  `artifacts/reliability/L7b_20260710`.

## Decisions Needed

- None. The user authorized consolidation, refactoring, deployment, and reliability hardening.
