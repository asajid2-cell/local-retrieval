# wp-discovery Fold

Status: READY FOR APEX INTEGRATION
Date: 2026-08-02

## Contract

Frozen first at `campaign/web-parity/folds/wp-discovery-contract.md`.

- Public routes: `/remote/api/discovery/chats` and `/remote/api/discovery/facets`.
- PC server routes: `/api/discovery/chats` and `/api/discovery/facets`.
- Offset + total pagination, default 50, max 100.
- Reuses `ChatFilter` and `ArchiveService.FilterChats`.
- Exact `[phrase]` search retains existing ArchiveService semantics.
- No command, executable, source path, transcript path, or working-directory path leaves the PC.
- `muxName` is the only additive row field beyond the charter because the existing relay
  `startmux` app-command requires it.

## D-PC

Implemented:

- `app/native/CodexLocalRetrieval.Core/Remote/DiscoveryApi.cs`
  - Typed query, page, row, and facet DTOs.
  - Text, include/exclude tags, ANY/ALL, agent, date range, user-message minimum, hidden toggle,
    project, sort, offset, and limit normalization.
  - Stable explicit sorting; search relevance order preserved.
  - Desktop-parity first/last-user row titles.
  - User-tag, phrase, and project facets.
  - Existing trusted resume gate and safe multiplex name.
  - Display metadata redaction consistent with `RemoteApi`.
- `app/native/CodexLocalRetrieval.Native.Tests/DiscoveryApiTests.cs`
  - 6 tests covering compound filters, default hidden behavior, exact phrase search, pagination,
    metadata disclosure boundary, resumability, facets, normalization, unknown projects, and
    first/last-user titles.

Removed the stale pushed archive index:

- Deleted `Core/Services/ArchiveService.ArchiveIndex.cs`.
- Deleted obsolete `Native.Tests/ArchiveIndexTests.cs`.
- Removed `PushArchiveIndexAsync`, its two call sites, and `_indexPushing` from
  `Native/MainPage.Remote.cs`.
- Confirmed no `PushArchiveIndexAsync`, `BuildArchiveIndexJson`, `ArchiveIndexMaxChats`, or
  `ArchiveIndexSchemaVersion` symbols remain under `app/native`.

## D-Web

Implemented:

- `relay/public/chats.html`
  - Phone-first operational archive browser.
  - Search, tri-state tags, phrase chips, project, agent, date, sort, message minimum, hidden toggle,
    explicit loading/empty/PC-unreachable states, pagination, and resume.
  - Dense desktop filter/results layout and in-flow mobile filter disclosure.
- `relay/public/chats.js`
  - Direct `/remote/api/discovery` requests; no relay proxy or second index.
  - Generation-fenced loads, facet rendering, selected zero-count filter preservation, paging,
    exact phrase queries, and existing picker-backed resume.
- `relay/public/picker.js`
  - Migrated from `/api/archive-index` to `/remote/api/discovery/chats`.
  - Existing intent journal, double-tap dedupe, app-command queue, refusal detail, polling, and
    queued outcome are unchanged.
- `relay/tests/discovery-page.test.js`
  - Query-contract checks, explicit state checks, DOM render smoke, facets, disabled resume,
    resume delegation, and markup/script-order checks.
- Updated the two existing resume-picker suites to supply discovery rows while continuing to use a
  real relay for queue, lease, ack, refusal, dedupe, and offline behavior.

## Verification

- Discovery .NET tests: `6/6` passed.
- Full relay suite: `388 passed`, `1 skipped`, `0 failed` (381 top-level subtests / 389 tests).
- Full native run: `710 passed`, `1 skipped`; two unrelated `WindowsProcessJobTests` failed because
  this machine refused a `powershell.exe` helper launch / omitted helper output. The class reproduced
  in isolation. No discovery code touches process jobs.
- Native run excluding that class hit two unrelated ledger lock stress timeouts under broad
  contention; both exact tests passed in isolation (`2/2`).
- `git diff --check`: clean apart from existing line-ending warnings.
- Stale archive-index symbol scan: clean.
- UI telltale audit: clean; the only `border-radius:50%` is the 8px semantic status dot.
- UI destruction battery, PC-unreachable state: 8/8 viewports, 0 defects.
- UI destruction battery, populated hostile fixture (52 rows, long unbroken title/workspace,
  facets, disabled row, pagination): 8/8 viewports, 0 defects.

Screenshots:

- `campaign/web-parity/folds/wp-discovery-phone.png`
- `campaign/web-parity/folds/wp-discovery-laptop.png`
- `campaign/web-parity/folds/wp-discovery-ultrawide.png`

Visual system: existing multiplex flat operational language: neutral surfaces, rose command accent,
green/amber/red state colors, compact rows, 7/10px radii, and no decorative gradients or card nesting.

## Apex Handoff

Expected by the write partition and still required:

1. Add the server-owned `MapDiscovery` HTTP adapter around `ArchiveRuntime.UseAsync` and
   `DiscoveryApi.Chats/Facets`. Bind the query names exactly as frozen in the contract.
2. Add the single `app.MapDiscovery(archiveRuntime, new DiscoveryApi(archive));` registration line
   in `CodexLocalRetrieval.Server/Program.cs`.
3. Add navigation links where apex wants them; this branch did not touch shared nav files.
4. Deploy PC server + nginx `/remote/` restore + static assets, then run real authenticated E2E for
   auth expiry, tunnel loss, pagination, phrase search, filters, and resume.

`Server/Program.cs`, `relay/server.js`, nginx, and the unrelated existing
`scripts/deploy-muxd.ps1` worktree change were not touched.
