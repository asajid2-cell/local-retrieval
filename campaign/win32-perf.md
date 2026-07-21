# PRONG PLAN: win32-perf — make the WinUI app feel native

Plan date: 2026-07-21. Planner: Fable (read-only audit; four parallel code-read agents over
`app/native/`, all findings cited by file:line below).
Product: `Z:\328\CMPUT328-A2\codexworks\301\mux-local-retrieval\app` — `CodexLocalRetrieval.Native`
(WinUI 3, unpackaged, net8.0, WinAppSDK 2.1.3 self-contained) + `CodexLocalRetrieval.Core`.
Builds on: `301/premax.md`, `301/premax-plan-win32-v3.md` (the premax campaign owned lifecycle
CORRECTNESS and explicitly deferred the perf pass to this campaign — §6 "Deferrable perf pass — L2"),
`app/PLAN.md`, `app/CURRENT.md` (gates green at tag `L8k_20260712_web_terminal_final`).

## 0. Win condition

The app is measurably native-fast, proven by a repeatable perf gate (`tools/perf_gates.ps1` running
`dotnet test --filter TestCategory=Perf`), all green, with baseline-vs-after numbers archived:

1. **Keystroke**: search/filter over a 4,000-session corpus completes < 25 ms and allocates < 4 MB
   per query (today: full O(n) scan rebuilding ~6 KB search text per session per keystroke ≈ 24 MB
   of string churn — ArchiveService.cs:5294, plus full screen re-render per keystroke).
2. **Click**: no UI-thread stall > 50 ms on chat-select, copy-command, pin/tag, or settings clicks
   (today: every chat click runs 2 WMI sweeps + handle probes + ledger read synchronously on the UI
   thread — MainPage.Integrity.cs:36; every pin rewrites + double-parses the whole multi-MB store —
   ArchiveService.cs:570-592).
3. **Live chat**: while an agent streams, per-tick work is O(appended bytes), not O(transcript)
   (today: full re-parse from byte 0 of up to 50 MB every 1.5 s + full bubble rebuild —
   MainPage.xaml.cs:118-152, 451-546; ArchiveService.cs:4922).
4. **Startup**: cold start to interactive list does not parse backup stores it doesn't need
   (today: up to 31 full store parses — ArchiveService.cs:259-270) and does not block first
   interactivity on WMI/handle scans.
5. **Idle**: background timer cost is bounded and does not grow with app age (today: the 1.5 s agent
   inbox tick re-reads an append-only, never-truncated file in full — MainPage.Agent.cs:62-73).

Every leaf's verifier is counter- or ratio-based where possible (immune to machine speed), with
generous absolute bounds (≥5× headroom) only as backstops. Perf tests fabricate corpora under
`%TEMP%` (fast local disk), never on Z:.

## 1. Confirmed hot-spot map (evidence, one line each)

