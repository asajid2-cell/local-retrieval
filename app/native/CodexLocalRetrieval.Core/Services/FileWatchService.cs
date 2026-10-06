namespace CodexLocalRetrieval.Core.Services;

/// Clock seam. Production uses the wall clock; tests drive <see cref="ManualFileWatchClock"/> so the
/// idle/back-off behaviour can be exercised over simulated minutes without a single real sleep.
public interface IFileWatchClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemFileWatchClock : IFileWatchClock
{
    public static readonly SystemFileWatchClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// Test clock. Time only moves when <see cref="Advance"/> is called.
public sealed class ManualFileWatchClock : IFileWatchClock
{
    private long _ticks;
    public ManualFileWatchClock(DateTimeOffset? start = null) =>
        _ticks = (start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)).UtcTicks;

    public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
}

public sealed class FileWatchOptions
{
    /// Coalescing window: a burst of writes to the same file produces one callback.
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(250);

    /// Fallback cadence while a target is changing. This is the worst-case latency when the OS drops
    /// an event on a volume whose change notifications are unreliable (network shares, some VHDs).
    public TimeSpan ActiveFallbackInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// Fallback cadence once a target has gone quiet. One no-change poll demotes to this rate, and any
    /// observed change promotes straight back to <see cref="ActiveFallbackInterval"/>.
    public TimeSpan IdleFallbackInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// How often the internal pump wakes to evaluate debounce + fallback deadlines. Ignored when
    /// <see cref="ManualPump"/> is set.
    public TimeSpan PumpInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// Tests drive <see cref="FileWatchService.Pump"/> themselves; no background timer is created.
    public bool ManualPump { get; init; }

    public IFileWatchClock Clock { get; init; } = SystemFileWatchClock.Instance;
}

/// Handle returned by a Watch call. Dispose to stop watching.
public interface IFileWatchRegistration : IDisposable
{
    /// Callbacks are best-effort deliveries, not a queue. A caller that DECLINES to act on one (busy,
    /// mid-sync, user scrolled away) must call this, otherwise the service treats the change as
    /// consumed and nothing re-delivers it until the file next changes. Rearm forgets the remembered
    /// stamp and puts the target back on the active fallback cadence, so the next poll re-fires.
    void Rearm();
}

/// Counters for tests and diagnostics. Fields are read via Interlocked; snapshots are cheap.
public sealed class FileWatchStats
{
    internal long _events, _fallbackPolls, _callbacks, _watcherErrors;
    public long Events => Interlocked.Read(ref _events);
    public long FallbackPolls => Interlocked.Read(ref _fallbackPolls);
    public long Callbacks => Interlocked.Read(ref _callbacks);
    public long WatcherErrors => Interlocked.Read(ref _watcherErrors);
}

/// Turns blind polling loops into change events.
///
/// Two rules make this safe to depend on:
///  1. ONE FileSystemWatcher per top-level directory, shared by every registration under it. Watchers
///     are a kernel-side handle plus a buffer; one per open chat / per inbox / per session root would
///     multiply them without bound.
///  2. Every registration is ALWAYS paired with a fallback stat poll. FSW silently misses events on
///     some volumes and drops the rest on buffer overflow, so correctness never depends on an event
///     arriving — an event only makes the answer arrive sooner than the poll would have found it.
public sealed class FileWatchService : IDisposable
{
    private readonly FileWatchOptions _options;
    private readonly object _gate = new();
    private readonly List<Registration> _registrations = new();
    private readonly Dictionary<string, DirectoryWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer? _pump;
    private bool _disposed;

    public FileWatchStats Stats { get; } = new();

    public FileWatchService(FileWatchOptions? options = null)
    {
        _options = options ?? new FileWatchOptions();
        if (!_options.ManualPump)
            _pump = new Timer(_ => { try { Pump(); } catch { } }, null, _options.PumpInterval, _options.PumpInterval);
    }

    /// Watch a single file. The callback fires on append/overwrite/create/delete.
    public IFileWatchRegistration WatchFile(string path, Action onChanged) => WatchFile(path, _ => onChanged());

