# wp-startchat Fold

Status: implementation complete in branch scope; apex route wiring remains.
Contract: `campaign/web-parity/folds/wp-startchat-contract.md`

## Implemented

- Added read-only start picker projections to `DiscoveryApi`: decks, deck-scoped collections,
  checkpoints, and opaque workspace identities. Workspace paths stay PC-local and are resolved
  again at command execution time.
- Added relay support for the `startchat` app command with `intent-fenced` replay, strict
  identity-only fields, launch-source validation, durable reload validation, normalized
  fingerprinting, lease delivery, and result labels.
- Added the phone-first Start chat dialog to `chats.html` and `chats.js`: deck/collection filing,
  optional new collection, checkpoint or blank-chat launch mode, tool, opaque workspace,
  subfolder, phrase, stable intent submission, result polling, duplicate-submit collapse, and
  queued retry against the same command.
- Added the native `startchat` handler. It validates stale picker identities before mutable start
  work, files checkpoint branches or queues blank-chat filing, and starts a browser-drivable mux
  through the existing governed launch path with local intent minting disabled.
- Added focused native, relay, and web tests plus phone, laptop, and ultrawide screenshots:
  `wp-startchat-phone.png`, `wp-startchat-laptop.png`, and `wp-startchat-ultrawide.png`.

## Frozen Boundary

The frozen command payload and picker response shapes are in
`campaign/web-parity/folds/wp-startchat-contract.md`. The relay stores picker identities and
display metadata only. It rejects executable, command-line, and path-shaped remote fields.

## Verification Re-run

- `dotnet build app\native\CodexLocalRetrieval.Native\CodexLocalRetrieval.Native.csproj -c Release --no-restore`
  passed with 0 errors and 2 pre-existing nullable warnings in `MainPage.ChatInfo.cs`.
- `dotnet test app\native\CodexLocalRetrieval.Native.Tests\CodexLocalRetrieval.Native.Tests.csproj -c Release --filter "FullyQualifiedName~DiscoveryApiTests|FullyQualifiedName~StartDiscoveryApiTests|FullyQualifiedName~RemoteStartChatContractTests|FullyQualifiedName~ArchitectureLaunchSurfaceTests|FullyQualifiedName~RemoteApiCommandPollerTests|FullyQualifiedName~SessionReclaimTests" --no-restore`
  passed 53/53.
- `dotnet test app\native\CodexLocalRetrieval.Native.Tests\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --no-restore`
  ran 721 tests: 718 passed, 1 skipped, and 2 failed in `WindowsProcessJobTests`. The failures are
  outside this diff: `powershell.exe` could not start due to access denied, and the abrupt-owner
  process probe returned one field instead of two.
- The two failing `WindowsProcessJobTests` were rerun in isolation once and failed identically.
- `npm test` in `relay` passed 403, skipped 1, failed 0.
- `node --test tests/start-chat-command.test.js tests/start-chat-ui.test.js` in `relay` passed 7/7.
- `node --check relay\public\chats.js` and `node --check relay\server.js` passed.
- `destruct_check.mjs relay/public/chats.html` passed with 0 defects across all 8 viewports.
- `destruct_check.mjs relay/public/chats.html --click "#startchatbtn"` passed with 0 defects across
  all 8 viewports in the open-dialog state.
- `git diff --check` passed; Git reported line-ending conversion warnings only.

## Apex Integration

`Program.cs` already calls `app.MapDiscovery(archiveRuntime)`, but the apex-owned
`app/native/CodexLocalRetrieval.Server/DiscoveryEndpoints.cs` adapter currently maps only chats
and facets. Apex must map these frozen GET routes before live end-to-end start-chat verification:

- `/api/discovery/start/decks`
- `/api/discovery/start/collections`
- `/api/discovery/start/checkpoints`
- `/api/discovery/start/workspaces`

The corresponding public paths remain under the existing
`/multiplex/pc/api/discovery/` gate. No nginx or public-gate change is required by this branch.

`git pull --ff-only` was not possible because this checkout has no configured remote or upstream.
