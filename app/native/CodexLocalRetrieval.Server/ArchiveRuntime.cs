using System.Diagnostics;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Server;

// ArchiveService owns mutable dictionaries and observable collections and is not safe for concurrent
// callers. This runtime is the sole server-side entrance to that state, including load, refresh, API
// operations, remote-bridge resolution, and idle unload.
public sealed class ArchiveRuntime
{
    private readonly ArchiveService _archive;
    private readonly bool _syncOnLoad;
    private readonly Action<string>? _log;
    private readonly bool _deferInitialRefresh;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // At most one disk sync at a time. The walk is the expensive half of a sync, and the background
    // watcher worker plus a request-driven refresh would otherwise each walk every transcript.
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly FileWatchService _watchService = new();
    private readonly List<SourceWatch> _sourceWatches = new();
    private int _disposed;
    private readonly object _watchGate = new();
    private HashSet<string> _watchedRoots = new(StringComparer.OrdinalIgnoreCase);
    private int _refreshPending;
    private int _refreshWorkerActive;
    private int _loaded;
    private int _sessionCount = -1;
    private long _lastAccessUtcTicks = DateTime.UtcNow.Ticks;
    // The watcher reports the exact files it saw change, so a refresh that has them scans only those
    // instead of walking every source tree (~16k transcripts here). Anything that makes the set
    // untrustworthy -- a poll that found the change without a path, a burst past the cap, an index-version
    // migration -- forces the full walk. The shortcut can therefore only ever NARROW a scan, never drop a
    // change: the safety net still ends in a full walk.
    private const int DirtyPathCap = 512;
    private readonly HashSet<string> _dirtyPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _dirtyGate = new();
    private int _fullScanNeeded = 1;

    // How often an already-loaded runtime may re-check the on-disk store for a newer generation. The check
    // itself is cheap (a bounded header probe), but when it DOES find a newer generation the runtime
    // deserializes the entire store -- tens of MB -- while holding the archive gate, so the page's own
    // concurrent calls (chats + facets) queue behind it. With the desktop app open the store is rewritten
    // every few seconds, so an unthrottled check meant most remote requests paid a full reload and the
    // first paint took tens of seconds. A remote reader is not a writer: it can afford a few seconds of
    // staleness, and the very next request after the window picks the change up.
    private static readonly TimeSpan ExternalStoreCheckInterval = TimeSpan.FromSeconds(5);
    private long _lastExternalCheckUtcTicks;

    // Minimum gap between watcher-driven syncs. A merge costs seconds and holds the gate, and the sources
    // are appended to continuously, so without this the worker syncs back-to-back and remote reads queue
    // behind it almost permanently. A remote list reader tolerates a few seconds of staleness.
    private static readonly TimeSpan RefreshCoalesceWindow = TimeSpan.FromSeconds(10);

    // Test seam, same shape as ArchiveService.SavePhaseMeasured. It exists so a test can park a sync
    // mid-walk and assert that a concurrent read is NOT queued behind it -- the exact regression that
    // put a 22-second disk walk inside the gate every remote read needs.
    internal Func<CancellationToken, Task>? SyncScanHook { get; set; }

    public ArchiveRuntime(ArchiveService archive, bool syncOnLoad, Action<string>? log = null, bool deferInitialRefresh = false)
    {
        _archive = archive;
        _syncOnLoad = syncOnLoad;
        _log = log;
        _deferInitialRefresh = deferInitialRefresh;
    }

    public bool IsLoaded => Volatile.Read(ref _loaded) == 1;
    public int SessionCount => Volatile.Read(ref _sessionCount);

    // A source watcher calls this without touching ArchiveService. The next archive operation performs
    // one coalesced scan while holding the same gate as every other server reader/writer.
    public void MarkRefreshPending() => MarkRefreshPending(null);

