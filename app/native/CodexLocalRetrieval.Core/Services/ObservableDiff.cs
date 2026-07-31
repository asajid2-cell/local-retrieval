using System.Collections.ObjectModel;

namespace CodexLocalRetrieval.Core.Services;

// Bring a bound ObservableCollection to a desired state using as few change notifications as possible.
//
// WHY THIS EXISTS: a ListView bound to an ObservableCollection rebuilds item containers in response to
// CollectionChanged. Clear() raises a Reset, which throws away EVERY container, and the N Add()s that
// follow build N new ones. Refreshing a 600-row list that way costs 601 notifications and a full
// relayout — on the dispatcher, on every keystroke. That is a visible freeze, and it happens whether
// or not the list actually changed.
//
// Apply() diffs by stable key instead. Re-running an unchanged filter emits zero notifications; a
// k-row delta emits O(k). Pure and WinUI-free so it can be tested without a UI thread.
public static class ObservableDiff
{
    // Above this fraction of churn, a Reset genuinely is cheaper than a long run of Moves: the ListView
    // rebuilds once instead of being walked row by row. Below it, the incremental path wins by a mile.
    // Expressed as "survivors must be at least this fraction of the desired list to diff in place".
    private const double MinSurvivorFraction = 0.5;

    // Same rows, different order. Membership overlap is total, so MinSurvivorFraction never catches it,
    // yet reordering in place costs one notification per displaced row AND a memmove per Move — quadratic
    // on a full reversal. A Reset costs the same notifications with linear work, so past this much
    // displacement the list is rebuilt instead of walked.
    private const double MaxDisplacementFraction = 0.5;

    // Returns the number of CollectionChanged notifications raised. Callers use it as an assertion
    // target and as a perf counter; it is exactly what the UI is being asked to react to.
    public static int Apply<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> desired,
        Func<T, string> keyOf,
        IEqualityComparer<string>? keyComparer = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(keyOf);
        var comparer = keyComparer ?? StringComparer.OrdinalIgnoreCase;

        // Fast path: already correct. This is the common case — a keystroke that narrows nothing, or a
        // mutation that re-runs the same filter — and it must cost nothing at all.
        if (AlreadyEqual(target, desired)) return 0;

        // Nothing wanted at all. ONE Reset, never one notification per row. This is what "the filter
        // matched nothing" looks like — an everyday keystroke, and the single worst case for the old
        // rebuild — so emptying the list row by row here would put the freeze straight back exactly
        // where the user is most likely to meet it.
        if (desired.Count == 0)
        {
            if (target.Count == 0) return 0;
            target.Clear();
            return 1;
        }

        var desiredKeys = new HashSet<string>(comparer);
        foreach (var item in desired) desiredKeys.Add(keyOf(item));

        // What target WOULD look like once everything unwanted is dropped. Computed before the
        // collection is touched, so a decision to rebuild never pays for removals it is about to throw
        // away.
        var survivors = new List<T>(target.Count);
        foreach (var item in target)
            if (desiredKeys.Contains(keyOf(item))) survivors.Add(item);

        // Too little overlap: a genuinely different result set is cheaper as one Reset than as hundreds
        // of Moves.
        if (survivors.Count < desired.Count * MinSurvivorFraction) return Rebuild(target, desired);

        // Enough overlap, but is it in roughly the right ORDER? See MaxDisplacementFraction.
        //
        // The measure is how many survivors genuinely have to MOVE: everything outside the longest
        // subsequence that is already in relative order. Counting plain positional mismatches instead
        // would bill floating ONE row to the top — a pin, or a bump-on-resume — as though three quarters
        // of the list had shifted, and rebuild a list that a single Move fixes.
        var survivorIndex = new Dictionary<string, int>(survivors.Count, comparer);
        for (var i = survivors.Count - 1; i >= 0; i--) survivorIndex[keyOf(survivors[i])] = i;

        var positions = new List<int>(desired.Count);
        foreach (var item in desired)
            if (survivorIndex.TryGetValue(keyOf(item), out var at)) positions.Add(at);

