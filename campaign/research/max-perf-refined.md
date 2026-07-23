# LANE max-perf — refined leaf set (win32-perf prong)

Refined from `campaign/win32-perf.md` against HEAD 7968259 (tree verified clean except untracked
`campaign/research/`). All findings below are from reading the current tree; no test suites were run
(gates last green at tag `L8k_20260712_web_terminal_final` per `app/CURRENT.md`; verifier commands
below copy the exact bounded invocation forms from `app/tools/run_gates.ps1`).

## Validation

### Task-1 landed-state checks (the plan's mandated FIRST ACTIONs)

**B1 (core scan cache): PARTIALLY LANDED — leaf survives as a scoped delta.**
- LANDED in `Core/Remote/RunningSessions.cs`: a per-pid transcript-probe cache, 3 s TTL, keyed by
  (pid, process start time), **positives-only** (`[F#8]` explicitly cited in the comment at :21-22;
  `CachePositiveTranscripts` called only on `PidOutcome.Resolved`, :507) — lines 23-27, 498-562.
  Also landed: the `CODEXLOCAL_LEGACY_HANDLE_SCAN` env fallback (:29-37) and `BoundedWmiOptions`
  (:38-42) used by `TryScan` — the base plan's claim that RunningChats' WMI is "unbounded" is now
  only true of the GUI copy (`MainPage.RunningChats.cs:54` uses a bare `ManagementObjectSearcher`).
- NOT LANDED: there is **no cache over the WMI world sweep or the claude-registry enumeration** —
  `TryScan` (:50-96) runs 2 WMI queries on every call; `TryLiveSessionPids` (:324-357) re-runs
  scan + registry + handle probe per call. `grep -ri "InvalidateScanCache|ScanCache" app/` → **zero
  matches**. No `[F#3]` bypass flag: `SessionLaunchClaims.cs:425` calls `TryAllLiveSessionIds`
  directly (base plan's ":130" anchor for the post-claim re-check has drifted; the oracle call is
  at :425). So B1's remaining scope = TTL cache around the sweep+registry layer, post-claim bypass,
  invalidation calls, mirroring the landed per-pid cache's fail-never-cached discipline.
- Oracle caller anchors: `SessionIntegrity.cs:165` (base said :154 — drifted +11),
  `SessionCustody.cs:227` (exact), `SessionLaunchClaims.cs:425` (exact), `MuxIdentityTransfer.cs:127`.
- GUI 4 s cache + `InvalidateRunningCache()` confirmed at `MainPage.RunningChats.cs:28-43`; still
  bypassed by all Core callers.

**B4 (integrity off-thread): NOT LANDED — full scope, new rebase surface.**
- `RenderIntegrity` still calls `SessionIntegrity.Build` synchronously on the UI thread:
  `MainPage.Integrity.cs:36` (anchor exact). `RiskySessionActionBlocked` is now at :315-320 (base
  said :311) and still forces a synchronous `RenderIntegrity(force: true)`.
- 413c13b's reclaim work added ~200 lines to MainPage.Integrity.cs: `ReclaimSelectedSessionAsync`
  (:117-201) — which already uses the exact pattern B4 needs (`await Task.Run(...)` +
  `_reclaimSeq` monotonic guard), `CanReclaim` delegating to Core `SessionIntegrity.ReclaimAvailable`
  (SessionIntegrity.cs:158-161), and new call sites `RenderIntegrity(force:true)` at
  Integrity.cs:199 and `MainPage.Sessions.cs:211`. B4 must preserve ReclaimAvailable/CanReclaim
  semantics and these call sites verbatim.
- The 3×150 ms registry retry sleeps are now in `TryReadRegistryFileWithRetry`
  (RunningSessions.cs:274-295, `Thread.Sleep(150)` at :280) — anchor range exact, function renamed.

**Polling-mesh facts (§1): HOLD, minor drift.**
- Timers: `MainPage.Remote.cs` — 30 s sync :266, 3 s cmd :273-276 (exact), 5 s tab :279-282;
  `PushProjectsAsync` :320 (exact), `PollCommandsAsync` :359 (base cited :359-377 for ssh — now
  the poll method start), `TryScan` call :331, `BuildProjectsProjectionJson` call :338.
- Agent inbox: `MainPage.Agent.cs` 1.5 s timer :46-48, full `File.ReadAllText` :66 (base :62-73 ✓).
- Live tick: `MainPage.xaml.cs` `_liveTimer` :119, `LiveTickAsync` :123 (base :118-152 ✓).
- New-chat poll: `MainPage.StartChat.cs:297` `SyncNowAsync` (base :292-309 ✓).
- No `FileSystemWatcher` anywhere in `app/native` (re-verified by the member greps above).

### Other anchor verification (current tree)

- `ArchiveService.cs` is **5,694 lines / 288,106 bytes** and — critically — is already
  `public sealed partial class ArchiveService` (line 22), with an existing second partial file
  `ArchiveService.Branch.cs`. The mechanical split has an in-repo precedent and zero declaration
  risk. There are **no `#region` markers** — the base brief's "region-named" split must be
  method-group-based (map below).
