namespace CodexLocalRetrieval.Core.Remote;

public sealed class HlAuthOutcomeCache
{
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _sequence;

    public HlAuthOutcomeCache(int capacity, TimeSpan ttl)
        : this(capacity, ttl, () => DateTimeOffset.UtcNow)
    {
    }

    internal HlAuthOutcomeCache(int capacity, TimeSpan ttl, Func<DateTimeOffset> utcNow)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        _capacity = capacity;
        _ttl = ttl;
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public bool TryGet(string key, out GateOutcome outcome)
    {
        if (string.IsNullOrEmpty(key))
        {
            outcome = default;
            return false;
        }

        lock (_sync)
        {
            var now = _utcNow();
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt > now)
                {
                    outcome = entry.Outcome;
                    return true;
                }
                _entries.Remove(key);
            }
        }

        outcome = default;
        return false;
    }

    public void Set(string key, GateOutcome outcome)
    {
        if (string.IsNullOrEmpty(key)) return;

        lock (_sync)
        {
            var now = _utcNow();
            RemoveExpired(now);
            if (!_entries.ContainsKey(key) && _entries.Count >= _capacity)
            {
                var oldest = _entries.MinBy(kv => (kv.Value.ExpiresAt, kv.Value.Sequence));
                _entries.Remove(oldest.Key);
            }
            _entries[key] = new Entry(now + _ttl, ++_sequence, outcome);
        }
    }

    internal int Count
    {
        get
        {
            lock (_sync)
            {
                RemoveExpired(_utcNow());
                return _entries.Count;
            }
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var key in _entries.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToArray())
            _entries.Remove(key);
    }

    private sealed record Entry(DateTimeOffset ExpiresAt, long Sequence, GateOutcome Outcome);
}