    // Path-aware form. `changedPaths` is the set of transcript files the watcher actually saw change
    // (null when the safety-net poll found the change and the file is unknown): a known set lets the
    // refresh scan just those files, an unknown set or an oversized burst falls back to the full walk.
    public void MarkRefreshPending(IReadOnlyList<string>? changedPaths)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (changedPaths is null || changedPaths.Count == 0 || changedPaths.Count > DirtyPathCap)
        {
            Interlocked.Exchange(ref _fullScanNeeded, 1);
        }
        else
        {
            lock (_dirtyGate)
            {
                foreach (var path in changedPaths) _dirtyPaths.Add(path);
                if (_dirtyPaths.Count > DirtyPathCap) Interlocked.Exchange(ref _fullScanNeeded, 1);
            }
        }
        Interlocked.Exchange(ref _refreshPending, 1);
        if (_syncOnLoad && IsLoaded) SchedulePendingRefresh();
    }

    // Drain the pending change set for one refresh. Always clears, so a full walk consumes the paths it
    // subsumes rather than leaving them to trigger a second, narrower scan right after.
    private (IReadOnlyList<string>? Paths, bool Full) TakePendingChanges()
    {
        var full = Interlocked.Exchange(ref _fullScanNeeded, 0) == 1;
        List<string>? paths = null;
        lock (_dirtyGate)
        {
            if (_dirtyPaths.Count > 0)
            {
                paths = _dirtyPaths.ToList();
                _dirtyPaths.Clear();
            }
        }
        return (paths, full);
    }

    private void EnsureSourceWatches()
    {
        if (!IsLoaded) return;
        var roots = _archive.EffectiveSources()
            .Where(source => source.Enabled && !string.IsNullOrWhiteSpace(source.Root))
            .Select(source => Path.GetFullPath(source.Root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_watchGate)
        {
            foreach (var stale in _watchedRoots.Except(roots, StringComparer.OrdinalIgnoreCase).ToList())
            {
                _sourceWatches.FirstOrDefault(w => w.Root.Equals(stale, StringComparison.OrdinalIgnoreCase))?.Registration.Dispose();
            }
            _sourceWatches.RemoveAll(w => !roots.Contains(w.Root));
            foreach (var root in roots.Except(_watchedRoots, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var registration = _watchService.WatchDirectory(root, "*.jsonl", recurse: true, paths => MarkRefreshPending(paths));
                    _sourceWatches.Add(new SourceWatch(root, registration));
                }
                catch (Exception ex) { _log?.Invoke("archive watch warning: " + ex.Message); }
            }
            _watchedRoots = roots;
        }
    }

    private void SchedulePendingRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshWorkerActive, 1, 0) != 0) return;
        _ = Task.Run(RefreshPendingAsync);
    }

    private async Task RefreshPendingAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _refreshPending, 0) == 1)
            {
                if (!IsLoaded) break;
                try { await RefreshAsync(preferFullScan: false, CancellationToken.None); }
                catch (Exception ex) { _log?.Invoke("archive watcher sync warning: " + ex.Message); }
                // A sync costs seconds and holds the gate a remote read needs, while the sources are being
                // written continuously -- a live session appends to its transcript every few seconds. With
                // no gap the worker would sync back-to-back and the gate would be held almost constantly,
                // which is what left the page at 15-28s on its first paint. One coalesced sync per window
                // is the whole point of the flag; this is where that becomes true.
                if (Volatile.Read(ref _refreshPending) == 1)
                    await Task.Delay(RefreshCoalesceWindow);
            }
        }
        finally
        {
            Volatile.Write(ref _refreshWorkerActive, 0);
            if (Volatile.Read(ref _refreshPending) == 1 && _syncOnLoad && IsLoaded)
                SchedulePendingRefresh();
        }
    }

    private sealed record SourceWatch(string Root, IFileWatchRegistration Registration);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_watchGate)
        {
            foreach (var watch in _sourceWatches) watch.Registration.Dispose();
            _sourceWatches.Clear();
            _watchedRoots.Clear();
        }
        _watchService.Dispose();
        _syncGate.Dispose();
        _gate.Dispose();
    }

    public async Task<T> UseAsync<T>(
        Func<ArchiveService, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default,
        bool refreshBeforeUse = false)
    {
        ArgumentNullException.ThrowIfNull(operation);
        // Phase attribution: a remote request's duration is otherwise one opaque number, and the phases
        // below (gate wait, lazy load, external-store reload, watcher refresh, the operation itself) have
        // completely different causes. One trace line per request makes the bridge's own log say which.
        var watch = Stopwatch.StartNew();
        long gateMs = 0, loadMs = 0, reloadMs = 0, refreshMs = 0;
        await _gate.WaitAsync(cancellationToken);
        gateMs = watch.ElapsedMilliseconds;
        Touch();
        bool wasLoaded, refreshNow;
        try
        {
            wasLoaded = IsLoaded;
            refreshNow = await EnsureLoadedAsync(cancellationToken);
            loadMs = watch.ElapsedMilliseconds - gateMs;
            // Only on an ALREADY-loaded runtime, and at most once per interval. On the cold path the store
            // was just read, so the check can only ever be a no-op; and on a warm path an unthrottled check
            // turns the desktop app's frequent saves into a full store deserialize per request.
            if (wasLoaded && ExternalStoreCheckDue())
            {
                var reloadStart = watch.ElapsedMilliseconds;
                await _archive.ReloadExternalStoreCommitAsync(cancellationToken);
                reloadMs = watch.ElapsedMilliseconds - reloadStart;
            }
            EnsureSourceWatches();
        }
        finally
        {
            if (IsLoaded) Volatile.Write(ref _sessionCount, _archive.Store.Sessions.Count);
            Touch();
            _gate.Release();
        }
        // The refresh runs OUTSIDE the gate, on purpose. It is the disk walk that took 5-28s in the
        // traces, and holding the gate across it is what made every other remote read wait 22-23s for a
        // walk it had no stake in.
        if (_syncOnLoad)
        {
            if (refreshBeforeUse || refreshNow)
            {
                // refreshBeforeUse: this caller asked for a fresh view by name (the canonical session
                // resolver, resolving an id that may have appeared moments ago). refreshNow: this caller
                // just read the store from disk and must not serve the pre-sync list. Both wait, and a
                // failure is the caller's failure.
                Interlocked.Exchange(ref _refreshPending, 0);
                var refreshStart = watch.ElapsedMilliseconds;
                await RefreshAsync(preferFullScan: true, cancellationToken);
                refreshMs = watch.ElapsedMilliseconds - refreshStart;
            }
            else if (Interlocked.Exchange(ref _refreshPending, 0) == 1)
            {
                // The WATCHER marked this pending, so it is background work and belongs on the worker.
                // Consuming it here would charge the walk to whichever reader happened to arrive first --
                // measured at refresh=28339ms, which is precisely the "PC archive unavailable" latency the
                // page was reporting. Hand it straight back and answer from the store we already have; a
                // remote list reader tolerates seconds of staleness, and the next read picks the merge up.
                Interlocked.Exchange(ref _refreshPending, 1);
                SchedulePendingRefresh();
            }
        }
        // A second gate wait, and it is counted separately: the refresh above runs without the gate, so
        // this is a real wait behind whatever is holding it, not part of the operation's own cost.
        await _gate.WaitAsync(cancellationToken);
        gateMs += watch.ElapsedMilliseconds - gateMs - loadMs - reloadMs - refreshMs;
        try
        {
            var opStart = watch.ElapsedMilliseconds;
            var result = await operation(_archive, cancellationToken);
            PerfCounters.Trace?.Invoke(
                $"use warm={wasLoaded} gate={gateMs}ms load={loadMs}ms reload={reloadMs}ms"
                + $" refresh={refreshMs}ms op={watch.ElapsedMilliseconds - opStart}ms"
                + $" total={watch.ElapsedMilliseconds}ms sessions={_archive.Store.Sessions.Count}"
                + $" heapMB={GC.GetTotalMemory(false) / 1048576.0:F0}");
            return result;
        }
        finally
        {
            if (IsLoaded) Volatile.Write(ref _sessionCount, _archive.Store.Sessions.Count);
            Touch();
            _gate.Release();
        }
    }

    public async Task<bool> TryUnloadIfIdleAsync(TimeSpan idleFor, CancellationToken cancellationToken = default)
    {
        if (idleFor < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleFor));
        if (!IsLoaded || IdleDuration() < idleFor) return false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsLoaded || IdleDuration() < idleFor) return false;
            // Commit any deferred background merge before the store leaves memory, so a graceful unload
            // never discards the last merge window.
            await _archive.FlushDeferredSaveAsync(cancellationToken);
            _archive.Unload();
            Volatile.Write(ref _sessionCount, -1);
            Volatile.Write(ref _loaded, 0);
            Touch();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Returns true when the caller must run one refresh before serving: a store read straight from disk
    // is only the cache as of the last write, and the caller that just paid for the load should not also
    // hand back a stale list. The deferred path returns false by design -- serving the cached store
    // immediately is its entire purpose -- and schedules the sync in the background instead.
    private async Task<bool> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (IsLoaded) return false;

        if (_deferInitialRefresh)
        {
            await _archive.LoadCachedAsync(cancellationToken);
            Volatile.Write(ref _loaded, 1);
            Volatile.Write(ref _sessionCount, _archive.Store.Sessions.Count);
            _log?.Invoke($"archive loaded from cache on demand: {_archive.Store.Sessions.Count} chats");
            if (_syncOnLoad)
            {
                // Background warm-up, deliberately NOT awaited: this path exists so the first paint
                // serves the cached store immediately. The refresh runs off the gate, so it cannot
                // block the concurrent request that is already rendering that first paint.
                Interlocked.Exchange(ref _refreshPending, 1);
                SchedulePendingRefresh();
            }
            return false;
        }

        await _archive.LoadAsync(cancellationToken);
        Volatile.Write(ref _loaded, 1);
        Volatile.Write(ref _sessionCount, _archive.Store.Sessions.Count);
        _log?.Invoke($"archive loaded on demand: {_archive.Store.Sessions.Count} chats");
        return _syncOnLoad;
    }

    // The disk walk is the expensive half of a sync -- 14k transcripts, 5-23s measured -- and
    // ArchiveService documents it as off-thread safe ("reads files only... touches no shared mutable
    // state"), the same split the desktop app already uses (MainPage.Sessions.cs: ScanDiskAsync on a
    // worker, MergeScanAsync on the UI thread). Running the WHOLE sync under the gate is what produced
    // the 22-23s `gate=` waits in the traces: every remote read queued behind a walk it did not need
    // to wait for. So walk first, gate only the merge. The merge is the part that touches the store.
    private async Task RefreshAsync(bool preferFullScan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            // A user-facing refresh ("show me the current list now") always walks everything. Only the
            // watcher's background refresh may narrow to the files it actually saw change.
            var (paths, full) = TakePendingChanges();
            var scan = preferFullScan || full || paths is null
                ? await _archive.ScanDiskAsync(cancellationToken: cancellationToken)
                : await _archive.ScanPathsAsync(paths, cancellationToken);
            if (SyncScanHook is { } hook) await hook(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await _gate.WaitAsync(cancellationToken);
            var mergeWatch = Stopwatch.StartNew();
            try
            {
                // Deliberately NO Touch() here. This refresh is background work: the sources are appended
                // to continuously, so stamping the idle clock on every merge would keep _lastAccessUtcTicks
                // fresh forever and the idle unload could never fire -- the archive would stay resident and
                // never hand its ~200 MB back. Only a real remote read/write stamps access (UseAsync).
                // Re-check under the gate: the scan above ran while the runtime could have been unloaded
                // and reloaded, and merging one store's disk walk into another's is the one thing the
                // split makes possible that the old whole-sync-under-gate could not do. A generation
                // check would be the wrong guard -- the merge's own StoreGenerationConflictException
                // recovery already covers a second writer, and refusing on a moved generation would drop
                // real work.
                if (!IsLoaded) return;
                await _archive.MergeScanAsync(scan, refreshList: false, cancellationToken: cancellationToken, deferSave: true);
                cancellationToken.ThrowIfCancellationRequested();
                Volatile.Write(ref _sessionCount, _archive.Store.Sessions.Count);
                // Attributed because the merge is the last thing holding the gate a remote read needs, and
                // "how long" is not actionable without "which part": the conflict-retry load, the save, and
                // the background search-index rebuild have completely different fixes.
                PerfCounters.Trace?.Invoke(
                    $"sync merged files={scan.Disk.Count} mergeMs={mergeWatch.ElapsedMilliseconds}"
                    + $" loadRetries={_archive.LastMergeLoadRetries} searchIndexQueued={_archive.LastMergeSearchIndexQueued}"
                    + $" watchHealthy={_watchService.AllWatchersHealthy}"
                    + $" watchEvents={_watchService.Stats.Events} watchFallbackPolls={_watchService.Stats.FallbackPolls}"
                    + $" watchErrors={_watchService.Stats.WatcherErrors}");
            }
            finally { _gate.Release(); }
        }
        finally { _syncGate.Release(); }
    }

    // Stamp-on-read: the FIRST caller inside a window takes the check (and so the reload), every other
    // caller in that window skips it rather than queueing for the same answer.
    private bool ExternalStoreCheckDue()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastExternalCheckUtcTicks);
        if (now - last < ExternalStoreCheckInterval.Ticks) return false;
        return Interlocked.CompareExchange(ref _lastExternalCheckUtcTicks, now, last) == last;
    }

    private TimeSpan IdleDuration() =>
        DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastAccessUtcTicks), DateTimeKind.Utc);

    private void Touch() => Interlocked.Exchange(ref _lastAccessUtcTicks, DateTime.UtcNow.Ticks);
}