- Hot-path anchors ALL EXACT: `CountUserPrompts`:92 (called :4652, :4818), `LoadStoreWithRecoveryAsync`:236,
  `EnsureContent`:534, `SaveAsync`:570, `RefreshSessions`:684, `Search`:701, `SearchDiskPhraseAsync`:738,
  `DeepSearchContentAsync`:823, `MaxAutoBackups=30`:1579, `UserTags`:2638, `AllChatTags`:2678,
  `HiddenChatCount`:2718, `FilterChats`:2720, `BuildProjectsProjectionJson`:3855 (ends :3992, next
  member :3993 — matches the head's "~3855-3990"), `ResolveMuxTabChats`:4022, claude tail parsers
  :4826/:4922, `SearchText`:5294, `FuzzyScore`:5354.
- MainPage anchors: `MainPage_Loaded`:53, `SearchBox_TextChanged`:215 (still zero debounce, calls
  `ApplyFilters()` directly), `RenderCurrent`:238, `RenderArchive`:451 with
  `MainContent.Children.Clear()`:459, `RenderIntegrity()` call :462 (exact),
  `AccentColorPicker.ColorChanged → await _archive.SaveAsync()` :1942-1949 (exact),
  `RiskySessionActionBlocked` call :2296 (exact), `PasswordVault` now at :2723/:2758/:2770/:2778
  (base said :2713-2725 — drifted ~+10, and there are 4 sites, not 1).
  `ApplyFilters` at `MainPage.Tags.cs:113-126` still calls `RenderCurrent()` (:123).
  Transcript pane still an unvirtualized StackPanel (`MainPage.xaml:332-334` exact); `SessionList`
  ListView at `MainPage.xaml:223`.
- `Native.csproj:80` `<PublishReadyToRun>False</PublishReadyToRun>` — exact.
- `GitHistory` lives at `Core/Memory/GitHistory.cs` (base plan gave no path; MainPage.Brain.cs:95 ✓).
- Install script is `app/scripts/install.ps1` (exists; D2's R2R flag goes there).

**Base-plan assumption now FALSE (B3 scope shrinks):** `SessionEventLedger.ReadForSession`
(SessionEventLedger.cs:177-202) no longer naively "reverse-parses every line" via string splits —
a streaming 64 KB reverse-block reader exists (`ReadFileNewestFirst`, :214-271,
`ReverseReadBufferBytes` :27). BUT it still **JSON-deserializes every reversed line of every
monthly file** (`TryParseReversedLine` :273-296 runs before any session-id filter,
`EventMatchesAnyId` :196 filters after the parse), and a quiet session still scans all-time.
B3 remains valid with reduced scope: cheap pre-parse id screen and/or per-session sidecar index;
the reverse reader itself is done.

### Verifier infrastructure facts

- `app/tools/run_gates.ps1` (NOT repo-root tools/ — confirmed): dotnet-tests gate =
  `dotnet test native\CodexLocalRetrieval.Native.Tests\... -c Release --no-build
  --filter 'TestCategory!=RealStore&TestCategory!=LiveCodex' --blame-hang --blame-hang-timeout 3m
  --blame-hang-dump-type mini`. Leaf verifiers below reuse these exact flags.
- run_gates.ps1 is NOT usable as a per-leaf verifier: it hardcodes external live checkouts
  (`C:\Users\Ahmed\multiplex-app-patch`, `C:\Users\Ahmed\muxd`), runs their suites, and fails on
  any dirty tree (source-provenance). Leaf verifiers therefore invoke dotnet directly.
- `CodexLocalRetrieval.Native.Tests` exists (40 test files); `ArchiveServiceTests.cs:16` has the
  `WriteRollout` corpus idiom (exact); `SessionLaunchGovernorTests` injects liveness (test seam for
  B1); `MainPage.Custody.cs:12-89` has the seq-guard + Task.Run pattern (exact, for B4);
  `app/tools/ProjectionContractProbe` exists — a ready-made projection-shape verifier for the split
  leaf and B5.
- `Z:\328\CMPUT328-A2\codexworks\301\premax-plan-win32-v3.md` exists (cited rules restated in B1/B2
  notes; not re-derived).
- `.orch/worktrees/` already exists in-repo — branch-per-leaf infrastructure is live.

## Leaves

Conventions carried from the base plan: perf tests in `CodexLocalRetrieval.Native.Tests/Perf/`,
`[TestCategory("Perf")]`, MSTest `[Timeout]` mandatory on every test, corpora fabricated under
`%TEMP%`, counters/ratios primary and wall-clock bounds ≥5× headroom. Every verifier below is one
bounded command (hang-bounded by `--blame-hang-timeout 3m` per test run; scripts self-bounded).
`BUILD2` in prose = build tests csproj + native app csproj with `-c Release -m:1
-p:UseSharedCompilation=false` (both, per run_gates precedent — a Core API change must not break
the app silently). The full-suite filter includes new Perf tests automatically (they are neither
RealStore nor LiveCodex).

```json
[
  {
    "id": "perf-s0-split",
    "title": "ArchiveService.cs mechanical partial-class split (pure code motion)",
    "kind": "build",
    "goal": "Split app/native/CodexLocalRetrieval.Core/Services/ArchiveService.cs (5694 lines, already 'public sealed partial class' with precedent file ArchiveService.Branch.cs) into method-group partial files with ZERO behavior change and zero declaration edits: ArchiveService.Store.cs (load/recovery/save/backups/settings-snapshot/lock: lines ~210-676 store block + ~1579-1700 backup block), ArchiveService.Content.cs (EnsureContentAsync/LoadContentCoreAsync/EnsureContent/ReloadContentAsync/SourceWriteTimeUtc ~468-557 + CopyPayload/RestorePacket ~2946-3025), ArchiveService.Search.cs (ReapplyList/RefreshSessions/Search/SearchDiskPhraseAsync/DeepSearchContentAsync/DeepSearch + helpers ~677-1110, RetrieveForQuestion ~2941, scoring block SearchText..Score ~5294-5467), ArchiveService.Filters.cs (tags/filters/layers/palette/colors ~2626-2913), ArchiveService.Parse.cs (text helpers+SafeRead*+CountUserPrompts ~37-160, ParseSourceAsync..DetectTool ~3093-3335, parse core ParseCodexCoreAsync..CleanFallbackTitle ~4464-5292), ArchiveService.Projection.cs (RemoteMuxLaunch..ReadCodexRolloutPath ~3727-3852, BuildProjectsProjectionJson 3855-3992, SetTabColorAsync/SetTabKind/ResolveMuxTabChats/ResolvePendingMuxBindings/AssignTabChats/FreshAgentSessionIdByStart/mux-tab history ~3993-4302), ArchiveService.Launch.cs (IsResumableId..QuotePowerShellSingle ~3527-3725, ResolveClaudeExe/ResolveCodexExe/working-dir helpers ~4304-4360, ResumePrompt ~5469). Everything else (ctor/fields/consts, renames, pending chats, collections/decks, agent commands, sync/scan, events, settings/titles) stays in ArchiveService.cs. Private helpers shared across groups may live in whichever partial file compiles cleanest — same class, placement is free. Also land app/tools/gates/verify_archiveservice_split.ps1: (a) git diff --numstat vs merge-base touches ONLY app/native/CodexLocalRetrieval.Core/Services/ArchiveService*.cs plus the gate script itself; (b) member-declaration parity — regex-extract all lines matching '^    (public|private|internal|protected)' from git show <merge-base>:ArchiveService.cs and from the union of ArchiveService*.cs at HEAD, sort both multisets, assert equal. Exit 1 on any mismatch.",
    "tier": "default",
    "deps": [],
    "reads": ["app/native/CodexLocalRetrieval.Core/Services/ArchiveService.cs", "app/native/CodexLocalRetrieval.Core/Services/ArchiveService.Branch.cs"],
    "verifier": "cmd /c \"cd /d app && powershell -NoProfile -ExecutionPolicy Bypass -File tools\\gates\\verify_archiveservice_split.ps1 && dotnet build native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet build native\\CodexLocalRetrieval.Native\\CodexLocalRetrieval.Native.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"TestCategory!=RealStore&TestCategory!=LiveCodex\" --blame-hang --blame-hang-timeout 3m --blame-hang-dump-type mini && dotnet run --project tools\\ProjectionContractProbe\\ProjectionContractProbe.csproj -c Release -- %TEMP%\\s0-projection.json\"",
    "size": "M",
    "notes": "MUST MERGE FIRST across the whole campaign (see Seams): every later ArchiveService leaf in any lane branches from post-split main. No 'using' additions beyond what moved code needs; no visibility changes; no method body edits. ProjectionContractProbe run proves the projection region survived the move byte-identically in behavior. Anchors verified 2026-07-22 at HEAD 7968259."
  },
  {
    "id": "perf-p0-harness",
    "title": "Perf measurement foundation: PerfCorpus + PerfCounters + perf_gates.ps1 + baselines",
    "kind": "build",
    "goal": "Land (1) native/CodexLocalRetrieval.Native.Tests/Perf/PerfCorpus.cs — fabricates N-session stores and realistic .jsonl transcripts incl. 30-50 MB ones under %TEMP%, reusing the WriteRollout idiom from ArchiveServiceTests.cs:16; (2) Core/Services/PerfCounters.cs (~40 lines) — static Interlocked counters (searchTextCompositions, storeFullDeserializes, wmiSweeps, transcriptBytesRead, projectionSerializes, ledgerBytesRead) with Reset()/Snapshot(), no-op cost when unread, no conditional compilation; (3) app/tools/perf_gates.ps1 -Tag <t> — builds (Release, -m:1, UseSharedCompilation=false), runs dotnet test --filter TestCategory=Perf with --blame-hang-timeout 3m, writes app/artifacts/perf/<tag>/results.json; (4) Perf/BaselineProbeTests.cs — 3 RECORD-ONLY tests (no asserts beyond [Timeout(120000)]): search-pass cost over a 4000-session corpus, SaveAsync wall on that store, SessionIntegrity-adjacent scan cost on a fabricated corpus — writing named numbers into results.json for the campaign before-picture.",
    "tier": "default",
    "deps": [],
    "reads": ["app/native/CodexLocalRetrieval.Native.Tests/ArchiveServiceTests.cs", "app/tools/run_gates.ps1"],
    "verifier": "cmd /c \"cd /d app && powershell -NoProfile -ExecutionPolicy Bypass -File tools\\perf_gates.ps1 -Tag p0 && powershell -NoProfile -Command \"if (-not (Test-Path 'artifacts\\perf\\p0\\results.json')) { exit 1 }; $j = Get-Content 'artifacts\\perf\\p0\\results.json' -Raw | ConvertFrom-Json; if (@($j.PSObject.Properties).Count -lt 3) { exit 1 }\" && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"TestCategory!=RealStore&TestCategory!=LiveCodex\" --blame-hang --blame-hang-timeout 3m\"",
    "size": "M",
    "notes": "Parallel-safe with perf-s0-split (new files only; PerfCounters.cs is a NEW Core file, no ArchiveService edit). perf_gates.ps1 must NOT depend on external checkouts (unlike run_gates.ps1). Counter call-site insertion into hot paths happens in the consuming leaves, not here."
  },
  {
    "id": "perf-a1-search",
    "title": "Search hot path: cached search text, precomputed sort keys, cached tag scans",
    "kind": "build",
    "goal": "Make one search/filter pass over 4000 sessions cost <25 ms and <4 MB alloc: cache the composed lowercase-normalized search text on SessionRecord/ArchiveSession (invalidate on tag/phrase/title mutation and re-parse; PerfCounters.searchTextCompositions increments on composition), precompute CreatedAtUtc DateTime + HasUserTags at parse/load time, cache AllChatTags/HiddenChatCount per store generation, kill the double sort (Search in ArchiveService.Search.cs returns unsorted or shares the comparer with FilterChats in ArchiveService.Filters.cs), cap fuzzy Levenshtein to title-class fields, move the per-pass session.Tags mutation out of RefreshSessions to parse time. Pre-split anchors (for orientation only): Search was ArchiveService.cs:701, SearchText :5294, FuzzyScore :5354, UserTags :2638, AllChatTags :2678, HiddenChatCount :2718, RefreshSessions :684.",
    "tier": "default",
    "deps": ["perf-p0-harness", "perf-s0-split"],
    "reads": ["app/native/CodexLocalRetrieval.Core/Services/ArchiveService.Search.cs", "app/native/CodexLocalRetrieval.Core/Services/ArchiveService.Filters.cs", "app/native/CodexLocalRetrieval.Core/Models/ArchiveModels.cs"],
    "verifier": "cmd /c \"cd /d app && dotnet build native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet build native\\CodexLocalRetrieval.Native\\CodexLocalRetrieval.Native.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"FullyQualifiedName~SearchHotPath\" --blame-hang --blame-hang-timeout 3m && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"TestCategory!=RealStore&TestCategory!=LiveCodex\" --blame-hang --blame-hang-timeout 3m\"",
    "size": "M",
    "notes": "Tests (SearchHotPathTests, [TestCategory(Perf)], [Timeout] each): (1) 4000-session corpus, 10 queries — median wall <25 ms AND GC.GetAllocatedBytesForCurrentThread delta <4 MB/query; (2) repeat query over unmutated store — searchTextCompositions counter delta == 0; (3) retag one session — counter +1 and result reflects the mutation. Wall bound has ~5x headroom vs target hardware; counters are the primary assert."
  },
  {
    "id": "perf-a2-listdiff",
    "title": "RefreshSessions diff-applies into the ObservableCollection (kill the 601-event rebuild)",
    "kind": "build",
    "goal": "RefreshSessions (post-split: ArchiveService.Search.cs) stable-key positional-diffs into the bound ObservableCollection<ArchiveSession> Sessions instead of Clear() + N Adds, so an unchanged filter emits 0 CollectionChanged events and a k-item delta emits O(k). Extract the differ as Core/Services/ObservableDiff.cs (pure, list-in/ops-out) so it is unit-testable without WinUI. Must preserve perf-a1-search's invariant that RefreshSessions performs no per-session Tags mutation.",
    "tier": "default",
    "deps": ["perf-s0-split", "perf-a1-search"],
    "reads": ["app/native/CodexLocalRetrieval.Core/Services/ArchiveService.Search.cs"],
    "verifier": "cmd /c \"cd /d app && dotnet build native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet build native\\CodexLocalRetrieval.Native\\CodexLocalRetrieval.Native.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"FullyQualifiedName~ListDiffRefresh\" --blame-hang --blame-hang-timeout 3m && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"TestCategory!=RealStore&TestCategory!=LiveCodex\" --blame-hang --blame-hang-timeout 3m\"",
    "size": "S",
    "notes": "Dep on a1 is SEMANTIC, not file-custom: both leaves rewrite the same RefreshSessions method body and a2 must preserve a1's no-mutation invariant — concurrent branches guarantee an unmergeable same-method conflict with behavioral entanglement. Tests subscribe CollectionChanged: same filter twice → 0 events; one add/remove/retitle → ≤3 events; final order equals a reference full rebuild (functional equivalence)."
  },
  {
    "id": "perf-a3-debounce",
    "title": "Search debounce + narrow render: keystrokes stop re-rendering the world",
    "kind": "build",
    "goal": "Add a 150 ms trailing debounce to SearchBox_TextChanged (MainPage.xaml.cs:215) via a new testable Core/Services/TrailingDebouncer.cs (dispatcher-agnostic: injected post-callback, trailing 150 ms, disposal cancels). Change ApplyFilters (MainPage.Tags.cs:113-126) to update the session list + tag strip only — no RenderCurrent() full-screen rebuild, no RenderRunningPage()/SSH spawn per keystroke; the transcript pane re-renders only when selection actually changes. Land app/tools/gates/a3_narrow_filter.ps1: Select-String over MainPage.Tags.cs asserting the ApplyFilters method body contains no call to RenderCurrent( or RenderRunningPage( — exit 1 on match.",
    "tier": "default",
    "deps": [],
    "reads": ["app/native/CodexLocalRetrieval.Native/MainPage.Tags.cs", "app/native/CodexLocalRetrieval.Native/MainPage.xaml.cs"],
    "verifier": "cmd /c \"cd /d app && powershell -NoProfile -ExecutionPolicy Bypass -File tools\\gates\\a3_narrow_filter.ps1 && dotnet build native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet build native\\CodexLocalRetrieval.Native\\CodexLocalRetrieval.Native.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"FullyQualifiedName~DebounceTests\" --blame-hang --blame-hang-timeout 3m && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"TestCategory!=RealStore&TestCategory!=LiveCodex\" --blame-hang --blame-hang-timeout 3m\"",
    "size": "S",
    "notes": "MainPage.xaml.cs region hint: only line 215 (one-liner handler) changes there. DebounceTests: 10 rapid Post()s within 150 ms → exactly 1 invocation with last value; a Post after quiet fires within 400 ms; Dispose cancels pending. No dep on P0 (no counters needed). MainPage.xaml.cs region is disjoint from a4/c2/d2/e1 regions — trivial merges."
  },
  {
    "id": "perf-a4-savelite",
    "title": "SaveAsync off-caller-thread + structural validation + coalescing RequestSave",
    "kind": "build",
    "goal": "In ArchiveService.Store.cs (pre-split anchor SaveAsync ArchiveService.cs:570-680): move generation-read + serialize + validation into the existing Task.Run; replace the full verify re-deserialize with ValidateStoreShape on the serialized bytes (JsonDocument, no POCO round-trip; PerfCounters.storeFullDeserializes must stay 0 on the save path); drop WriteIndented for the store payload; add a coalescing RequestSave() wrapper (trailing 300 ms, max-latency 2 s) and route the settings handlers in MainPage.xaml.cs:1898-2045 (esp. AccentColorPicker.ColorChanged :1942-1949, which today fires a full SaveAsync per drag tick) through it. Durability semantics (3-file commit protocol, generation check via AcquireStoreLockAsync/ReadStoreGeneration, DurableFileStore) UNCHANGED — this leaf moves/thins CPU work only.",
    "tier": "default",
    "deps": ["perf-p0-harness", "perf-s0-split"],
    "reads": ["app/native/CodexLocalRetrieval.Core/Services/ArchiveService.Store.cs", "app/native/CodexLocalRetrieval.Core/Services/DurableFileStore.cs", "app/native/CodexLocalRetrieval.Native/MainPage.xaml.cs"],
    "verifier": "cmd /c \"cd /d app && dotnet build native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet build native\\CodexLocalRetrieval.Native\\CodexLocalRetrieval.Native.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"FullyQualifiedName~SaveLite\" --blame-hang --blame-hang-timeout 3m && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"TestCategory!=RealStore&TestCategory!=LiveCodex\" --blame-hang --blame-hang-timeout 3m\"",
    "size": "M",
    "notes": "MainPage.xaml.cs region 1898-2045 only. SaveLite tests: (1) 4000-session store SaveAsync wall <400 ms AND storeFullDeserializes counter == 0 on the save path; (2) 50 RequestSave() in 2 s → on-disk generation advances ≤4; (3) existing store recovery/backup tests stay green (crash-safety unchanged); (4) awaited-caller returns <50 ms before I/O completion via the coalescer. No semantic dep on a1/a2 (different methods post-split)."
  },
  {
    "id": "perf-b1-scancache",
    "title": "Core scan cache: one WMI sweep + registry read per burst, with claim-check bypass",
    "kind": "build",
    "goal": "Add a short-TTL (2-4 s) cache inside Core RunningSessions over the WMI world sweep (TryScan, RunningSessions.cs:50-96) and the claude-registry enumeration feeding TryLiveSessionPids/TryAllLiveSessionIds (:311-357), so integrity + custody + claim checks within one burst share one sweep. The per-pid transcript-probe cache (:23-27, :498-562) ALREADY EXISTS — do not rebuild it; mirror its discipline. Rules from premax-plan-win32-v3 §2.5: [F#3] the post-claim re-check at SessionLaunchClaims.cs:425 BYPASSES the cache (add a bypass parameter/flag threaded to that call site only); [F#4] add RunningSessions.InvalidateScanCache() called from every kill/create site — RunningSessions.Kill success paths, MainPage.RunningChats.cs (alongside the existing GUI InvalidateRunningCache :31), MuxIdentityTransfer.cs, and the mux delete path in MainPage.Remote.cs; [F#8] a failed sweep is NEVER cached (only successful scans get a TTL). Instrument PerfCounters.wmiSweeps in the underlying sweep.",
    "tier": "default",
    "deps": ["perf-p0-harness"],
    "reads": ["app/native/CodexLocalRetrieval.Core/Remote/RunningSessions.cs", "app/native/CodexLocalRetrieval.Core/Remote/SessionLaunchClaims.cs", "app/native/CodexLocalRetrieval.Native/MainPage.RunningChats.cs", "app/native/CodexLocalRetrieval.Core/Remote/MuxIdentityTransfer.cs"],
    "verifier": "cmd /c \"cd /d app && dotnet build native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet build native\\CodexLocalRetrieval.Native\\CodexLocalRetrieval.Native.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"FullyQualifiedName~ScanCache\" --blame-hang --blame-hang-timeout 3m && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"TestCategory!=RealStore&TestCategory!=LiveCodex\" --blame-hang --blame-hang-timeout 3m\"",
    "size": "M",
    "notes": "Scan source must be injectable for tests (pattern: SessionLaunchGovernorTests injects liveness; KillSignals seam at RunningSessions.cs:721 is precedent). ScanCacheTests: (1) 10 TryAllLiveSessionIds in 1 s → wmiSweeps delta == 1; (2) post-claim re-check increments (bypass proven); (3) InvalidateScanCache() → next call sweeps; (4) injected sweep failure → next call retries, not served from cache. Kill must remain oracle-free (RunningSessions.cs:674 comment) — the cache must not leak into Kill's signal path. All RunningSessions/claims/custody/reclaim tests green via full suite."
  },
  {
    "id": "perf-b2-snapshot",
    "title": "WMI → native process snapshot (Toolhelp32 + NtQueryInformationProcess cmdlines)",
    "kind": "build",
    "goal": "New Core/Remote/ProcessSnapshot.cs: CreateToolhelp32Snapshot (or NtQuerySystemInformation) for the pid/name/ppid list + per-pid NtQueryInformationProcess(ProcessCommandLineInformation=60) for command lines of claude.exe/codex.exe candidates only (Toolhelp alone returns no command lines — premax v3 §6). Rewire the sweep INSIDE the perf-b1-scancache cache seam so callers and cache semantics are unchanged: TryScan, ProcessParentMap (RunningSessions.cs:100-116), ScanAgentsWithPpid (:121-145), TryMuxOwnedAgentPids parent map (:408-426), SnapshotProcessTree (:1002-1029), and the GUI GetRunningSessionsUncached (MainPage.RunningChats.cs:45-80 — currently UNBOUNDED bare ManagementObjectSearcher; it inherits the bounded snapshot). Keep the WMI path behind CODEXLOCAL_LEGACY_PROCESS_SCAN=1 for one release (pattern: CODEXLOCAL_LEGACY_HANDLE_SCAN, RunningSessions.cs:29-37).",
    "tier": "default",
    "deps": ["perf-b1-scancache"],
    "reads": ["app/native/CodexLocalRetrieval.Core/Remote/RunningSessions.cs", "app/native/CodexLocalRetrieval.Native/MainPage.RunningChats.cs", "app/native/CodexLocalRetrieval.Core/Remote/ProcessOpenFiles.cs"],
    "verifier": "cmd /c \"cd /d app && dotnet build native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet build native\\CodexLocalRetrieval.Native\\CodexLocalRetrieval.Native.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"FullyQualifiedName~ProcessSnapshot\" --blame-hang --blame-hang-timeout 3m && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"TestCategory!=RealStore&TestCategory!=LiveCodex\" --blame-hang --blame-hang-timeout 3m\"",
    "size": "L",
    "notes": "Dep on b1 is semantic: b1 defines the injectable-sweep + cache contract this plugs into, and both rewrite TryScan internals. Tests (pattern: ProcessOpenFilesTests spawns real children): (1) parity — 3 spawned cmd.exe /c ping -n 30 children with unique marker args found with correct ppid + full cmdline; (2) killed pid absent; (3) full snapshot + cmdline resolution <250 ms wall ([Timeout(60000)] backstop); (4) protected pid (e.g. 4): cmdline fails soft, pid listed, no throw. Consider splitting cmdline P/Invoke into its own sitting if it runs long — L size flagged."
  },
  {
    "id": "perf-b3-ledger",
    "title": "Ledger reads stop JSON-parsing the whole history for quiet sessions",
    "kind": "build",
    "goal": "SessionEventLedger.ReadForSession (Core/Remote/SessionEventLedger.cs:177-202): the streaming reverse-block reader ALREADY EXISTS (ReadFileNewestFirst :214-271, 64 KB blocks) — do NOT rebuild it. Remaining cost: TryParseReversedLine (:273-296) JSON-deserializes EVERY line before EventMatchesAnyId (:196) filters, and a session with fewer than max events forces an all-time scan of every monthly file. Fix: (1) cheap pre-parse screen — byte/ordinal-ignore-case containment check of any candidate id in the reversed line before JSON parse; (2) per-session sidecar index events-index/<sid>.jsonl of (file, offset) entries appended in the SAME mutex-guarded append path, with lazy one-time backfill when absent, making read cost O(events-of-session). Append schema and event semantics FROZEN (adversarial prong owns them); ordering/limit semantics identical to the current reader. Instrument PerfCounters.ledgerBytesRead.",
    "tier": "default",
    "deps": ["perf-p0-harness"],
    "reads": ["app/native/CodexLocalRetrieval.Core/Remote/SessionEventLedger.cs"],
    "verifier": "cmd /c \"cd /d app && dotnet build native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet build native\\CodexLocalRetrieval.Native\\CodexLocalRetrieval.Native.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"FullyQualifiedName~LedgerRead\" --blame-hang --blame-hang-timeout 3m && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"TestCategory!=RealStore&TestCategory!=LiveCodex\" --blame-hang --blame-hang-timeout 3m\"",
    "size": "M",
    "notes": "LedgerReadTests: (1) 100k events across 3 monthly files for 500 other sessions + 2 for target → ReadForSession(target,12) wall <100 ms AND ledgerBytesRead <256 KB (index path); (2) old-vs-new equivalence on a mixed fixture + existing SessionEventLedgerTests green; (3) missing index → backfill produces identical results. Own file — fully parallel."
  },
  {
    "id": "perf-b4-integrity-offthread",
    "title": "Integrity oracle off the UI thread: StaleGuardedRefresher + cached-panel-first render",
    "kind": "build",
    "goal": "RenderIntegrity (MainPage.Integrity.cs:20-87) renders the cached summary immediately, then awaits Task.Run(SessionIntegrity.Build) behind a monotonic sequence guard with a 'checking…' chip — the exact pattern already at MainPage.Custody.cs:12-89 and in this same file's ReclaimSelectedSessionAsync (:117-201, _reclaimSeq). Extract the schedule/sequence logic as Core/Services/StaleGuardedRefresher<T> (func-injected build, thread-capture assertable, force-refresh coalesces with in-flight). RiskySessionActionBlocked (:315-320) becomes async-aware: buttons disable-while-checking (SetRiskySessionActionsEnabled :322) instead of blocking the click on a sync Build; call site MainPage.xaml.cs:2296 adjusts accordingly, as do RenderIntegrity call sites MainPage.xaml.cs:462, MainPage.Integrity.cs:199, MainPage.Sessions.cs:211. PRESERVE VERBATIM the 413c13b reclaim semantics: CanReclaim/SessionIntegrity.ReclaimAvailable gating, reclaim event recording, and the reclaim flow itself. Land app/tools/gates/b4_no_sync_build.ps1: Select-String over MainPage.Integrity.cs asserting no bare SessionIntegrity.Build( outside a Task.Run lambda; exit 1 on match.",
    "tier": "default",
    "deps": [],
    "reads": ["app/native/CodexLocalRetrieval.Native/MainPage.Integrity.cs", "app/native/CodexLocalRetrieval.Native/MainPage.Custody.cs", "app/native/CodexLocalRetrieval.Core/Remote/SessionIntegrity.cs"],
    "verifier": "cmd /c \"cd /d app && powershell -NoProfile -ExecutionPolicy Bypass -File tools\\gates\\b4_no_sync_build.ps1 && dotnet build native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet build native\\CodexLocalRetrieval.Native\\CodexLocalRetrieval.Native.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"FullyQualifiedName~StaleGuardedRefresher\" --blame-hang --blame-hang-timeout 3m && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"TestCategory!=RealStore&TestCategory!=LiveCodex\" --blame-hang --blame-hang-timeout 3m\"",
    "size": "M",
    "notes": "No dep on b1: UI-thread liveness is won by moving the build off-thread regardless of how fast the build is (b1/b2 make it cheap; independent axes). SessionReclaimTests source-asserts (:585-587) already fence what Integrity code may reference — keep them green. The 3x150ms registry retry sleeps (RunningSessions.cs:274-295) are acceptable once inside Task.Run; do not touch them here. Refresher tests: build on non-caller thread (managed thread id capture); two overlapping refreshes → only newer published; force-refresh coalesces with in-flight."
  },
  {
    "id": "perf-b5-idle-ticks",
    "title": "Background ticks share one snapshot; projection rebuilt only on change",
    "kind": "build",
    "goal": "The 5 s mux-tab resolver (MainPage.Remote.cs OnTabTick :286, ArchiveService.Projection.cs ResolveMuxTabChats — pre-split ArchiveService.cs:4022) and the 30 s projection push (PushProjectsAsync :320-357, BuildProjectsProjectionJson — pre-split :3855-3992) share ONE cached ProcessSnapshot from perf-b2-snapshot instead of ~4 independent sweeps per 30 s window; ResolveMuxTabChats short-circuits to zero scans when no mux tab is pending/unbound; BuildProjectsProjectionJson output is cached and rebuilt only when store generation or the live-session set changed (PerfCounters.projectionSerializes); the ~/.claude/projects stat storm in MainPage.RunningTranscripts.cs:54-76 becomes mtime-gated. Projection JSON shape unchanged (ProjectionContractProbe proves it).",
    "tier": "default",
    "deps": ["perf-b2-snapshot", "perf-s0-split", "perf-p0-harness"],
    "reads": ["app/native/CodexLocalRetrieval.Native/MainPage.Remote.cs", "app/native/CodexLocalRetrieval.Core/Services/ArchiveService.Projection.cs", "app/native/CodexLocalRetrieval.Native/MainPage.RunningTranscripts.cs"],
    "verifier": "cmd /c \"cd /d app && dotnet build native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet build native\\CodexLocalRetrieval.Native\\CodexLocalRetrieval.Native.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"FullyQualifiedName~IdleTick\" --blame-hang --blame-hang-timeout 3m && dotnet run --project tools\\ProjectionContractProbe\\ProjectionContractProbe.csproj -c Release -- %TEMP%\\b5-projection.json && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"TestCategory!=RealStore&TestCategory!=LiveCodex\" --blame-hang --blame-hang-timeout 3m\"",
    "size": "M",
    "notes": "IdleTickTests (counters): no pending mux tabs → resolver scan counter 0; two projection builds with unchanged generation + live set → projectionSerializes == 1; generation bump → rebuild. MERGE-ORDER SEAM with value lane's val-v7a-app/val-v8a-app in the same files — see Seams; B5 merges AFTER them and its generation/live-set cache may optionally gate the new archive-index push too."
  },
  {
    "id": "perf-c1-tail",
    "title": "Live-chat reload costs O(appended bytes): incremental tail parse",
    "kind": "build",
    "goal": "In ArchiveService.Parse.cs (+ live-tick handle in MainPage.xaml.cs:118-165): remember per-open-chat byte offset + rolling parse state; on mtime change parse only the appended tail; fall back to a full re-parse on truncation/rewrite (offset > length, or changed head hash). Kill the 32 MB byte[] → giant string → Split('\\n') tail parse (pre-split anchors ArchiveService.cs:4826 claude / :4922 codex, ClaudeTailBytes=32MB :31) in favor of seek + StreamReader line loop. Fold CountUserPrompts (pre-split :92, called :4652/:4818) into the main parse pass so indexing stops double-reading every file (the >18k-line overflow minority may keep a second scan). Build the 6 KB SearchText cap from tail messages only (CapText/KeepRecentWindow path ~:5158). MUST NOT change indexing semantics (session keying, aliases, CurrentIndexVersion bump rules) — equivalence asserts are the honesty gate.",
    "tier": "default",
    "deps": ["perf-p0-harness", "perf-s0-split"],
    "reads": ["app/native/CodexLocalRetrieval.Core/Services/ArchiveService.Parse.cs", "app/native/CodexLocalRetrieval.Native/MainPage.xaml.cs"],
    "verifier": "cmd /c \"cd /d app && dotnet build native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet build native\\CodexLocalRetrieval.Native\\CodexLocalRetrieval.Native.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"FullyQualifiedName~TailIncremental\" --blame-hang --blame-hang-timeout 3m && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"TestCategory!=RealStore&TestCategory!=LiveCodex\" --blame-hang --blame-hang-timeout 3m\"",
    "size": "L",
    "notes": "HIGHEST-RISK leaf (parser correctness). TailIncrementalTests: (1) 30 MB fabricated transcript + 20 appended lines → transcriptBytesRead <512 KB, wall <80 ms, parsed messages IDENTICAL to from-scratch parse; (2) truncate/rewrite → full-parse fallback, still correct; (3) tail-parsing a 40 MB file allocates <8 MB; (4) indexing a 100-file corpus → total bytes-read ≤1.15x total file size. Dense existing parser suites (ArchiveServiceTests, RolloutAndLiveMappingTests) are the safety net via full suite. MainPage.xaml.cs region: LiveTickAsync :123-165 only."
  },
  {
    "id": "perf-c2-renderdiff",
    "title": "Live tick appends bubbles instead of rebuilding the transcript pane",
    "kind": "build",
    "goal": "Extract pure Core/Chat/TranscriptRenderPlan.cs: Diff(oldMsgs, newMsgs, shownWindow) → ops (AppendBubble | UpdateLastBubble | FullRebuild | none). The live tick (MainPage.xaml.cs LiveTickAsync :123-165) applies append/update ops to MainContent instead of Children.Clear() + full rebuild (RenderArchive :451-546, Clear at :459); pagination and chat-switch keep full rebuild; streaming a reply updates ONE bubble. Land app/tools/gates/c2_no_live_clear.ps1: assert the live-tick reload continuation contains no MainContent.Children.Clear( (Select-String over the LiveTick/apply-ops methods; exit 1 on match).",
    "tier": "default",
    "deps": ["perf-c1-tail"],
    "reads": ["app/native/CodexLocalRetrieval.Native/MainPage.xaml.cs", "app/native/CodexLocalRetrieval.Core/Services/ArchiveService.Parse.cs"],
    "verifier": "cmd /c \"cd /d app && powershell -NoProfile -ExecutionPolicy Bypass -File tools\\gates\\c2_no_live_clear.ps1 && dotnet build native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet build native\\CodexLocalRetrieval.Native\\CodexLocalRetrieval.Native.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"FullyQualifiedName~RenderPlan\" --blame-hang --blame-hang-timeout 3m && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"TestCategory!=RealStore&TestCategory!=LiveCodex\" --blame-hang --blame-hang-timeout 3m\"",
    "size": "M",
    "notes": "Dep on c1 is semantic: the differ consumes the incremental reload's exposed delta. RenderPlanTests (pure, no WinUI): append-only delta → appends only; last-message-grew → single UpdateLastBubble; reordered/edited history → FullRebuild; no change → 0 ops. UI apply code is thin and untested-by-unit — the pure differ carries the logic."
  },
  {
    "id": "perf-c4-watcher",
    "title": "FileWatchService: events instead of blind ticks (live chat, agent inbox, new-chat)",
    "kind": "build",
    "goal": "New Core/Services/FileWatchService.cs (testable; per-top-level-directory FileSystemWatcher, debounced, ALWAYS paired with a slow fallback poll since FSW misses events on some volumes — correctness never depends on an event). Wire: (a) live transcript — watch the open chat's file; the 1.5 s tick becomes event-driven with a 5 s fallback poll (MainPage.xaml.cs _liveTimer :119); (b) agent inbox — cursor becomes a BYTE OFFSET persisted in the cursor file, reads only appended bytes off the UI thread, replacing the per-1.5 s full File.ReadAllText of an unbounded file (MainPage.Agent.cs :42-73); include a one-time migration from the old line-count cursor preserving exactly-once processing; (c) new-chat detection — watch the expected session dir instead of full SyncNowAsync every 5 s (MainPage.StartChat.cs :292-309).",
    "tier": "default",
    "deps": ["perf-p0-harness"],
    "reads": ["app/native/CodexLocalRetrieval.Native/MainPage.Agent.cs", "app/native/CodexLocalRetrieval.Native/MainPage.StartChat.cs", "app/native/CodexLocalRetrieval.Native/MainPage.xaml.cs"],
    "verifier": "cmd /c \"cd /d app && dotnet build native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet build native\\CodexLocalRetrieval.Native\\CodexLocalRetrieval.Native.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"FullyQualifiedName~FileWatch\" --blame-hang --blame-hang-timeout 3m && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"TestCategory!=RealStore&TestCategory!=LiveCodex\" --blame-hang --blame-hang-timeout 3m\"",
    "size": "L",
    "notes": "FileWatchTests (temp dirs, real FSW, injected clock — NO real sleeps for the idle test): append → callback within 2 s; 60 s simulated idle → ≤1 fallback poll; 50 MB fabricated inbox at cursor=end + 1 KB append → bytes-read ≤64 KB; cursor-format migration fixture (10 processed + 2 new lines, none replayed, none skipped). MERGE-ORDER note: its MainPage.xaml.cs live-reader wiring touches the same region c2 rewrites — no semantic dep (fallback poll keeps semantics either way), but merge c2 first to avoid rebasing the region twice."
  },
  {
    "id": "perf-d1-startup-load",
    "title": "Startup store load: header-scan backups, deserialize only the winner",
    "kind": "build",
    "goal": "LoadStoreWithRecoveryAsync in ArchiveService.Store.cs (pre-split ArchiveService.cs:236-302; MaxAutoBackups=30 at :1579): read only Generation + shape sanity of each backup via a bounded prefix/JsonDocument header scan (ReadStoreGeneration :663-676 is the seed); fully deserialize ONLY the chosen winner; the primary-valid fast path touches backup headers only (today it can full-parse up to 30 backups). Recovery semantics IDENTICAL: corrupt primary → the same highest-generation valid backup the old algorithm chose; corrupt newest backup → fall through to next valid. Existing recovery/backup tests plus new equivalence fixtures prove it.",
    "tier": "default",
    "deps": ["perf-p0-harness", "perf-s0-split"],
    "reads": ["app/native/CodexLocalRetrieval.Core/Services/ArchiveService.Store.cs"],
    "verifier": "cmd /c \"cd /d app && dotnet build native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet build native\\CodexLocalRetrieval.Native\\CodexLocalRetrieval.Native.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"FullyQualifiedName~StartupLoad\" --blame-hang --blame-hang-timeout 3m && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"TestCategory!=RealStore&TestCategory!=LiveCodex\" --blame-hang --blame-hang-timeout 3m\"",
    "size": "M",
    "notes": "StartupLoadTests: (1) valid primary + 30 fabricated 10 MB backups → LoadAsync wall <1.5 s AND backup bytes-read counter <2 MB total; (2) corrupt-primary equivalence fixture — same winner as old algorithm; (3) corrupt primary + corrupt newest backup → next valid wins. Parallel with perf-a4-savelite (different methods in the same partial — trivial merge); no semantic dep on c1."
  },
  {
    "id": "perf-d2-first-frame",
    "title": "Startup order: interactive list before any oracle; R2R publish; marker-order gate",
    "kind": "build",
    "goal": "Reorder MainPage_Loaded (MainPage.xaml.cs:53-116): bind + render the session list from the loaded store BEFORE any integrity/oracle work; integrity fills in async via perf-b4's refresher; StartAgentBridge's sync file writes (MainPage.Agent.cs:35-45) go async. Flip PublishReadyToRun to true for the installed publish profile (Native.csproj:80; wire through app/scripts/install.ps1). Add ordered Diag markers startup.store-loaded / startup.list-interactive / startup.integrity-complete (Native/Diag.cs) and land app/scripts/perf_startup.ps1: launch the built exe with the diag dir set, wait ≤30 s for startup.list-interactive, ALWAYS taskkill the process (finally block), assert marker ORDER (list-interactive precedes integrity-complete) and record deltas to app/artifacts/perf/. The ORDER is the assert; the timing number is only an artifact (machine-dependent).",
    "tier": "default",
    "deps": ["perf-b4-integrity-offthread"],
    "reads": ["app/native/CodexLocalRetrieval.Native/MainPage.xaml.cs", "app/native/CodexLocalRetrieval.Native/MainPage.Agent.cs", "app/native/CodexLocalRetrieval.Native/Diag.cs", "app/native/CodexLocalRetrieval.Native/CodexLocalRetrieval.Native.csproj", "app/scripts/install.ps1"],
    "verifier": "cmd /c \"cd /d app && dotnet build native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet build native\\CodexLocalRetrieval.Native\\CodexLocalRetrieval.Native.csproj -c Release -m:1 -p:UseSharedCompilation=false && powershell -NoProfile -ExecutionPolicy Bypass -File scripts\\perf_startup.ps1 && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"TestCategory!=RealStore&TestCategory!=LiveCodex\" --blame-hang --blame-hang-timeout 3m\"",
    "size": "M",
    "notes": "The verifier launches a real window on the build box — bounded (30 s + guaranteed taskkill), self-cleaning, precedent: the capture harness (MainPage.Capture.cs). NOT a human gate (governing call 4 respected — no human eyes required, the script asserts marker order). Dep on b4 is semantic: the marker-order assert is only satisfiable once integrity is async. No dep on d1 (order != speed); run after d1 merges anyway for a truthful timing artifact."
  },
  {
    "id": "perf-e1-hygiene",
    "title": "UI-thread I/O long-tail sweep + the permanent regression fence",
    "kind": "build",
    "goal": "Fix remaining click-path blockers: cache PasswordVault reads per-source with 60 s TTL (4 sites: MainPage.xaml.cs:2723, :2758, :2770, :2778); GitHistory().IsAvailable() (up to 20 s git spawn) off-thread on first Brain render (MainPage.Brain.cs:95, Core/Memory/GitHistory.cs); route CopyPayload callers through CopyPayloadAsync and quarantine sync EnsureContent (post-split: ArchiveService.Content.cs, pre-split anchors :534-556, :2946-3025); bound the GUI WMI in MainPage.RunningChats.cs:45-80 with BoundedWmiOptions if perf-b2 has not already retired it; move live-tick SourceWriteTimeUtc stat off the UI thread. Then land app/tools/verify_ui_hygiene.ps1: curated Select-String gate over app/native/CodexLocalRetrieval.Native/MainPage.*.cs asserting no File.ReadAllText|File.WriteAllText|.GetAwaiter().GetResult()|.Wait(|Thread.Sleep|new PasswordVault inside Render*/*_Click/*_Changed method bodies, except entries in an explicit allowlist file; ships a -SelfTest flag that seeds a violation in a temp copy and asserts exit 1.",
    "tier": "default",
    "deps": ["perf-a3-debounce", "perf-b4-integrity-offthread", "perf-c2-renderdiff", "perf-c4-watcher"],
    "reads": ["app/native/CodexLocalRetrieval.Native/MainPage.xaml.cs", "app/native/CodexLocalRetrieval.Native/MainPage.Brain.cs", "app/native/CodexLocalRetrieval.Native/MainPage.RunningChats.cs", "app/native/CodexLocalRetrieval.Core/Services/ArchiveService.Content.cs", "app/native/CodexLocalRetrieval.Core/Memory/GitHistory.cs"],
    "verifier": "cmd /c \"cd /d app && powershell -NoProfile -ExecutionPolicy Bypass -File tools\\verify_ui_hygiene.ps1 -SelfTest && powershell -NoProfile -ExecutionPolicy Bypass -File tools\\verify_ui_hygiene.ps1 && dotnet build native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet build native\\CodexLocalRetrieval.Native\\CodexLocalRetrieval.Native.csproj -c Release -m:1 -p:UseSharedCompilation=false && dotnet test native\\CodexLocalRetrieval.Native.Tests\\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter \"TestCategory!=RealStore&TestCategory!=LiveCodex\" --blame-hang --blame-hang-timeout 3m\"",
    "size": "M",
    "notes": "Deps are semantic: the fence asserts properties those four leaves establish — landing it earlier forces allowlisting their un-fixed violations and then un-allowlisting (churn + false green risk). PROND FENCE: this is the last perf leaf. Emits human-checklist item: 'click through every screen once; no visible stutter on chat-select/copy/pin/settings' for the end-of-campaign batch."
  }
]
```

## Ordering

Recommended first cut (branch-per-leaf, maximum width):

```
wave-less flow (deps only — no wave gating):

perf-s0-split ──────┬──────────────┬───────────┬───────────┬─────────────┐
perf-p0-harness ────┤              │           │           │             │
                    v              v           v           v             v
              perf-a1-search  perf-a4-savelite perf-c1-tail perf-d1-... perf-b5 (also needs b2)
                    │                              │
                    v                              v
              perf-a2-listdiff              perf-c2-renderdiff ──────┐
                                                                     │
perf-b1-scancache ── perf-b2-snapshot ── perf-b5-idle-ticks          │
perf-b4-integrity-offthread ── perf-d2-first-frame                   │
perf-a3-debounce ────────────────────────────────────────────────────┤
perf-b3-ledger  (independent)                                        │
perf-c4-watcher ─────────────────────────────────────────────────────┤
                                                                     v
                       perf-e1-hygiene  (deps: a3, b4, c2, c4 — prong fence, last)
```

- **Start immediately, in parallel (no deps):** perf-s0-split, perf-p0-harness, perf-a3-debounce,
  perf-b4-integrity-offthread, perf-b1-scancache*, perf-b3-ledger*, perf-c4-watcher*
  (* = dep only on perf-p0-harness for counters; if the orchestrator wants them truly immediate,
  they can start against a stub counter and rebase — not recommended, P0 is small and fast).
- **Widest point** after s0+p0 merge: a1, a4, c1, d1, b2 (after b1), plus the independents — 8-9
  concurrent branches.
- **Serialized chains (all semantic):** a1→a2 (same method + invariant), b1→b2 (cache seam contract)
  →b5 (snapshot service), c1→c2 (delta contract), b4→d2 (marker order needs async integrity),
  {a3,b4,c2,c4}→e1 (fence asserts their properties).
- **Deps DROPPED from the base plan (were file-ownership only):** A2→A4, A4→C1, C1→D1, A1→B5,
  B1→B4, D1→D2, C2→C4 (kept only as merge-order recommendations where noted).
- perf-s0-split merges to main FIRST among all ArchiveService-touching leaves campaign-wide.

## Seams

1. **ArchiveService partial split vs value lane (val-v7a-app, val-v8a-app) — MERGE-ORDER SEAM.**
   Those leaves' reads/anchors were written against the monolith: val-v7a-app reads
   "ArchiveService.cs:3884-3909" (ChatProjection pipeline inside BuildProjectsProjectionJson) and
   "CanBuildTrustedResumeLaunch 3566-3578"; val-v8a-app reads "ExtractReaderMessagesAsync 3239+".
   After perf-s0-split these re-point to:
   - `ArchiveService.Projection.cs` — BuildProjectsProjectionJson + ChatProjection rows (val-v7a-app's
     reshape target), ResolveMuxTabChats, tab history.
   - `ArchiveService.Launch.cs` — CanBuildTrustedResumeLaunch (+ ParseResumedSessionId).
   - `ArchiveService.Parse.cs` — ExtractReaderMessagesAsync (and ParseFullAsync/FullReaderMessagesAsync).
   RULE I propose: **perf-s0-split merges before any other ArchiveService-touching leaf in ANY lane
   branches** (or such leaves rebase onto post-split main before their first commit). A value-lane
   branch cut from pre-split main will merge as an add/delete move conflict — cheap to avoid, ugly
   to resolve. Owner of the split file map: max-perf (this lane). Owner of projection JSON
   semantics: unchanged (ProjectionContractProbe is the neutral referee; value lane extends it for
   the new archive-index shape).
   Also note val-v8a-app's "PollCommandsAsync L391-473" anchor has already drifted (now :359 at
   HEAD) — their leaf is method-name-anchored, so this is cosmetic, but the orchestrator should
   treat all line anchors in refined docs as at-HEAD-of-writing.
2. **MainPage.Remote.cs — three writers, one file:** perf-b5 (caching/short-circuiting),
   val-v7a-app (new push sibling next to PushProjectsAsync :320), val-v8a-app (new arm inside
   PollCommandsAsync :359+). Different methods → mergeable; recommended merge order
   val-v7a-app → val-v8a-app → perf-b5, so b5's snapshot/generation cache can (optionally) also
   gate the new archive-index push. Transport stays SSH — any persistent-connection change is
   value/parity territory, perf claims only local caching (base plan §5 stands).
3. **Liveness oracle (RunningSessions.cs, SessionLaunchClaims.cs, ProcessOpenFiles.cs):** perf owns
   PERFORMANCE (b1/b2); premax semantics [F#3]/[F#4]/[F#8] and the landed per-pid cache + reclaim
   behavior are frozen contracts, restated in perf-b1's verifier. Kill stays oracle-free
   (RunningSessions.cs:674). Adversarial lane owns any custody-semantics change; if it touches
   these files, its leaves merge before b2's rewrite or rebase onto it — flag to the orchestrator
   when the adversarial refined set lands.
4. **SessionEventLedger.cs:** perf-b3 changes the READ path + adds a sidecar index only; append
   schema/event kinds frozen — adversarial lane owns them. New event kinds are transparently
   compatible with both the prefilter and the index (index stores offsets, not schemas).
5. **MainPage.Integrity.cs:** integration lane's reclaim work is LANDED (413c13b) — the old "wait
   for lane" gate is lifted; perf-b4 rebases on it and must preserve ReclaimAvailable/CanReclaim
   and reclaim event recording verbatim. If the adversarial lane specs reclaim-adjacent leaves,
   b4 is the structural change and should merge first, with adversarial edits rebasing on the
   async shape.
6. **Perf harness (PerfCounters.cs, PerfCorpus.cs, app/tools/perf_gates.ps1, Diag markers):**
   max-perf owns; all lanes may consume counters but only this lane adds counter names (append-only
   contract, no renames).
7. **muxd:** this prong touches muxd not at all; the native lane's note ("a perf prong owns
   muxctl throughput/batching") refers to a hypothetical future muxd-perf effort — out of scope
   here, listed under Open questions.

## Open questions

1. **Verifier policy vs run_gates.ps1:** per-leaf verifiers invoke dotnet directly (run_gates.ps1
   hardcodes external live checkouts `C:\Users\Ahmed\multiplex-app-patch` / `C:\Users\Ahmed\muxd`
   and fails on dirty/worktree state). Campaign-level acceptance should still run the full
   `app/tools/run_gates.ps1 -Tag <t>` once per merged trunk checkpoint — orchestrator to schedule.
2. **Perf tests run inside every other leaf's full-suite verifier** (the standing filter includes
   TestCategory=Perf). Counters are primary and wall bounds carry ≥5× headroom, but a busy build
   box can still flake a wall assert in an unrelated leaf's gate. If that friction materializes,
   the head may amend the standing full-suite filter to `...&TestCategory!=Perf` and give perf
   leaves a dedicated Perf pass — decision above lane level since it edits the shared verifier
   convention.
3. **Who updates val-v7a-app/val-v8a-app reads[] post-split** — their deliverable is already
   written with monolith anchors. Proposal: the orchestrator applies the re-pointing in §Seams-1
   mechanically when ingesting; alternatively the value lane re-emits. Needs one owner.
4. **a1→a2 dep:** I kept it (same-method rewrite + invariant entanglement). If the head wants a2
   fully parallel, the alternative is folding a2 into a1 as one leaf — I recommend keeping the
   dep instead; a2 is small.
5. **Deferred items — named future plan-nodes (kept OUT; none became load-bearing at HEAD):**
   `plan-transcript-virtualization` (ItemsRepeater migration of MainContent; revisit only if
   c2+a3 leave residual jank on 5k-message chats), `plan-store-sqlite-split` (data-format change
   with backup/recovery implications), `plan-deep-search-fts` (explicit-action path;
   DeepSearchContentAsync:823 unchanged; seams with value-lane search features),
   `plan-ssh-transport` (persistent connection / app-commands lease channel — value/parity owned),
   `plan-ledger-inbox-retention` (pruning policy — adversarial custody question),
   `plan-muxd-throughput` (native lane's muxctl batching pointer).
6. **B2 cmdline P/Invoke depth:** NtQueryInformationProcess(ProcessCommandLineInformation) is
   documented-enough but undocumented-classed; if the builder hits x64/WOW64 or access-rights
   walls beyond the soft-fail test, the fallback is keeping WMI ONLY for cmdline resolution of
   the (few) claude/codex candidate pids while Toolhelp supplies the world list — still kills the
   2 full WMI sweeps. Pre-authorizing that fallback here so the leaf doesn't stall.