        var moves = survivors.Count - LongestOrderedRun(positions);
        if (moves > desired.Count * MaxDisplacementFraction) return Rebuild(target, desired);

        var ops = 0;

        // 1. Drop everything that is no longer wanted, back to front so indices stay valid.
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (desiredKeys.Contains(keyOf(target[i]))) continue;
            target.RemoveAt(i);
            ops++;
        }

        // key -> current index, built once and kept in step with every Move and Insert below. Without it
        // the walk rescans the tail for each row it has to relocate, which is a scan per row.
        // Maintenance costs no more than the collection operation it accompanies (Move and Insert both
        // shift the same span internally), so lookups become O(1) without changing the asymptotics.
        // Built back-to-front so the FIRST occurrence of a duplicated key is the one recorded.
        var indexOf = new Dictionary<string, int>(target.Count, comparer);
        for (var i = target.Count - 1; i >= 0; i--) indexOf[keyOf(target[i])] = i;

        // 2. Walk the desired order, moving survivors into place and inserting newcomers. Every item
        //    already in the right slot costs nothing.
        for (var i = 0; i < desired.Count; i++)
        {
            var wanted = desired[i];
            var wantedKey = keyOf(wanted);

            if (i < target.Count && comparer.Equals(keyOf(target[i]), wantedKey))
            {
                // Right key, but make sure it is the same instance the caller handed us — a stale
                // object here would silently diverge from a full rebuild.
                if (!ReferenceEquals(target[i], wanted)) { target[i] = wanted; ops++; }
                continue;
            }

            // The map is authoritative; the bounds and key re-check keep a duplicated key from
            // resurrecting a slot that has already been consumed.
            var found = indexOf.TryGetValue(wantedKey, out var candidate)
                        && candidate > i
                        && candidate < target.Count
                        && comparer.Equals(keyOf(target[candidate]), wantedKey)
                ? candidate
                : -1;

            if (found >= 0)
            {
                target.Move(found, i);
                ops++;
                if (!ReferenceEquals(target[i], wanted)) { target[i] = wanted; ops++; }
                for (var k = i; k <= found && k < target.Count; k++) indexOf[keyOf(target[k])] = k;
            }
            else
            {
                target.Insert(i, wanted);
                ops++;
                for (var k = i; k < target.Count; k++) indexOf[keyOf(target[k])] = k;
            }
        }

        // 3. Trim any tail left over (duplicate keys, or a shorter desired list).
        while (target.Count > desired.Count)
        {
            var last = target.Count - 1;
            indexOf.Remove(keyOf(target[last]));
            target.RemoveAt(last);
            ops++;
        }

        return ops;
    }

    // Length of the longest strictly-increasing subsequence — the rows that can stay exactly where they
    // are while everything else moves around them. Patience-sorting, O(n log n); the naive alternative
    // is the quadratic scan this whole map exists to avoid.
    private static int LongestOrderedRun(List<int> values)
    {
        var tails = new List<int>();
        foreach (var value in values)
        {
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (tails[mid] < value) lo = mid + 1; else hi = mid;
            }
            if (lo == tails.Count) tails.Add(value); else tails[lo] = value;
        }
        return tails.Count;
    }

    private static int Rebuild<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
        where T : class
    {
        target.Clear();
        foreach (var item in desired) target.Add(item);
        return 1 + desired.Count;
    }

    // Deliberately REFERENCE equality, not key equality. A re-parse or a merge-scan can put a brand new
    // object under an id the list already holds; if this fast path accepted that as "unchanged", Apply
    // would report zero operations and leave the UI bound to the stale instance — a silent staleness bug
    // that no amount of key-matching would reveal. Matching keys with differing instances therefore falls
    // through to the diff, which replaces them. The common case (same objects, same order) is unaffected.
    private static bool AlreadyEqual<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
        where T : class
    {
        if (target.Count != desired.Count) return false;
        for (var i = 0; i < desired.Count; i++)
            if (!ReferenceEquals(target[i], desired[i]))
                return false;
        return true;
    }
}
