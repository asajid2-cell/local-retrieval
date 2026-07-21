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
            if (refreshBeforeUse && _syncOnLoad && wasLoaded)
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

        await _archive.LoadAsync();
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
        await _archive.SyncFromDiskAsync(refreshList: false);
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _sessionCount, _archive.Store.Sessions.Count);
    }

    private TimeSpan IdleDuration() =>
        DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastAccessUtcTicks), DateTimeKind.Utc);

    private void Touch() => Interlocked.Exchange(ref _lastAccessUtcTicks, DateTime.UtcNow.Ticks);
}
