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
    private readonly SemaphoreSlim _gate = new(1, 1);
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

    public ArchiveRuntime(ArchiveService archive, bool syncOnLoad, Action<string>? log = null)
    {
        _archive = archive;
        _syncOnLoad = syncOnLoad;
        _log = log;
    }

    public bool IsLoaded => Volatile.Read(ref _loaded) == 1;
    public int SessionCount => Volatile.Read(ref _sessionCount);

    // A source watcher calls this without touching ArchiveService. The next archive operation performs
    // one coalesced scan while holding the same gate as every other server reader/writer.
    public void MarkRefreshPending()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Exchange(ref _refreshPending, 1);
        if (_syncOnLoad && IsLoaded) SchedulePendingRefresh();
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
                    var registration = _watchService.WatchDirectory(root, "*.jsonl", recurse: true, MarkRefreshPending);
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
                await _gate.WaitAsync();
                try
                {
                    Touch();
                    if (IsLoaded && _syncOnLoad) await RefreshAsync(CancellationToken.None);
                }
                catch (Exception ex) { _log?.Invoke("archive watcher sync warning: " + ex.Message); }
                finally { _gate.Release(); }
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
        _gate.Dispose();
    }

    public async Task<T> UseAsync<T>(
        Func<ArchiveService, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default,
        bool refreshBeforeUse = false)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(cancellationToken);
        Touch();
        try
        {
            var wasLoaded = IsLoaded;
            await EnsureLoadedAsync(cancellationToken, strictRefresh: refreshBeforeUse);
            if (wasLoaded) await _archive.ReloadExternalStoreCommitAsync(cancellationToken);
            EnsureSourceWatches();
            if ((refreshBeforeUse || Interlocked.Exchange(ref _refreshPending, 0) == 1) && _syncOnLoad && wasLoaded)
                await RefreshAsync(cancellationToken);
            return await operation(_archive, cancellationToken);
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

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken, bool strictRefresh)
    {
        if (IsLoaded) return;

        await _archive.LoadAsync(cancellationToken);
        Volatile.Write(ref _loaded, 1);
        Volatile.Write(ref _sessionCount, _archive.Store.Sessions.Count);
        if (_syncOnLoad)
        {
            if (strictRefresh)
                await RefreshAsync(cancellationToken);
            else
            {
                try { await RefreshAsync(cancellationToken); }
                catch (Exception ex) { _log?.Invoke("sync warning: " + ex.Message); }
            }
        }
        _log?.Invoke($"archive loaded on demand: {_archive.Store.Sessions.Count} chats");
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _archive.SyncFromDiskAsync(refreshList: false, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _sessionCount, _archive.Store.Sessions.Count);
    }

    private TimeSpan IdleDuration() =>
        DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastAccessUtcTicks), DateTimeKind.Utc);

    private void Touch() => Interlocked.Exchange(ref _lastAccessUtcTicks, DateTime.UtcNow.Ticks);
}
