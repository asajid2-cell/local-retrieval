using System.Diagnostics;

namespace CodexLocalRetrieval.Core.Remote;

// One live agent session as the snapshot probe sees it.
public sealed record FleetLiveSession(string Tool, string SessionId, IReadOnlyCollection<int> Pids);

// The always-on fleet recorder: keeps <fleet>/current.json describing every live claude/codex
// session on this PC, so an involuntary reboot leaves behind "what was open when it died"
// (promoted to a named state by FleetStore.TryPromoteStaleCurrent at next start). Exactly one
// host process writes (FleetWriterLock, an OS-held handle); everyone else reads.
//
// Trust rule: a tick whose probe reports partial visibility (ok=false from the liveness sweep)
// NEVER writes — a partial world would emit false "session ended" transitions and could demote
// live sessions out of the record. Skipping a tick only delays freshness; writing a wrong world
// corrupts the crash record, which is the one thing this service exists to keep true.
public sealed class FleetSnapshotService
{
    public sealed record Options(
        FleetStore.Options? Store = null,
        TimeSpan? Interval = null,          // probe cadence (default 60s)
        TimeSpan? ForceWriteEvery = null,   // freshness floor even with no membership change (default 10min)
        Func<(bool Ok, List<FleetLiveSession> Live, string Detail)>? Probe = null,
        Action<FleetSessionTransition>? OnTransition = null,
        Action<string>? Log = null,
        Func<DateTimeOffset>? Clock = null)
    {
        public FleetStore.Options EffectiveStore => Store ?? new FleetStore.Options();
        public TimeSpan EffectiveInterval => Interval is { } i && i > TimeSpan.Zero ? i : TimeSpan.FromSeconds(60);
        public TimeSpan EffectiveForceWriteEvery => ForceWriteEvery is { } f && f > TimeSpan.Zero ? f : TimeSpan.FromMinutes(10);
        public Func<(bool Ok, List<FleetLiveSession> Live, string Detail)> EffectiveProbe => Probe ?? DefaultProbe;
        public Func<DateTimeOffset> EffectiveClock => Clock ?? (() => DateTimeOffset.UtcNow);
    }

    public sealed record FleetSessionTransition(string Kind, string Tool, string SessionId); // Kind: "started" | "ended"

    private readonly string _host;
    private readonly Options _options;
    private FleetState? _previous;          // last state this instance wrote (or read at startup)
    private DateTimeOffset _lastWriteUtc = DateTimeOffset.MinValue;

    public FleetSnapshotService(string host, Options? options = null)
    {
        _host = string.IsNullOrWhiteSpace(host) ? "unknown" : host.Trim();
        _options = options ?? new Options();
    }

    // The real-world probe: one cached liveness sweep (cmdline ids + Claude live-session registry +
    // open transcript handles), tool attributed per pid from the process scan. Composes only public
    // RunningSessions surface. ok=false whenever any leg reports partial visibility.
    public static (bool Ok, List<FleetLiveSession> Live, string Detail) DefaultProbe()
    {
        var live = new List<FleetLiveSession>();
        if (!RunningSessions.TryScan(out var procs, out var scanDetail))
            return (false, live, scanDetail);
        var toolByPid = new Dictionary<int, string>();
        foreach (var p in procs)
            if (p.Pid > 0 && !string.IsNullOrWhiteSpace(p.Tool))
                toolByPid[p.Pid] = p.Tool;

        if (!RunningSessions.TryLiveSessionPids(out var byId, out _, out var liveDetail, bypassCache: false))
            return (false, live, liveDetail);

        foreach (var kv in byId)
        {
            var tool = "";
            foreach (var pid in kv.Value)
                if (toolByPid.TryGetValue(pid, out var t)) { tool = t; break; }
            // A pid outside the scan universe shouldn't happen (all legs are bounded to scanned
            // pids); if it ever does, keep the session with tool "" rather than dropping a live id.
            live.Add(new FleetLiveSession(tool, kv.Key, kv.Value.ToList()));
        }
        return (true, live, "");
    }

    // One tick: probe, carry firstSeen forward, write when membership changed or freshness floor hit.
    public (bool Ok, bool Wrote, FleetState? State, string Detail) RunOnce()
    {
        var now = _options.EffectiveClock();
        var (ok, live, probeDetail) = _options.EffectiveProbe();
        if (!ok)
        {
            _options.Log?.Invoke($"fleet: skipping tick — partial visibility: {probeDetail}");
            return (false, false, null, probeDetail);
        }

        _previous ??= ReadCurrentOrEmpty();
        var prevById = new Dictionary<string, FleetSessionRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _previous.Sessions)
            if (!string.IsNullOrWhiteSpace(s.SessionId) && !prevById.ContainsKey(s.SessionId))
                prevById[s.SessionId] = s;