- **UI-thread liveness oracle**: `SessionIntegrity.Build` sync on UI thread from `RenderArchive`
  (MainPage.Integrity.cs:36 ← MainPage.xaml.cs:462), re-run per chat click (cache key includes
  session id, Integrity.cs:30-32), per 5 s while streaming, and `force:true` per copy/resume click
  (Integrity.cs:311-316, MainPage.xaml.cs:2296). One Build = 2 WMI `Win32_Process` sweeps
  (RunningSessions.cs:57-89, ~100-300 ms each per the app's own comment, RunningChats.cs:25-27) +
  `~/.claude/sessions` enumeration with up to 3×150 ms `Thread.Sleep` per unreadable file
  (RunningSessions.cs:274-295) + per-pid handle probes each `work.Wait(1s)` (ProcessOpenFiles.cs:139)
  + ledger read (below). No Core-level WMI cache — the GUI 4 s cache (RunningChats.cs:28-43) is
  bypassed by Core callers (SessionIntegrity.cs:154, SessionCustody.cs:227, SessionLaunchClaims.cs:425).
  One resume click stacks up to 4 oracle runs (premax v3 §2 confirmed).
- **Ledger**: `SessionEventLedger.ReadForSession` reverse-parses EVERY line of every monthly
  `events-*.jsonl` until 12 matches; quiet sessions force an all-time parse per integrity build
  (SessionEventLedger.cs:177-271).
- **Search keystroke**: no debounce; `SearchBox_TextChanged` → `ApplyFilters` → full store scan with
  fresh 6 KB `SearchText` string per session per term-check (ArchiveService.cs:701, 5294),
  `Regex.Replace(ToLowerInvariant())` per session in fuzzy (5354-5393), double sort (701-731 then
  FilterChats re-sorts), `Sessions.Clear()` + up to 600 `Add`s = ~601 CollectionChanged events per
  keystroke (ArchiveService.cs:684-692), then a full `RenderCurrent()` — including RenderIntegrity,
  RenderTags, and on the Running screen an SSH spawn per keystroke (MainPage.Tags.cs:113-126,
  MainPage.Running.cs:25-77). `AllChatTags`/`HiddenChatCount` full scans per render
  (ArchiveService.cs:2678, 2718); `UserTags` LINQ chain + `DateTimeOffset.TryParse` per sort key per
  pass (2638, 2709-2720).
- **Store save**: `SaveAsync` on caller (UI) thread does full-store read+parse (generation check,
  :663-672) + full serialize (`WriteIndented`) + full verify re-deserialize (:592) + 3 durable file
  writes — per pin/tag/rename; `AccentColorPicker.ColorChanged` fires it per drag tick
  (MainPage.xaml.cs:1942-1949).
- **Startup**: `LoadStoreWithRecoveryAsync` full-parses primary + up to 30 backups even when primary
  is valid (ArchiveService.cs:236-302, MaxAutoBackups=30 at :1579); first interactive frame then
  waits on the synchronous integrity Build; `PublishReadyToRun=False` (Native.csproj:80) = full JIT
  cold start.
- **Live transcript**: 1.5 s tick → on mtime change, re-parse whole file from byte 0 + second
  full-file `CountUserPrompts` scan (ArchiveService.cs:92 called at :4652/:4818) + tail parse that
  allocates a 32 MB byte[] → giant string → `Split('\n')` → 2 list copies ≈ 150 MB LOH garbage per
  large-transcript tick (ArchiveService.cs:4922/4826); then `MainContent.Children.Clear()` + full
  bubble rebuild on the UI thread (MainPage.xaml.cs:459-546); "Start" jump realizes ALL messages
  (:416). Transcript pane is a plain StackPanel in a ScrollViewer — zero virtualization
  (MainPage.xaml:332-334); the sidebar `SessionList` ListView virtualizes correctly (MainPage.xaml:222).
- **Polling mesh (no FileSystemWatcher anywhere, no timer ever stopped)**: 1.5 s live reader;
  1.5 s agent-inbox full `File.ReadAllText` on UI thread of an unbounded file (MainPage.Agent.cs:46-73);
  3 s fresh `ssh.exe` spawn to VPS (MainPage.Remote.cs:273-276, 359-377); 5 s mux-tab resolver = 2
  WMI sweeps forever even with nothing pending (MainPage.Remote.cs:279, ArchiveService.cs:4022);
  30 s projection push = 2 more WMI sweeps + `~/.claude/projects` full stat storm + 3 full-store
  iterations + possible full SaveAsync (MainPage.Remote.cs:320-354, ArchiveService.cs:3855-3974,
  MainPage.RunningTranscripts.cs:54-76); new-chat poll = full disk re-sync every 5 s
  (MainPage.StartChat.cs:292-309).
- **Misc click-path sync I/O**: `git --version` up to 20 s on first Brain render
  (MainPage.Brain.cs:95, GitHistory.cs:25-35); `PasswordVault.Retrieve` per render/keystroke-composed
  (MainPage.xaml.cs:2713-2725); sync-over-async `EnsureContent` (ArchiveService.cs:534-538);
  unbounded WMI in `GetRunningSessionsUncached` (RunningChats.cs:45-80, no BoundedWmiOptions).

## 2. Leaves

Conventions for every leaf:
- Perf tests live in `CodexLocalRetrieval.Native.Tests`, tagged `[TestCategory("Perf")]`, corpus in
  `%TEMP%`, each test self-bounded (< 60 s worst case; MSTest `[Timeout]` attribute mandatory).
- Verifier command prefix (from `app/`):
  `dotnet build native\CodexLocalRetrieval.Native.Tests\CodexLocalRetrieval.Native.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false` then
  `dotnet test native\CodexLocalRetrieval.Native.Tests\CodexLocalRetrieval.Native.Tests.csproj -c Release --no-build --filter "<FILTER>"`.
  Every leaf ALSO runs the full existing suite filter from run_gates.ps1
  (`TestCategory!=RealStore&TestCategory!=LiveCodex`) — a perf leaf that breaks a functional test is
  not done.
- Prefer counter/ratio asserts (instrumented I/O byte counters, CollectionChanged counters, sweep
  counters, `GC.GetAllocatedBytesForCurrentThread`) over wall-clock; wall-clock bounds get ≥5× headroom.

### P0. perf-harness — the measurement foundation
- **goal**: One `PerfCorpus` test utility (fabricates N-session stores + realistic .jsonl transcripts
  incl. 30-50 MB ones, reusing the `WriteRollout` idiom from ArchiveServiceTests.cs:16), counter hooks
  (an injectable byte-counting stream/reader wrapper in Core behind `#if`-free plumbing — a simple
  static `PerfCounters` class with Interlocked counters, no-op cost when unread), plus
  `tools/perf_gates.ps1` that builds, runs `TestCategory=Perf`, and writes
  `artifacts/perf/<tag>/results.json` with the measured numbers. Land 3 BASELINE-RECORDING tests
  (search, save, integrity-build cost on synthetic corpus) that only RECORD numbers (no assert) so
  the campaign has an archived before-picture.
- **files**: `native/CodexLocalRetrieval.Native.Tests/Perf/PerfCorpus.cs` (new),
  `.../Perf/BaselineProbeTests.cs` (new), `Core/Services/PerfCounters.cs` (new, ~40 lines),
  `tools/perf_gates.ps1` (new).
- **verifier**: `powershell tools/perf_gates.ps1 -Tag p0` exits 0; `results.json` exists and contains
  ≥3 named measurements; full existing suite green.
- **deps**: none. Everything else depends on P0.

### A1. search-hot-path — cached search text, precomputed sort keys
- **goal**: Make one search/filter pass over 4,000 sessions cost < 25 ms and < 4 MB allocation:
  cache the composed lowercase-normalized search text on `SessionRecord` (invalidate on
  tag/phrase/title mutation and re-parse), precompute `CreatedAtUtc DateTime` + `HasUserTags` at
  parse/load, cache `AllChatTags`/`HiddenChatCount` per store generation, kill the double sort
  (Search returns unsorted or shares the comparer with FilterChats), cap fuzzy Levenshtein to
  title-class fields, remove per-pass `session.Tags` mutation in RefreshSessions
  (ArchiveService.cs:692 → move to parse time).
- **files**: `Core/Services/ArchiveService.cs` (regions: Search/FilterChats/SearchText/FuzzyScore/
  UserTags/AllChatTags/HiddenChatCount/RefreshSessions), `Core/Models/ArchiveModels.cs` (cached
  fields).
- **verifier**: filter `TestCategory=Perf&FullyQualifiedName~SearchHotPath`. Tests: (1) 4,000-session
  corpus, 10 queries: median wall < 25 ms, `GC.GetAllocatedBytesForCurrentThread` delta < 4 MB per
  query; (2) ratio: second identical query ≥ 5× faster than a forced cache-invalidated one is NOT
  required (cache is per-session, not per-query) — instead assert SearchText composition counter
  (PerfCounters) increments 0 on a repeat query over an unmutated store; (3) mutation invalidation:
  retag one session → its search text recomposed (counter +1) and query result reflects it. Full
  suite green.
- **deps**: P0. Touches ArchiveService.cs — serialize with A2/A4/C1/D1/B5 (see §4).

### A2. list-diff-refresh — stop the 601-event ListView rebuild
- **goal**: `RefreshSessions` diff-applies (stable-key positional diff) into the bound
  `ObservableCollection` instead of `Clear()` + N `Add`s, so an unchanged filter emits 0 collection
  events and a k-item delta emits O(k).
- **files**: `Core/Services/ArchiveService.cs` (RefreshSessions:684), possibly a small
  `Core/Services/ObservableDiff.cs`.
- **verifier**: filter `FullyQualifiedName~ListDiffRefresh`. Tests subscribe `CollectionChanged`:
  (1) same filter twice → 0 events on the second; (2) one session added/removed/retitled → ≤ 3
  events; (3) order matches the pre-change full rebuild (functional equivalence vs a reference
  rebuild). Full suite green.
- **deps**: P0. Same-file: after A1.

### A3. search-debounce-and-narrow-render — keystroke stops re-rendering the world
- **goal**: 150 ms trailing debounce on `SearchBox_TextChanged`; `ApplyFilters` updates the list +
  tag strip only — it no longer triggers `RenderCurrent()`'s full screen rebuild (no
  RenderIntegrity, no RenderRunningPage/SSH spawn per keystroke; transcript pane re-renders only when
  selection actually changes). Extract a testable `TrailingDebouncer` (DispatcherQueue-agnostic).
