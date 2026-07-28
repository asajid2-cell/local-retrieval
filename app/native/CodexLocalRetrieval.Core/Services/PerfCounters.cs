namespace CodexLocalRetrieval.Core.Services;

// Process-wide instrumentation for the win32-perf campaign. Every counter is a bare Interlocked add on a
// static long: no allocation, no lock, no logging, nothing to flush — so a hot path pays the same whether
// anyone ever reads it. Deliberately NOT behind #if, because a number that only exists in a debug build
// cannot gate a Release perf run; the gates measure exactly the binary that ships.
//
// The KEY STRINGS BELOW ARE AN APPEND-ONLY CONTRACT owned by the perf prong: baselines recorded under a
// name must stay comparable across the campaign. Add counters freely; never rename or repurpose one.
public static class PerfCounters
{
    private static long _searchTextCompositions;
    private static long _storeFullDeserializes;
    private static long _wmiSweeps;
    private static long _transcriptBytesRead;
    private static long _projectionSerializes;
    private static long _ledgerBytesRead;

    /// One SearchText(session) interpolation — recomposed per session per term today.
    public static void SearchTextComposed(long count = 1) => Interlocked.Add(ref _searchTextCompositions, count);

    /// One full AppStoreData deserialize (store load, or SaveAsync's read-back verification).
    public static void StoreFullDeserialize(long count = 1) => Interlocked.Add(ref _storeFullDeserializes, count);

    /// One WMI/CIM enumeration pass (process tree, open files, owner discovery).
    public static void WmiSweep(long count = 1) => Interlocked.Add(ref _wmiSweeps, count);

    /// Bytes pulled off disk while parsing .jsonl transcripts.
    public static void TranscriptBytesRead(long bytes) => Interlocked.Add(ref _transcriptBytesRead, bytes);

    /// One projection serialize (the projects/tabs payload handed to the relay).
    public static void ProjectionSerialize(long count = 1) => Interlocked.Add(ref _projectionSerializes, count);

    /// Bytes pulled off disk while replaying session/event ledgers.
    public static void LedgerBytesRead(long bytes) => Interlocked.Add(ref _ledgerBytesRead, bytes);

    /// Zero every counter. Call immediately before a measured region; counters are process-wide.
    public static void Reset()
    {
        Interlocked.Exchange(ref _searchTextCompositions, 0);
        Interlocked.Exchange(ref _storeFullDeserializes, 0);
        Interlocked.Exchange(ref _wmiSweeps, 0);
        Interlocked.Exchange(ref _transcriptBytesRead, 0);
        Interlocked.Exchange(ref _projectionSerializes, 0);
        Interlocked.Exchange(ref _ledgerBytesRead, 0);
    }

    /// Current values keyed by their contract names. Ordinal-sorted so artifacts diff cleanly.
    public static IReadOnlyDictionary<string, long> Snapshot() => new SortedDictionary<string, long>(StringComparer.Ordinal)
    {
        ["searchTextCompositions"] = Interlocked.Read(ref _searchTextCompositions),
        ["storeFullDeserializes"] = Interlocked.Read(ref _storeFullDeserializes),
        ["wmiSweeps"] = Interlocked.Read(ref _wmiSweeps),
        ["transcriptBytesRead"] = Interlocked.Read(ref _transcriptBytesRead),
        ["projectionSerializes"] = Interlocked.Read(ref _projectionSerializes),
        ["ledgerBytesRead"] = Interlocked.Read(ref _ledgerBytesRead),
    };
}