        var nowStr = now.ToString("O");
        var sessions = new List<FleetSessionRecord>();
        foreach (var s in live.OrderBy(x => x.SessionId, StringComparer.OrdinalIgnoreCase))
        {
            prevById.TryGetValue(s.SessionId, out var prev);
            sessions.Add(new FleetSessionRecord(
                s.Tool,
                s.SessionId,
                s.Pids.Where(p => p > 0).OrderBy(p => p).ToList(),
                string.IsNullOrWhiteSpace(prev?.FirstSeenUtc) ? nowStr : prev!.FirstSeenUtc,
                nowStr));
        }

        var state = new FleetState
        {
            Name = "",
            Kind = "rolling",
            SavedAtUtc = nowStr,
            BootStampUtc = FleetStore.CurrentBootStampUtc(now),
            Sessions = sessions,
        };

        var currentIds = new HashSet<string>(sessions.Select(s => s.SessionId), StringComparer.OrdinalIgnoreCase);
        var previousIds = new HashSet<string>(prevById.Keys, StringComparer.OrdinalIgnoreCase);
        var changed = !currentIds.SetEquals(previousIds);
        var due = now - _lastWriteUtc >= _options.EffectiveForceWriteEvery;
        if (!changed && !due)
            return (true, false, state, "");

        if (!FleetStore.TryWriteCurrent(state, out var writeDetail, _options.EffectiveStore))
        {
            _options.Log?.Invoke("fleet: write failed: " + writeDetail);
            return (false, false, state, writeDetail);
        }

        if (changed && _options.OnTransition is { } emit)
        {
            foreach (var id in currentIds.Except(previousIds))
                emit(new FleetSessionTransition("started", sessions.First(s => s.SessionId.Equals(id, StringComparison.OrdinalIgnoreCase)).Tool, id));
            foreach (var id in previousIds.Except(currentIds))
                emit(new FleetSessionTransition("ended", prevById[id].Tool, id));
        }

        _previous = state;
        _lastWriteUtc = now;
        return (true, true, state, "");
    }

    // Capture the world right now as a user-named state. Deliberately does NOT need the writer lock:
    // named saves are one-shot user actions and never touch current.json.
    public (bool Ok, string Detail) SaveNamed(string name)
    {
        var now = _options.EffectiveClock();
        var (ok, live, probeDetail) = _options.EffectiveProbe();
        if (!ok) return (false, "refusing to save a partially-visible fleet: " + probeDetail);
        var nowStr = now.ToString("O");
        var state = new FleetState
        {
            Kind = "named",
            SavedAtUtc = nowStr,
            BootStampUtc = FleetStore.CurrentBootStampUtc(now),
            Sessions = live
                .OrderBy(x => x.SessionId, StringComparer.OrdinalIgnoreCase)
                .Select(s => new FleetSessionRecord(s.Tool, s.SessionId, s.Pids.Where(p => p > 0).OrderBy(p => p).ToList(), nowStr, nowStr))
                .ToList(),
        };
        return FleetStore.TrySaveNamed(name, state, out var detail, _options.EffectiveStore)
            ? (true, "")
            : (false, detail);
    }

    // Host loop. Returns false immediately (after logging who holds it) when another process is the
    // writer — the caller then serves reads only. Promotion runs only on the elected writer, before
    // its first write, so the pre-reboot record is preserved exactly once.
    public async Task<bool> RunAsync(CancellationToken ct)
    {
        if (!FleetWriterLock.TryAcquire(_host, out var lockHandle, out var lockDetail,
                new FleetWriterLock.Options(_options.EffectiveStore.RootDirectory)))
        {
            FleetWriterLock.TryReadHolder(out var holder, out _, new FleetWriterLock.Options(_options.EffectiveStore.RootDirectory));
            _options.Log?.Invoke($"fleet: read-only (writer is {(holder is null ? "unknown" : holder.Host + " pid " + holder.Pid)}): {lockDetail}");
            return false;
        }

        using (lockHandle)
        {
            if (FleetStore.TryPromoteStaleCurrent(out var promoted, out var promoteDetail, _options.EffectiveStore))
            {
                if (promoted is not null)
                    _options.Log?.Invoke($"fleet: preserved pre-reboot fleet as state '{promoted}'");
            }
            else
                _options.Log?.Invoke("fleet: promote check failed: " + promoteDetail);

            _options.Log?.Invoke($"fleet: recording as '{_host}' every {(int)_options.EffectiveInterval.TotalSeconds}s → {_options.EffectiveStore.EffectiveRootDirectory}");
            while (!ct.IsCancellationRequested)
            {
                try { RunOnce(); }
                catch (Exception ex) { _options.Log?.Invoke("fleet: tick failed: " + ex.Message); }
                try { await Task.Delay(_options.EffectiveInterval, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        return true;
    }

    private FleetState ReadCurrentOrEmpty()
    {
        if (FleetStore.TryReadCurrent(out var state, out _, _options.EffectiveStore) && state is not null)
            return state;
        return new FleetState { SavedAtUtc = "", BootStampUtc = "", Sessions = new() };
    }
}