- **files**: `Native/MainPage.Tags.cs` (ApplyFilters:113-126), `Native/MainPage.xaml.cs`
  (SearchBox_TextChanged:215), `Core/Services/TrailingDebouncer.cs` (new).
- **verifier**: filter `FullyQualifiedName~DebounceTests`: 10 rapid Post()s within 150 ms → exactly 1
  invocation, with the last value; a Post after quiet → fires within 400 ms; disposal cancels. PLUS
  grep gate in the leaf script: `ApplyFilters` body contains no call to `RenderCurrent(` or
  `RenderRunningPage(` (bounded `Select-String` over MainPage.Tags.cs, exit 1 on match). Full suite
  green.
- **deps**: P0. Parallel-safe with A1/A2 (different files).

### A4. save-async-lite — a pin click stops rewriting the archive twice
- **goal**: `SaveAsync`: move generation-read + serialize + validation into the existing `Task.Run`;
  replace the full verify re-deserialize (:592) with structural validation on the serialized bytes
  (`ValidateStoreShape` on a `JsonDocument` — no POCO round-trip); drop `WriteIndented`; add a
  coalescing wrapper (`RequestSave()` — trailing 300 ms, max-latency 2 s) and route the
  ColorPicker/theme/density settings through it. Durability semantics (3-file commit protocol,
  generation check, DurableFileStore) UNCHANGED — this leaf only moves/thins CPU work.
