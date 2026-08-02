# Fold - wp-telemetry

## Result

The app now records structured, local, redacted terminal events for the scoped branch/checkpoint,
spawn configuration, rename/archive, sync, kill/takeover, recency bump, and start-chat/filing paths.
Branch, checkpoint, and checkpoint-spawn events are written synchronously in `ArchiveService`, so
the event exists before the UI refreshes the existing Custody/Integrity panel. Existing launch
governor and remote-command telemetry remain the authority for launch claims and remote execution.

## Changes

- `SessionEventLedger` now rotates monthly files into ordered segments at 8 MiB, caps retained
  event bytes at 64 MiB, removes files older than 180 days, and rebuilds its sidecar index after
  pruning. Limits are configurable in tests.
- Segment ordering is explicit by month and segment for both scans and indexed reads. A runtime
  test caught and fixed the initial lexical-order bug where the oldest base file appeared newest.
- All event fields still pass through the existing secret/path redaction before durable write.
- `Diag` now rotates at 2 MiB and retains four archived files instead of growing without bound.
- Branch/checkpoint/spawn failures retain the exact user-visible error and correlate source,
  created-session, checkpoint, tool, and outcome identities.
- The existing Integrity panel is force-refreshed after branch/checkpoint outcomes, making the
  recorded reason visible under Recent events without adding a parallel telemetry UI.
- Sync, resume takeover kill, bump, start-chat folder/intent/launch, checkpoint filing/rename, and
  branch archive paths now record terminal outcomes. Generic app events retain intent/tool/workspace
  correlation when no session exists yet.

## Forced Failure Trace

Runtime action: branch a real on-disk transcript whose tool is `broken-tool`.

User-visible result:

```text
Branch failed: branching isn't supported for tool 'broken-tool'.
```

Event found afterwards with `SessionEventLedger.ReadForSession("telemetry-source")`:

```json
{"kind":"branch.failed","severity":"error","source":"archive","sessionId":"telemetry-source","sessionIds":["telemetry-source"],"tool":"broken-tool","title":"Telemetry source","summary":"Branch failed: branching isn't supported for tool 'broken-tool'.","details":{"operation":"branch","outcome":"failed"}}
```

The exact error is present without a screenshot. A separate durable-byte test writes an exception
containing `'0x00' is an invalid start of a value`, a Windows user path, and a fake API key; the
error text remains while the path becomes `[path]` and the key becomes `[redacted]`.

## Verification

- `dotnet test native\CodexLocalRetrieval.Native.Tests\CodexLocalRetrieval.Native.Tests.csproj --no-restore --filter "FullyQualifiedName~SessionEventLedgerTests|FullyQualifiedName~BranchSessionTests|FullyQualifiedName~DiagTests"`:
  40 passed, 0 failed.
- Forced-failure test rerun alone with detailed console logging:
  1 passed; emitted the `branch.failed` event shown above.
- `dotnet test --no-restore`: final rerun passed 727, failed 0, skipped 1
  (`Pty_BidirectionalMirror_TypeThenSeeOutput`).
- `dotnet build native\CodexLocalRetrieval.Native\CodexLocalRetrieval.Native.csproj --no-restore -p:Platform=x64`:
  succeeded with 0 errors and two pre-existing nullable warnings in `MainPage.ChatInfo.cs`.
- `git diff --check` across every wp-telemetry-owned source/test file: passed.

The first full-suite attempt encountered a transient `Access is denied` while another lane's
modified `WindowsProcessJobTests` launched `powershell.exe`; the isolated test reproduced once,
the equivalent process launch succeeded independently, and the required full-suite rerun passed.