    /// Path-aware form: the callback receives the changed file's full path (an event-path delivery), or
    /// null when the change was discovered by the safety-net poll, where the changed file is unknown.
    public IFileWatchRegistration WatchFile(string path, Action<IReadOnlyList<string>?> onChanged)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full) ?? throw new ArgumentException("File path has no directory: " + path, nameof(path));
        return Register(dir, recurse: false,
            matches: changed => string.Equals(changed, full, StringComparison.OrdinalIgnoreCase),
            probe: () => FileStamp(full), onChanged);
    }

    /// Watch a directory tree for files matching a simple `*.ext` style pattern. A tree probe is the
    /// whole recursive walk, so it is marked coarse: while the OS watcher is healthy the safety net runs
    /// at the idle cadence instead of walking the tree every few seconds for an answer the event path
    /// already delivered. It drops to the active cadence only when there is no watcher or it errored.
    public IFileWatchRegistration WatchDirectory(string directory, string pattern, bool recurse, Action onChanged)
        => WatchDirectory(directory, pattern, recurse, _ => onChanged());

    /// Path-aware form: the callback receives every file path that changed inside the debounce window
    /// (so a burst under one tree is reported whole), or null when the safety-net poll found the change
    /// and the individual file is unknown -- the caller must then treat it as "everything changed".
    public IFileWatchRegistration WatchDirectory(string directory, string pattern, bool recurse, Action<IReadOnlyList<string>?> onChanged)
    {
        var dir = Path.GetFullPath(directory);
        return Register(dir, recurse,
            matches: changed => MatchesPattern(Path.GetFileName(changed), pattern),
            probe: () => DirectoryStamp(dir, pattern, recurse), onChanged, coarseFallback: true);
    }

    private IFileWatchRegistration Register(string directory, bool recurse, Func<string, bool> matches, Func<string> probe, Action<IReadOnlyList<string>?> onChanged, bool coarseFallback = false)
    {
        var reg = new Registration(this, directory, recurse, matches, probe, onChanged, coarseFallback);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            reg.LastStamp = SafeProbe(probe);
            EnsureWatcherLocked(directory, recurse);
            reg.Watcher = _watchers.TryGetValue(directory, out var watcher) ? watcher : null;
            reg.FallbackInterval = FallbackIntervalFor(reg);
            reg.NextFallbackAt = _options.Clock.UtcNow + reg.FallbackInterval;
            _registrations.Add(reg);
        }
        return reg;
    }

    // The cadence of the safety-net poll. A cheap single-file probe stays at the active cadence, so a
    // dropped event on a single watched file is noticed in seconds. A coarse tree probe is only walked
    // when the event path is not there to carry the change.
    private TimeSpan FallbackIntervalFor(Registration reg) =>
        reg.CoarseFallback && (reg.Watcher?.Healthy ?? false)
            ? _options.IdleFallbackInterval
            : _options.ActiveFallbackInterval;

    // Diagnostics: whether the OS watchers backing the registrations are carrying events or whether the
    // registrations are living on the fallback poll. Lets the server's trace say which mode it is in.
    public bool AllWatchersHealthy
    {
        get { lock (_gate) return _watchers.Values.All(w => w.Healthy); }
    }

    /// Evaluate every registration's debounce and fallback deadlines against the clock. Callbacks are
    /// invoked OUTSIDE the lock so a slow consumer cannot stall registration or disposal.
    public void Pump()
    {
        var now = _options.Clock.UtcNow;
        List<(Registration Reg, IReadOnlyList<string>? Paths)>? fire = null;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var reg in _registrations)
            {
                if (reg.Disposed) continue;

                if (reg.PendingSince is { } since)
                {
                    if (now - since < _options.Debounce) continue;
                    reg.PendingSince = null;
                    Promote(reg, now);
                    (fire ??= new()).Add((reg, SnapshotPaths(reg)));
                    continue;
                }

                if (now < reg.NextFallbackAt) continue;

                Interlocked.Increment(ref Stats._fallbackPolls);
                PerfCounters.FileWatchFallbackPoll();
                var stamp = SafeProbe(reg.Probe);
                if (reg.LastStamp is null || !string.Equals(stamp, reg.LastStamp, StringComparison.Ordinal))
                {
                    Promote(reg, now);
                    // A poll found the change, so which file moved is unknown: null means "assume all".
                    (fire ??= new()).Add((reg, null));
                }
                else
                {
                    // Quiet. One no-change poll is enough to demote to the slow cadence; the FSW event
                    // is what makes activity visible immediately, this is only the safety net. A coarse
                    // registration whose watcher is not carrying events never demotes -- the poll IS its
                    // only signal then, so it must keep the active cadence.
                    reg.FallbackInterval = reg.CoarseFallback && !(reg.Watcher?.Healthy ?? false)
                        ? _options.ActiveFallbackInterval
                        : _options.IdleFallbackInterval;
                    reg.NextFallbackAt = now + reg.FallbackInterval;
                }
            }
            // Refresh remembered stamps under the lock so a concurrent event cannot race a stale value in.
            if (fire is not null)
                foreach (var (reg, _) in fire) reg.LastStamp = SafeProbe(reg.Probe);
        }

        if (fire is null) return;
        foreach (var (reg, paths) in fire)
        {
            if (reg.Disposed) continue;
            Interlocked.Increment(ref Stats._callbacks);
            try { reg.OnChanged(paths); } catch { /* a consumer's failure must not kill the pump */ }
        }
    }

    // Drain and return the paths accumulated during a debounce window, so a burst under one tree is
    // delivered whole instead of collapsing to whichever file happened to be written last.
    private static IReadOnlyList<string>? SnapshotPaths(Registration reg)
    {
        if (reg.PendingPaths is not { Count: > 0 } set) return null;
        var snapshot = set.ToArray();
        set.Clear();
        return snapshot;
    }

    private void Promote(Registration reg, DateTimeOffset now)
    {
        reg.FallbackInterval = FallbackIntervalFor(reg);
        reg.NextFallbackAt = now + reg.FallbackInterval;
    }

    private void OnRearm(Registration reg)
    {
        lock (_gate)
        {
            if (_disposed || reg.Disposed) return;
            reg.LastStamp = null;                       // unknown ⇒ the next poll always re-fires
            reg.FallbackInterval = FallbackIntervalFor(reg);
            reg.NextFallbackAt = _options.Clock.UtcNow + reg.FallbackInterval;
        }
    }

    private void OnFileSystemEvent(string fullPath)
    {
        var now = _options.Clock.UtcNow;
        Interlocked.Increment(ref Stats._events);
        PerfCounters.FileWatchEvent();
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var reg in _registrations)
            {
                if (reg.Disposed) continue;
                bool hit;
                try { hit = reg.Matches(fullPath); } catch { hit = false; }
                if (!hit) continue;
                // Keep every path seen in this debounce window, not just the first: the caller scans the
                // whole set, and collapsing to one path would silently skip the rest of the burst.
                (reg.PendingPaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(fullPath);
                reg.PendingSince ??= now;
            }
        }
    }

    private void EnsureWatcherLocked(string directory, bool recurse)
    {
        if (_watchers.TryGetValue(directory, out var existing))
        {
            if (recurse && !existing.Recursive) existing.SetRecursive(true);
            existing.RefCount++;
            return;
        }
        var watcher = new DirectoryWatcher(directory, recurse, OnFileSystemEvent, () => Interlocked.Increment(ref Stats._watcherErrors));
        watcher.RefCount = 1;
        _watchers[directory] = watcher;
    }

    private void ReleaseLocked(Registration reg)
    {
        _registrations.Remove(reg);
        if (!_watchers.TryGetValue(reg.Directory, out var watcher)) return;
        if (--watcher.RefCount > 0) return;
        _watchers.Remove(reg.Directory);
        watcher.Dispose();
    }

    private static string SafeProbe(Func<string> probe)
    {
        try { return probe(); } catch { return "?"; }
    }

    /// Stamp for a single file: presence + length + last-write. Length is what actually moves on an
    /// append, and it moves even when a filesystem's mtime granularity is coarse.
    public static string FileStamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length.ToString() + ":" + info.LastWriteTimeUtc.Ticks.ToString() : "-";
    }

    /// Stamp for a directory: how many matching files exist and the newest write among them. Enough to
    /// notice "a new transcript appeared" without reading a byte of content. The traversal is iterative
    /// and does not follow reparse-point directories: recursive enumeration over a large or cyclic source
    /// tree can otherwise exhaust the process stack before the watcher can report a useful error.
    public static string DirectoryStamp(string directory, string pattern, bool recurse)
    {
        if (!Directory.Exists(directory)) return "-";
        var pending = new Stack<string>();
        pending.Push(directory);
        long count = 0, newest = 0;
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(current, pattern, SearchOption.TopDirectoryOnly); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var file in files)
            {
                count++;
                long ticks;
                try { ticks = File.GetLastWriteTimeUtc(file).Ticks; } catch { continue; }
                if (ticks > newest) newest = ticks;
            }

            if (!recurse) continue;
            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var child in directories)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push(child);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return count.ToString() + ":" + newest.ToString();
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        if (pattern is "*" or "*.*") return true;
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
            return name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        List<DirectoryWatcher> watchers;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            watchers = _watchers.Values.ToList();
            _watchers.Clear();
            _registrations.Clear();
        }
        _pump?.Dispose();
        foreach (var w in watchers) w.Dispose();
    }

    private sealed class Registration : IFileWatchRegistration
    {
        private readonly FileWatchService _owner;
        internal readonly string Directory;
        internal readonly bool Recurse;
        internal readonly Func<string, bool> Matches;
        internal readonly Func<string> Probe;
        internal readonly Action<IReadOnlyList<string>?> OnChanged;
        // A recursive tree probe is expensive, so it only runs at the active cadence when the OS watcher
        // is not carrying the events (missing or errored). A single-file probe is cheap and is not coarse.
        internal readonly bool CoarseFallback;
        internal DirectoryWatcher? Watcher;

        internal string? LastStamp;
        internal DateTimeOffset? PendingSince;
        internal HashSet<string>? PendingPaths;
        internal DateTimeOffset NextFallbackAt;
        internal TimeSpan FallbackInterval;
        internal bool Disposed;

        internal Registration(FileWatchService owner, string directory, bool recurse,
            Func<string, bool> matches, Func<string> probe, Action<IReadOnlyList<string>?> onChanged, bool coarseFallback)
        {
            _owner = owner; Directory = directory; Recurse = recurse;
            Matches = matches; Probe = probe; OnChanged = onChanged; CoarseFallback = coarseFallback;
        }

        public void Rearm() => _owner.OnRearm(this);

        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (Disposed) return;
                Disposed = true;
                _owner.ReleaseLocked(this);
            }
        }
    }

    /// One FileSystemWatcher per top-level directory, shared by every registration beneath it.
    private sealed class DirectoryWatcher : IDisposable
    {
        private readonly FileSystemWatcher? _fsw;
        private readonly Action _onError;
        private bool _errored;
        internal int RefCount;
        internal bool Recursive { get; private set; }
        // Healthy = the OS watcher exists and has not reported an overflow/teardown. A coarse registration
        // only falls back to the cheap cadence while this is true.
        internal bool Healthy => _fsw is not null && !_errored;

        internal DirectoryWatcher(string directory, bool recurse, Action<string> onEvent, Action onError)
        {
            Recursive = recurse;
            _onError = onError;
            try
            {
                _fsw = new FileSystemWatcher(directory)
                {
                    // 64 KB is the largest buffer the kernel will pin per watcher; a small buffer is the
                    // usual cause of a silent overflow-and-drop under a burst of writes.
                    InternalBufferSize = 64 * 1024,
                    IncludeSubdirectories = recurse,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    Filter = "*",
                };
                void Handle(object _, FileSystemEventArgs e) { try { onEvent(e.FullPath); } catch { } }
                _fsw.Changed += Handle;
                _fsw.Created += Handle;
                _fsw.Deleted += Handle;
                _fsw.Renamed += (_, e) => { try { onEvent(e.FullPath); } catch { } };
                _fsw.Error += (_, _) =>
                {
                    // Overflow or the directory going away. The paired fallback poll is what keeps the
                    // registration correct, so the mode drops back to the active cadence (see Healthy)
                    // and there is nothing else to recover here beyond the count.
                    _errored = true;
                    try { _onError(); } catch { }
                };
                _fsw.EnableRaisingEvents = true;
            }
            catch
            {
                // No watcher (missing directory, unsupported volume, handle exhaustion). Registrations
                // under it degrade to pure fallback polling, which is exactly the designed safety net.
                _fsw?.Dispose();
                _fsw = null;
            }
        }

        internal void SetRecursive(bool recurse)
        {
            Recursive = recurse;
            if (_fsw is null) return;
            try { _fsw.IncludeSubdirectories = recurse; } catch { }
        }

        public void Dispose()
        {
            if (_fsw is null) return;
            try { _fsw.EnableRaisingEvents = false; } catch { }
            try { _fsw.Dispose(); } catch { }
        }
    }
}