- **files**: `Core/Services/ArchiveService.cs` (SaveAsync:570-680), `Native/MainPage.xaml.cs`
  (settings handlers :1898-2045).
- **verifier**: filter `TestCategory=Perf&FullyQualifiedName~SaveLite`. Tests: (1) 4,000-session
  store: SaveAsync wall < 400 ms (was: multi-second on large stores) AND PerfCounters
  full-deserialize counter == 0 on the save path; (2) burst: 50 `RequestSave()` in 2 s → on-disk
  generation advances ≤ 4; (3) crash-safety unchanged: existing store recovery/backup tests green
  (they exist in ArchiveServiceTests); (4) caller-thread check: SaveAsync returns to caller in
  < 50 ms before completion when awaited via the coalescer (assert scheduling, not I/O, on caller).
  Full suite green.
- **deps**: P0. Same-file: after A2.

### B1. core-scan-cache — one oracle run per burst, at the layer every caller uses
- **goal**: Short-TTL (2-4 s) cache inside Core `RunningSessions.TryScan`/`TryAllLiveSessionIds` so
  integrity build + custody + claim checks within one burst share one sweep. RULES INHERITED FROM
  premax-plan-win32-v3 §2.5 (this leaf implements premax Phase 0 item 6 if the integration lane has
  not already landed it — FIRST ACTION: check; if landed, verify tests below exist and close):
  [F#3] the post-claim re-check (SessionLaunchClaims.cs:130) BYPASSES the cache; [F#4] one
  `InvalidateScanCache()` called from every kill/create site incl. muxd-side kills; [F#8] failed
  scans are never cached.
- **files**: `Core/Remote/RunningSessions.cs`, `Core/Remote/SessionLaunchClaims.cs` (bypass flag),
  invalidation call sites (`Native/MainPage.RunningChats.cs`, `Core/Remote/MuxIdentityTransfer.cs`,
  mux delete path in `Native/MainPage.Remote.cs`).
- **verifier**: filter `FullyQualifiedName~ScanCache`. Tests (sweep counter via PerfCounters,
  scan source injectable — the test seam already exists, SessionLaunchGovernorTests injects
  liveness): (1) 10 `TryAllLiveSessionIds` calls in 1 s → underlying sweep counter == 1;
  (2) post-claim re-check increments the counter (cache bypassed); (3) `InvalidateScanCache()` →
  next call sweeps; (4) injected sweep failure → next call retries (not served from cache). Full
  suite green.
- **deps**: P0. SEAM: integration lane (premax item 6 HELD) — see §5.

### B2. wmi-replacement — the deferred premax perf pass, verbatim spec
- **goal**: Replace WMI `Win32_Process` queries with `CreateToolhelp32Snapshot` (or
  `NtQuerySystemInformation(SystemProcessInformation)`) for the pid/name/ppid list + per-pid
  `NtQueryInformationProcess(ProcessCommandLineInformation /*60*/)` for command lines of
  claude/codex candidates only (premax v3 §6 "Deferrable perf pass — L2": Toolhelp alone cannot
  replace WMI, it returns no command lines). Applies to `TryScan`, `ProcessParentMap`,
  `ScanAgentsWithPpid` (RunningSessions.cs:57-145) and the GUI's `GetRunningSessionsUncached`
  (RunningChats.cs:45-80 — which today is also UNBOUNDED; it inherits the new bounded scanner).
  Old WMI path stays behind a fallback env flag for one release (pattern:
  `CODEXLOCAL_LEGACY_HANDLE_SCAN`, RunningSessions.cs:29-37).
- **files**: `Core/Remote/ProcessSnapshot.cs` (new), `Core/Remote/RunningSessions.cs`,
  `Native/MainPage.RunningChats.cs`.
- **verifier**: filter `FullyQualifiedName~ProcessSnapshot`. Tests (pattern:
  ProcessOpenFilesTests spawns real children): (1) parity — spawn 3 `cmd.exe /c ping -n 30`
  children with unique marker args; snapshot finds all 3 pids with correct ppid and full command
  line containing the marker; (2) exited pid absent after kill; (3) perf: full snapshot + cmdline
  resolution for the live machine < 250 ms wall ([Timeout(60000)] backstop) — vs WMI's 2×100-300 ms;
  (4) access-denied pid (spawn elevated is not testable headless — instead query a protected system
  pid, e.g. 4): cmdline read fails soft, pid still listed, no throw. Full suite green incl. all
  RunningSessions/claims/custody tests.
- **deps**: P0, B1 (same file; B1 first so the cache wraps the new scanner unchanged).

### B3. ledger-read-bounded — quiet sessions stop paying for the whole ledger
- **goal**: `ReadForSession` stops parsing every line of all-time monthly files: maintain a tiny
  per-session sidecar index (`events-index/<sid>.jsonl` of file+offset entries, appended in the same
  queued-append path SessionEventLedger already has) OR a bounded reverse block reader that
  string-scans for the session id before JSON-parsing a line (choose in-leaf; index preferred —
  reverse block scan still O(file) worst case). Read cost becomes O(events-of-session), append cost
  unchanged (+1 small append). Backward compatible: no index file → one-time lazy backfill scan.
- **files**: `Core/Remote/SessionEventLedger.cs`.
- **verifier**: filter `TestCategory=Perf&FullyQualifiedName~LedgerRead`. Tests: (1) fabricate
  100,000 events across 3 monthly files for 500 other sessions + 2 events for target session →
  `ReadForSession(target,12)` wall < 100 ms AND bytes-read counter < 256 KB; (2) ordering/limit
  semantics identical to old reader on a mixed fixture (existing SessionEventLedgerTests green +
  new equivalence test old-vs-new on same fixture); (3) missing index → backfill produces same
  results. Full suite green.
- **deps**: P0. Parallel-safe (own file). SEAM: adversarial prong may add event kinds — read-path
  change only, append schema untouched.

### B4. integrity-offthread — no oracle ever runs on the UI thread again
- **goal**: `RenderIntegrity` renders the cached panel immediately, then `await Task.Run(Build)` +
  monotonic sequence guard + "checking…" chip (exact pattern already in the codebase:
  MainPage.Custody.cs:62-89). `RiskySessionActionBlocked` (Integrity.cs:311) becomes async-aware
  (buttons disable-while-checking rather than blocking). Extract the schedule/sequence logic into a
  testable `StaleGuardedRefresher<T>` (func-injected build, thread-capture assertable). Also: the
  3×150 ms `Thread.Sleep` registry retries (RunningSessions.cs:274-295) move off any
  caller-blocking path (async delay inside the Task.Run is acceptable). This implements premax v3
  Phase 1 item 11's async half (HELD there; perf campaign owns it per premax scope split) — FIRST
  ACTION: check whether the integration lane landed it; if yes, verify + close.
- **files**: `Native/MainPage.Integrity.cs`, `Core/Services/StaleGuardedRefresher.cs` (new),
  `Native/MainPage.xaml.cs` (call site :462, :2296).
- **verifier**: filter `FullyQualifiedName~StaleGuardedRefresher`: (1) build func runs on a
  non-caller thread (capture managed thread id); (2) two overlapping refreshes → only the newer
  result is published (stale discarded); (3) force-refresh coalesces with in-flight. PLUS grep gate:
  `MainPage.Integrity.cs` contains no bare `SessionIntegrity.Build(` outside the Task.Run lambda
  (Select-String script, exit 1 on match). Full suite green.
- **deps**: P0, B1 (so the off-thread build is also cheap). SEAM: integration lane owns
  MainPage.Integrity.cs correctness changes (reclaim button) — coordinate; see §5.

### B5. background-tick-consolidation — one process snapshot, shared; idle ticks ~free
- **goal**: The 5 s mux-tab resolver and 30 s projection push share one cached `ProcessSnapshot`
  (from B2) instead of 4 independent WMI sweeps per 30 s window; `ResolveMuxTabChats` short-circuits
  to zero scans when no mux tab is pending/unbound; `BuildProjectsProjectionJson` is rebuilt only
  when store generation or the live-session set changed (cached otherwise); the
  `~/.claude/projects` per-push stat storm (MainPage.RunningTranscripts.cs:54-76) is mtime-gated.
- **files**: `Native/MainPage.Remote.cs` (:263-354), `Core/Services/ArchiveService.cs`
  (ResolveMuxTabChats:4022, BuildProjectsProjectionJson:3855), `Native/MainPage.RunningTranscripts.cs`.
- **verifier**: filter `FullyQualifiedName~IdleTick`. Tests (counters): (1) no pending mux tabs →
  `ResolveMuxTabChats` scan counter == 0; (2) two projection builds with unchanged generation +
  unchanged live set → serializer counter == 1; (3) generation bump → rebuild. Full suite green.
- **deps**: B2 (snapshot service), A1 (same ArchiveService regions — after).

### C1. tail-incremental-reload — live chat costs O(appended bytes)
- **goal**: Remember per-open-chat byte offset + rolling parse state; on mtime change parse only the
  appended tail (fall back to full re-parse on truncation/rewrite detected by offset > length or
  changed head hash). Kill the 32 MB byte[]/Split tail parse (ArchiveService.cs:4922/4826) → seek +
  `StreamReader` line loop. Fold `CountUserPrompts` into the main parse pass (single read; the
  > 18k-line overflow minority may keep the second scan) (ArchiveService.cs:92, :4652, :4818). Build
  the 6 KB `Text` cap from tail messages only, stop joining megabytes (KeepRecentWindow/:5160 path).
- **files**: `Core/Services/ArchiveService.cs` (parse/reload regions), `Native/MainPage.xaml.cs`
  (LiveTick :118-152 passes/holds the incremental handle).
- **verifier**: filter `TestCategory=Perf&FullyQualifiedName~TailIncremental`. Tests: (1) 30 MB
  fabricated transcript, append 20 lines, incremental reload: bytes-read counter < 512 KB and wall
  < 80 ms; parsed messages identical to a from-scratch parse (equivalence assert — the honesty
  gate); (2) truncate/rewrite the file → falls back to full parse, still correct; (3) allocation:
  tail-parsing a 40 MB file allocates < 8 MB; (4) indexing a 100-file corpus: total bytes-read
  ≤ 1.15× total file size (CountUserPrompts no longer doubles I/O). Full suite green (parser
  correctness tests exist throughout ArchiveServiceTests/RolloutAndLiveMappingTests).
- **deps**: P0. Same-file: after A4. HIGH-RISK leaf (parser correctness) — the equivalence asserts
  are mandatory, and it must NOT change any indexing semantics (session keying, aliases).

### C2. render-append-diff — live tick appends bubbles instead of rebuilding the pane
- **goal**: Extract a pure `TranscriptRenderPlan.Diff(oldMsgs, newMsgs, shownWindow) → ops
  (AppendBubble/UpdateLastBubble/FullRebuild)` and make the live tick apply append/update ops to
  `MainContent` instead of `Children.Clear()` + rebuild (MainPage.xaml.cs:459-546); pagination and
  chat-switch keep full rebuild. Streaming a reply updates ONE bubble.
- **files**: `Native/MainPage.xaml.cs` (RenderArchive/LiveTick), `Core/Chat/TranscriptRenderPlan.cs`
  (new, pure).
- **verifier**: filter `FullyQualifiedName~RenderPlan`: (1) append-only delta → ops are appends only,
  no FullRebuild; (2) last-message-grew → single UpdateLastBubble; (3) reordered/edited history →
  FullRebuild; (4) no change → 0 ops. PLUS grep gate: the live-tick path
  (`LiveTickAsync`/its reload continuation) contains no `MainContent.Children.Clear(`. Full suite
  green.
- **deps**: C1 (reload must expose the delta). UI-heavy leaf but the logic is fully testable via the
  pure differ.

### C4. watcher-not-polling — file events instead of blind ticks
- **goal**: One `FileWatchService` (Core, testable): (a) live transcript — watch the open chat's
  file, tick becomes event-driven with a 5 s fallback poll; (b) agent inbox — cursor becomes a BYTE
  OFFSET, poll/event reads only appended bytes off the UI thread (fixes the unbounded
  `File.ReadAllText` per 1.5 s, MainPage.Agent.cs:62-73); (c) new-chat detection — watch the
  expected session dir instead of full `SyncNowAsync` every 5 s ×24 (MainPage.StartChat.cs:292-309).
  Watchers are per-directory (top-level), debounced, and always paired with a slow fallback poll
  (FileSystemWatcher on some volumes misses events — the fallback keeps correctness).
- **files**: `Core/Services/FileWatchService.cs` (new), `Native/MainPage.Agent.cs`,
  `Native/MainPage.xaml.cs` (live reader), `Native/MainPage.StartChat.cs`.
- **verifier**: filter `FullyQualifiedName~FileWatch`. Tests (temp dirs, real FileSystemWatcher):
  (1) append to watched file → callback within 2 s; (2) 60 s idle simulated via injected clock → ≤ 1
  fallback poll fired (bounded test time via clock injection, NOT real sleeps); (3) inbox: 50 MB
  fabricated inbox at cursor=end, append 1 KB → bytes-read counter ≤ 64 KB; (4) offset-cursor
  migration from the old count-cursor file format preserves exactly-once processing (no line
  replayed, none skipped — fixture with 10 processed + 2 new lines). Full suite green.
- **deps**: P0. Parallel-safe with A/B groups (new service + Agent/StartChat files); the live-reader
  wiring in MainPage.xaml.cs lands after C2 to avoid rebasing the same region twice.

### D1. startup-store-load — stop parsing 30 backups you don't need
- **goal**: `LoadStoreWithRecoveryAsync`: read only the `Generation` (+shape sanity) of each backup
  via a cheap bounded prefix/`JsonDocument` header scan; fully deserialize ONLY the chosen winner;
  primary-valid fast path touches backups' headers only. Recovery semantics identical (corrupt
  primary → correct highest-generation valid backup wins — the existing recovery tests plus new
  ones prove it).
- **files**: `Core/Services/ArchiveService.cs` (:236-302, :663-672).
- **verifier**: filter `TestCategory=Perf&FullyQualifiedName~StartupLoad`. Tests: (1) valid primary
  + 30 fabricated 10 MB backups → `LoadAsync` wall < 1.5 s AND backup bytes-read counter < 2 MB
  total; (2) corrupt primary → same backup chosen as the old algorithm (equivalence fixture);
  (3) corrupt primary + corrupt newest backup → falls through to next valid. Full suite green.
- **deps**: P0. Same-file: after C1 (or coordinate — different regions, low collision).

### D2. startup-first-frame — interactive before oracle; R2R publish
- **goal**: Reorder `MainPage_Loaded` (MainPage.xaml.cs:53-79): bind + render the session list from
  the loaded store BEFORE any integrity work; integrity fills in async (B4); `StartAgentBridge`'s
  sync writes (MainPage.Agent.cs:39-42) go async. Flip `PublishReadyToRun` to true for the installed
  publish profile (scripts/install.ps1 path; Native.csproj:80). Add ordered Diag markers
  (`startup.store-loaded`, `startup.list-interactive`, `startup.integrity-complete`) + new
  `scripts/perf_startup.ps1`: launch the built exe with `CLR_DIAG_DIR` set, wait ≤ 30 s for
  `startup.list-interactive`, kill the process, assert marker ORDER (list-interactive precedes
  integrity-complete) and record deltas to `artifacts/perf/`.
- **files**: `Native/MainPage.xaml.cs` (:53-79), `Native/MainPage.Agent.cs` (:35-59), `Native/Diag.cs`
  (markers), `Native/CodexLocalRetrieval.Native.csproj` (:80), `scripts/perf_startup.ps1` (new),
  `scripts/install.ps1` (R2R publish arg).
- **verifier**: (1) `powershell scripts/perf_startup.ps1` exits 0: markers present, correct order,
  bounded by its own 30 s timeout + guaranteed taskkill; timing recorded (no absolute assert —
  machine-dependent; the ORDER is the assert, the number is the artifact); (2) Release build gate
  green; (3) full test suite green. Note: this verifier launches a real window on the dev box —
  bounded and self-cleaning, matches the existing capture-harness precedent (MainPage.Capture.cs).
- **deps**: B4 (async integrity), D1 (fast load). Last of the first cut.

### E1. ui-thread-io-hygiene-sweep — the long tail, mechanically gated
- **goal**: Fix the remaining click-path blockers: cache `PasswordVault` reads (per-source, 60 s TTL
  — MainPage.xaml.cs:2713-2725); `GitHistory.IsAvailable` off-thread on first Brain render
  (MainPage.Brain.cs:95); route `CopyPayload` through `CopyPayloadAsync` and quarantine sync
  `EnsureContent` (ArchiveService.cs:534-538); `BoundedWmiOptions` everywhere WMI remains (until B2
  retires it; RunningChats.cs:45-80); live-tick `SourceWriteTimeUtc` stat off the UI thread. Then
  land `tools/verify_ui_hygiene.ps1`: a curated Select-String gate over `Native/MainPage.*.cs`
  asserting no `File.ReadAllText|File.WriteAllText|\.GetAwaiter\(\)\.GetResult\(\)|\.Wait\(|Thread\.Sleep|PasswordVault` inside `Render*`/`*_Click`/`*_Changed` method bodies except an explicit
  allowlist file — the regression fence for every other leaf.
- **files**: `Native/MainPage.xaml.cs`, `Native/MainPage.Brain.cs`, `Native/MainPage.Agent.cs`,
  `Native/MainPage.RunningChats.cs`, `Core/Services/ArchiveService.cs` (EnsureContent),
  `tools/verify_ui_hygiene.ps1` (new).
- **verifier**: `powershell tools/verify_ui_hygiene.ps1` exits 0 (and demonstrably exits 1 when a
  seeded violation is present — the script ships with a self-test flag); full suite green.
- **deps**: after A3/B4/C2/C4 (it fences what they fixed).

## 3. Dependencies & parallelism

```
P0 ─┬─ A1 ─ A2 ─ A4 ─ C1 ─ C2 ─┐            (ArchiveService.cs chain — SERIALIZED, one file)
    │                  └─ D1 ──┤            (D1 after C1 or careful region split)
    ├─ A3 ──────────────────────┤            (parallel: Tags/Debouncer)
    ├─ B1 ─ B2 ─ B5 ────────────┤            (parallel lane: RunningSessions/snapshot)
    │    └─ B4 ─────────────────┤            (Integrity partial + refresher)
    ├─ B3 ──────────────────────┤            (parallel: ledger, own file)
    ├─ C4 ──────────────────────┤            (parallel: watcher service; final wiring after C2)
    └──────────────────────── D2 ─ E1
```
Parallel-safe at any moment: one ArchiveService-chain leaf + A3 + one B-lane leaf + B3 + C4.
The single biggest orchestration constraint: **ArchiveService.cs is one 288 KB file** — never run two
leaves that edit it concurrently. (A mechanical partial-class split would unlock parallelism but
MUST NOT run while the integration lane is still landing fixes in this tree — deferred, see §5/§6.)

## 4. Ranking & first cut

Value/effort ranking (perceived-latency-per-token):
1. **P0** (enables everything; cheap)
2. **B1 + B4** — every chat click and copy click stops freezing the window 200-600 ms; the app's
   worst "Electron" feel is these two.
3. **A1 + A3** — typing becomes instant; kills the 24 MB/keystroke churn + full-screen re-render.
4. **A4** — pin/tag/settings clicks stop rewriting the archive; kills the ColorPicker drag disaster.
5. **C1 + C2** — live streaming chats stop costing O(transcript) every 1.5 s.
6. **D1** — cold start stops parsing up to 31 stores.
7. **B3** — integrity cost stops growing with ledger age (composes with B1/B4).
8. **A2, B2, C4, B5, D2, E1** — second wave.

**BOUNDED FIRST CUT (recommended campaign slice): P0, B1, B4, A1, A3, A4, C1, C2, D1 — 9 leaves.**
This slice alone converts every reported symptom class (click freeze, keystroke lag, live-chat churn,
slow start) with the lowest-risk changes. Second cut: B3, A2, B2, C4, B5, D2, E1.

**Explicitly DEFERRED (not in this plan's leaves; each needs its own `kind: plan` decomposition):**
- **Transcript pane virtualization** (ItemsRepeater migration of MainContent; "Start" jump without
  full realize — MainPage.xaml.cs:416). C2 removes ~90 % of the pain (rebuild churn); full
  virtualization is a risky rewrite of every screen that shares MainContent. Plan later if C2+A3
  leave residual jank on 5k-message chats.
- **Store split / SQLite migration** (metadata vs 6 KB Text blobs; kills SaveAsync O(store) at the
  root). A4/D1 make the current format cheap enough; the migration is a data-format change with
  backup/recovery implications — own plan.
- **Deep-search FTS index** (DeepSearchContentAsync's 3,000-file / 128 MB crawl,
  ArchiveService.cs:823; SearchDiskPhraseAsync:738). Explicit-action path, not ambient jank; an
  FTS5/token-index is a feature-sized build and seams with the value prong's search features.
- **SSH transport** (3 s `ssh.exe` spawn + 30 s projection push — MainPage.Remote.cs:273-354):
  replacing with a persistent connection or folding into the relay's existing `/api/app-commands`
  lease channel is a cross-component protocol decision owned by the native-parity/value prongs;
  perf claims only B5's snapshot/rebuild caching on the PC side.
- **Ledger/inbox retention** (files grow forever; B3/C4 bound the READ cost — pruning policy is an
  adversarial-prong custody question).

## 5. Cross-prong seams (one owner per seam — proposals for reconcile)

| Seam | Files | Prongs touching | Proposal |
|---|---|---|---|
| Liveness oracle & cache | RunningSessions.cs, SessionLaunchClaims.cs, ProcessOpenFiles.cs | integration lane (premax Phase 0 HELD items 6-7), adversarial, win32-perf | Integration lane finishes correctness FIRST; win32-perf B1/B2 then owns performance of the same code, preserving premax [F#3/4/8] semantics verbatim (they are restated in B1's verifier). B1/B4 leaves start with a "check if already landed" step. |
| MainPage.Integrity.cs / reclaim UI | MainPage.Integrity.cs | integration lane (reclaim-button fix, in flight NOW), win32-perf B4 | B4 must rebase on the lane's landed reclaim fix; do not schedule B4 until the lane's Integrity.cs work is merged. |
| ArchiveService.cs | one 288 KB file | value (features/search), win32-perf (A1/A2/A4/C1/D1/B5) | Perf owns the hot-path regions this campaign; value-prong edits to the same file serialize behind perf's chain (or vice-versa — reconcile decides; the point is ONE writer at a time). The partial-class split is a good joint first move once the tree is quiescent. |
| SessionEventLedger.cs | read path (B3) | adversarial (event kinds/custody), win32-perf | Perf changes read path + adds sidecar index only; append schema and event semantics frozen — adversarial owns them. |
| MainPage.Remote.cs / projection & command transport | :263-492 | native-parity, value (relay `/api/app-commands`), win32-perf B5 | Perf claims only local caching/short-circuiting (B5); any transport change (persistent SSH/WS) belongs to the parity/value prong on top of the existing app-commands lease API. |
| Perf harness & Diag | PerfCorpus, PerfCounters, perf_gates.ps1, Diag markers | all prongs (measurement) | win32-perf owns; other prongs consume. |

## 6. Risks

- **Biggest risk: C1 parser-correctness** — incremental tail parse silently diverging from full
  parse (session keying/aliases/counts). Mitigation: mandatory equivalence asserts in the verifier
  + full-parse fallback on any anomaly + the existing dense parser test suite.
- **Concurrent integration lane**: B1/B4 and anything in MainPage.Integrity.cs can collide with the
  in-flight reclaim/reliability work. Mitigation: those leaves are sequenced behind "lane merged"
  and begin with a landed-state check.
- **Perf-test flake**: absolute wall-time asserts on a busy dev box. Mitigation: counters/ratios as
  primary asserts, wall bounds at ≥5× headroom, `[Timeout]` on every test, corpus on %TEMP%.
- **FileSystemWatcher unreliability** (C4): always paired with fallback polling; correctness never
  depends on an event arriving.
