using System.IO.Enumeration;

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

    /// First cadence for a COARSE (whole-tree) probe whose OS watcher is not carrying events. A tree probe
    /// is a recursive walk of every transcript - ~20k files here - so the 5 s active cadence is only
    /// tolerable for a single stat. It doubles from here up to <see cref="IdleFallbackInterval"/> while
    /// polls keep finding no change, which bounds a dead watcher to one walk a minute instead of twelve.
    public TimeSpan CoarseFallbackInterval { get; init; } = TimeSpan.FromSeconds(30);

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
    private int _pumping;
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

    // Test seam: register with an injected probe so a test can drive a synthetic or deliberately slow
    // stamp without a real tree. Production registrations always use WatchFile/WatchDirectory.
    internal IFileWatchRegistration WatchDirectoryForTest(string directory, Func<string> probe, Action<IReadOnlyList<string>?> onChanged)
        => Register(Path.GetFullPath(directory), recurse: true, matches: _ => true, probe, onChanged, coarseFallback: true);

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
    // dropped event on a single watched file is noticed in seconds. A coarse tree probe is a recursive
    // walk, so it is slow when the event path carries the change and merely bounded (never per-tick) when
    // it does not. The 30s floor is what keeps a dead watcher from walking every transcript on every tick.
    private TimeSpan FallbackIntervalFor(Registration reg) =>
        !reg.CoarseFallback
            ? _options.ActiveFallbackInterval
            : (reg.Watcher?.Healthy ?? false)
                ? _options.IdleFallbackInterval
                : _options.CoarseFallbackInterval;

    // Diagnostics: whether the OS watchers backing the registrations are carrying events or whether the
    // registrations are living on the fallback poll. Lets the server's trace say which mode it is in.
    public bool AllWatchersHealthy
    {
        get { lock (_gate) return _watchers.Values.All(w => w.Healthy); }
    }

    /// Evaluate every registration's debounce and fallback deadlines against the clock. Non-reentrant:
    /// a tick that overlaps one still in flight returns immediately instead of queueing a second body.
    public void Pump()
    {
        // A 250 ms tick whose body is stuck in a filesystem walk would otherwise queue a fresh callback
        // every interval, and every queued callback parks a thread-pool thread until the walk finishes --
        // enough of them starve unrelated work (the server's /healthz). One body at a time is enough: a
        // registration's deadlines are only advanced by a pump that actually ran.
        if (Interlocked.CompareExchange(ref _pumping, 1, 0) != 0) return;
        try { PumpCore(); }
        finally { Interlocked.Exchange(ref _pumping, 0); }
    }

    /// The pump body. Probes (the recursive tree walk) run OUTSIDE <see cref="_gate"/>: holding the lock
    /// across a walk of ~16k transcripts (~1.1 s measured here) is what let a fallback poll park every
    /// other thread that needed the service -- including the timer's own overlapping ticks -- behind it.
    /// Callbacks are likewise invoked outside the lock so a slow consumer cannot stall registration.
    private void PumpCore()
    {
        var now = _options.Clock.UtcNow;
        List<(Registration Reg, IReadOnlyList<string>? Paths)>? fire = null;
        List<Registration>? fallbackDue = null;   // need a probe + stamp comparison
        List<Registration>? refreshStamp = null;  // already fired on the event path; just refresh the stamp

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
                    if (reg.CoarseFallback) reg.EventDeliveredSincePoll = true;
                    (fire ??= new()).Add((reg, SnapshotPaths(reg)));
                    // Refresh the remembered stamp so the next poll does not re-fire the change the event
                    // already delivered. Probing it is the expensive half, so it happens off the lock below.
                    (refreshStamp ??= new()).Add(reg);
                    continue;
                }

                if (now < reg.NextFallbackAt) continue;

                Interlocked.Increment(ref Stats._fallbackPolls);
                PerfCounters.FileWatchFallbackPoll();
                (fallbackDue ??= new()).Add(reg);
            }
        }

        var stamps = new Dictionary<Registration, string>();
        if (fallbackDue is not null) foreach (var reg in fallbackDue) stamps[reg] = SafeProbe(reg.Probe);
        // After an event-delivered change only a CHEAP probe is refreshed. For a coarse registration the
        // probe is a recursive walk of the whole tree, and agents write constantly, so this refresh was
        // walking every transcript on every debounce window (250 ms) - the largest remaining burn. Its
        // stamp is left stale instead: the next scheduled fallback poll compares it, sees the change it
        // already acted on, and re-baselines then, once per poll rather than four times a second.
        if (refreshStamp is not null)
            foreach (var reg in refreshStamp)
                if (!reg.CoarseFallback) stamps[reg] = SafeProbe(reg.Probe);

        if (stamps.Count > 0)
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (refreshStamp is not null)
                    foreach (var reg in refreshStamp)
                        if (!reg.Disposed && stamps.TryGetValue(reg, out var refreshed))
                            reg.LastStamp = refreshed;

                if (fallbackDue is not null)
                    foreach (var reg in fallbackDue)
                    {
                        if (reg.Disposed) continue;
                        var stamp = stamps[reg];
                        var changed = reg.LastStamp is null || !string.Equals(stamp, reg.LastStamp, StringComparison.Ordinal);
                        reg.LastStamp = stamp;
                        if (changed)
                        {
                            var alreadyDelivered = reg.CoarseFallback
                                && reg.EventDeliveredSincePoll
                                && (reg.Watcher?.Healthy ?? false);
                            reg.EventDeliveredSincePoll = false;
                            Promote(reg, now);
                            // Re-baseline without firing when the change this poll sees is the one the event
                            // path already delivered: the stamp was left stale on purpose, and treating that
                            // as news would rescan every transcript on every poll while an agent is writing.
                            if (!alreadyDelivered)
                                (fire ??= new()).Add((reg, null)); // which file moved is unknown: "assume all"
                        }
                        else
                        {
                            // Quiet. One no-change poll is enough to demote to the slow cadence; the FSW
                            // event is what makes activity visible immediately, this is only the safety
                            // net. A coarse registration whose watcher is not carrying events cannot
                            // demote all the way, but it must BACK OFF: keeping it at the 5s active
                            // cadence walked every transcript tree twelve times a minute, continuously,
                            // for the rest of the process once a watcher had errored (measured: ~46% of
                            // a core on one thread). Doubling from the coarse floor to the idle ceiling
                            // bounds that to one walk a minute.
                            reg.FallbackInterval = reg.CoarseFallback && !(reg.Watcher?.Healthy ?? false)
                                ? TimeSpan.FromTicks(Math.Min(
                                    _options.IdleFallbackInterval.Ticks,
                                    Math.Max(_options.CoarseFallbackInterval.Ticks, reg.FallbackInterval.Ticks * 2)))
                                : _options.IdleFallbackInterval;
                            reg.NextFallbackAt = now + reg.FallbackInterval;
                        }
                    }
            }
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
    ///
    /// The write time comes out of the directory entry the enumeration already returned, so a walk is one
    /// batched pass instead of a `GetFileAttributes`/stat syscall per file - the difference between ~20k
    /// syscalls and ~20k struct reads for the same answer.
    public static string DirectoryStamp(string directory, string pattern, bool recurse)
    {
        if (!Directory.Exists(directory)) return "-";
        var pending = new Stack<string>();
        pending.Push(directory);
        long count = 0, newest = 0;
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                var entries = new FileSystemEnumerable<(string Name, long Ticks, bool IsDirectory)>(
                    current,
                    static (ref FileSystemEntry entry) =>
                        (entry.FileName.ToString(), entry.LastWriteTimeUtc.UtcTicks, entry.IsDirectory),
                    StampWalkOptions);
                foreach (var (name, ticks, isDirectory) in entries)
                {
                    if (isDirectory)
                    {
                        if (recurse) pending.Push(Path.Combine(current, name));
                        continue;
                    }
                    if (!MatchesPattern(name, pattern)) continue;
                    count++;
                    if (ticks > newest) newest = ticks;
                }
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
        }
        return count.ToString() + ":" + newest.ToString();
    }

    // Top-level entries only (the walk pushes subdirectories itself, iteratively, so a cyclic or deep
    // tree cannot exhaust the stack) and reparse points skipped, matching the old traversal's rules.
    private static readonly EnumerationOptions StampWalkOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MatchType = MatchType.Simple,
    };

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
        // Set when a coarse registration already delivered a change on the event path. The tree stamp is
        // deliberately left stale after such a delivery (probing it per debounce was the burn), so the
        // next fallback poll is EXPECTED to see a difference -- re-baselining silently is what stops that
        // difference from firing a whole-tree rescan on every poll for the rest of the session.
        internal bool EventDeliveredSincePoll;

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
        private volatile bool _errored;
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
                // An event proves the channel is carrying again, so a past overflow stops demoting this
                // watcher's registrations to the coarse poll. Errored flips back here rather than staying
                // true for the life of the process.
                void Handle(object _, FileSystemEventArgs e)
                {
                    _errored = false;
                    try { onEvent(e.FullPath); } catch { }
                }
                _fsw.Changed += Handle;
                _fsw.Created += Handle;
                _fsw.Deleted += Handle;
                _fsw.Renamed += (_, e) => { _errored = false; try { onEvent(e.FullPath); } catch { } };
                _fsw.Error += (_, _) =>
                {
                    // Overflow or the directory going away. The paired fallback poll keeps the
                    // registration correct; toggling the watcher drains the overflowed buffer and starts
                    // a fresh one so the event path can resume. Without the toggle, ONE overflow left
                    // this watcher "unhealthy" forever and every tree registration walked ~20k files on
                    // the active cadence for the rest of the run.
                    _errored = true;
                    try { _onError(); } catch { }
                    try
                    {
                        _fsw.EnableRaisingEvents = false;
                        _fsw.EnableRaisingEvents = true;
                    }
                    catch { /* directory gone: stay on the fallback poll */ }
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
