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

## Decisions Needed

- None. The user authorized consolidation, refactoring, deployment, and reliability hardening.
