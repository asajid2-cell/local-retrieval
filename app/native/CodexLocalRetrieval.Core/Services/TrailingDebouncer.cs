using System;
using System.Threading;

namespace CodexLocalRetrieval.Core.Services;

// Trailing-edge debouncer: a burst of Post() calls collapses into ONE callback, fired `delay` after
// the LAST post and carrying the last posted value. Typing "hello" in the search box is 5 keystrokes
// but must only cost one filter pass.
//
// Deliberately dispatcher-agnostic: the caller injects HOW the callback reaches its thread (WinUI hands
// us DispatcherQueue.TryEnqueue; tests run it inline), so this stays a plain Core service that can be
// tested without a UI thread. Disposal drops anything still pending — a closed page never fires.
public sealed class TrailingDebouncer<T> : IDisposable
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(150);

    private readonly Action<T> _callback;
    private readonly Action<Action> _post;
    private readonly TimeSpan _delay;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private T? _pending;
    private bool _hasPending;
    private bool _disposed;

    /// <param name="callback">Invoked once per settled burst, with the last posted value.</param>
    /// <param name="delay">Quiet period before firing. Defaults to <see cref="DefaultDelay"/> (150 ms).</param>
    /// <param name="post">Marshals the callback onto the right thread. Defaults to running it inline.</param>
    public TrailingDebouncer(Action<T> callback, TimeSpan? delay = null, Action<Action>? post = null)
    {
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _delay = delay ?? DefaultDelay;
        _post = post ?? (run => run());
        _timer = new Timer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public TimeSpan Delay => _delay;

    // Restarts the quiet period and replaces whatever value was queued.
    public void Post(T value)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = value;
            _hasPending = true;
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    // Drop a queued invocation without firing it (e.g. the user hit Enter and we ran the filter eagerly).
    public void Cancel()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _hasPending = false;
            _pending = default;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        T value;
        lock (_gate)
        {
            if (_disposed || !_hasPending) return;
            value = _pending!;
            _hasPending = false;
            _pending = default;
        }
        _post(() => _callback(value));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _hasPending = false;
            _pending = default;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        _timer.Dispose();
    }
}
