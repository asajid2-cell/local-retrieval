namespace CodexLocalRetrieval.Core.Services;

/// <summary>
/// The result of one <see cref="StaleGuardedRefresher{T}.RefreshAsync"/> call.
/// <paramref name="IsCurrent"/> is the whole point: it is false when a newer refresh was started while
/// this one was in flight, which is the caller's signal to drop the value on the floor instead of
/// painting a stale panel over a fresh one.
/// </summary>
public readonly record struct RefreshOutcome<T>(bool IsCurrent, string Key, T? Value, Exception? Error)
    where T : class;

/// <summary>
/// A monotonic-sequence refresh guard for an expensive oracle that must never run on the caller's thread.
///
/// Three invariants, all of them assertable:
///   1. the build func runs on the dispatcher's thread, never the caller's (default dispatcher is
///      <see cref="Task.Run(Func{T})"/>);
///   2. of two overlapping refreshes only the newer one publishes, whatever order they finish in;
///   3. a refresh — force included — that arrives while a VALID/CURRENT build for the SAME key is already in
///      flight coalesces onto that build instead of starting a second one. An invalidated build is not current
///      and must never absorb a replacement refresh.
///
/// Coalescing is limited to an in-flight task whose sequence still equals the current sequence. This preserves
/// normal force-refresh coalescing while ensuring invalidation starts a replacement build for the same key.
///
/// The build func is supplied per refresh rather than per instance on purpose: it has to close over the
/// exact subject that was current when the refresh was requested. A ctor-injected build would have to read
/// shared mutable state at an arbitrary later moment, on another thread, and would happily publish a
/// summary of the wrong subject under the right key.
/// </summary>
public sealed class StaleGuardedRefresher<T> where T : class
{
    private readonly Func<Func<T>, Task<T>> _dispatch;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    private long _seq;
    private T? _value;
    private string _valueKey = "";
    private DateTimeOffset _builtAt;

    private Task<RefreshOutcome<T>>? _inFlight;
    private string _inFlightKey = "";
    private long _inFlightSeq;
    private int _coalesced;
    private int _builds;

    /// <param name="ttl">How long a published value stays fresh for <see cref="NeedsRefresh"/>.</param>
    /// <param name="dispatch">
    /// How a build is taken off the caller's thread. Defaults to <c>Task.Run</c>; tests inject their own to
    /// capture the thread the build actually ran on.
    /// </param>
    /// <param name="clock">Injectable now, so staleness is testable without sleeping.</param>
    public StaleGuardedRefresher(
        TimeSpan? ttl = null,
        Func<Func<T>, Task<T>>? dispatch = null,
        Func<DateTimeOffset>? clock = null)
    {
        _ttl = ttl ?? TimeSpan.FromSeconds(5);
        _dispatch = dispatch ?? (build => Task.Run(build));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The last successfully published value, whatever key it belongs to.</summary>
    public T? Current { get { lock (_gate) return _value; } }

    /// <summary>The key the published value belongs to; empty when nothing is published.</summary>
    public string CurrentKey { get { lock (_gate) return _valueKey; } }

    /// <summary>True while a build is in flight — the caller's cue to show a "checking" affordance.</summary>
    public bool IsRefreshing { get { lock (_gate) return _inFlight is not null; } }

    /// <summary>How many refreshes were absorbed by an in-flight build. Diagnostics and tests.</summary>
    public int CoalescedCount { get { lock (_gate) return _coalesced; } }

    /// <summary>How many times a build func was actually invoked. Diagnostics and tests.</summary>
    public int BuildCount { get { lock (_gate) return _builds; } }

    /// <summary>The published value for <paramref name="key"/>, or null if what we hold is for another key.</summary>
    public T? CurrentFor(string key)
    {
        lock (_gate)
            return string.Equals(_valueKey, key ?? "", StringComparison.Ordinal) ? _value : null;
    }

    /// <summary>Nothing published for this key, or what is published has aged past the TTL.</summary>
    public bool NeedsRefresh(string key)
    {
        lock (_gate)
        {
            if (_value is null || !string.Equals(_valueKey, key ?? "", StringComparison.Ordinal)) return true;
            return _clock() - _builtAt > _ttl;
        }
    }

    /// <summary>
    /// Publish a value the refresher did not build (a seed from a cheaper source). Bumps the sequence, so an
    /// in-flight build started before the seed will not overwrite it.
    /// </summary>
    public void Publish(string key, T value)
    {
        lock (_gate)
        {
            _seq++;
            _value = value ?? throw new ArgumentNullException(nameof(value));
            _valueKey = key ?? "";
            _builtAt = _clock();
        }
    }

    /// <summary>Forget everything published and let the next refresh start a new build.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _seq++;
            _value = null;
            _valueKey = "";
            _builtAt = default;
        }
    }

    /// <summary>
    /// Run <paramref name="build"/> off the caller's thread and publish the result only if no newer refresh
    /// started meanwhile. A refresh for a key that is already in flight returns that same task.
    /// </summary>
    public Task<RefreshOutcome<T>> RefreshAsync(string key, Func<T> build, bool force = false)
    {
        if (build is null) throw new ArgumentNullException(nameof(build));
        key ??= "";

        long seq;
        // The in-flight slot is claimed under the SAME lock that hands out the sequence number, so a build
        // that finishes before RefreshAsync returns still finds a slot to clear. Claiming it afterwards
        // leaves a completed task parked in the slot and IsRefreshing stuck on forever.
        var tcs = new TaskCompletionSource<RefreshOutcome<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            // A force refresh wants a build newer than the published value. An in-flight build for the same
            // key already IS that, so piling a second one on top buys nothing but contention.
            if (_inFlight is not null && _inFlightSeq == _seq && string.Equals(_inFlightKey, key, StringComparison.Ordinal))
            {
                _coalesced++;
                return _inFlight;
            }

            if (!force && !NeedsRefreshLocked(key))
                return Task.FromResult(new RefreshOutcome<T>(true, key, _value, null));

            seq = ++_seq;
            _builds++;
            _inFlight = tcs.Task;
            _inFlightKey = key;
            _inFlightSeq = seq;
        }

        _ = RunAsync(key, build, seq, tcs);
        return tcs.Task;
    }

    private async Task RunAsync(string key, Func<T> build, long seq, TaskCompletionSource<RefreshOutcome<T>> tcs)
    {
        T? value = null;
        Exception? error = null;
        try
        {
            value = await _dispatch(build).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        RefreshOutcome<T> outcome;
        lock (_gate)
        {
            if (_inFlightSeq == seq)
            {
                _inFlight = null;
                _inFlightKey = "";
                _inFlightSeq = 0;
            }

            if (seq != _seq)
            {
                // A newer refresh has already been asked for; whatever we built is answering a question
                // nobody is waiting on any more.
                outcome = new RefreshOutcome<T>(false, key, value, error);
            }
            else
            {
                if (error is null && value is not null)
                {
                    _value = value;
                    _valueKey = key;
                    _builtAt = _clock();
                }
                else
                {
                    // A failed oracle must not leave a reassuring stale answer standing in for a real one.
                    _value = null;
                    _valueKey = "";
                    _builtAt = default;
                }

                outcome = new RefreshOutcome<T>(true, key, value, error);
            }
        }

        tcs.SetResult(outcome);
    }

    private bool NeedsRefreshLocked(string key)
    {
        if (_value is null || !string.Equals(_valueKey, key, StringComparison.Ordinal)) return true;
        return _clock() - _builtAt > _ttl;
    }
}
